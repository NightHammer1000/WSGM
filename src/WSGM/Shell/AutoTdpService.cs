using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Truthful state of AutoTDP for the overlay, native QAM, and diagnostics.</summary>
internal enum AutoTdpState
{
    /// <summary>The user has not enabled AutoTDP.</summary>
    Off,

    /// <summary>Enabled, but a prerequisite is missing.</summary>
    Unavailable,

    /// <summary>Enabled and waiting for a foreground application to render.</summary>
    Idle,

    /// <summary>Actively controlling the power limit.</summary>
    Controlling,

    /// <summary>Suspended because the power limit was changed by hand.</summary>
    Paused,
}

/// <summary>The complete AutoTDP projection.</summary>
internal sealed record AutoTdpStatus(
    AutoTdpState State,
    int? Watts,
    double? FrametimeMs,
    double? TargetFrametimeMs,
    string? ApplicationId,
    string Detail);

/// <summary>
/// The one AutoTDP session service.
/// </summary>
/// <remarks>
/// A thin binding around <see cref="AutoTdpController"/>: it decides nothing itself, so the whole
/// control policy stays replayable from a recorded trace without a device. What lives here is the
/// plumbing the controller must not know about — which application is in front, which capability is
/// the primary power limit, and the rule that only one power write may be in flight.
/// <para>
/// Every prerequisite is optional and checked each tick. No RTSS, no plugin, no power capability, or
/// no rendering application simply means AutoTDP holds; none of them is an error, and none of them
/// may take a frame limit or a manual power setting away from the user.
/// </para>
/// </remarks>
internal sealed class AutoTdpService : IAsyncDisposable
{
    /// <summary>How often frame delivery is judged.</summary>
    /// <remarks>
    /// One second per window. Shorter windows judge a power change before the SoC has finished
    /// responding to the previous one; longer ones let a stutter run for too long before power rises.
    /// </remarks>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly IFrametimeSource _frametimes;
    private readonly Func<IReadOnlyList<DeviceCapabilityView>> _capabilities;
    private readonly Func<string, string?, CapabilityValue, CancellationToken, Task<CapabilityCommandResult>> _writeAsync;
    private readonly Func<double> _targetFrametimeMs;
    private readonly AutoTdpController _controller = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();

    private Task _worker = Task.CompletedTask;
    private Task<bool> _lastStop = Task.FromResult(true);
    private CancellationTokenSource? _generation;
    private RunningApplicationTargetSnapshot? _running;
    private int? _restoreTo;
    private bool _controllerStarted;
    private bool _powerMayDiffer;
    private bool _enabled;
    private bool _resync;
    private bool _disposed;

    internal AutoTdpService(
        IFrametimeSource frametimes,
        Func<IReadOnlyList<DeviceCapabilityView>> capabilities,
        Func<string, string?, CapabilityValue, CancellationToken, Task<CapabilityCommandResult>> writeAsync,
        Func<double> targetFrametimeMs)
    {
        ArgumentNullException.ThrowIfNull(frametimes);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(writeAsync);
        ArgumentNullException.ThrowIfNull(targetFrametimeMs);
        _frametimes = frametimes;
        _capabilities = capabilities;
        _writeAsync = writeAsync;
        _targetFrametimeMs = targetFrametimeMs;
    }

    /// <summary>Raised when the projection changes.</summary>
    internal event Action<AutoTdpStatus>? StatusChanged;

    /// <summary>Current projection.</summary>
    internal AutoTdpStatus Status { get; private set; } = new(
        AutoTdpState.Off,
        null,
        null,
        null,
        null,
        "AutoTDP is off.");

    /// <summary>Enables or disables automatic control.</summary>
    /// <param name="enabled">Whether AutoTDP should run.</param>
    /// <remarks>
    /// One tick loop exists at a time, and disabling ends the current one before the previous limit
    /// is restored. Leaving the loop alive across a disable let the next enable start a second one
    /// against the same flag: every off/on cycle then multiplied the policy rate and its hardware
    /// writes, and a tick already inside <see cref="TickAsync"/> could write AutoTDP's own value
    /// after the restore and leave it latched while the feature was off.
    /// </remarks>
    internal void Apply(bool enabled)
    {
        Task worker;
        Task<bool> stop;
        CancellationTokenSource? generation;
        lock (_gate)
        {
            if (_disposed || _enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            if (enabled)
            {
                _resync = false;
                _controllerStarted = false;
                // The token is taken from a local, not from the field: a later disable clears the
                // field, and a worker that read it there would dereference null on the thread pool
                // instead of running.
                CancellationTokenSource started =
                    CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _generation = started;
                Task<bool> priorStop = _lastStop;
                _worker = Task.Run(async () =>
                {
                    await priorStop.ConfigureAwait(false);
                    started.Token.ThrowIfCancellationRequested();
                    await RunAsync(started.Token).ConfigureAwait(false);
                }, started.Token);
                return;
            }

            worker = _worker;
            generation = _generation;
            _worker = Task.CompletedTask;
            _generation = null;
            stop = StopGenerationAsync(worker, generation);
            _lastStop = stop;
        }

        Log.Observe(stop, "AutoTDP stop");
    }

    /// <summary>Records the running application whose frames are being judged.</summary>
    /// <param name="snapshot">The canonical running-application snapshot.</param>
    internal void ApplyRunningApplication(RunningApplicationTargetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _running = snapshot;
        }
    }

    /// <summary>Suspends control because the power limit was set by hand.</summary>
    /// <param name="watts">The limit that was just set.</param>
    /// <remarks>
    /// Called by whoever writes the power capability from a user action. Control does not resume by
    /// itself — only switching AutoTDP off and on does: the user has overridden the controller, and
    /// quietly taking the limit back would make the manual control look broken.
    /// </remarks>
    internal void NoteManualChange(int watts)
    {
        lock (_gate)
        {
            _controller.PauseForManualChange(watts);
        }

        Publish(AutoTdpState.Paused, watts, null, null, "Paused by a manual power change.");
    }

    /// <summary>Resumes automatic control that a per-application limit had paused.</summary>
    /// <remarks>
    /// Called when the application whose own limit paused control is no longer running and no limit
    /// is preferred for what replaced it. The next window re-bases on whatever the device reports —
    /// the same recovery path an unapplied write uses — so control continues from the real limit
    /// rather than from a stale believed one. A no-op while AutoTDP is off: there is no control to
    /// resume, and the next enable starts a fresh generation anyway.
    /// </remarks>
    internal void ResumeAutomaticControl()
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            _controller.ResumeAutomaticControl();
            // Force the next tick through Start(current, …) so control re-bases on the limit the
            // device actually holds now, not the value it believed before the application's override.
            _controllerStarted = false;
        }

        Publish(AutoTdpState.Idle, null, null, null, "Automatic control resumed.");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Task worker;
        Task<bool> lastStop;
        CancellationTokenSource? generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _enabled = false;
            worker = _worker;
            lastStop = _lastStop;
            generation = _generation;
            _worker = Task.CompletedTask;
            _generation = null;
        }

        // A disable may already be restoring the previous value. Let that finish before stopping a
        // newer generation or disposing the shared write gate; otherwise its late write would race
        // a disposed semaphore and could leave AutoTDP's value latched during shutdown.
        _ = await lastStop.ConfigureAwait(false);

        // The tick loop ends first and the restore follows, while the write path still works:
        // exiting with WSGM's probe value latched would leave the user's handheld on a limit they
        // never chose, and a surviving tick could re-latch it after the restore.
        _ = await StopGenerationAsync(worker, generation).ConfigureAwait(false);
        await _shutdown.CancelAsync().ConfigureAwait(false);

        int? unrestored;
        lock (_gate)
        {
            unrestored = _restoreTo;
        }
        (_frametimes as IDisposable)?.Dispose();
        _write.Dispose();
        _shutdown.Dispose();
        if (unrestored is { } watts)
        {
            throw new InvalidOperationException(
                $"AutoTDP could not verify restoration of the previous {watts} W power limit.");
        }
    }

    /// <summary>Ends one enable generation and restores the limit it took over from.</summary>
    /// <param name="worker">The tick loop that generation started.</param>
    /// <param name="generation">Its cancellation source, or null when none was running.</param>
    private async Task<bool> StopGenerationAsync(Task worker, CancellationTokenSource? generation)
    {
        if (generation is not null)
        {
            await generation.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // The restore below still has to run: a worker that died is exactly when the hardware
            // is most likely to be sitting on AutoTDP's last value.
            Log.Error("The AutoTDP tick loop ended with a failure", ex);
        }

        generation?.Dispose();
        return await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(Window);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("AutoTDP stopped after an unexpected failure", ex);
            Publish(AutoTdpState.Unavailable, null, null, null, ex.Message);
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!Volatile.Read(ref _enabled))
        {
            return;
        }

        if (FindPowerCapability() is not { } power)
        {
            Publish(AutoTdpState.Unavailable, null, null, null, "No primary power limit is available.");
            return;
        }

        AutoTdpLimits limits = new(
            power.Descriptor.Minimum ?? 0,
            power.Descriptor.Maximum ?? 0,
            power.Descriptor.Step ?? 0);
        if (!limits.IsUsable || power.Projection.State.ObservedValue?.IntegerValue is not { } current)
        {
            Publish(AutoTdpState.Unavailable, null, null, null, "The power limit reports no usable range.");
            return;
        }

        RunningApplicationTargetSnapshot? running;
        lock (_gate)
        {
            running = _running;
        }

        if (SelectSample(running) is not { } frametime)
        {
            Publish(AutoTdpState.Idle, current, null, null, "No application is rendering.");
            return;
        }

        double target = _targetFrametimeMs();
        string context = ContextKey(running, frametime);
        AutoTdpDecision decision;
        bool rebased;
        lock (_gate)
        {
            rebased = _resync && _controllerStarted;
            if (!_controllerStarted || _resync)
            {
                // Either the first window of this generation, or the window after a write that
                // never reached hardware. Re-basing on the value just observed is the only honest
                // way back from the second: continuing would judge frames against a limit the
                // device never took. The captured restore value is deliberately not moved — the
                // user's own limit is still what a stop has to return to.
                _controllerStarted = true;
                _resync = false;
                _controller.Start(current, limits, context);
            }

            decision = _controller.Evaluate(
                new AutoTdpSample(frametime.MeanFrametimeMs, target, IsCapped(frametime, target), context),
                limits);
        }

        if (rebased)
        {
            Log.Info($"AutoTDP re-based on the observed limit of {current} W after an unapplied write.");
        }

        if (decision.RequiresWrite)
        {
            lock (_gate)
            {
                _restoreTo ??= current;
            }
            if (!await WriteAsync(power, decision, cancellationToken).ConfigureAwait(false))
            {
                Publish(
                    AutoTdpState.Unavailable,
                    current,
                    frametime.MeanFrametimeMs,
                    target,
                    running?.ApplicationId,
                    "The power limit did not accept the last write; control holds for one window.");
                return;
            }
        }

        Publish(
            _controller.IsPaused ? AutoTdpState.Paused : AutoTdpState.Controlling,
            decision.Watts,
            frametime.MeanFrametimeMs,
            target,
            running?.ApplicationId,
            decision.Reason);
    }

    /// <summary>Writes one power limit and reports whether it reached the hardware.</summary>
    /// <param name="power">The primary power-limit capability.</param>
    /// <param name="decision">The decision being applied.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when the device accepted the value.</returns>
    /// <remarks>
    /// The outcome is acted on, not merely logged. <see cref="AutoTdpController"/> has already moved
    /// its believed wattage by the time this runs, so a refused, timed-out or indeterminate write
    /// leaves every later decision resting on a limit the device may never have taken; the resync
    /// flag makes the next window re-base on what the hardware actually reports.
    /// </remarks>
    private async Task<bool> WriteAsync(
        DeviceCapabilityView power,
        AutoTdpDecision decision,
        CancellationToken cancellationToken)
    {
        // One power command at a time. An overlapping write would leave the controller unable to say
        // which value the hardware actually ended up with, and an uncertain hardware write is never
        // retried behind the user's back.
        if (!await _write.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            Log.Warn("AutoTDP skipped a power write: an earlier write is still in flight.");
            MarkWriteUnapplied();
            return false;
        }

        try
        {
            CapabilityCommandResult result = await _writeAsync(
                power.Descriptor.CapabilityId,
                power.Descriptor.InstanceId,
                new CapabilityValue
                {
                    Kind = CapabilityValueKind.Integer,
                    IntegerValue = decision.Watts,
                },
                cancellationToken).ConfigureAwait(false);
            bool applied = IsApplied(result.Outcome);
            lock (_gate)
            {
                if (result.Outcome == CommandOutcome.Rejected && !_powerMayDiffer)
                {
                    // Rejected is the one outcome that proves nothing reached hardware. If this
                    // was the generation's first write, there is consequently nothing to restore.
                    _restoreTo = null;
                }
                else if (result.Outcome != CommandOutcome.Rejected)
                {
                    _powerMayDiffer = true;
                }
            }
            Log.Info(
                $"AutoTDP {decision.Action}: {decision.Watts} W ({decision.Reason}), "
                + $"outcome={result.Outcome}, applied={applied}.");
            if (!applied)
            {
                MarkWriteUnapplied();
            }

            return applied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"AutoTDP power write failed: {ex.Message}");
            lock (_gate)
            {
                // The call may have failed after dispatch; preserve the restore obligation.
                _powerMayDiffer = true;
            }
            MarkWriteUnapplied();
            return false;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Whether an outcome means the value reached the device.</summary>
    /// <param name="outcome">The capability command outcome.</param>
    /// <returns><see langword="true"/> for a written value, verified or not.</returns>
    /// <remarks>
    /// <see cref="CommandOutcome.AppliedUnverified"/> counts: a device with no readback for its
    /// power limit is normal, and refusing to trust it would disable AutoTDP on that hardware.
    /// Everything else — queued, refused, timed out, interrupted — did not demonstrably arrive.
    /// </remarks>
    private static bool IsApplied(CommandOutcome outcome) =>
        outcome is CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified;

    private void MarkWriteUnapplied()
    {
        lock (_gate)
        {
            _resync = true;
        }
    }

    private async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        int? restoreTo;
        DeviceCapabilityView? power = FindPowerCapability();
        lock (_gate)
        {
            restoreTo = _restoreTo;
        }

        if (restoreTo is not { } watts || power is null)
        {
            if (restoreTo is not null && power is null)
            {
                // The capability went away before the limit could be handed back — during a device
                // fault, or a shutdown that retired the coordinator first. Nothing can be done about
                // it here, but a handheld left on AutoTDP's last value must not be silent.
                Log.Warn(
                    $"AutoTDP could not restore {restoreTo} W: the primary power limit is no "
                    + "longer available.");
            }

            Publish(AutoTdpState.Off, null, null, null, "AutoTDP is off.");
            return restoreTo is null;
        }

        AutoTdpDecision decision = _controller.Stop(watts);
        // Reported from the write's own outcome. Saying "restored" for a value that was refused,
        // timed out, or skipped is the one message that makes the handheld's real state
        // undiagnosable from a log.
        bool restored = await WriteAsync(power, decision, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (restored && _restoreTo == watts)
            {
                _restoreTo = null;
                _powerMayDiffer = false;
                _controllerStarted = false;
            }
            else if (!restored)
            {
                _resync = true;
            }
        }
        Publish(
            AutoTdpState.Off,
            watts,
            null,
            null,
            restored
                ? "AutoTDP is off; the previous limit was restored."
                : $"AutoTDP is off; restoring {watts} W was not confirmed.");
        return restored;
    }

    private DeviceCapabilityView? FindPowerCapability() => _capabilities()
        .FirstOrDefault(view =>
            view.Descriptor.Role is CapabilityRole.PowerSustainedLimit
            && view.Descriptor.SupportsWrite
            && view.Descriptor.ValueKind is CapabilityValueKind.Integer);

    private RtssFrametimeSample? SelectSample(RunningApplicationTargetSnapshot? running)
    {
        IReadOnlyList<RtssFrametimeSample> live = _frametimes.ReadLive();
        if (live.Count == 0)
        {
            return null;
        }

        // The running-application monitor knows which executable Steam launched; RTSS knows which
        // process is drawing. Matching them is what keeps AutoTDP from tuning power for a launcher
        // or a background renderer that happens to be in the table.
        if (running?.ExecutablePath is { Length: > 0 } executable)
        {
            string leaf = Path.GetFileName(executable);
            foreach (RtssFrametimeSample sample in live)
            {
                if (string.Equals(
                    Path.GetFileName(sample.ExecutablePath),
                    leaf,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return sample;
                }
            }
        }

        // With exactly one renderer there is nothing to confuse it with. With several and no
        // identity, AutoTDP declines rather than guessing which one the user is playing.
        return live.Count == 1 ? live[0] : null;
    }

    private static bool IsCapped(RtssFrametimeSample sample, double targetFrametimeMs) =>
        sample.MeanFrametimeMs >= targetFrametimeMs * 0.97
        && sample.MeanFrametimeMs <= targetFrametimeMs * AutoTdpController.MissRatio;

    private static string ContextKey(
        RunningApplicationTargetSnapshot? running,
        RtssFrametimeSample sample) =>
        running?.ApplicationId is { Length: > 0 } identity
            ? identity
            : $"process:{Path.GetFileName(sample.ExecutablePath)}";

    private void Publish(
        AutoTdpState state,
        int? watts,
        double? frametimeMs,
        double? targetFrametimeMs,
        string detail) =>
        Publish(state, watts, frametimeMs, targetFrametimeMs, Status.ApplicationId, detail);

    private void Publish(
        AutoTdpState state,
        int? watts,
        double? frametimeMs,
        double? targetFrametimeMs,
        string? applicationId,
        string detail)
    {
        AutoTdpStatus status = new(
            state,
            watts,
            frametimeMs,
            targetFrametimeMs,
            applicationId,
            detail);
        if (status == Status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(status);
    }

}
