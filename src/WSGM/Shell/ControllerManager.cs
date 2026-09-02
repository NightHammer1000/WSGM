using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Input;

namespace WSGM.Shell;

/// <summary>Truthful state of WSGM's controller management for one session.</summary>
internal enum ControllerManagementState
{
    /// <summary>The user has not enabled controller management, or the release gate is closed.</summary>
    Off,

    /// <summary>Enabled, but no usable backend exists on this machine.</summary>
    Unavailable,

    /// <summary>Enabled and ready, with no virtual target present.</summary>
    Idle,

    /// <summary>A virtual target exists and canonical samples reach it.</summary>
    Active,

    /// <summary>Management faulted for this run; input falls back to SDL and the Steam lease.</summary>
    Faulted,
}

/// <summary>Combined physical-release and WSGM make-safe result.</summary>
internal sealed record ControllerHandoff
{
    internal required ControllerHandoffStep Step { get; init; }
    internal required ControllerHandoffResult Result { get; init; }
    internal IReadOnlyList<PhysicalDeviceIdentity> ReleasedDevices { get; init; } = [];
}

/// <summary>The complete controller-management projection consumed by the overlay and diagnostics.</summary>
internal sealed record ControllerManagerStatus(
    ControllerManagementState State,
    ManagedControllerTarget? Target,
    ControllerTargetSource TargetSource,
    string? ApplicationId,
    UiInputSource UiSource,
    string Detail);

/// <summary>
/// The one owner of WSGM's controller management for a session.
/// </summary>
/// <remarks>
/// Everything WSGM does to the controller happens here: the virtual target and its replacement, the
/// haptic return path, WSGM's owned HidHide delta, the local UI capture, the source WSGM's own
/// surfaces navigate from, and the make-safe handoff. There is deliberately no second policy layer
/// between a setting and this object: the overlay, Settings, and the shared running-application
/// monitor all call it directly.
/// <para>
/// <see cref="DeviceCoordinator"/> owns the plugin lifecycle; this object owns WSGM's virtual
/// controller half and orders the two through
/// <see cref="ControllerMakeSafeSequence"/>.
/// </para>
/// </remarks>
internal sealed class ControllerManager : IAsyncDisposable
{
    private readonly IHidBackend _backend;
    private readonly HidHideOwnedDeltaManager _hidHide;
    private readonly ManagedControllerRouter _router;
    private readonly UiCaptureState _uiCapture = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly string _controllerReaderApplication;
    private readonly object _stateGate = new();
    private readonly object _sampleGate = new();

    /// <summary>Serializes routing a sample against the neutralizations that must precede it.</summary>
    /// <remarks>
    /// A lock cannot do this: the publication it protects is asynchronous, and a route decided
    /// under <see cref="_stateGate"/> but published outside it can land a stale live sample on top
    /// of the neutral packet a capture claim just wrote.
    /// </remarks>
    private readonly SemaphoreSlim _routeGate = new(1, 1);

    private IReadOnlyList<PhysicalDeviceIdentity> _physicalDevices = [];
    private ControllerSelection _selection = new(
        Enabled: false,
        ManagedControllerTarget.SteamDeckComposite,
        [],
        "Controller management has not started.");
    private ResolvedControllerTarget? _effective;
    private IReadOnlyList<ManagedControllerTarget> _supportedTargets = [];
    private CanonicalButtons _lastButtons;
    private CanonicalControllerSample? _lastSample;
    private CanonicalButtons _syntheticButtons;
    private long _sourceGeneration;
    private bool _forwardingBlocked;
    private CanonicalControllerSample? _pendingSample;
    private bool _sampleDrainRunning;
    private Task _sampleDrain = Task.CompletedTask;

    // Written under the transition gate but read from the sample path, which must not take it.
    private volatile bool _disposed;

    internal ControllerManager(
        IHidBackend backend,
        IPhysicalHapticSink hapticSink,
        HidHideOwnedDeltaManager hidHide,
        string controllerReaderApplication,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(hapticSink);
        ArgumentNullException.ThrowIfNull(hidHide);
        _backend = backend;
        _hidHide = hidHide;
        _controllerReaderApplication = controllerReaderApplication;
        _router = new ManagedControllerRouter(backend, hapticSink, timeProvider);
        _router.TargetFaulted += OnRouterTargetFaulted;
    }

    /// <summary>Reports the projection change a lost target must produce.</summary>
    /// <param name="detail">Why the router faulted.</param>
    /// <remarks>
    /// The manager must stop reporting Active once the backend stops accepting frames, or WSGM's
    /// surfaces stay on a managed source that has gone silent.
    /// </remarks>
    private void OnRouterTargetFaulted(string detail)
    {
        SetState(ControllerManagementState.Faulted, detail);
        Log.Observe(
            BlockForwardingAsync("source-faulted", CancellationToken.None),
            "Controller source-fault neutralization");
    }

    /// <summary>Raised when the projection changes, for the overlay and Settings.</summary>
    internal event Action<ControllerManagerStatus>? StatusChanged;

    /// <summary>Raised for each canonical sample WSGM's own surfaces should navigate from.</summary>
    internal event Action<CanonicalControllerSample>? UiSampleReceived;

    /// <summary>Every physical sample, unfiltered, for diagnostics only.</summary>
    /// <remarks>
    /// Raised before routing and never used to drive input. It exists so a surface can show what
    /// the plugin actually reports — which is not what <see cref="UiSampleReceived"/> carries, since
    /// that one has the controls the UI is using filtered out.
    /// </remarks>
    internal event Action<CanonicalControllerSample>? PhysicalSampleObserved;

    /// <summary>Current state of controller management.</summary>
    internal ControllerManagementState State { get; private set; } = ControllerManagementState.Off;

    /// <summary>Why the current state holds, for logs and the overlay.</summary>
    internal string Detail { get; private set; } = "Controller management has not started.";

    /// <summary>Where WSGM's own surfaces are reading controller input from.</summary>
    /// <remarks>
    /// The managed source is used only while a healthy target is actually being driven. Every other
    /// state falls back to SDL with the Steam Input lease, which is why that path stays a permanent
    /// capability rather than a transitional one.
    /// </remarks>
    internal UiInputSource UiSource => State is ControllerManagementState.Active
        ? UiInputSource.ManagedCanonical
        : UiInputSource.SdlWithSteamLease;

    /// <summary>The target in effect and the layer that chose it.</summary>
    internal ResolvedControllerTarget? Effective => _effective;

    /// <summary>Targets the backend on this machine can create, once it has been discovered.</summary>
    /// <remarks>
    /// Empty until controller management starts, which is also the only time a surface offers the
    /// choice. Advertising a target the backend cannot build is worse than offering fewer: the
    /// selection persists, the target creation fails, and management reports itself unavailable.
    /// </remarks>
    internal IReadOnlyList<ManagedControllerTarget> SupportedTargets => _supportedTargets;

    /// <summary>Returns the current projection.</summary>
    /// <returns>The controller-management projection.</returns>
    internal ControllerManagerStatus Snapshot() => new(
        State,
        _effective?.Target,
        _effective?.Source ?? ControllerTargetSource.GlobalDefault,
        _effective?.ApplicationId,
        UiSource,
        Detail);

    /// <summary>
    /// Starts controller management for the current plugin cycle.
    /// </summary>
    /// <param name="selection">The controller selection in effect.</param>
    /// <param name="physicalDevices">Physical devices the plugin owns and WSGM must hide.</param>
    /// <param name="applicationId">Canonical identity of the running application, when known.</param>
    /// <param name="sourceGeneration">Cycle generation the canonical samples carry.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The resulting projection.</returns>
    /// <remarks>
    /// Fails open in every unavailable case. A missing backend, unhealthy HidHide, or a target that
    /// does not enumerate leaves the shell, the SDL path, and the Steam Input lease exactly as they
    /// were; it never changes global HidHide state and never removes an external owner's entries.
    /// </remarks>
    internal async Task<ControllerManagerStatus> StartAsync(
        ControllerSelection selection,
        IReadOnlyList<PhysicalDeviceIdentity> physicalDevices,
        string? applicationId,
        long sourceGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _physicalDevices = physicalDevices;
            _selection = selection;
            Interlocked.Exchange(ref _sourceGeneration, sourceGeneration);
            lock (_sampleGate)
            {
                _pendingSample = null;
            }

            if (!selection.Enabled)
            {
                return SetState(ControllerManagementState.Off, selection.DisabledDetail);
            }

            HidBackendHealth health = await _backend.DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);
            if (health.State is not HidBackendHealthState.Ready || health.Capabilities is null)
            {
                _supportedTargets = [];
                return SetState(ControllerManagementState.Unavailable, health.Detail);
            }

            // What the backend on this machine can actually create: the surfaces offer these and
            // nothing else, because an advertised target the backend has no encoder for reads as a
            // broken feature rather than an unimplemented one.
            _supportedTargets = [.. health.Capabilities.SupportedTargets];

            ResolvedControllerTarget resolved = ControllerTargetSelection.Resolve(
                selection.GlobalDefault,
                selection.Overrides,
                applicationId);
            if (!health.Capabilities.SupportedTargets.Contains(resolved.Target))
            {
                return SetState(
                    ControllerManagementState.Unavailable,
                    $"The backend cannot create a {resolved.Target} target.");
            }

            HidHideActivationResult hidHide = await _hidHide.StartAsync(
                _controllerReaderApplication,
                physicalDevices,
                cancellationToken).ConfigureAwait(false);
            if (!hidHide.Activated)
            {
                return SetState(ControllerManagementState.Unavailable, hidHide.Detail);
            }

            try
            {
                await ApplyTargetUnderGateAsync(
                    resolved,
                    replace: _router.Target is not null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("Controller management could not create its virtual target", ex);
                await CleanupHidHideUnderGateAsync(cancellationToken).ConfigureAwait(false);
                return SetState(ControllerManagementState.Faulted, ex.Message);
            }

            return SetState(
                ControllerManagementState.Active,
                $"Managed target {resolved.Target} is active ({resolved.Source}).");
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>
    /// Applies a changed selection, replacing the target when the effective target changed.
    /// </summary>
    /// <param name="selection">The new controller selection.</param>
    /// <param name="applicationId">Canonical identity of the running application, when known.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>The resulting projection.</returns>
    /// <remarks>
    /// Turning management off here is not the same as a make-safe handoff and deliberately does not
    /// perform one: the caller that owns the plugin conversation runs
    /// <see cref="MakeSafeAsync"/> so the physical release is ordered against WSGM's own removal.
    /// </remarks>
    internal async Task<ControllerManagerStatus> ApplySelectionAsync(
        ControllerSelection selection,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _selection = selection;
            return await ReconcileTargetUnderGateAsync(applicationId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>
    /// Applies a running-application change from the one shared monitor.
    /// </summary>
    /// <param name="snapshot">The canonical running-application snapshot.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>The resulting projection.</returns>
    /// <remarks>
    /// The same monitor resolves the RTSS profile, so the controller target and the performance
    /// profile can never disagree about which application is running.
    /// </remarks>
    internal async Task<ControllerManagerStatus> ApplyRunningApplicationAsync(
        RunningApplicationTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await ReconcileTargetUnderGateAsync(snapshot.ApplicationId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>Makes WSGM readable to HidHide before the plugin tries to find the controller.</summary>
    /// <param name="controllerManagementEnabled">Whether management may run at all.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task completing once the check has run.</returns>
    /// <remarks>
    /// Called before the plugin's cycle starts, which is the only point that helps: once discovery
    /// has run against a device it could not see, allowlisting WSGM afterwards changes nothing for
    /// that cycle. Never fatal — the result is logged and the cycle continues, because a machine
    /// with no HidHide at all is the normal one.
    /// </remarks>
    internal async Task EnsureHidHideReadableAsync(
        bool controllerManagementEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            string detail = await _hidHide.EnsureReadableAsync(
                controllerManagementEnabled,
                _controllerReaderApplication,
                cancellationToken).ConfigureAwait(false);
            Log.Info($"HidHide readability: {detail}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"HidHide readability check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Forwards one canonical sample published by the plugin.
    /// </summary>
    /// <param name="sample">The sample the plugin published.</param>
    /// <remarks>
    /// A captured sample never reaches the virtual target. It reaches WSGM's own surfaces with the
    /// controls held at capture filtered out, so the chord that opened the overlay cannot activate
    /// whatever now has focus underneath it.
    /// </remarks>
    internal void Submit(CanonicalControllerSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        TaskCompletionSource? completion = null;
        lock (_sampleGate)
        {
            if (_disposed)
            {
                Log.Change(
                    "controller-sample-after-dispose",
                    "Controller sample ignored because controller management is disposed.");
                return;
            }

            long generation = Interlocked.Read(ref _sourceGeneration);
            if (sample.CycleGeneration != generation)
            {
                Log.Change(
                    "controller-stale-sample",
                    $"Controller sample ignored: sampleGeneration={sample.CycleGeneration}, activeGeneration={generation}.");
                return;
            }

            _pendingSample = sample;
            if (!_sampleDrainRunning)
            {
                _sampleDrainRunning = true;
                completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _sampleDrain = completion.Task;
            }
        }

        if (completion is not null)
        {
            _ = DrainSamplesAsync(completion);
        }
    }

    private async Task DrainSamplesAsync(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                CanonicalControllerSample? sample;
                lock (_sampleGate)
                {
                    sample = _pendingSample;
                    _pendingSample = null;
                    if (sample is null)
                    {
                        _sampleDrainRunning = false;
                        return;
                    }
                }

                try
                {
                    await RouteAsync(sample, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Change(
                        "controller-sample-route-fault",
                        $"Controller sample route recovered after {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    /// <summary>Routes one canonical sample and reports whether it reached the virtual target.</summary>
    /// <param name="sample">The sample the plugin published.</param>
    /// <param name="cancellationToken">Cancels the route.</param>
    /// <returns><see langword="true"/> when the sample reached the virtual target.</returns>
    internal async Task<bool> RouteAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        // Stale generations are refused at admission (Submit) and re-checked by the router's sample
        // validator, which covers the generation changes ActivateSource can make mid-flight.

        // Raised before any routing decision and deliberately unfiltered, because this is what the
        // plugin reported. The filtered stream that follows is what the UI may act on; a diagnostic
        // that showed only that would hide the controls the UI had swallowed, which are exactly the
        // ones someone checking a mapping needs to see. Read-only: an observer cannot change what
        // is routed, so it is not a second input path.
        PhysicalSampleObserved?.Invoke(sample);

        // Held across the decision and the publication it authorizes. Every neutralization takes
        // the same gate, so a live sample can no longer be written after the neutral packet that
        // was meant to replace it.
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool toUi;
            CanonicalButtons uiButtons;
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return false;
                }

                _lastButtons = sample.Buttons;
                _lastSample = sample;
                // Forwarding resumes only on a clean boundary: every control the UI used has to be
                // released first, or the game sees a press whose start it never saw.
                toUi = _uiCapture.IsCaptured
                    || _forwardingBlocked
                    || !_uiCapture.CanResumeForwarding(sample.Buttons);
                uiButtons = toUi ? _uiCapture.FilterForUi(sample.Buttons) : sample.Buttons;
            }

            if (toUi)
            {
                UiSampleReceived?.Invoke(sample with { Buttons = uiButtons });
                return false;
            }

            // Capture and lifecycle blocks leave the target neutral rather than removed, so the
            // first clean sample after they clear re-arms forwarding. This sample is the only point
            // at which the release boundary is proven safe.
            if (_router.State is ManagedTargetState.Neutral && _router.Target is not null)
            {
                _router.ActivateSource(_sourceGeneration);
            }

            CanonicalControllerSample routed;
            lock (_stateGate)
            {
                routed = _syntheticButtons is CanonicalButtons.None
                    ? sample
                    : sample with { Buttons = sample.Buttons | _syntheticButtons };
            }
            return await _router.RouteAsync(routed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _routeGate.Release();
        }
    }

    /// <summary>Claims controller input for one WSGM surface.</summary>
    /// <param name="surfaceId">Identifier of the claiming surface.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <returns>A task completing once the target has been left neutral.</returns>
    internal Task ClaimUiAsync(string surfaceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        bool started;
        lock (_stateGate)
        {
            started = _uiCapture.Claim(surfaceId, _lastButtons);
        }

        return started
            ? NeutralizeForUiCaptureAsync(cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>Releases one surface's claim on controller input.</summary>
    /// <param name="surfaceId">Identifier of the releasing surface.</param>
    /// <remarks>
    /// Releasing the last claim does not resume forwarding by itself. Forwarding resumes on the
    /// first sample in which every control the UI used is up, so the press that closed the surface
    /// never arrives in the game as a fresh input.
    /// </remarks>
    internal void ReleaseUi(string surfaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        lock (_stateGate)
        {
            _uiCapture.Release(surfaceId);
        }
    }

    /// <summary>Stops game forwarding until a target is successfully created or replaced.</summary>
    /// <param name="reason">Diagnostic reason recorded with the neutral report.</param>
    /// <param name="cancellationToken">Cancels the neutralization.</param>
    /// <returns>A task completing once the target has been left neutral.</returns>
    internal Task BlockForwardingAsync(string reason, CancellationToken cancellationToken) =>
        NeutralizeRoutingAsync(reason, blockForwarding: true, cancellationToken);

    private Task NeutralizeForUiCaptureAsync(CancellationToken cancellationToken) =>
        NeutralizeRoutingAsync("ui-capture", blockForwarding: false, cancellationToken);

    private async Task NeutralizeRoutingAsync(
        string reason,
        bool blockForwarding,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        // Under the route gate so forwarding closes and the target is neutralized without a sample
        // decided a moment earlier landing between the two.
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool neutralize;
            lock (_stateGate)
            {
                bool newlyBlocked = blockForwarding && !_forwardingBlocked;
                _forwardingBlocked |= blockForwarding;
                neutralize = State is ControllerManagementState.Active
                    && (newlyBlocked || !blockForwarding);
            }

            if (neutralize)
            {
                await _router.NeutralizeAsync(reason, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _routeGate.Release();
        }
    }

    /// <summary>Sends one bounded rear-button pulse through the active virtual target.</summary>
    /// <param name="button">One-based rear-button number.</param>
    /// <param name="cancellationToken">Cancels the press interval.</param>
    /// <returns>Whether an active target and source sample accepted the pulse.</returns>
    internal async Task<bool> PulseRearButtonAsync(
        int button,
        CancellationToken cancellationToken)
    {
        CanonicalButtons pressed = button switch
        {
            1 => CanonicalButtons.RearPaddle1,
            2 => CanonicalButtons.RearPaddle2,
            _ => CanonicalButtons.None,
        };
        if (pressed is CanonicalButtons.None)
        {
            Log.Warn($"Virtual rear-button pulse refused: unsupported button={button}.");
            return false;
        }

        if (!await SetSyntheticButtonAsync(pressed, enabled: true, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            // A cancelled OEM action must still publish the release; otherwise the virtual target
            // retains a rear paddle until the next physical sample happens to arrive.
            await SetSyntheticButtonAsync(pressed, enabled: false, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> SetSyntheticButtonAsync(
        CanonicalButtons button,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CanonicalControllerSample? sample;
            lock (_stateGate)
            {
                if (!enabled)
                {
                    _syntheticButtons &= ~button;
                }

                if (_disposed || State is not ControllerManagementState.Active)
                {
                    Log.Warn(
                        $"Virtual rear-button {(enabled ? "press" : "release")} refused: "
                        + $"controllerState={State}.");
                    return false;
                }

                sample = _lastSample;
                if (sample is null)
                {
                    Log.Warn(
                        $"Virtual rear-button {(enabled ? "press" : "release")} refused: "
                        + "no canonical controller sample has arrived.");
                    return false;
                }

                if (enabled)
                {
                    _syntheticButtons |= button;
                }
                sample = sample with { Buttons = sample.Buttons | _syntheticButtons };
            }

            return await _router.RouteAsync(sample, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _routeGate.Release();
        }
    }

    /// <summary>
    /// Runs the complete make-safe handoff and returns its combined result.
    /// </summary>
    /// <param name="scope">Whether only the controller or the whole cycle is being released.</param>
    /// <param name="releasePhysicalAsync">Asks the plugin to stop reading and restore its mode.</param>
    /// <param name="cancellationToken">Cancels the handoff.</param>
    /// <returns>The handoff response describing both halves of the sequence.</returns>
    /// <remarks>
    /// The returned response is WSGM's, not the plugin's: it reports how far the whole sequence got,
    /// including the WSGM-owned removal that runs after an unverified or failed plugin answer. The
    /// user's stop request is always honoured; the result records whether it could be verified.
    /// </remarks>
    internal async Task<ControllerHandoff> MakeSafeAsync(
        HandoffScope scope,
        Func<CancellationToken, Task<ControllerHandoff>> releasePhysicalAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(releasePhysicalAsync);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await MakeSafeUnderGateAsync(scope, releasePhysicalAsync, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }
        finally
        {
            _transition.Release();
        }

        _router.TargetFaulted -= OnRouterTargetFaulted;
        Task sampleDrain;
        lock (_sampleGate)
        {
            _pendingSample = null;
            sampleDrain = _sampleDrain;
        }
        await sampleDrain.ConfigureAwait(false);
        // Order matters here exactly as it does in the make-safe sequence: the router removes the
        // virtual target first, and only then are WSGM's HidHide entries dropped.
        await _router.DisposeAsync().ConfigureAwait(false);
        await CleanupHidHideUnderGateAsync(CancellationToken.None).ConfigureAwait(false);
        _transition.Dispose();
        _routeGate.Dispose();
    }

    private async Task<ControllerHandoff> MakeSafeUnderGateAsync(
        HandoffScope scope,
        Func<CancellationToken, Task<ControllerHandoff>> releasePhysicalAsync,
        CancellationToken cancellationToken)
    {
        ControllerMakeSafeSequence sequence = new();
        IReadOnlyList<PhysicalDeviceIdentity> released = [];

        lock (_sampleGate)
        {
            _pendingSample = null;
        }

        // Admission closes before the target is quietened, not after: a sample arriving once the
        // router reaches Neutral would re-activate the source and publish a non-neutral report
        // behind the handoff's back.
        bool neutralized = false;
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                _forwardingBlocked = true;
            }

            await _router.NeutralizeAsync("make-safe", cancellationToken).ConfigureAwait(false);
            neutralized = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Controller make-safe could not verify a neutral target: {ex.Message}");
        }
        finally
        {
            _routeGate.Release();
        }

        sequence.RecordNeutralized(neutralized);

        try
        {
            ControllerHandoff plugin = await releasePhysicalAsync(cancellationToken)
                .ConfigureAwait(false);
            released = plugin.ReleasedDevices;
            sequence.RecordPluginRelease(plugin.Step, plugin.Result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            sequence.RecordPluginReleaseUnobserved();
            Log.Warn($"Controller make-safe: the plugin release was unverified: {ex.Message}");
        }

        bool targetRemoved = false;
        try
        {
            await _router.RemoveAsync("make-safe", cancellationToken).ConfigureAwait(false);
            targetRemoved = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Controller make-safe could not verify target removal: {ex.Message}");
        }

        // The sequence continues even when removal was unverified — leaving WSGM's HidHide entries
        // behind would hide the physical controller with nothing driving it — but it must not
        // *claim* the removal: a virtual controller still enumerated beside the newly exposed
        // physical one is duplicate input, and ReleasedVerified would make that undiagnosable.
        sequence.RecordTargetRemoved(targetRemoved);
        sequence.RecordHidHideRemoved(
            await CleanupHidHideUnderGateAsync(cancellationToken).ConfigureAwait(false));

        ControllerHandoffResult result = sequence.Complete();
        SetState(
            scope is HandoffScope.FullDeactivation
                ? ControllerManagementState.Off
                : ControllerManagementState.Idle,
            $"Controller make-safe completed: {sequence.Step}, {result}.");
        Log.Info(
            $"Controller make-safe: scope={scope}, step={sequence.Step}, result={result}, "
            + $"targetRemoved={sequence.TargetRemoved}, hidHideRemoved={sequence.HidHideRemoved}.");
        return new ControllerHandoff
        {
            Step = sequence.Step,
            Result = result,
            ReleasedDevices = released,
        };
    }

    private async Task<bool> CleanupHidHideUnderGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            HidHideCleanupResult cleanup = await _hidHide.CleanupAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!cleanup.Verified)
            {
                Log.Warn($"Controller HidHide cleanup was unverified: {cleanup.Detail}");
            }

            return cleanup.Verified;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Controller HidHide cleanup failed", ex);
            return false;
        }
    }

    private async Task<ControllerManagerStatus> ReconcileTargetUnderGateAsync(
        string? applicationId,
        CancellationToken cancellationToken)
    {
        ResolvedControllerTarget resolved = ControllerTargetSelection.Resolve(
            _selection.GlobalDefault,
            _selection.Overrides,
            applicationId);
        // A disabled selection is not reconciled here. Removing the target without ordering it
        // against the plugin's physical release is the duplicate-input window make-safe exists to
        // prevent, so the caller that owns the plugin conversation runs that sequence instead.
        if (State is not ControllerManagementState.Active || !_selection.Enabled)
        {
            return Snapshot();
        }

        if (_effective is { } current && current.Target == resolved.Target)
        {
            _effective = resolved;
            return Snapshot();
        }

        try
        {
            // Replacement is one operation on purpose: the old target is neutralized and removed
            // before the new one is created, so no window exists in which both are enumerated.
            await ApplyTargetUnderGateAsync(resolved, replace: true, cancellationToken)
                .ConfigureAwait(false);
            return SetState(
                ControllerManagementState.Active,
                $"Managed target {resolved.Target} is active ({resolved.Source}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Managed controller target replacement failed", ex);
            await CleanupHidHideUnderGateAsync(cancellationToken).ConfigureAwait(false);
            return SetState(ControllerManagementState.Faulted, ex.Message);
        }
    }

    private async Task ApplyTargetUnderGateAsync(
        ResolvedControllerTarget resolved,
        bool replace,
        CancellationToken cancellationToken)
    {
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (replace)
            {
                lock (_stateGate)
                {
                    _forwardingBlocked = true;
                }
            }

            HidTargetHandle target = replace
                ? await _router.ReplaceAsync(resolved.Target, _sourceGeneration, cancellationToken)
                    .ConfigureAwait(false)
                : await _router.CreateAsync(resolved.Target, _sourceGeneration, cancellationToken)
                    .ConfigureAwait(false);
            _effective = resolved;
            bool captured;
            lock (_stateGate)
            {
                _forwardingBlocked = false;
                captured = _uiCapture.IsCaptured;
            }

            if (captured)
            {
                await _router.NeutralizeAsync("ui-capture", cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _router.ActivateSource(_sourceGeneration);
            }

            Log.Info(replace
                ? $"Managed controller target replaced: {resolved.Target} ({resolved.Source}), "
                    + $"generation={target.Generation}."
                : $"Managed controller target created: {resolved.Target} ({resolved.Source}), "
                    + $"generation={target.Generation}, devices={_physicalDevices.Count}.");
        }
        finally
        {
            _routeGate.Release();
        }
    }

    private ControllerManagerStatus SetState(ControllerManagementState state, string detail)
    {
        State = state;
        Detail = detail;
        if (state is not (ControllerManagementState.Active or ControllerManagementState.Idle))
        {
            _effective = null;
        }

        ControllerManagerStatus status = Snapshot();
        StatusChanged?.Invoke(status);
        return status;
    }
}

/// <summary>Reference-counted controller capture for WSGM's visible surfaces.</summary>
internal sealed class UiCaptureState
{
    private readonly HashSet<string> _surfaces = new(StringComparer.Ordinal);
    private CanonicalButtons _suppressedForUi;
    private CanonicalButtons _withheldFromGame;

    /// <summary>Whether any WSGM surface currently holds capture.</summary>
    internal bool IsCaptured => _surfaces.Count > 0;

    /// <summary>Claims capture and remembers controls held before the first surface opened.</summary>
    /// <returns><see langword="true"/> when this claim started capture.</returns>
    internal bool Claim(string surfaceId, CanonicalButtons heldAtOpen)
    {
        bool wasCaptured = IsCaptured;
        if (!_surfaces.Add(surfaceId))
        {
            Log.Change(
                $"ui-capture.{surfaceId}",
                $"Managed UI capture claim ignored: surface={surfaceId}, reason=already-claimed.");
            return false;
        }

        if (!wasCaptured)
        {
            _suppressedForUi = heldAtOpen;
            _withheldFromGame = heldAtOpen;
        }

        return !wasCaptured;
    }

    /// <summary>Releases a claim and reports whether the last known surface closed.</summary>
    internal bool Release(string surfaceId)
    {
        if (!_surfaces.Remove(surfaceId))
        {
            Log.Change(
                $"ui-capture.{surfaceId}",
                $"Managed UI capture release ignored: surface={surfaceId}, reason=not-claimed.");
            return false;
        }

        return !IsCaptured;
    }

    /// <summary>Removes buttons still held from before capture began.</summary>
    internal CanonicalButtons FilterForUi(CanonicalButtons buttons)
    {
        _suppressedForUi &= buttons;
        if (IsCaptured)
        {
            // Keep the most recent physical state. When the last surface closes, every control the
            // UI was still using must be observed up before the same state can reach the game.
            _withheldFromGame = buttons;
        }

        return buttons & ~_suppressedForUi;
    }

    /// <summary>Reports whether forwarding can resume without inventing a press edge.</summary>
    internal bool CanResumeForwarding(CanonicalButtons buttons)
    {
        if (IsCaptured)
        {
            return false;
        }

        _withheldFromGame &= buttons;
        return _withheldFromGame == CanonicalButtons.None;
    }
}
