using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
/// Projects the one canonical Steam running-application identity into every per-application
/// consumer: the shared RTSS service and the managed controller target. Rapid transitions coalesce
/// to the latest identity and never retain an executable after Steam reports exit, ambiguity, or
/// loss of observation.
/// </summary>
/// <remarks>
/// One monitor and one projection for both consumers on purpose. A second observer would poll the
/// live Steam client again over CEF and could resolve a different application than the one the RTSS
/// profile was chosen for, so the controller target and the performance profile could disagree about
/// what is running.
/// </remarks>
internal sealed class RunningApplicationCoordinator : IAsyncDisposable
{
    private readonly RunningApplicationMonitor _monitor;
    private readonly Func<PerformanceApplicationTarget?, CancellationToken, Task> _setTargetAsync;
    private readonly Func<RunningApplicationTargetSnapshot, CancellationToken, Task>?
        _setControllerTargetAsync;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private IDisposable? _observation;
    private RunningApplicationTargetSnapshot? _pending;
    private Task _worker = Task.CompletedTask;
    private bool _workerRunning;
    private bool _disposed;

    internal RunningApplicationCoordinator(
        RunningApplicationMonitor monitor,
        Func<PerformanceApplicationTarget?, CancellationToken, Task> setTargetAsync,
        Func<RunningApplicationTargetSnapshot, CancellationToken, Task>? setControllerTargetAsync = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _setTargetAsync = setTargetAsync ?? throw new ArgumentNullException(nameof(setTargetAsync));
        _setControllerTargetAsync = setControllerTargetAsync;
        _monitor.Changed += OnTargetChanged;
        try
        {
            _observation = _monitor.AcquireObservation();
            Queue(_monitor.Current);
        }
        catch
        {
            _monitor.Changed -= OnTargetChanged;
            _observation?.Dispose();
            _shutdown.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = null;
            worker = _worker;
        }

        _monitor.Changed -= OnTargetChanged;
        _observation?.Dispose();
        _observation = null;
        _shutdown.Cancel();
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await _setTargetAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"RTSS running-application target cleanup failed: {ex.Message}");
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task ApplyAsync(
        Func<CancellationToken, Task> applyAsync,
        string consumer,
        RunningApplicationTargetSnapshot snapshot)
    {
        try
        {
            await applyAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"{consumer} running-application target apply failed for generation "
                + $"{snapshot.Generation}: {ex.Message}");
        }
    }

    internal static PerformanceApplicationTarget? Project(
        RunningApplicationTargetSnapshot snapshot)
        => snapshot.State is RunningApplicationTargetState.Active
                or RunningApplicationTargetState.IdentityOnly
            && snapshot.ApplicationId is { Length: > 0 } applicationId
                ? new PerformanceApplicationTarget(
                    applicationId,
                    snapshot.SteamAppId,
                    snapshot.RtssProfileName)
                : null;

    private void OnTargetChanged(RunningApplicationTargetSnapshot snapshot) => Queue(snapshot);

    private void Queue(RunningApplicationTargetSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = snapshot;
            if (!_workerRunning)
            {
                _workerRunning = true;
                _worker = Task.Run(ApplyPendingAsync);
            }
        }
    }

    /// <summary>Whether a newer snapshot is already waiting.</summary>
    private bool Superseded()
    {
        lock (_gate)
        {
            return _pending is not null || _disposed;
        }
    }

    private async Task ApplyPendingAsync()
    {
        while (true)
        {
            RunningApplicationTargetSnapshot? snapshot;
            lock (_gate)
            {
                snapshot = _pending;
                _pending = null;
                if (snapshot is null || _disposed)
                {
                    _workerRunning = false;
                    return;
                }
            }

            // Each consumer is applied independently: an RTSS failure must not leave the
            // controller on the previous application's target, and the reverse.
            await ApplyAsync(
                token => _setTargetAsync(Project(snapshot), token),
                "RTSS",
                snapshot).ConfigureAwait(false);
            // Rechecked between consumers. A slow RTSS apply for one application could otherwise be
            // followed by replacing the managed controller with that application's target after it
            // had already exited and the next one was published — a target swap during a launch,
            // and the opposite of the latest-identity coalescing this class exists to provide.
            if (Superseded())
            {
                Log.Info(
                    $"Running-application apply for {snapshot.ApplicationId ?? "(none)"} stopped "
                    + "before the controller target: a newer snapshot is already queued.");
                continue;
            }

            if (_setControllerTargetAsync is { } applyController)
            {
                await ApplyAsync(
                    token => applyController(snapshot, token),
                    "Controller",
                    snapshot).ConfigureAwait(false);
            }
        }
    }
}
