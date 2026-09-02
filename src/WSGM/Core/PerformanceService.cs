using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Pure global/per-application RTSS policy and edit-target resolution.</summary>
internal static class PerformancePolicyResolver
{
    internal static (
        PerformanceValues Values,
        PerformancePolicyLayer FrameLimitLayer,
        PerformancePolicyLayer OverlayLevelLayer) Resolve(
        PerformancePolicy policy,
        PerformanceApplicationTarget? target)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled)
        {
            return (
                PerformanceValues.Empty,
                PerformancePolicyLayer.None,
                PerformancePolicyLayer.None);
        }

        PerformanceApplicationPolicy? application = Find(policy, target?.ApplicationId);
        PerformanceValues persistent = application is null
            ? policy.Global
            : new PerformanceValues(
                application.Values.FrameLimit ?? policy.Global.FrameLimit,
                application.Values.OverlayLevel ?? policy.Global.OverlayLevel);
        return (
            persistent,
            LayerFor(application?.Values.FrameLimit, policy.Global.FrameLimit),
            LayerFor(application?.Values.OverlayLevel, policy.Global.OverlayLevel));
    }

    internal static PerformancePersistenceTarget ResolveEditTarget(
        PerformancePolicy policy,
        PerformanceApplicationTarget? target) => Find(policy, target?.ApplicationId) is null
            ? PerformancePersistenceTarget.Global
            : PerformancePersistenceTarget.Application;

    internal static PerformancePolicy Write(
        PerformancePolicy policy,
        PerformanceApplicationTarget? target,
        PerformancePersistenceTarget persistence,
        PerformanceControl control,
        int value)
    {
        if (persistence == PerformancePersistenceTarget.Global)
        {
            return policy with { Global = policy.Global.With(control, value) };
        }

        if (target is null)
        {
            throw new InvalidOperationException("An application edit requires an active application target.");
        }

        List<PerformanceApplicationPolicy> applications = [.. policy.Applications];
        int index = applications.FindIndex(item => string.Equals(
            item.ApplicationId,
            target.ApplicationId,
            StringComparison.Ordinal));
        PerformanceApplicationPolicy current = applications[index];
        applications[index] = current with
        {
            RtssProfileName = target.RtssProfileName ?? current.RtssProfileName,
            Values = current.Values.With(control, value),
        };
        return policy with { Applications = applications.ToArray() };
    }

    internal static PerformanceApplicationPolicy? Find(
        PerformancePolicy policy,
        string? applicationId) => string.IsNullOrWhiteSpace(applicationId)
            ? null
            : policy.Applications.FirstOrDefault(item => string.Equals(
                item.ApplicationId,
                applicationId,
                StringComparison.Ordinal));

    private static PerformancePolicyLayer LayerFor(int? application, int? global) =>
        application is not null
            ? PerformancePolicyLayer.Application
            : global is not null ? PerformancePolicyLayer.Global : PerformancePolicyLayer.None;
}

/// <summary>
/// One session-owned RTSS service shared by every UI projection. Adapter access and commands are
/// serialized, polling runs only while a client holds an observation lease, and RTSS failures never
/// escape into shell/session transitions.
/// </summary>
internal sealed class PerformanceService : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly RtssProbe InitialProbe = new(
        RtssAvailability.Unknown,
        null,
        null,
        0,
        null,
        "RTSS discovery has not run.");

    private readonly IRtssAdapter _adapter;
    private readonly Func<PerformancePolicy, CancellationToken, Task> _persistPolicy;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _adapterGate = new(1, 1);
    private readonly SemaphoreSlim _observerSignal = new(0, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly RtssLauncher _launcher;
    private readonly Task _pollTask;
    private readonly Dictionary<long, string> _commandProfiles = [];
    private PerformancePolicy _policy;
    private PerformanceState _state;
    private int _observerCount;
    private long _commandSequence;
    private bool _disposed;

    internal PerformanceService(
        IRtssAdapter adapter,
        Func<PerformancePolicy, CancellationToken, Task> persistPolicy,
        PerformancePolicy? policy = null,
        TimeSpan? pollInterval = null,
        TimeSpan? commandTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _launcher = new RtssLauncher();
        _persistPolicy = persistPolicy ?? throw new ArgumentNullException(nameof(persistPolicy));
        _policy = NormalizePolicy(policy ?? PerformancePolicy.Empty);
        _pollInterval = BoundInterval(pollInterval ?? DefaultPollInterval);
        _commandTimeout = BoundTimeout(commandTimeout ?? DefaultCommandTimeout);
        _timeProvider = timeProvider ?? TimeProvider.System;
        (
            PerformanceValues desired,
            PerformancePolicyLayer frameLimitLayer,
            PerformancePolicyLayer overlayLevelLayer) = PerformancePolicyResolver.Resolve(
            _policy,
            null);
        _state = new PerformanceState(
            InitialProbe,
            null,
            false,
            frameLimitLayer,
            overlayLevelLayer,
            desired,
            PerformanceValues.Empty,
            PerformanceReadbackQuality.Unavailable,
            PerformanceReadbackQuality.Unavailable,
            null,
            PerformanceCommandState.Idle);
        _pollTask = Task.Run(PollAsync);
    }

    internal event Action<PerformanceState>? StateChanged;

    internal PerformanceState Current
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    internal int ObserverCount => Volatile.Read(ref _observerCount);

    internal bool Enabled
    {
        get
        {
            lock (_stateGate)
            {
                return _policy.Enabled;
            }
        }
    }

    internal TimeSpan PollInterval => _pollInterval;

    /// <summary>Hands the Custom overlay's configuration (selector level 4) to the adapter's
    /// renderer.</summary>
    /// <param name="settings">The widget order and per-widget detail.</param>
    /// <remarks>Deliberately outside the adapter gate: it changes what the renderer draws on its
    /// next tick, not RTSS state, and must stay applicable while a command is in flight.</remarks>
    internal void ApplyOsdCustomization(RtssOsdCustomSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _adapter.ApplyOsdCustomization(settings);
    }

    internal IDisposable AcquireObservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Increment(ref _observerCount) == 1)
        {
            TrySignalObserver();
        }

        return new ObservationLease(this);
    }

    /// <summary>Resets the profile currently in force to its defaults.</summary>
    /// <param name="cancellationToken">Cancels the apply that follows.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// Resets whichever layer is actually in force, which is the only reading that matches what the
    /// user sees: with a per-application profile active they are looking at that profile, and
    /// clearing the global one underneath it would appear to do nothing.
    /// <para>
    /// The application's entry is kept and its values emptied, rather than the entry being removed.
    /// Removing it is what the per-game toggle means; reset must not silently turn that toggle off
    /// as a side effect.
    /// </para>
    /// </remarks>
    internal async Task<bool> ResetProfileAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PerformancePolicy policy;
        PerformanceApplicationTarget? target;
        lock (_stateGate)
        {
            policy = _policy;
            target = _state.Target;
        }

        PerformanceApplicationPolicy? application = PerformancePolicyResolver.Find(
            policy,
            target?.ApplicationId);

        if (application is not null)
        {
            if (application.Values == PerformanceValues.Empty)
            {
                return false;
            }

            List<PerformanceApplicationPolicy> applications = [.. policy.Applications];
            applications[applications.IndexOf(application)] =
                application with { Values = PerformanceValues.Empty };
            Log.Info($"Performance profile reset for {application.ApplicationId}.");
            await UpdatePolicyAsync(
                policy with { Applications = applications },
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (policy.Global == PerformanceValues.Empty)
        {
            return false;
        }

        Log.Info("Global performance profile reset.");
        await UpdatePolicyAsync(
            policy with { Global = PerformanceValues.Empty },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Gives the running application its own performance profile, or takes it away.
    /// </summary>
    /// <param name="enabled">Whether the application should keep its own values.</param>
    /// <param name="cancellationToken">Cancels the apply that follows.</param>
    /// <returns>Whether the policy changed.</returns>
    /// <remarks>
    /// Turning it on seeds the application's values from what is <em>currently in force</em> rather
    /// than from nothing. A per-game profile that started empty would drop the user to the global
    /// defaults the instant they created it, which reads as the toggle having reset their settings.
    /// <para>
    /// Turning it off removes the entry rather than blanking it, so the application falls back to
    /// the global layer through the ordinary resolution path instead of carrying an empty override
    /// that has to be special-cased everywhere it is read.
    /// </para>
    /// <para>
    /// Refused when nothing identifiable is running: there is no application to attach a profile to,
    /// and silently writing the global layer instead is the wrong reading of a per-game toggle.
    /// </para>
    /// </remarks>
    internal async Task<bool> SetApplicationProfileEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PerformancePolicy policy;
        PerformanceApplicationTarget? target;
        PerformanceValues desired;
        lock (_stateGate)
        {
            policy = _policy;
            target = _state.Target;
            desired = _state.Desired;
        }

        if (target is null)
        {
            Log.Warn(
                "Per-application performance profile refused: no identifiable application is "
                + "running.");
            return false;
        }

        PerformanceApplicationPolicy? existing = PerformancePolicyResolver.Find(
            policy,
            target.ApplicationId);
        if (existing is not null == enabled)
        {
            return false;
        }

        List<PerformanceApplicationPolicy> applications = [.. policy.Applications];
        if (enabled)
        {
            applications.Add(new PerformanceApplicationPolicy(
                target.ApplicationId,
                target.RtssProfileName ?? string.Empty,
                desired));
            Log.Info(
                $"Per-application performance profile created for {target.ApplicationId}, seeded "
                + $"from the values in force.");
        }
        else
        {
            applications.Remove(existing!);
            Log.Info(
                $"Per-application performance profile removed for {target.ApplicationId}; the "
                + "global profile applies.");
        }

        await UpdatePolicyAsync(
            policy with { Applications = applications },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal async Task UpdatePolicyAsync(
        PerformancePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policy);
        PerformancePolicy normalized = NormalizePolicy(policy);
        PerformanceState next;
        lock (_stateGate)
        {
            if (PoliciesEqual(_policy, normalized))
            {
                return;
            }

            _policy = normalized;
            next = WithResolvedDesired(_state);
            _state = next;
        }

        RaiseStateChanged(next);
        await ApplyEffectiveDesiredAsync("policy-reload", cancellationToken).ConfigureAwait(false);
    }

    internal async Task SetTargetAsync(
        PerformanceApplicationTarget? target,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (target is not null && !ValidTarget(target))
        {
            throw new ArgumentException("The RTSS application target is invalid.", nameof(target));
        }

        if (target is not null)
        {
            target = target with
            {
                ApplicationId = target.ApplicationId.Trim(),
                RtssProfileName = target.RtssProfileName?.Trim(),
            };
        }

        PerformanceState next;
        lock (_stateGate)
        {
            if (_state.Target == target)
            {
                return;
            }

            _state = WithResolvedDesired(_state with { Target = target });
            next = _state;
        }

        RaiseStateChanged(next);
        await ApplyEffectiveDesiredAsync("application-transition", cancellationToken).ConfigureAwait(false);
    }

    internal Task<PerformanceCommandState> SetAsync(
        PerformanceControl control,
        int value,
        PerformancePersistenceTarget persistence,
        string origin,
        string correlationId,
        CancellationToken cancellationToken = default) => SetCoreAsync(
            control,
            value,
            origin,
            correlationId,
            cancellationToken,
            updateDesired: true);

    private async Task<PerformanceCommandState> SetCoreAsync(
        PerformanceControl control,
        int value,
        string origin,
        string correlationId,
        CancellationToken cancellationToken,
        bool updateDesired)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        origin = SanitizeToken(origin, "unknown");
        correlationId = SanitizeToken(correlationId, Guid.NewGuid().ToString("N"));
        long sequence = Interlocked.Increment(ref _commandSequence);
        PerformanceCommandState Command(PerformanceCommandPhase phase, string? diagnostic = null) =>
            new(sequence, origin, correlationId, control, value, phase, diagnostic);

        UpdateCommand(Command(PerformanceCommandPhase.Queued));

        bool enabled;
        lock (_stateGate)
        {
            enabled = _policy.Enabled;
        }
        if (!enabled)
        {
            return UpdateCommand(Command(
                PerformanceCommandPhase.Rejected,
                "RTSS integration is disabled."));
        }

        using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCts.Token);
        try
        {
            await _adapterGate.WaitAsync(admission.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return UpdateCommand(Command(
                PerformanceCommandPhase.Rejected,
                _disposeCts.IsCancellationRequested
                    ? "RTSS is stopping."
                    : "Command was cancelled before it reached RTSS."));
        }

        try
        {
            // Rechecked after the wait, not only before it. A Settings or config update can switch
            // RTSS integration off while this command is queued, and that path takes no adapter
            // gate of its own — with a disabled policy there are no desired values to apply — so
            // without this the queued command still wrote its value into a switched-off feature.
            if (_disposed)
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Rejected,
                    "RTSS is stopping."));
            }

            lock (_stateGate)
            {
                enabled = _policy.Enabled;
            }

            if (!enabled)
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Rejected,
                    "RTSS integration was switched off while the command was queued."));
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCts.Token);
            timeout.CancelAfter(_commandTimeout);
            return await ApplyOneAsync(
                sequence,
                control,
                value,
                origin,
                correlationId,
                timeout.Token,
                cancellationToken,
                updateDesired).ConfigureAwait(false);
        }
        finally
        {
            _adapterGate.Release();
        }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCts.Token);
        RtssProbe? launchProbe;
        await _adapterGate.WaitAsync(admission.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            launchProbe = await RefreshInsideGateAsync(admission.Token).ConfigureAwait(false);
        }
        finally
        {
            _adapterGate.Release();
        }

        // Starting RTSS can wait up to ten seconds for its tray process. Keep that settle outside
        // the adapter gate so UI commands can still observe and report the unavailable state.
        if (launchProbe is not null)
        {
            await _launcher.TryStartAsync(launchProbe, Enabled, admission.Token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        TrySignalObserver();
        try
        {
            await _pollTask.WaitAsync(_commandTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal service shutdown.
        }
        catch (TimeoutException)
        {
            Log.Warn("RTSS poll did not stop within its disposal budget; process exit will reclaim it.");
            return;
        }

        if (!await _adapterGate.WaitAsync(_commandTimeout).ConfigureAwait(false))
        {
            Log.Warn("RTSS adapter remained busy beyond its disposal budget; process exit will reclaim it.");
            return;
        }

        try
        {
            await _adapter.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _adapterGate.Release();
        }
    }

    private async Task<PerformanceCommandState> ApplyOneAsync(
        long sequence,
        PerformanceControl control,
        int value,
        string origin,
        string correlationId,
        CancellationToken boundedCancellation,
        CancellationToken callerCancellation,
        bool updateDesired)
    {
        PerformanceCommandState Command(PerformanceCommandPhase phase, string? diagnostic = null) =>
            new(sequence, origin, correlationId, control, value, phase, diagnostic);

        UpdateCommand(Command(PerformanceCommandPhase.Applying));

        try
        {
            RtssProbe probe = await _adapter.ProbeAsync(boundedCancellation).ConfigureAwait(false);
            UpdateProbe(probe);
            if (probe.Availability != RtssAvailability.Ready || probe.Capabilities is null)
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Rejected,
                    probe.Diagnostic ?? "RTSS is unavailable."));
            }

            if (!probe.Capabilities.Supports(control)
                || !probe.Capabilities.IsValid(control, value))
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Rejected,
                    "The requested value is outside the adapter's verified bounds."));
            }

            PerformanceApplicationTarget? target;
            bool applicationOptedIn;
            PerformancePolicy? previousPolicy = null;
            PerformancePolicy? changedPolicy = null;
            lock (_stateGate)
            {
                target = _state.Target;
                if (updateDesired)
                {
                    previousPolicy = _policy;
                    _policy = PerformancePolicyResolver.Write(
                        _policy,
                        target,
                        PerformancePolicyResolver.ResolveEditTarget(_policy, target),
                        control,
                        value);
                    changedPolicy = _policy;
                    _state = WithResolvedDesired(_state);
                }

                applicationOptedIn = PerformancePolicyResolver.Find(
                    _policy,
                    target?.ApplicationId) is not null;
            }

            // Saving an RTSS profile that does not exist creates it, which sprayed a profile onto
            // every executable that ever took focus (device-observed 2026-09-02). A running
            // application's own profile is therefore written only when the user opted the
            // application in, or when RTSS already carries that profile — whose explicit values
            // would otherwise stay the stronger RTSS layer and silently override the global write.
            // Everything else goes to the global profile, which covers the application anyway.
            string profile = EffectiveRtssProfile(target, applicationOptedIn);
            lock (_stateGate)
            {
                _commandProfiles[sequence] = target is null
                    ? string.Empty
                    : target.RtssProfileName is null
                        ? $"pending application {target.ApplicationId}"
                        : profile;
            }

            if (changedPolicy is not null)
            {
                try
                {
                    await _persistPolicy(changedPolicy, boundedCancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    RestorePolicyAfterPersistenceFailure(changedPolicy, previousPolicy!);
                    throw;
                }
                catch (Exception ex)
                {
                    RestorePolicyAfterPersistenceFailure(changedPolicy, previousPolicy!);
                    Log.Error("Persisting RTSS performance policy failed", ex);
                    return UpdateCommand(Command(
                        PerformanceCommandPhase.Failed,
                        "The performance preference could not be persisted."));
                }
            }

            RaiseStateChanged(Current);

            if (target is { RtssProfileName: null or "" })
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Deferred,
                    "The application preference was saved and will apply when its foreground "
                        + "executable is known."));
            }

            RtssApplyResult applied = await _adapter.ApplyAsync(
                new RtssApplyRequest(profile, control, value, probe.Generation),
                boundedCancellation).ConfigureAwait(false);
            if (!applied.Applied)
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Rejected,
                    applied.Diagnostic ?? "RTSS rejected the profile update."));
            }

            RtssProbe after = await _adapter.ProbeAsync(boundedCancellation).ConfigureAwait(false);
            if (after.Generation != probe.Generation || after.Availability != RtssAvailability.Ready)
            {
                UpdateProbe(after);
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Indeterminate,
                    "RTSS restarted while the command was being applied."));
            }

            if (!probe.Capabilities.HasVerifiedReadback(control))
            {
                MarkAppliedUnverified(control, value);
                return UpdateCommand(Command(
                    PerformanceCommandPhase.AppliedUnverified,
                    "RTSS accepted the update but exposes no proven readback for this property."));
            }

            RtssReadback readback = await _adapter.ReadAsync(
                profile,
                probe.Generation,
                boundedCancellation).ConfigureAwait(false);
            UpdateReadback(after, readback, detectExternalChange: false);
            if (readback.Values.ValueFor(control) != value)
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Failed,
                    "RTSS readback did not match the requested value; another profile writer may have won."));
            }

            return UpdateCommand(Command(PerformanceCommandPhase.SucceededVerified));
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            return UpdateCommand(Command(
                PerformanceCommandPhase.TimedOut,
                "RTSS did not finish within the bounded command timeout."));
        }
        catch (OperationCanceledException)
        {
            return UpdateCommand(Command(
                PerformanceCommandPhase.Indeterminate,
                "The caller cancelled after RTSS command processing began."));
        }
        catch (Exception ex)
        {
            Log.Error("RTSS performance command failed", ex);
            MarkDegraded(ex.Message);
            return UpdateCommand(Command(PerformanceCommandPhase.Failed, ex.Message));
        }
    }

    private async Task ApplyEffectiveDesiredAsync(string origin, CancellationToken cancellationToken)
    {
        PerformanceState snapshot = Current;
        if (snapshot.Desired.FrameLimit is int frameLimit)
        {
            await SetCoreAsync(
                PerformanceControl.FrameLimit,
                frameLimit,
                origin,
                $"{origin}-frame-limit",
                cancellationToken,
                updateDesired: false).ConfigureAwait(false);
        }

        snapshot = Current;
        if (snapshot.Desired.OverlayLevel is int overlayLevel)
        {
            await SetCoreAsync(
                PerformanceControl.OverlayLevel,
                overlayLevel,
                origin,
                $"{origin}-overlay-level",
                cancellationToken,
                updateDesired: false).ConfigureAwait(false);
        }
    }

    private async Task PollAsync()
    {
        CancellationToken cancellationToken = _disposeCts.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref _observerCount) == 0)
            {
                await _observerSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("RTSS refresh failed", ex);
                MarkDegraded(ex.Message);
            }

            await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RtssProbe?> RefreshInsideGateAsync(CancellationToken cancellationToken)
    {
        RtssProbe probe = await _adapter.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (probe.Availability != RtssAvailability.Ready || probe.Capabilities is null)
        {
            PerformanceState unavailable;
            RtssProbe previousProbe;
            lock (_stateGate)
            {
                previousProbe = _state.Probe;
                _state = WithResolvedDesired(_state with
                {
                    Probe = probe,
                    Observed = PerformanceValues.Empty,
                    FrameLimitQuality = PerformanceReadbackQuality.Unavailable,
                    OverlayLevelQuality = PerformanceReadbackQuality.Unavailable,
                    RefreshedAt = _timeProvider.GetUtcNow(),
                });
                unavailable = _state;
            }

            LogProbeChange(previousProbe, probe);
            RaiseStateChanged(unavailable);

            return probe;
        }

        PerformanceApplicationTarget? target = Current.Target;
        if (target is { RtssProfileName: null or "" })
        {
            PerformanceState pending;
            RtssProbe previousProbe;
            lock (_stateGate)
            {
                previousProbe = _state.Probe;
                _state = WithResolvedDesired(_state with
                {
                    Probe = probe,
                    Observed = PerformanceValues.Empty,
                    FrameLimitQuality = PerformanceReadbackQuality.Unavailable,
                    OverlayLevelQuality = PerformanceReadbackQuality.Unavailable,
                    RefreshedAt = _timeProvider.GetUtcNow(),
                });
                pending = _state;
            }

            LogProbeChange(previousProbe, probe);
            RaiseStateChanged(pending);
            return null;
        }

        bool applicationOptedIn;
        lock (_stateGate)
        {
            applicationOptedIn = PerformancePolicyResolver.Find(
                _policy,
                target?.ApplicationId) is not null;
        }

        // The same profile-selection rule as the apply path, so readback observes the profile the
        // writes actually target instead of reporting a phantom external change against a
        // never-written application profile.
        RtssReadback readback = await _adapter.ReadAsync(
            EffectiveRtssProfile(target, applicationOptedIn),
            probe.Generation,
            cancellationToken).ConfigureAwait(false);
        UpdateReadback(probe, readback, detectExternalChange: true);
        return null;
    }

    /// <summary>The RTSS profile a command or readback for this target actually addresses.</summary>
    /// <param name="target">The running-application target, or null for global.</param>
    /// <param name="applicationOptedIn">Whether WSGM policy holds a per-application entry.</param>
    private string EffectiveRtssProfile(
        PerformanceApplicationTarget? target,
        bool applicationOptedIn)
    {
        string name = target?.RtssProfileName ?? string.Empty;
        if (name.Length == 0)
        {
            return string.Empty;
        }

        return applicationOptedIn || _adapter.ProfileExists(name) ? name : string.Empty;
    }

    private void UpdateReadback(RtssProbe probe, RtssReadback readback, bool detectExternalChange)
    {
        PerformanceState next;
        lock (_stateGate)
        {
            bool changed = detectExternalChange
                && _state.RefreshedAt is not null
                && _state.Observed != readback.Values
                && _state.Command.Phase is not PerformanceCommandPhase.Applying
                    and not PerformanceCommandPhase.Queued;
            PerformanceCommandState command = changed
                ? new PerformanceCommandState(
                    Interlocked.Increment(ref _commandSequence),
                    "external",
                    "rtss-external-change",
                    ChangedControl(_state.Observed, readback.Values),
                    null,
                    PerformanceCommandPhase.ExternalChange,
                    "RTSS state changed outside WSGM.")
                : _state.Command;
            _state = WithResolvedDesired(_state with
            {
                Probe = probe,
                Observed = readback.Values,
                FrameLimitQuality = readback.FrameLimitQuality,
                OverlayLevelQuality = readback.OverlayLevelQuality,
                RefreshedAt = readback.Timestamp,
                Command = command,
            });
            next = _state;
        }

        RaiseStateChanged(next);
    }

    private void MarkAppliedUnverified(PerformanceControl control, int value)
    {
        PerformanceState next;
        lock (_stateGate)
        {
            _state = _state with
            {
                Observed = _state.Observed.With(control, value),
                FrameLimitQuality = control == PerformanceControl.FrameLimit
                    ? PerformanceReadbackQuality.AppliedUnverified
                    : _state.FrameLimitQuality,
                OverlayLevelQuality = control == PerformanceControl.OverlayLevel
                    ? PerformanceReadbackQuality.AppliedUnverified
                    : _state.OverlayLevelQuality,
                RefreshedAt = _timeProvider.GetUtcNow(),
            };
            next = _state;
        }

        RaiseStateChanged(next);
    }

    private void RestorePolicyAfterPersistenceFailure(
        PerformancePolicy failedPolicy,
        PerformancePolicy previousPolicy)
    {
        PerformanceState next;
        lock (_stateGate)
        {
            if (ReferenceEquals(_policy, failedPolicy))
            {
                _policy = previousPolicy;
                _state = WithResolvedDesired(_state);
            }

            next = _state;
        }

        RaiseStateChanged(next);
    }

    private void UpdateProbe(RtssProbe probe)
    {
        PerformanceState next;
        RtssProbe previous;
        lock (_stateGate)
        {
            previous = _state.Probe;
            _state = _state with { Probe = probe };
            next = _state;
        }

        LogProbeChange(previous, probe);
        RaiseStateChanged(next);
    }

    /// <summary>Logs an RTSS probe result when it changes.</summary>
    /// <param name="previous">The probe this replaces.</param>
    /// <param name="probe">The new probe.</param>
    /// <remarks>
    /// The probe runs on every poll, so only transitions are logged. Each transition includes the
    /// availability and diagnostic needed for remote RTSS diagnosis.
    /// </remarks>
    private static void LogProbeChange(RtssProbe previous, RtssProbe probe)
    {
        if (previous.Availability == probe.Availability
            && string.Equals(previous.Diagnostic, probe.Diagnostic, StringComparison.Ordinal))
        {
            return;
        }

        string version = string.IsNullOrWhiteSpace(probe.Version) ? "unknown" : probe.Version;
        string detail = string.IsNullOrWhiteSpace(probe.Diagnostic)
            ? string.Empty
            : $" - {probe.Diagnostic}";
        string line = $"RTSS: {probe.Availability}, version {version}{detail}";
        if (probe.Availability is RtssAvailability.Ready)
        {
            Log.Info(line);
        }
        else
        {
            Log.Warn(line);
        }
    }

    private void MarkDegraded(string diagnostic)
    {
        PerformanceState next;
        lock (_stateGate)
        {
            _state = _state with
            {
                Probe = _state.Probe with
                {
                    Availability = RtssAvailability.Degraded,
                    Diagnostic = diagnostic,
                },
            };
            next = _state;
        }

        RaiseStateChanged(next);
    }

    private PerformanceCommandState UpdateCommand(PerformanceCommandState command)
    {
        PerformanceState next;
        string? appliedProfile;
        lock (_stateGate)
        {
            command = UpdateCommandLocked(command);
            next = _state;
            _commandProfiles.TryGetValue(command.Sequence, out appliedProfile);
            if (command.Phase is not (PerformanceCommandPhase.Queued
                or PerformanceCommandPhase.Applying))
            {
                _commandProfiles.Remove(command.Sequence);
            }
        }

        LogCommandOutcome(command, next, appliedProfile);
        RaiseStateChanged(next);
        return command;
    }

    /// <summary>Records what one RTSS write actually did.</summary>
    /// <param name="command">The command that reached a terminal phase.</param>
    /// <param name="state">The state it left behind, for the profile it was written to.</param>
    /// <param name="appliedProfile">RTSS profile the command targeted.</param>
    /// <remarks>
    /// Every terminal outcome is recorded through <see cref="Log.Change"/> keyed per control. The
    /// profile is included because global and per-application writes target different RTSS files.
    /// </remarks>
    private static void LogCommandOutcome(
        PerformanceCommandState command,
        PerformanceState state,
        string? appliedProfile)
    {
        if (command.Phase
            is PerformanceCommandPhase.Idle
            or PerformanceCommandPhase.Queued
            or PerformanceCommandPhase.Applying)
        {
            return;
        }

        string profile = appliedProfile is not null
            ? string.IsNullOrWhiteSpace(appliedProfile) ? "the global profile" : appliedProfile
            : string.IsNullOrWhiteSpace(state.Target?.RtssProfileName)
                ? "the global profile"
                : state.Target!.RtssProfileName;
        string detail = string.IsNullOrWhiteSpace(command.Diagnostic)
            ? string.Empty
            : $" — {command.Diagnostic}";
        bool succeeded = command.Phase
            is PerformanceCommandPhase.Deferred
            or PerformanceCommandPhase.SucceededVerified
            or PerformanceCommandPhase.AppliedUnverified;
        Log.Change(
            $"rtss.command.{command.Control}",
            $"RTSS {command.Control}={command.RequestedValue?.ToString() ?? "none"} on {profile}: "
                + $"{command.Phase}{detail}",
            succeeded ? "info " : "warn ");
    }

    private PerformanceCommandState UpdateCommandLocked(PerformanceCommandState command)
    {
        if (command.Sequence >= _state.Command.Sequence)
        {
            _state = _state with { Command = command };
        }

        return command;
    }

    private PerformanceState WithResolvedDesired(PerformanceState state)
    {
        (
            PerformanceValues values,
            PerformancePolicyLayer frameLimitLayer,
            PerformancePolicyLayer overlayLevelLayer) = PerformancePolicyResolver.Resolve(
            _policy,
            state.Target);
        return state with
        {
            Desired = values,
            ApplicationProfileEnabled = PerformancePolicyResolver.Find(
                _policy,
                state.Target?.ApplicationId) is not null,
            FrameLimitLayer = frameLimitLayer,
            OverlayLevelLayer = overlayLevelLayer,
        };
    }

    private void ReleaseObservation()
    {
        int remaining = Interlocked.Decrement(ref _observerCount);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _observerCount, 0);
        }
    }

    private void TrySignalObserver()
    {
        try
        {
            if (_observerSignal.CurrentCount == 0)
            {
                _observerSignal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // A racing observation release during disposal has no work left to wake.
        }
    }

    private void RaiseStateChanged(PerformanceState state)
    {
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            Log.Error("RTSS state observer failed", ex);
        }
    }

    private static PerformanceControl ChangedControl(PerformanceValues old, PerformanceValues current)
        => old.FrameLimit != current.FrameLimit
            ? PerformanceControl.FrameLimit
            : PerformanceControl.OverlayLevel;

    private static PerformancePolicy NormalizePolicy(PerformancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy.Global);
        List<PerformanceApplicationPolicy> applications = [];
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (PerformanceApplicationPolicy application in policy.Applications ?? [])
        {
            if (application is null || application.Values is null)
            {
                Log.Warn("RTSS policy entry dropped: the application or its values were null.");
                continue;
            }

            string applicationId = application.ApplicationId?.Trim() ?? string.Empty;
            if (applicationId.Length == 0)
            {
                Log.Warn("RTSS policy entry dropped: the application identity was empty.");
                continue;
            }
            if (!identities.Add(applicationId))
            {
                Log.Warn($"RTSS policy entry dropped: duplicate application identity '{SanitizeToken(applicationId, "unknown")}'.");
                continue;
            }

            applications.Add(application with
            {
                ApplicationId = applicationId,
                RtssProfileName = ValidProfileName(application.RtssProfileName)
                    ? application.RtssProfileName.Trim()
                    : string.Empty,
            });
        }

        return new PerformancePolicy(policy.Global, applications.ToArray(), policy.Enabled);
    }

    private static bool PoliciesEqual(PerformancePolicy left, PerformancePolicy right)
    {
        if (left.Enabled != right.Enabled
            || left.Global != right.Global
            || left.Applications.Count != right.Applications.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Applications.Count; index++)
        {
            if (left.Applications[index] != right.Applications[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidTarget(PerformanceApplicationTarget target) =>
        !string.IsNullOrWhiteSpace(target.ApplicationId)
        && target.ApplicationId.Length <= 1024
        && (target.RtssProfileName is null || ValidProfileName(target.RtssProfileName))
        && target.ProcessId is null or > 0;

    private static bool ValidProfileName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && string.Equals(System.IO.Path.GetFileName(value), value, StringComparison.Ordinal)
        && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeToken(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string sanitized = new(value.Where(character => !char.IsControl(character)).Take(80).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static TimeSpan BoundInterval(TimeSpan interval) => interval < TimeSpan.FromMilliseconds(250)
        ? TimeSpan.FromMilliseconds(250)
        : interval > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : interval;

    private static TimeSpan BoundTimeout(TimeSpan timeout) => timeout < TimeSpan.FromMilliseconds(100)
        ? TimeSpan.FromMilliseconds(100)
        : timeout > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : timeout;

    private sealed class ObservationLease : IDisposable
    {
        private PerformanceService? _owner;

        internal ObservationLease(PerformanceService owner)
        {
            _owner = owner;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseObservation();
    }
}
