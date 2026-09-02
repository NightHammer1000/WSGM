using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Interop;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>Shell-mode orchestrator: starts startup apps and the home app, arms the
/// overlay (hotkey + edge swipes + home-exit), stays resident for the session.</summary>
public sealed class ShellSession : IAsyncDisposable
{
    // Replaced wholesale on every reload (see Reload) so this stays the same
    // instance the overlay, SessionModes and DisplayScale's saved-scale snapshot
    // live on — the volume OSD's UI-scale callback reads it long after boot.
    private AppConfig _config;
    private readonly bool _overlayTestOnly;
    private readonly bool _serviceBoot;
    private bool _tookOverFromExplorer;
    private SteamMonitor? _monitor;
    private SessionModes? _modes;
    private ExplorerDesktopHost? _desktopHost;
    private StartupAppWatcher? _startupWatcher;
    private OverlayController? _overlay;
    private TrayHost? _trayHost;
    private VolumeButtonService? _volumeButtons;
    private CardAcfWatcher? _cardAcfWatcher;
    private CardVolumeMonitor? _cardVolumes;
    private KeepAwakeService? _keepAwake;
    private BootSplash? _splash;
    // Non-null from the moment the service-boot splash becomes interactive until
    // the worker releases SessionModes' transition gate. The splash's desktop
    // recovery cancels through this owner instead of racing that gate.
    private BootTakeoverCancellation? _bootTakeover;
    private Task? _bootWork;
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private volatile bool _shutdownRequested;
    // Replaced (not just cancelled) on every game-mode entry: a single cancelled
    // source would permanently kill boot syncing after the first desktop trip.
    private CancellationTokenSource _tabBootSyncCancellation = new();
    // True for the direct game-mode boot; the desktop-resume paths clear it, and
    // DesktopModeStarting/GameModeEntered keep it current afterwards.
    private volatile bool _inGameMode = true;
    // Last applied master CEF state, so a reload can tell an on->off transition
    // (which must retract first) from a repeat of the same value. Volatile: the
    // retraction task reads it to decide whether closing the choke point is still
    // wanted, while the UI thread writes it.
    private volatile bool _cefMasterEnabled;
    // True from just before a transition asks Steam for Big Picture until that transition
    // settles (PrepareSteamUiForBigPictureAsync / ReleaseSteamUiBigPictureHold). The request
    // rebuilds Steam's front-end, so the transport hold must begin before it fires.
    private volatile bool _gameModeCefTransitionPending;
    // One gate for the whole master-switch workflow: a retraction is three CEF
    // round-trips long, and overlapping applies must not interleave their
    // retract-then-close ordering.
    private readonly System.Threading.SemaphoreSlim _cefMasterGate = new(1, 1);
    // The transport's enabled flag is the one choke point every automatic CEF touch
    // passes: the patch host, the running-application probe and the static
    // evaluators. Its open/closed state is decided only by the readiness loop
    // (see SteamUiReadiness.TransportShouldBeOpen) and always under _cefMasterGate,
    // so a mode change or Steam lifecycle edge merely signals a re-check instead of
    // flipping the transport underneath a retract-then-close in flight.
    private readonly System.Threading.SemaphoreSlim _transportGateSignal = new(0);
    private Task? _transportGateWork;
    // Live Wi-Fi-indicator gate: the applied state, so a reload can tell an
    // on->off transition from a repeat of the same value.
    private bool _wifiIndicatorEnabled;
    // Same for the injected download-queue sort buttons. The session host owns
    // their target generation and retries through the common patch registry.
    private bool _downloadSortEnabled;
    // Field-rooted for the session lifetime: it owns a native power-setting
    // registration and the "did WSGM mute this?" flag.
    private DisplayOffMuteService? _displayMute;
    // Field-rooted deliberately: an unreferenced enabled FileSystemWatcher is
    // GC-collectible (it holds only a WeakReference to itself in its pending
    // ReadDirectoryChangesW state) and silently stops raising events.
    private System.IO.FileSystemWatcher? _configWatcher;
    private System.Threading.Timer? _configDebounce;
    private readonly object _configDebounceGate = new();
    private long _configReloadGeneration;
    private Task? _startupTask;
    private DeviceCoordinator? _deviceCoordinator;
    private IDeviceOverlaySource? _deviceOverlay;
    private PerformanceService? _performance;
    private RefreshRatePairingService? _refreshPairing;
    private DisplayResolutionService? _resolutions;

    // Whether WSGM's per-application feature is the reason the device currently holds a power limit,
    // so an application transition knows whether it has a limit of its own to take back. Written and
    // read only from the running-application transition path and the manual funnels, all of which the
    // running-application coordinator serialises. See PerApplicationPowerPolicy / PerApplicationVrrPolicy.
    private bool _profilePowerImposed;
    private bool _profileVrrImposed;

    // The application identity the power limit was last reconciled for. The running-application
    // snapshot bumps on foreground-executable enrichment as well as on a real application change, so
    // this reconciles the power limit only when the identity itself changes — a limit does not move
    // because focus flicked to a launcher and back, and re-writing the EC on every poll would thrash
    // it. The sentinel differs from every real id and from the null-application empty string, so the
    // first transition always reconciles. Touched only on the serialised transition path.
    private string _lastReconciledApplicationId = "(uninitialised)";

    /// <summary>
    /// The one audio manager for this session, shared by the taskbar's status cluster and Steam's
    /// audio namespace.
    /// </summary>
    /// <remarks>
    /// Session-scoped because the taskbar is not: it comes and goes, and Steam's audio store has to
    /// answer for the whole session. A second manager would enumerate endpoints twice and could
    /// disagree with the taskbar about which device is default.
    /// </remarks>
    private AudioManager? _audio;

    /// <summary>
    /// The one radio manager for this session, shared by the taskbar's status cluster and Steam's
    /// network surface.
    /// </summary>
    /// <remarks>
    /// Session-scoped for the same reason as the audio manager, and idle by default: scanning costs
    /// power and only makes sense while a network list is on screen.
    /// </remarks>
    private RadioManager? _radios;
    private int _pairedFrameLimit = -1;
    private PerformanceOverlayBridge? _performanceOverlay;
    private PersistentSteamUiTransport? _steamUiTransport;
    private RunningApplicationMonitor? _runningApplications;
    private ForegroundWindowWatcher? _foregroundWindows;
    private AutoTdpService? _autoTdp;
    private RunningApplicationCoordinator? _runningApplicationTargets;
    private SteamUiSessionHost? _steamUi;
    private MessageWindow? _messageWindow;
    private readonly object _devicePowerGate = new();
    private Task _devicePowerWork = Task.CompletedTask;
    private bool _deviceSuspended;
    private bool? _pendingDeviceSuspended;
    private long _devicePowerRequestGeneration;
    private bool _disposed;

    /// <summary>Creates the shell session without performing any Windows state changes.</summary>
    /// <param name="config">The configuration to apply when the session starts.</param>
    /// <param name="overlayTestOnly">Whether to omit normal shell startup for the manual overlay test.</param>
    /// <param name="serviceBoot">Whether the logon service launched this process over a
    /// live, still-initializing explorer (--boot) — enables the takeover flow.</param>
    public ShellSession(
        AppConfig config,
        bool overlayTestOnly = false,
        bool serviceBoot = false)
    {
        _config = config;
        _cefMasterEnabled = config.Cef.Enabled;
        _wifiIndicatorEnabled = config.Cef.Enabled && config.Cef.WifiIndicator;
        _downloadSortEnabled = config.Cef.Enabled && config.Cef.DownloadQueueSort;
        // The real shell opens the transport only through the readiness gate, once it is
        // running and knows whether Steam is cold-starting under it. Overlay-test never
        // attaches a transport and keeps the plain master flag so its static callers
        // report the configured state.
        SteamUiTransportSession.SetEnabled(overlayTestOnly && config.Cef.Enabled);
        SteamInputShim.SetEnabled(config.SteamInputManagementEnabled);
        _overlayTestOnly = overlayTestOnly;
        _serviceBoot = serviceBoot;
    }

    /// <summary>Opens or closes the Steam UI transport from the master switch, the shell mode and
    /// the Big Picture window. Callers that can race the master switch hold <c>_cefMasterGate</c>.</summary>
    /// <remarks>Only game mode asks Windows anything: a desktop session opens on the master switch
    /// alone, so the poll costs nothing there.</remarks>
    private void ApplySteamUiTransportGate()
    {
        bool master = _cefMasterEnabled;
        bool inGameMode = _inGameMode;
        bool transitionPending = _gameModeCefTransitionPending;
        bool bigPictureReady = master && (inGameMode || transitionPending) && SteamUiReadiness.IsReady;
        bool open = SteamUiReadiness.TransportShouldBeOpen(
            master, inGameMode, transitionPending, bigPictureReady);
        SteamUiTransportSession.SetEnabled(open);
        string state;
        if (open)
        {
            state = inGameMode || transitionPending
                ? "Steam UI transport open: Big Picture window is up."
                : "Steam UI transport open: desktop mode.";
        }
        else if (master)
        {
            state = inGameMode
                ? "Steam UI transport closed: game mode without a Big Picture window — "
                    + "holding every automatic CEF touch until Steam's UI exists."
                : "Steam UI transport closed: Big Picture was requested — "
                    + "holding every automatic CEF touch until Steam's UI exists.";
        }
        else
        {
            state = "Steam UI transport closed: Steam CEF integration is off.";
        }
        Log.Change("steam-ui-transport-gate", state);
    }

    /// <summary>Asks the gate loop to re-read the shell state now rather than at its next tick.</summary>
    private void RequestSteamUiTransportGateCheck()
    {
        if (_transportGateWork is null || _shutdownRequested)
        {
            return;
        }
        _transportGateSignal.Release();
    }

    /// <summary>Retracts every injected Steam UI surface and closes the transport before a
    /// transition asks Steam for Big Picture.</summary>
    /// <remarks>
    /// Steam rebuilds its whole front-end for that request, and the gamepad UI bootstraps against
    /// whatever <c>SteamClient.System.*</c> then says exists. Namespaces WSGM supplied from
    /// desktop mode were found there and went unanswered the moment the game-mode gate closed the
    /// transport two seconds later: the desired Big Picture window stayed recorded native-side
    /// while no window was ever created (device-diagnosed over CDP, 2026-09-01). Stock Windows
    /// client state is the one bootstrap Valve ships on this platform, so that is what the rebuild
    /// must see; everything re-applies through the normal gate once the window exists.
    /// </remarks>
    private async Task PrepareSteamUiForBigPictureAsync()
    {
        if (_steamUiTransport is null)
        {
            return;
        }
        _gameModeCefTransitionPending = true;
        await _cefMasterGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cefMasterEnabled)
            {
                if (_steamUi is not null)
                {
                    try
                    {
                        await _steamUi.DisableAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Retracting the native Steam UI patch for the Big Picture "
                            + $"request failed: {ex.Message}");
                    }
                }
                try
                {
                    await SteamPageBridge.DisableBadgeAsync().ConfigureAwait(false);
                    await SteamLibraryTabs.DisableAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn("Retracting legacy injected Steam UI for the Big Picture "
                        + $"request failed: {ex.Message}");
                }
            }
            ApplySteamUiTransportGate();
        }
        finally
        {
            _cefMasterGate.Release();
        }
    }

    /// <summary>Ends the Big Picture request hold and re-applies the configured Steam UI state
    /// for whichever mode the transition settled in. UI thread; safe when no hold is pending.</summary>
    private void ReleaseSteamUiBigPictureHold()
    {
        if (!_gameModeCefTransitionPending)
        {
            return;
        }
        _gameModeCefTransitionPending = false;
        RequestSteamUiTransportGateCheck();
        _steamUi?.Apply(_config.Cef.Enabled && _config.Cef.NativeQuickAccess);
        _steamUi?.ApplyNetworkIndicator(_inGameMode && _wifiIndicatorEnabled);
        _steamUi?.ApplyDownloadSort(_inGameMode && _downloadSortEnabled);
        KickTabBootSync();
    }

    /// <summary>Owns the transport gate for the session: re-decides it on every signal and at
    /// <see cref="SteamUiReadiness.TransportGatePollInterval"/>, always under the master-switch
    /// gate so it can never interleave with a retraction.</summary>
    private async Task RunSteamUiTransportGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _cefMasterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ApplySteamUiTransportGate();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Steam UI transport gate check failed: {ex.Message}");
                }
                finally
                {
                    _cefMasterGate.Release();
                }
                await _transportGateSignal
                    .WaitAsync(SteamUiReadiness.TransportGatePollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Session shutdown; the owner disposes the transport itself.
        }
    }

    /// <summary>Starts device admission off-thread, then creates shell and overlay services on the UI thread.</summary>
    /// <returns>The complete asynchronous session-start operation.</returns>
    public Task StartAsync()
    {
        _startupTask ??= StartUnderDeviceAdmissionAsync();
        return _startupTask;
    }

    private async Task StartUnderDeviceAdmissionAsync()
    {
        DeviceCoordinator? coordinator = null;
        bool coordinatorAdopted = false;
        try
        {
            // Overlay test deliberately never discovers packages or loads plugin code.
            coordinator = _overlayTestOnly
                ? null
                : await DeviceCoordinator.TryStartAsync(
                    _config,
                    _shutdownCancellation.Token).ConfigureAwait(false);
            if (_shutdownRequested)
            {
                return;
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _deviceCoordinator = coordinator;
                coordinatorAdopted = true;
                StartOnUiThread();
            });
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (!coordinatorAdopted && coordinator is not null)
            {
                await coordinator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void StartOnUiThread()
    {
        // The resident shell is the sole device-cycle authority. Overlay test deliberately never
        // creates this object, discovers packages, or loads plugin code.
        if (!_overlayTestOnly)
        {
            _messageWindow = MessageWindow.Create();
            _messageWindow.SessionEnding += OnSessionEnding;
            // The device cycle follows the session it belongs to. Without these the Claw's
            // controller, motion, OEM and suppressor services stayed live across a lock and a
            // system sleep, and the fresh cycle generation the resume contract requires was never
            // established afterwards.
            _messageWindow.SessionLocked += OnSessionLocked;
            _messageWindow.SessionUnlocked += OnSessionUnlocked;
            _messageWindow.SystemSuspending += OnSystemSuspending;
            _messageWindow.SystemResumed += OnSystemResumed;
            if (_deviceCoordinator is { } deviceCoordinator)
            {
                _autoTdp = new AutoTdpService(
                    new RtssFrametimeReader(),
                    deviceCoordinator.Capabilities.Snapshot,
                    (capabilityId, instanceId, value, token) =>
                        deviceCoordinator.ExecuteCapabilityAsync(
                            capabilityId,
                            instanceId,
                            value,
                            TimeSpan.FromSeconds(5),
                            CapabilityCommandOrigin.AutomaticControl,
                            token),
                    TargetFrametimeMs);
                _autoTdp.Apply(
                    ShouldRunAutoTdp(_config.DeviceIntegration));
                // A power limit the user set by hand pauses control permanently and is persisted to
                // whichever profile layer is in force, so it is restored on the next launch instead
                // of leaking onto the desktop. The hook is rooted here because this is where both
                // objects exist; every surface's power write already goes through the coordinator,
                // so this is the one place that sees all of them. Restore writes use the
                // ProfileRestore origin and never reach this funnel.
                deviceCoordinator.AttachAutoTdpManualOverride(watts =>
                {
                    _autoTdp?.NoteManualChange(watts);
                    PersistManualPowerLimit(watts);
                });

                _deviceOverlay = new DeviceOverlayBridge(deviceCoordinator, _autoTdp);
            }
        }
        else
        {
            _deviceOverlay = new SimulatedDeviceOverlaySource();
        }

        _performance = new PerformanceService(
            _overlayTestOnly ? new SimulatedRtssAdapter() : new RtssNativeAdapter(),
            _overlayTestOnly ? PersistSimulatedPerformancePolicyAsync : PersistPerformancePolicyAsync,
            BuildPerformancePolicy(_config, forceEnabled: _overlayTestOnly));
        _performanceOverlay = new PerformanceOverlayBridge(_performance);
        _performance.ApplyOsdCustomization(RtssOsdCustomSettings.FromConfig(_config.Performance));
        if (_overlayTestOnly)
        {
            // The per-application workflow must be inspectable in the safe UI mode: pretend one
            // Steam game is running and focused, so Device -> Profiles shows the application layer
            // instead of a permanently unavailable row. The real shell gets this target from
            // RunningApplicationCoordinator, which overlay-test deliberately never creates.
            _ = _performance.SetTargetAsync(new PerformanceApplicationTarget(
                "steam:480",
                480,
                "PreviewGame.exe"));
        }

        // Overlay-test runs without a real display to move, and pairing is the one performance
        // concern that changes hardware state rather than an RTSS profile.
        if (!_overlayTestOnly)
        {
            _refreshPairing = new RefreshRatePairingService();
            _resolutions = new DisplayResolutionService();
            _refreshPairing.SetStrategy(_config.Performance.FrameLimitStrategy);
            _performance.StateChanged += OnPerformanceStateForPairing;
        }
        if (!_overlayTestOnly)
        {
            _steamUiTransport = new PersistentSteamUiTransport();
            // Decide the gate BEFORE attaching: Attach copies the session flag into the
            // transport, and an open transport with a subscriber starts discovering
            // Steam's port at once.
            ApplySteamUiTransportGate();
            SteamUiTransportSession.Attach(_steamUiTransport);
            _transportGateWork = Task.Run(() =>
                RunSteamUiTransportGateAsync(_shutdownCancellation.Token));
            _runningApplications = new RunningApplicationMonitor(
                new SteamRunningApplicationProbe(_steamUiTransport),
                _config.Cef.Enabled);

            // The second identity source. It feeds the same monitor rather than driving policy on
            // its own, so per-application settings also work on the desktop and for titles Steam
            // never launched — which is the only way the overlay's per-game rows mean anything
            // outside a Steam game.
            _foregroundWindows = new ForegroundWindowWatcher();
            _foregroundWindows.ApplicationChanged += OnForegroundApplicationChanged;
            _runningApplicationTargets = new RunningApplicationCoordinator(
                _runningApplications,
                _performance.SetTargetAsync,
                _deviceCoordinator is null
                    ? null
                    : ApplyRunningApplicationTargetAsync);
        }

        _monitor = new SteamMonitor();
        if (!_overlayTestOnly)
        {
            _desktopHost = new ExplorerDesktopHost();
        }
        _modes = _desktopHost is null
            ? new SessionModes(_config, _monitor)
            : new SessionModes(_config, _monitor, _desktopHost);
        // Session-lifetime on purpose (survives desktop trips): a Steam download must
        // keep the device awake in both modes, and the manual hold belongs to the user.
        // The automatic side is off in overlay-test mode: its poll drives the live
        // Steam client over CEF (and would write the debug flag into a Steam install
        // that never opted in), which the safe local modes must not do. The manual
        // toggle still works there — it only takes a local power request.
        _keepAwake = KeepAwakeService.StartNew(
            _monitor,
            AutoKeepAwakeEnabled(_config),
            DownloadMonitoringEnabled(_config),
            () => !_inGameMode || SteamUiReadiness.IsReady);
        _keepAwake.DownloadActivityChanged += OnDownloadActivityChanged;
        // --overlay-test shares the Settings preview's exposure: it has no boot takeover
        // and no watchdog behind it, so the mode row must not offer a real transition.
        // Started here rather than by the taskbar, because Steam's audio namespace has to answer
        // while the taskbar is closed. Overlay-test keeps the old behaviour and lets the status
        // cluster own its own, since no Steam surface exists there to serve.
        if (!_overlayTestOnly)
        {
            _audio = new AudioManager();
            _audio.Start();

            // Not started here: scanning is expensive and belongs to whichever surface is showing a
            // network list. The manager exists for the whole session so Steam's Internet page can
            // drive it, but it stays idle until something asks.
            _radios = new RadioManager();
        }

        _overlay = new OverlayController(
            _config,
            _monitor,
            _modes,
            _keepAwake,
            previewOnly: _overlayTestOnly,
            device: _deviceOverlay,
            performance: _performanceOverlay,
            audio: _audio,
            radios: _radios);
        // The sheet is recreated per open, so its one-time cost — compiled-XAML populate JIT for
        // the process's largest window — lands on the user's first swipe (~1.5 s on the Claw).
        // Pay it at idle instead; every later open constructs against warm code.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            _overlay.WarmUp,
            Avalonia.Threading.DispatcherPriority.ApplicationIdle);

        if (_deviceCoordinator is { } controllerCapture && _overlay is { } captureSurface)
        {
            captureSurface.UiSurfaceOpened += surfaceId =>
                _ = ObserveUiCaptureClaimAsync(controllerCapture, surfaceId);
            captureSurface.UiSurfaceClosed += controllerCapture.ReleaseUi;
        }

        // WSGM's own navigation runs on the managed canonical stream when one is delivering, and on
        // SDL otherwise. Subscribed here rather than inside the overlay because this is where both
        // objects exist: the coordinator owns the stream and the controller owns the surfaces.
        // Nothing is unsubscribed on device teardown — the manager simply stops raising, and the
        // router falls back to SDL, which never stopped running.
        if (_deviceCoordinator is { } canonicalSource && _overlay is { } overlay)
        {
            // Posted, never called inline. This event is raised from the plugin runtime's registered
            // ThreadPool wait and runs straight into GamepadNavigation, which reads window
            // visibility and mutates Avalonia focus and controls — UI-thread-owned state that a
            // worker thread must not touch. The rate is bounded by design: the manager raises this
            // only while a WSGM surface has captured input.
            canonicalSource.Controllers.UiSampleReceived += sample =>
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => overlay.SubmitCanonicalSample(sample));
            canonicalSource.StateChanged += state =>
            {
                if (state is not DeviceCycleState.Active)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(overlay.ManagedInputLost);
                }
            };
            // The cycle staying Active is not the same as samples still arriving. Disabling
            // controller management runs make-safe and leaves the cycle Active while the plugin
            // stops publishing, so without this the router waited on a source that had gone quiet
            // and WSGM's own surfaces stopped answering a controller SDL could already see.
            canonicalSource.Controllers.StatusChanged += status =>
            {
                if (status.State is not ControllerManagementState.Active)
                {
                    Log.Info(
                        $"Managed UI input falls back to SDL: controller management is "
                        + $"{status.State} ({status.Detail}).");
                    Avalonia.Threading.Dispatcher.UIThread.Post(overlay.ManagedInputLost);
                }
            };
        }

        _deviceCoordinator?.ConfigureOemActions(new DeviceOemActionServices
        {
            ToggleOverlayAsync = cancellationToken => RunUiActionAsync(() =>
            {
                _overlay?.ToggleOverlay();
                return _overlay is not null;
            }, cancellationToken),
            ToggleSteamQuickAccessAsync = cancellationToken => RunUiActionAsync(() =>
                _monitor?.IsAlive is true
                && Steam.IsBigPictureVisible
                && Steam.TrySendBigPictureShortcut(BigPictureShortcut.QuickAccess),
                cancellationToken),
            ToggleDevicePageAsync = cancellationToken => RunUiActionAsync(() =>
            {
                _overlay?.ShowDevicePage();
                return _overlay is not null;
            }, cancellationToken),
            ToggleOpenAppsAsync = cancellationToken => RunUiActionAsync(() =>
            {
                _overlay?.ToggleOpenApps();
                return _overlay is not null;
            }, cancellationToken),
            ToggleDesktopGameModeAsync = cancellationToken => RunUiActionAsync(() =>
            {
                if (_modes is null)
                {
                    return false;
                }

                if (ExplorerControl.IsRunningInSession())
                {
                    _modes.EnterGameMode();
                }
                else
                {
                    _modes.EnterDesktopMode();
                }

                return true;
            }, cancellationToken),
            ToggleOnScreenKeyboardAsync = cancellationToken =>
                RunUiActionAsync(TouchKeyboard.Toggle, cancellationToken),
            CyclePerformanceProfileAsync = CyclePerformanceProfileAsync,
            CyclePerformanceOverlayLevelAsync = CyclePerformanceOverlayLevelAsync,
            SetRearButtonAsync = (button, cancellationToken) =>
                _deviceCoordinator?.PulseRearButtonAsync(button, cancellationToken)
                ?? Task.FromResult(false),
        });
        if (!_overlayTestOnly)
        {
            _steamUi = new SteamUiSessionHost(
                _steamUiTransport
                    ?? throw new InvalidOperationException("Steam UI transport was not created."),
                cancellationToken => RunUiActionAsync(() =>
            {
                _overlay?.ToggleOverlay();
                return _overlay is not null;
            }, cancellationToken),
                _deviceCoordinator,
                _performance,
                _audio,
                _radios,
                // Null in overlay-test, where there is no real display to move. The patch is then
                // never registered, so the row cannot appear offering a control with nothing behind
                // it.
                _overlayTestOnly ? null : _resolutions,
                _autoTdp,
                ReadNativeQamPerfSupport,
                ApplyManualRefreshRate,
                // Null when no plugin publishes VRR, which is also when the projection omits
                // is_vrr_supported and Valve's row does not render. One fact, one source. The
                // user-facing wrapper persists the state to the per-application layer in force; the
                // bare ApplyVariableRefreshRateAsync stays the profile restore's device write.
                _deviceCoordinator is null ? null : SetVariableRefreshRateFromUserAsync);
            _steamUi.Apply(_config.Cef.Enabled && _config.Cef.NativeQuickAccess);
            _steamUi.ApplyNetworkIndicator(_inGameMode && _wifiIndicatorEnabled);
            _steamUi.ApplyDownloadSort(_inGameMode && _downloadSortEnabled);
            ApplyGlyphConfig(_config);
            if (_deviceCoordinator is not null)
            {
                // Two sources change the active profile: the package publishing its profiles, and
                // the user changing the selection mode. Both land on the same apply.
                _deviceCoordinator.PhysicalGlyphCatalog.Changed += OnPhysicalGlyphProfilesChanged;
            }
        }

        // The tray host must never coexist with explorer's taskbar (Z-order war
        // over FindWindow — see TrayHost): gone before explorer starts, back
        // after game mode kills it. Apps re-home their icons on each side's
        // TaskbarCreated broadcast.
        _modes.DesktopModeStarting += () =>
        {
            _inGameMode = false;
            RequestSteamUiTransportGateCheck();
            _tabBootSyncCancellation.Cancel();
            // Tabs and the badge are game-mode surfaces; the ACF watcher only exists
            // to keep them fresh, so it stands down with them.
            ApplyCardServices(gameModeActive: false);
            _steamUi?.ApplyNetworkIndicator(false);
            _steamUi?.ApplyDownloadSort(false);
            _ = SteamPageBridge.DisableBadgeAsync();
            _ = SteamLibraryTabs.DisableAsync();
            _volumeButtons?.SetGameModeActive(false);
            _overlay?.AttachTrayHost(null);
            _trayHost?.Dispose();
            _trayHost = null;
        };
        _modes.PrepareSteamUiForBigPictureAsync = PrepareSteamUiForBigPictureAsync;
        _modes.SteamUiBigPictureRequestSettled = () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(ReleaseSteamUiBigPictureHold);
        _modes.GameModeEntered += () =>
        {
            _inGameMode = true;
            ReleaseSteamUiBigPictureHold();
            RequestSteamUiTransportGateCheck();
            EnterGameModeSurfaces();
            _steamUi?.ApplyNetworkIndicator(_wifiIndicatorEnabled);
            _steamUi?.ApplyDownloadSort(_downloadSortEnabled);
            // Returning from desktop mode disabled tabs/badge and cancelled the boot
            // sync; re-inject without requiring an overlay open.
            KickTabBootSync();
        };
        // A fresh Steam start while WSGM keeps running (client update, crash restart)
        // wipes the injected tabs and the resident badge with the old CEF session —
        // re-inject once the new UI is up.
        _monitor.SteamStarted += () =>
        {
            RequestSteamUiTransportGateCheck();
            if (_inGameMode)
            {
                KickTabBootSync();
                // A restarted client rebuilds its folder list from libraryfolders.vdf,
                // which can bring back a library for a card that is no longer in the
                // reader — and no volume notification will fire to say so.
                _cardVolumes?.Kick("Steam restarted");
            }
        };
        // Steam leaving in game mode closes the transport gate at once, so a restart's
        // fresh, still-headless CEF session cannot be connected before its own Big
        // Picture window exists.
        _monitor.SteamExited += RequestSteamUiTransportGateCheck;

        if (_overlayTestOnly)
        {
            // Paused so a Steam exit can never trigger auto-relaunch/overlay-pop
            // reactions on a dev machine ("no apps started" contract); IsAlive
            // still updates for the HomeAppAlive display.
            _monitor.Paused = true;
            Log.Info("Overlay test mode (no apps started).");
            _overlay.ShowOverlay();
            return;
        }

        _volumeButtons = new VolumeButtonService(
            _messageWindow!,
            () => DisplayScale.GetUiScalePercent(_config) / 100.0,
            _audio!);
        _displayMute = new DisplayOffMuteService(_messageWindow!);
        _displayMute.ApplyConfig(_config.MuteWhileDisplayOff);
        _displayMute.SetDownloadActive(_keepAwake.DownloadActive);
        if (_config.MuteWhileDisplayOff && !_config.Cef.Enabled)
        {
            // The mute only engages while Steam reports a download, and that comes
            // from the CEF poll. An upgraded config can carry MuteWhileDisplayOff
            // true with Steam integration off, where every log line lives inside the
            // poll that never runs — so say it once here, or a pasted log shows
            // nothing at all for a feature the user can see switched on.
            Log.Warn(
                "Mute screen-off downloads is enabled but Steam integration is off; "
                + "download state is unavailable, so muting will never engage.");
        }

        // Refresh boot.json every session start so a stale Elevate/ExePath heals
        // itself before the next sign-in.
        BootManifestWriter.WriteCurrent(_config);

        // Service boot: the service launches WSGM at WTS_SESSION_LOGON — usually
        // BEFORE Winlogon has even started explorer (device-observed 2026-08-07:
        // gating this on IsRunningInSession made the takeover never run, leaving
        // explorer alive behind Big Picture next to our tray host). The takeover
        // owns every explorer state: its readiness poll waits for explorer to
        // appear AND finish logon prep, then shuts it down cleanly; if explorer
        // never shows within the 60 s cap it proceeds like a plain game-mode boot.
        if (_serviceBoot)
        {
            StartBootTakeover();
            return;
        }

        if (ExplorerControl.IsRunningInSession())
        {
            // A live desktop at --shell start means this is NOT a logon boot: it is
            // the update restart (updates only run in desktop mode) or a manual
            // start next to a desktop. Resume in desktop mode — no splash, no
            // startup apps, no Steam, no game posture/scale — with the overlay armed
            // so the panel is available; EnterGameMode brings everything back.
            Log.Info("Shell started with a live desktop — resuming in desktop mode (overlay armed).");
            // No DesktopModeStarting fires for a session that never entered game
            // mode, so clear the flag here: the game-mode-only CEF injections must
            // not start next to a live explorer (and nothing would retract them).
            _inGameMode = false;
            RequestSteamUiTransportGateCheck();
            _monitor.Paused = true;
            WatchStartupAppsAndConfig();
            return;
        }

        // Boot recomputes the posture value, so game mode re-applies it each start.
        // Posture first: it changes the display scale, and the splash sizes itself
        // to the final screen metrics.
        _modes.ApplyGameModePosture();
        EnterGameModeSurfaces();
        ShowBootSplashIfEnabled();
        WatchStartupAppsAndConfig();

        _bootWork = Task.Run(async () =>
        {
            await RunLaunchSequenceAsync();
            _ = TrimAfterBootSettlesAsync(_shutdownCancellation.Token);
        });
    }

    /// <summary>Creates the game-mode-only surfaces in one shared order: tray host
    /// first (startup apps' Shell_NotifyIcon registrations need a living
    /// Shell_TrayWnd, or they only get an icon after the TaskbarCreated-driven retry,
    /// which message-only tray windows never hear), volume buttons, then card
    /// services. Direct boot and the service takeover are separate entry paths from
    /// the desktop-to-game transition — only the latter raises GameModeEntered — so
    /// each initial entry calls this explicitly, or an entire direct-boot session
    /// misses every card eject and insert (device log, 2026-08-22).</summary>
    private void EnterGameModeSurfaces()
    {
        _trayHost = TrayHost.Create();
        if (_trayHost is not null)
        {
            _overlay?.AttachTrayHost(_trayHost);
        }
        _volumeButtons?.SetGameModeActive(true);
        ApplyCardServices(gameModeActive: true);
    }

    /// <summary>Covers the screen with the boot splash when configured; the overlay
    /// opening dismisses it.</summary>
    private void ShowBootSplashIfEnabled()
    {
        if (!_config.BootSplashEnabled)
        {
            return;
        }
        _splash = new BootSplash(_config, SwitchToDesktopFromSplash);
        _overlay!.OverlayShown += () => _splash?.Dismiss("quick access opened");
        _splash.Show();
    }

    private void WatchStartupAppsAndConfig()
    {
        _startupWatcher = new StartupAppWatcher(_config.StartupApps);
        WatchConfig();
    }

    /// <summary>Runs the startup-app/Steam launch sequence, containing cancellation
    /// and failure so a boot worker never faults.</summary>
    private async Task RunLaunchSequenceAsync()
    {
        try
        {
            await LaunchAppsAsync(_shutdownCancellation.Token);
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
            Log.Info("Shell launch sequence cancelled for application shutdown.");
        }
        catch (Exception ex)
        {
            Log.Error("Shell session launch sequence failed", ex);
        }
    }

    /// <summary>Service-boot takeover: cover the booting desktop with the splash
    /// FIRST (before any posture change — the cover is the point of the early
    /// launch), let explorer finish its logon prep once, then cleanly shut it down
    /// and run the normal game-mode boot. The one-per-session explorer init is what
    /// keeps touch features (touch keyboard) alive in game mode.</summary>
    private void StartBootTakeover()
    {
        Log.Info("Boot cover: waiting for explorer logon prep.");
        _tookOverFromExplorer = true;
        var takeover = new BootTakeoverCancellation();
        _bootTakeover = takeover;

        ShowBootSplashIfEnabled();
        if (_splash is null)
        {
            Log.Info("Boot splash disabled — takeover runs uncovered.");
        }

        WatchStartupAppsAndConfig();

        // Mode switches must not race the takeover (the overlay is live behind the
        // splash and its Desktop button would start a second explorer transition).
        _modes!.BeginTransition();

        _bootWork = Task.Run(async () =>
        {
            var result = BootTakeoverResult.DesktopRestoreRequired;
            try
            {
                result = await RunBootTakeoverAsync(takeover.Token);
            }
            catch (OperationCanceledException) when (takeover.DesktopRequested)
            {
                Log.Info("Boot takeover cancelled by the splash desktop recovery.");
            }
            catch (OperationCanceledException) when (takeover.ShutdownRequested
                || _shutdownCancellation.IsCancellationRequested)
            {
                Log.Info("Boot takeover cancelled for application shutdown.");
            }
            catch (Exception ex)
            {
                Log.Error("Boot takeover failed", ex);
            }
            finally
            {
                // The gate guards the TAKEOVER only, not the launch sequence:
                // released here, the splash's Switch-to-desktop can run and
                // LaunchAppsAsync's monitor-paused guard skips Big Picture.
                _modes!.EndTransition();
                takeover.Complete();
            }

            var desktopRequested = takeover.DesktopRequested;
            if (takeover.ShutdownRequested || _shutdownRequested)
            {
                if (ReferenceEquals(_bootTakeover, takeover))
                {
                    _bootTakeover = null;
                }
                takeover.Dispose();
                return;
            }

            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_bootTakeover, takeover))
                    {
                        _bootTakeover = null;
                    }
                    if (_shutdownRequested)
                    {
                        return;
                    }
                    if (desktopRequested)
                    {
                        BeginDesktopModeFromSplash();
                    }
                    else if (result is BootTakeoverResult.DesktopPreserved)
                    {
                        ResumePreservedDesktopAfterBootFailure();
                    }
                    else if (result is BootTakeoverResult.DesktopRestoreRequired)
                    {
                        BeginDesktopModeAfterBootFailure();
                    }
                });
            }
            finally
            {
                if (ReferenceEquals(_bootTakeover, takeover))
                {
                    _bootTakeover = null;
                }
                takeover.Dispose();
            }

            if (result is BootTakeoverResult.EnteredGameMode
                && !desktopRequested
                && !_shutdownRequested)
            {
                await RunLaunchSequenceAsync();
            }
            _ = TrimAfterBootSettlesAsync(_shutdownCancellation.Token);
        });
    }

    /// <summary>Runs the takeover phase only (input-desktop barrier, explorer
    /// readiness, orderly exit, posture, tray host). Returns false when it failed
    /// open with explorer preserved — the caller then skips the launch sequence.</summary>
    /// <param name="cancellationToken">Cancelled by the splash's desktop recovery.
    /// Before the orderly exit it preserves Explorer; after that irreversible
    /// request began, it skips game-mode setup so the caller can restart Explorer.</param>
    private async Task<BootTakeoverResult> RunBootTakeoverAsync(CancellationToken cancellationToken)
    {
        // Input-desktop barrier (era-proven): WTS_SESSION_LOGON fires while the
        // Welcome screen still owns the input desktop — proceeding then starts
        // Steam audibly behind LogonUI. WTS_SESSION_DESKTOP_READY never arrives
        // on this hardware; polling for winsta0\Default is the working signal.
        var desktopWatch = System.Diagnostics.Stopwatch.StartNew();
        while (!InputDesktop.IsDefaultInputDesktop())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (desktopWatch.Elapsed >= TimeSpan.FromSeconds(60))
            {
                Log.Warn("Input desktop never became winsta0\\Default within 60 s — proceeding anyway.");
                break;
            }
            await Task.Delay(250, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (desktopWatch.ElapsedMilliseconds > 250)
        {
            Log.Info($"Interactive desktop ready after {desktopWatch.ElapsedMilliseconds} ms.");
        }

        var settleDuration = TimeSpan.FromMilliseconds(Math.Max(0, _config.ExplorerLogonSettleMs));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        System.Diagnostics.Stopwatch? settle = null;
        long shellSeenMs = -1, taskbarSeenMs = -1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shellWindow = NativeMethods.GetShellWindow() != 0;
            var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null) != 0;
            var bigPicture = Steam.IsBigPictureVisible;
            if (shellWindow && shellSeenMs < 0)
            {
                shellSeenMs = watch.ElapsedMilliseconds;
            }
            if (taskbar && taskbarSeenMs < 0)
            {
                taskbarSeenMs = watch.ElapsedMilliseconds;
            }

            // The invariant-7 acceleration exists solely so an OPAQUE cover never
            // sits over a live BP window. With the splash disabled there is no
            // cover, so report no BP and let explorer finish its logon prep — that
            // one-per-session init is what keeps touch features alive in game mode.
            var coveredBigPicture = bigPicture && _splash is not null;
            var action = ExplorerReadiness.Decide(shellWindow, taskbar, coveredBigPicture,
                watch.Elapsed, settle?.Elapsed, settleDuration, ExplorerReadiness.MaxWait);
            if (action == ExplorerReadinessAction.BeginSettle)
            {
                settle = System.Diagnostics.Stopwatch.StartNew();
                Log.Info($"Explorer readiness: shell window after {shellSeenMs} ms, " +
                         $"taskbar after {taskbarSeenMs} ms — settling {(int)settleDuration.TotalMilliseconds} ms.");
            }
            else if (action == ExplorerReadinessAction.ProceedAccelerated)
            {
                Log.Info("Big Picture appeared during boot cover — accelerating takeover (invariant 7).");
                break;
            }
            else if (action == ExplorerReadinessAction.ProceedTimeout)
            {
                Log.Warn($"Explorer readiness timeout after {(int)ExplorerReadiness.MaxWait.TotalSeconds} s — proceeding anyway.");
                break;
            }
            else if (action == ExplorerReadinessAction.Proceed)
            {
                break;
            }
            await Task.Delay(250, cancellationToken);
        }

        // Already off the UI thread — the bounded exit wait never blocks the
        // splash's spinner/fade. Logs its own outcome. The budget covers
        // ExplorerControl's 8 s linger grace (waiting out a slow remnant is
        // cheaper than terminating it — that is what Winlogon respawns) AND the
        // respawn retry, which shares the same deadline.
        cancellationToken.ThrowIfCancellationRequested();
        ExplorerPreparationResult preparation = _desktopHost is null
            ? new ExplorerPreparationResult(false, ExplorerShellRejection.ProcessUnavailable, "host-unavailable")
            : await _desktopHost.PrepareForExplorerExitAsync(cancellationToken).ConfigureAwait(false);
        if (!preparation.Prepared)
        {
            Log.Warn("Boot takeover refused before Explorer exit because no verified jobless "
                + $"shell launch owner could be retained ({preparation.Detail}).");
            bool desktopPresent;
            try
            {
                desktopPresent = ExplorerControl.IsRunningInSession()
                    || NativeMethods.GetShellWindow() != 0
                    || NativeMethods.FindWindowW("Shell_TrayWnd", null) != 0;
            }
            catch (Exception ex)
            {
                Log.Error("Checking desktop after refused boot takeover failed", ex);
                desktopPresent = false;
            }
            return desktopPresent
                ? BootTakeoverResult.DesktopPreserved
                : BootTakeoverResult.DesktopRestoreRequired;
        }
        var exited = ExplorerControl.ExitExplorerAndWait(TimeSpan.FromSeconds(30));
        // Posting Explorer's orderly-exit command is irreversible. A desktop
        // request that landed during the bounded wait must recover by starting
        // Explorer again, never continue into posture/tray/Steam game mode.
        cancellationToken.ThrowIfCancellationRequested();
        if (!exited)
        {
            bool explorerStillRunning;
            try
            {
                explorerStillRunning = ExplorerControl.IsRunningInSession();
            }
            catch (Exception ex)
            {
                Log.Error("Checking Explorer after failed boot takeover failed", ex);
                explorerStillRunning = false;
            }
            Log.Warn(explorerStillRunning
                ? "Boot takeover failed open — explorer was preserved."
                : "Boot takeover could not prove Explorer exited and no live shell remains — restoring desktop.");
            return explorerStillRunning
                ? BootTakeoverResult.DesktopPreserved
                : BootTakeoverResult.DesktopRestoreRequired;
        }

        var enteredGameMode = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            // Same order as the direct game-mode boot: posture (scale) with the
            // splash re-covering on the display change, then the tray host —
            // explorer is verifiably gone, so Create() can't race a dying taskbar.
            _modes!.ApplyGameModePosture();
            EnterGameModeSurfaces();
            return true;
        });
        if (!enteredGameMode || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return BootTakeoverResult.EnteredGameMode;
    }

    /// <summary>Handles the boot splash's recovery/quickswitch action on the UI
    /// thread. During the service takeover, cancellation owns the eventual desktop
    /// transition; outside it, the ordinary session transition can start now.</summary>
    private void SwitchToDesktopFromSplash()
    {
        if (_bootTakeover?.RequestDesktop() == true)
        {
            // Pause immediately so even a worker already leaving the takeover
            // cannot race through LaunchAppsAsync into Big Picture.
            if (_monitor is not null)
            {
                _monitor.Paused = true;
            }
            Log.Info("Boot splash desktop request accepted — cancelling takeover.");
            return;
        }
        BeginDesktopModeFromSplash();
    }

    /// <summary>Starts the normal desktop transition and supplies windowed Steam.
    /// The caller must own the UI thread and, for a cancelled service takeover,
    /// release its transition gate first.</summary>
    private void BeginDesktopModeFromSplash()
    {
        // The boot sequence skips its Big Picture start once the monitor is paused. Windowed
        // Steam starts only after Explorer's actual taskbar owner has been verified.
        _modes!.EnterDesktopMode(startSteamDesktop: true);
    }

    /// <summary>Completes a refused boot takeover without starting another Explorer. The original
    /// taskbar owner is still present, so dismissing the opaque cover is the recovery operation.</summary>
    private void ResumePreservedDesktopAfterBootFailure()
    {
        _splash?.Dismiss("takeover refused");
        _inGameMode = false;
        RequestSteamUiTransportGateCheck();
        if (_monitor is not null)
        {
            _monitor.Paused = true;
        }
        _modes!.ReportWarning(SessionModes.ExplorerTakeoverRefusedWarning);
    }

    /// <summary>Starts the ordinary verified desktop restoration after boot crossed an uncertain
    /// Explorer-exit boundary. The transition gate has already been released by the caller.</summary>
    private void BeginDesktopModeAfterBootFailure()
    {
        _splash?.Dismiss("takeover recovery");
        _modes!.ReportWarning(SessionModes.ExplorerExitFailedWarning);
        _modes.EnterDesktopMode();
    }

    /// <summary>Name-based liveness check for the double-launch guard. Deliberately
    /// name-only (not full-path): MainModule of a cross-integrity process throws,
    /// and a same-named copy running from elsewhere still means the user's tool is
    /// up. Protocol/non-exe targets always report false.</summary>
    private static bool IsAppAlreadyRunning(string path)
    {
        try
        {
            return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && WindowFinder.FindProcessIds(
                    System.IO.Path.GetFileNameWithoutExtension(path)).Count > 0;
        }
        catch
        {
            // Enumeration hiccups must not block the launch sequence.
            return false;
        }
    }

    /// <summary>Cancels any in-flight boot sync and starts a fresh one (waits for
    /// Steam's UI, then injects tabs and pushes the badge map). Safe to call on
    /// every trigger — SyncAllAsync's gate serializes overlapping runs (each queued
    /// caller still runs a full sync; they are not collapsed into one).</summary>
    private void KickTabBootSync()
    {
        if (_shutdownRequested)
        {
            return;
        }
        CancellationTokenSource previous = _tabBootSyncCancellation;
        var current = new CancellationTokenSource();
        _tabBootSyncCancellation = current;
        previous.Cancel();
        _ = RunTabBootSyncAsync(current);
    }

    private async Task RunTabBootSyncAsync(CancellationTokenSource owner)
    {
        try
        {
            await new LibraryTabManager().SyncOnBootAsync(owner.Token).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(_tabBootSyncCancellation, owner))
            {
                owner.Dispose();
            }
        }
    }

    /// <summary>Starts or retracts the injected download-queue sort buttons to match a
    /// reloaded configuration, so the toggle applies without a re-logon.</summary>
    /// <param name="enabled">Whether the sort buttons should be injected.</param>
    private void ApplyDownloadSort(bool enabled)
    {
        if (_overlayTestOnly || enabled == _downloadSortEnabled)
        {
            _downloadSortEnabled = enabled;
            return;
        }
        _downloadSortEnabled = enabled;
        _steamUi?.ApplyDownloadSort(_inGameMode && enabled);
        Log.Info($"Download queue sorting {(enabled ? "enabled" : "disabled")}.");
    }

    /// <summary>Applies a Steam Input Management change that arrived through a
    /// config reload.</summary>
    /// <remarks>
    /// The park/restore rename touches Steam's directory, so it runs off the UI
    /// thread. Reconciles are idempotent and serialized inside
    /// <see cref="SteamInputShim"/>, which is what lets the Settings save path and
    /// this watcher both fire without coordinating.
    /// </remarks>
    private static void ApplySteamInputManagement(bool enabled)
    {
        if (SteamInputShim.Enabled == enabled)
        {
            return;
        }
        SteamInputShim.SetEnabled(enabled);
        _ = System.Threading.Tasks.Task.Run(() => SteamInputShim.Reconcile("settings-change"));
    }

    /// <summary>Mirrors the master CEF switch, retracting anything WSGM already
    /// injected on the way down. Ordering is load-bearing: the switch fails every
    /// evaluation closed, including WSGM's own retractions, so flipping it first
    /// would strand the registered patches, tabs and badge in Steam until the client
    /// restarted — with the desktop-trip cleanup dead for the same reason. Both
    /// directions run through <c>_cefMasterGate</c> and re-read the field (the
    /// wanted state) once they own it, so a flip landing inside a retraction's
    /// removal sequence cannot leave the choke point closed while the field —
    /// and the equality guard that would have repaired it — say enabled.</summary>
    /// <param name="enabled">The reloaded <c>Cef.Enabled</c> value.</param>
    private void ApplyCefMasterSwitch(bool enabled)
    {
        if (_cefMasterEnabled == enabled)
        {
            return;
        }
        _cefMasterEnabled = enabled;
        _runningApplications?.SetSteamEnabled(enabled);
        if (enabled)
        {
            _ = Task.Run(async () =>
            {
                await _cefMasterGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!_cefMasterEnabled)
                    {
                        // Turned off again before this apply owned the gate — that
                        // apply's retraction owns the choke point now.
                        return;
                    }
                    // Through the readiness gate, not straight to open: a master switch
                    // turned on while Steam is cold-starting in game mode still waits
                    // for its window.
                    ApplySteamUiTransportGate();
                }
                finally
                {
                    _cefMasterGate.Release();
                }
                // Field-mutating and fire-and-forget from the UI thread, like every
                // other caller.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ApplyCardServices(_inGameMode);
                    KickTabBootSync();
                    _steamUi?.ApplyDownloadSort(_inGameMode && _downloadSortEnabled);
                });
            });
            return;
        }
        // The volume monitor owns autonomous CEF traffic. Stop it as soon as the
        // master gate closes; the ACF watcher remains because it is Steam-file only.
        ApplyCardServices(_inGameMode);
        // A boot sync still in its retry loop would otherwise re-inject the tabs
        // between the awaited DisableAsync and the choke point closing behind it,
        // stranding them until Steam restarts (the desktop trip cancels for the
        // same reason).
        _tabBootSyncCancellation.Cancel();
        _ = Task.Run(async () =>
        {
            await _cefMasterGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_steamUi is not null)
                {
                    try
                    {
                        await _steamUi.DisableAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Retracting the native Steam UI patch failed: {ex.Message}");
                    }
                }

                try
                {
                    await SteamPageBridge.DisableBadgeAsync().ConfigureAwait(false);
                    await SteamLibraryTabs.DisableAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Retracting legacy injected Steam UI failed: {ex.Message}");
                }
            }
            finally
            {
                // Only close the choke point while OFF is still the wanted state:
                // a re-enable that landed during these three round-trips already
                // reopened it, and the equality guard above means no later reload
                // would ever repair an overwrite here.
                if (!_cefMasterEnabled)
                {
                    ApplySteamUiTransportGate();
                    Log.Info("Steam CEF integration disabled — injected UI retracted.");
                }
                else
                {
                    Log.Info("Steam CEF integration was re-enabled during the retraction — " +
                             "leaving the choke point to the enable apply.");
                }
                _cefMasterGate.Release();
            }
        });
    }

    /// <summary>Whether the automatic download wake lock may poll Steam: its CEF
    /// query is autonomous Steam traffic, so it stays off in overlay-test mode
    /// alongside the other injections that mode excludes.</summary>
    /// <param name="config">The configuration to read the gates from.</param>
    private bool AutoKeepAwakeEnabled(AppConfig config)
        => !_overlayTestOnly && config.Cef.Enabled && config.Cef.DownloadKeepAwake;

    /// <summary>Whether the shared Steam download poll has at least one consumer.
    /// The mute feature reuses the same answer even when its automatic wake lock is
    /// disabled; overlay-test still excludes all autonomous Steam traffic.</summary>
    /// <param name="config">The configuration to read the gates from.</param>
    private bool DownloadMonitoringEnabled(AppConfig config)
        => !_overlayTestOnly
            && config.Cef.Enabled
            && (config.Cef.DownloadKeepAwake || config.MuteWhileDisplayOff);

    /// <summary>Marshals the shared poller's download transition onto the UI thread,
    /// where the display mute service and its timers are owned.</summary>
    /// <param name="active">Whether Steam reports an active download.</param>
    private void OnDownloadActivityChanged(bool active)
        => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _displayMute?.SetDownloadActive(active));

    private void OnSessionLocked() => QueueDevicePowerTransition(suspend: true, "session locked");

    private void OnSessionUnlocked() => QueueDevicePowerTransition(suspend: false, "session unlocked");

    private void OnSystemSuspending() => QueueDevicePowerTransition(suspend: true, "system suspending");

    private void OnSystemResumed() => QueueDevicePowerTransition(suspend: false, "system resumed");

    /// <summary>Quiesces or revives the device cycle with the session it belongs to.</summary>
    /// <param name="suspend">Whether the cycle should quiesce.</param>
    /// <param name="reason">The notification that asked for it, for the log.</param>
    /// <remarks>
    /// Edge-triggered and serialized, because the four notifications overlap: a sleep started from
    /// the lock screen delivers a lock and a suspend, and Windows sends both resume events for one
    /// wake. Neither coordinator call is idempotent — resume advances the cycle generation — so
    /// only a real transition is forwarded, and each one waits for the previous to finish.
    /// </remarks>
    private void QueueDevicePowerTransition(bool suspend, string reason)
    {
        if (_deviceCoordinator is not { } coordinator)
        {
            Log.Info(
                $"Device cycle {(suspend ? "suspend" : "resume")} skipped ({reason}): no "
                + "device coordinator is active.");
            return;
        }

        lock (_devicePowerGate)
        {
            bool effective = _pendingDeviceSuspended ?? _deviceSuspended;
            if (effective == suspend)
            {
                Log.Info(
                    $"Device cycle {(suspend ? "suspend" : "resume")} skipped ({reason}): the "
                    + $"cycle is already {(suspend ? "suspended or suspending" : "running or resuming")}.");
                return;
            }

            _pendingDeviceSuspended = suspend;
            long requestGeneration = ++_devicePowerRequestGeneration;
            _devicePowerWork = ApplyDevicePowerTransitionAsync(
                _devicePowerWork,
                coordinator,
                suspend,
                reason,
                requestGeneration);
        }
    }

    private async Task ApplyDevicePowerTransitionAsync(
        Task previous,
        DeviceCoordinator coordinator,
        bool suspend,
        string reason,
        long requestGeneration)
    {
        // Never faults: the continuation below reports its own failures and returns normally, so
        // awaiting the previous transition cannot throw here.
        await previous.ConfigureAwait(false);
        try
        {
            if (suspend)
            {
                await coordinator.SuspendAsync().ConfigureAwait(false);
            }
            else
            {
                await coordinator.ResumeAsync().ConfigureAwait(false);
            }

            lock (_devicePowerGate)
            {
                _deviceSuspended = suspend;
                if (_devicePowerRequestGeneration == requestGeneration)
                {
                    _pendingDeviceSuspended = null;
                }
            }
            Log.Info($"Device cycle {(suspend ? "suspended" : "resumed")}: {reason}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_devicePowerGate)
            {
                if (_devicePowerRequestGeneration == requestGeneration)
                {
                    _pendingDeviceSuspended = null;
                }
            }
            Log.Error($"Device cycle {(suspend ? "suspend" : "resume")} failed ({reason})", ex);
        }
    }

    /// <summary>Hands a foreground application change to the running-application monitor.</summary>
    /// <param name="executable">Foreground executable file name.</param>
    /// <remarks>
    /// Straight through, with no policy of its own: the monitor's projection decides whether this
    /// identity is used at all, so the precedence between Steam and the foreground stays in the one
    /// pure function that can be tested.
    /// </remarks>
    private void OnForegroundApplicationChanged(string executable, string? imagePath)
        => _runningApplications?.ReportForeground(executable, imagePath);

    /// <summary>Turns variable refresh rate on or off through the device plugin.</summary>
    /// <param name="enabled">The requested state.</param>
    /// <param name="cancellationToken">Cancels the device write.</param>
    /// <returns>Whether the device applied it.</returns>
    /// <remarks>
    /// The plugin owns the transport — Arc Sync on the reference device — because it touches the
    /// GPU driver, and chasing driver changes is the plugin author's burden rather than WSGM's.
    /// This only finds the published capability and asks.
    /// </remarks>
    private async Task<bool> ApplyVariableRefreshRateAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (_deviceCoordinator is not { } coordinator)
        {
            return false;
        }

        DeviceCapabilityView? view = coordinator.Capabilities.Snapshot().FirstOrDefault(candidate =>
            candidate.Descriptor.Role is CapabilityRole.VariableRefreshRate
            && candidate.Projection.State.Available);
        if (view is null)
        {
            Log.Warn("Variable refresh rate refused: no available capability publishes it.");
            return false;
        }

        CapabilityCommandResult result = await coordinator.ExecuteCapabilityAsync(
            view.Descriptor.CapabilityId,
            view.Descriptor.InstanceId,
            new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = enabled },
            TimeSpan.FromSeconds(5),
            // User, not AutomaticControl: this is a switch the user pressed, and the distinction is
            // what lets a manual change pause automatic control rather than look like it.
            CapabilityCommandOrigin.User,
            cancellationToken).ConfigureAwait(false);

        // Verified counts, unverified counts. A timeout does not: whether the panel changed is
        // unknown, and reporting success would leave Steam's toggle disagreeing with the display.
        return result.Outcome is CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified;
    }

    /// <summary>Applies a refresh rate the user chose by hand.</summary>
    /// <param name="refreshHz">The chosen rate.</param>
    /// <returns>Whether the display is now at that rate.</returns>
    /// <remarks>
    /// The ownership and validation rules live with <see
    /// cref="RefreshRatePairingService.TryApplyManual"/>; this only supplies the cap in force.
    /// </remarks>
    private bool ApplyManualRefreshRate(int refreshHz)
        => _refreshPairing?.TryApplyManual(
            refreshHz,
            _performance?.Current.Desired.FrameLimit ?? 0) ?? false;

    private NativeQamPerfSupport ReadNativeQamPerfSupport()
    {
        RefreshRatePairingService? pairing = _refreshPairing;
        IReadOnlyList<int> options = pairing?.FrameLimitOptions() ?? [];
        // The same predicate the pairing service decides by, not a second copy of the comparison:
        // under either coupled strategy the pairing policy owns the refresh rate, so Steam's manual
        // refresh row must not be offered at all — a user setting it would watch the next frame-cap
        // change overwrite it.
        bool manualRefresh = FrameLimitPairing.RefreshRateIsUserOwned(
            _config.Performance.FrameLimitStrategy);

        bool vrr = false;
        bool vrrEnabled = false;
        if (_deviceCoordinator is { } coordinator)
        {
            DeviceCapabilityView? view = coordinator.Capabilities.Snapshot().FirstOrDefault(candidate =>
                candidate.Descriptor.Role is CapabilityRole.VariableRefreshRate
                && candidate.Projection.State.Available);
            vrr = view is not null;
            // Read from the same capability that reports support, so the toggle cannot show a state
            // the device disagrees with.
            vrrEnabled = view?.Projection.State.ObservedValue?.BooleanValue ?? false;
        }

        // Read through the pairing service's session cache: this runs on every state publication,
        // and enumerating plus CDS_TESTing every mode each time hammers the display driver.
        // Enumerated under every strategy: with the frame limit switched off the unified row
        // becomes a refresh-rate slider, offered whatever the pairing strategy is because there is
        // no cap left for it to fight. RefreshRatesSelectable below still gates Valve's SEPARATE
        // manual row, which must stay hidden while a cap owns the rate.
        IReadOnlyList<int> refreshRates = pairing?.AcceptedRates() ?? [];
        return new NativeQamPerfSupport(
            options,
            vrr,
            manualRefresh && refreshRates.Count > 0,
            refreshRates.Count > 0 ? refreshRates.Min() : null,
            refreshRates.Count > 0 ? refreshRates.Max() : null,
            vrrEnabled,
            refreshRates.Count > 0 ? DisplayProfiles.ReadCurrentRefreshRate() : null,
            ReadPairedRefreshRates(pairing, options, manualRefresh),
            refreshRates);
    }

    /// <summary>The refresh rate each offered cap will be presented at.</summary>
    /// <remarks>
    /// Built here rather than in the injected half so the pairing policy stays one decision in one
    /// place. Empty under the uncoupled strategy, where a cap changes no display state and the row
    /// therefore has no rate to name — which is also what makes the label collapse from
    /// "60 FPS (60 Hz)" to plain "60 FPS" without a second flag saying so.
    /// </remarks>
    private static IReadOnlyDictionary<int, int>? ReadPairedRefreshRates(
        RefreshRatePairingService? pairing,
        IReadOnlyList<int> options,
        bool uncoupled)
    {
        if (uncoupled || pairing is null || options.Count == 0)
        {
            return null;
        }

        Dictionary<int, int> paired = new(options.Count);
        foreach (int cap in options)
        {
            if (cap > 0 && pairing.SelectRefreshHz(cap) is { } hz)
            {
                paired[cap] = hz;
            }
        }

        return paired.Count > 0 ? paired : null;
    }

    /// <summary>Starts or stops the game-mode card services from one shared policy.</summary>
    /// <remarks>
    /// Initial direct boot and a later desktop-to-game transition are separate entry
    /// paths: only the latter raises <c>GameModeEntered</c>. Keeping their activation
    /// here prevents one path from silently losing volume notifications again.
    /// </remarks>
    /// <param name="gameModeActive">Whether the destination/current mode is game mode.</param>
    private void ApplyCardServices(bool gameModeActive)
    {
        var state = GameModeCardServicePolicy.Decide(
            gameModeActive, _overlayTestOnly, _cefMasterEnabled);

        if (state.WatchAppManifests)
        {
            _cardAcfWatcher ??= CardAcfWatcher.StartNew();
        }
        else
        {
            _cardAcfWatcher?.Dispose();
            _cardAcfWatcher = null;
        }

        if (state.ReconcileSteamLibraries)
        {
            // Card swaps are reconciled against Steam's install-folder list on the
            // volume notification itself. The callback refreshes both consumers of
            // the changed library membership after Steam accepts the reconcile.
            _cardVolumes ??= CardVolumeMonitor.StartNew(
                MessageWindow.Create(),
                () => _cefMasterEnabled,
                () =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(KickTabBootSync);
                    return Task.CompletedTask;
                });
        }
        else
        {
            _cardVolumes?.Dispose();
            _cardVolumes = null;
        }
    }

    /// <summary>Starts or stops the Big Picture Wi-Fi indicator to match a reloaded
    /// configuration. Without this the feed keeps running (and keeps being recreated
    /// on every game-mode entry) after the user turns the toggle off, because the
    /// start gates read the boot-time configuration.</summary>
    /// <param name="enabled">Whether the indicator should be feeding Steam.</param>
    private void ApplyNetworkIndicator(bool enabled)
    {
        if (_overlayTestOnly)
        {
            _wifiIndicatorEnabled = enabled;
            Log.Change(
                "steam.network-indicator",
                "Big Picture Wi-Fi indicator not applied: mode=overlay-test.");
            return;
        }
        if (enabled == _wifiIndicatorEnabled)
        {
            _wifiIndicatorEnabled = enabled;
            return;
        }
        _wifiIndicatorEnabled = enabled;
        if (!enabled)
        {
            _steamUi?.ApplyNetworkIndicator(false);
            Log.Info("Big Picture Wi-Fi indicator turned off.");
            return;
        }
        if (_inGameMode)
        {
            _steamUi?.ApplyNetworkIndicator(true);
            Log.Info("Big Picture Wi-Fi indicator turned on.");
        }
        else
        {
            Log.Change(
                "steam.network-indicator",
                "Big Picture Wi-Fi indicator deferred: requested=true, mode=desktop.");
        }
    }

    private void WatchConfig()
    {
        try
        {
            _configWatcher = new System.IO.FileSystemWatcher(Log.Directory, "config.json")
            {
                EnableRaisingEvents = true,
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName,
            };
            // The LOAD stays off the UI thread: it takes the cross-process config
            // mutex (2 s timeout) that a settings save holds across the write, the
            // splash-asset promotion and the boot manifest — 500 ms of debounce does
            // not reliably outlast that. Only the cheap, UI-affine apply is posted.
            void Reload(object? state)
                => _ = Task.Run(() =>
                {
                    long generation = (long)(state ?? 0L);
                    var config = ConfigStore.Load();
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed || generation != Interlocked.Read(ref _configReloadGeneration))
                        {
                            return;
                        }
                        // One instance for every reader: the volume OSD's UI-scale
                        // callback and DisplayScale's saved-scale snapshot must not
                        // drift onto different AppConfig objects.
                        _config = config;
                        ApplyDeviceConfig(config);
                        ApplyPerformanceConfig(config);
                        ApplyCefMasterSwitch(config.Cef.Enabled);
                        if (config.Cef.Enabled)
                        {
                            _steamUi?.Apply(config.Cef.NativeQuickAccess);
                            ApplyGlyphConfig(config);
                        }
                        ApplySteamInputManagement(config.SteamInputManagementEnabled);
                        ApplyNetworkIndicator(config.Cef.Enabled && config.Cef.WifiIndicator);
                        ApplyDownloadSort(config.Cef.Enabled && config.Cef.DownloadQueueSort);
                        _displayMute?.ApplyConfig(config.MuteWhileDisplayOff);
                        _overlay?.ApplyConfig(config);
                        _startupWatcher?.Apply(config.StartupApps);
                        _keepAwake?.ApplyConfig(
                            AutoKeepAwakeEnabled(config),
                            DownloadMonitoringEnabled(config));
                    });
                });
            // Changed/Renamed fire on threadpool threads — the swap must be locked
            // so two near-simultaneous events can't both dispose the same timer and
            // orphan one that still fires.
            void Debounce()
            {
                lock (_configDebounceGate)
                {
                    _configDebounce?.Dispose();
                    long generation = Interlocked.Increment(ref _configReloadGeneration);
                    _configDebounce = new System.Threading.Timer(
                        Reload, generation, 500, System.Threading.Timeout.Infinite);
                }
            }
            _configWatcher.Changed += (_, _) => Debounce();
            _configWatcher.Renamed += (_, _) => Debounce();
            // Internal-buffer overflow or a directory-level error kills the change
            // events silently — settings would stop applying for the rest of the
            // session with nothing in the log to diagnose it from. Log, reload once
            // (the missed write is already on disk), and re-arm by restarting the
            // watch. Deliberately NOT a recreate: this handler would resubscribe
            // itself and a persistently failing directory would spin.
            _configWatcher.Error += (sender, e) =>
            {
                Log.Warn($"Config watcher error: {e.GetException().Message} — re-arming.");
                Debounce();
                try
                {
                    if (sender is System.IO.FileSystemWatcher watcher)
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.EnableRaisingEvents = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Config watcher could not be re-armed: {ex.Message}");
                }
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Config watcher not available: {ex.Message}");
        }
    }

    private async Task<bool> RunUiActionAsync(
        Func<bool> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_shutdownRequested)
        {
            return false;
        }
        return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            _shutdownRequested ? false : action());
    }

    private async Task<bool> CyclePerformanceOverlayLevelAsync(
        CancellationToken cancellationToken)
    {
        if (_shutdownRequested || _performanceOverlay is null)
        {
            return false;
        }
        return await _performanceOverlay.CycleOverlayLevelAsync("oem-action", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> CyclePerformanceProfileAsync(CancellationToken cancellationToken)
    {
        IDeviceOverlaySource? device = _deviceOverlay;
        if (device?.Snapshot().Profile?.CanInvoke is not true)
        {
            Log.Info("OEM performance-profile cycle skipped: no selectable hardware profile is active.");
            return false;
        }

        await device.CycleHardwareProfileAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void OnSessionEnding()
    {
        if (_disposed)
        {
            return;
        }
        Log.Info("Interactive session is ending; requesting bounded session cleanup.");
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);
        ApplicationShutdownRequest.ShutdownLifetime();
    }

    /// <summary>Runs bounded device cleanup before the application lifetime ends.</summary>
    public ValueTask DisposeAsync() => ShutdownAsync(
        ApplicationShutdownReason.Normal,
        DateTimeOffset.UtcNow.Add(ApplicationShutdownCoordinator.BudgetFor(
            ApplicationShutdownReason.Normal)));

    /// <summary>Runs session cleanup with the device protocol reason and one outer deadline.</summary>
    internal async ValueTask ShutdownAsync(
        ApplicationShutdownReason reason,
        DateTimeOffset deadline)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdownRequested = true;
        _shutdownCancellation.Cancel();
        // Every cleanup step still runs after an earlier one fails; the collected
        // failures are reported once at the end so the outer coordinator records the
        // shutdown as unverified without any step having been skipped.
        List<Exception> failures = [];
        if (_startupTask is not null)
        {
            try
            {
                await _startupTask;
            }
            catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Error("Shell startup failed before shutdown cleanup", ex);
            }
        }
        _bootTakeover?.RequestShutdown();
        _modes?.RequestShutdown();
        try
        {
            _splash?.Dismiss("application shutdown");
        }
        catch (Exception ex)
        {
            failures.Add(ex);
            Log.Error("Dismissing the boot splash during application shutdown failed", ex);
        }
        // Close input admission on the UI thread before any safety-critical asynchronous cleanup.
        try
        {
            _overlay?.Dispose();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
            Log.Error("Closing overlay command admission during application shutdown failed", ex);
        }
        finally
        {
            _overlay = null;
        }
        _tabBootSyncCancellation.Cancel();

        // Device cleanup is the safety-critical part of the outer application budget.
        // Run it before waiting on shell transitions or doing Explorer/CEF/RTSS teardown.
        // If the outer owner reaches its deadline, process exit still unloads the in-process
        // runtime while the shell anchor remains available for owner-loss desktop recovery.
        // Before the coordinator, deliberately. AutoTDP restores the limit it took over from
        // through that coordinator's capability path, so disposing it afterwards issued the restore
        // into an already-disconnected runtime and left the handheld on the last automatically
        // selected wattage on every exit, update, uninstall and session end.
        if (_autoTdp is not null)
        {
            try
            {
                await _autoTdp.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Error("AutoTDP restoration was unverified during application shutdown", ex);
            }
            finally
            {
                _autoTdp = null;
                _deviceCoordinator?.AttachAutoTdpManualOverride(null);
            }
        }

        if (_deviceCoordinator is not null)
        {
            PluginStopReason deviceReason = reason switch
            {
                ApplicationShutdownReason.Update =>
                    PluginStopReason.Updating,
                ApplicationShutdownReason.SessionEnd =>
                    PluginStopReason.SessionEnding,
                ApplicationShutdownReason.Uninstall =>
                    PluginStopReason.Uninstalling,
                _ => PluginStopReason.WsgmExiting,
            };
            _deviceCoordinator.PhysicalGlyphCatalog.Changed -= OnPhysicalGlyphProfilesChanged;
            try
            {
                await _deviceCoordinator.ShutdownAsync(deviceReason, deadline).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Error(
                    "Device cleanup was unverified; remaining shell cleanup continues",
                    ex);
            }
            finally
            {
                _deviceCoordinator = null;
            }
        }

        try
        {
            // Shutdown rejects every new transition before reaching this point. Let the one
            // existing transition and the separately-rooted boot worker cross their Explorer/UI
            // boundaries before disposing anything they can still access. The application
            // coordinator owns the only deadline; a nested timeout here could retire the recovery
            // anchor underneath them.
            if (_modes is not null)
            {
                await _modes.WaitForTransitionAsync().ConfigureAwait(false);
            }
            if (_bootWork is not null)
            {
                await _bootWork.ConfigureAwait(false);
                _bootWork = null;
            }
            if (_transportGateWork is not null)
            {
                // Ends on the cancelled session token; awaited so it can never re-decide the
                // transport after the disposal below has begun.
                await _transportGateWork.ConfigureAwait(false);
                _transportGateWork = null;
            }

            bool trayRetired = false;
            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RetireTrayHostForShutdown);
                trayRetired = true;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                Log.Error("Retiring the WSGM taskbar during application shutdown failed", ex);
            }
            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(DisposeUiOwnedSessionResources);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                Log.Error("UI-owned shell cleanup failed during application shutdown", ex);
            }

            bool desktopVerified = trayRetired
                && await RestoreDesktopBeforeShutdownAsync(reason, deadline).ConfigureAwait(false);
            if (desktopVerified && _desktopHost is not null)
            {
                await _desktopHost.DisposeAsync().ConfigureAwait(false);
                _desktopHost = null;
            }

            // AutoTDP is already gone: it is disposed before the device coordinator, above,
            // because its restoration needs that coordinator's write path.
            if (_runningApplicationTargets is not null)
            {
                await _runningApplicationTargets.DisposeAsync().ConfigureAwait(false);
                _runningApplicationTargets = null;
            }
            if (_foregroundWindows is not null)
            {
                _foregroundWindows.ApplicationChanged -= OnForegroundApplicationChanged;
                _foregroundWindows.Dispose();
                _foregroundWindows = null;
            }
            if (_runningApplications is not null)
            {
                await _runningApplications.DisposeAsync().ConfigureAwait(false);
                _runningApplications = null;
            }
            await _cefMasterGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_steamUi is not null)
                {
                    await _steamUi.DisposeAsync().ConfigureAwait(false);
                    _steamUi = null;
                }
            }
            finally
            {
                _cefMasterGate.Release();
            }
            if (_steamUiTransport is not null)
            {
                SteamUiTransportSession.Detach(_steamUiTransport);
                await _steamUiTransport.DisposeAsync().ConfigureAwait(false);
                _steamUiTransport = null;
            }
            if (_performance is not null)
            {
                _performance.StateChanged -= OnPerformanceStateForPairing;
                await _performance.DisposeAsync().ConfigureAwait(false);
                _performance = null;
            }

            // Before the session ends, not after: the applied rate is transient and would
            // heal on its own eventually, but leaving the desktop at 48 Hz until something
            // else resets it is a change the user never made and would have to hunt for.
            if (_refreshPairing is not null)
            {
                if (!_refreshPairing.Restore())
                {
                    failures.Add(new InvalidOperationException(
                        "The pre-game display refresh rate could not be restored."));
                }
                _refreshPairing = null;
            }

            // Same reasoning, and separately owned: a resolution the user picked from the menu
            // is transient too, and leaving the desktop at a game's resolution is the more
            // visible of the two changes to be left with.
            if (_resolutions is not null)
            {
                if (!_resolutions.Restore())
                {
                    failures.Add(new InvalidOperationException(
                        "The pre-game display resolution could not be restored."));
                }
                _resolutions = null;
            }

            // After the Steam host and the overlay, both of which hold them.
            if (_audio is not null)
            {
                _audio.Dispose();
                _audio = null;
            }

            if (_radios is not null)
            {
                _radios.Dispose();
                _radios = null;
            }
            _tabBootSyncCancellation.Dispose();
            _shutdownCancellation.Dispose();

            if (!desktopVerified)
            {
                throw new InvalidOperationException(
                    "Application shutdown could not verify a usable Explorer desktop; "
                    + "the retained shell anchor will recover after process exit.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
        }

        if (ShutdownFailure(failures) is { } unverified)
        {
            throw unverified;
        }
    }

    /// <summary>Reports collected cleanup failures once, or null when every step was verified.</summary>
    /// <remarks>
    /// The single-failure case keeps that exception as the inner one rather than burying it in a
    /// one-element aggregate, because the log line a maintainer reads is the inner message. That
    /// every step still ran is guaranteed by the straight-line shutdown above, which has no early
    /// return — this only decides how what failed is reported.
    /// </remarks>
    internal static Exception? ShutdownFailure(IReadOnlyList<Exception> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return failures.Count == 0
            ? null
            : new InvalidOperationException(
                "Application shutdown completed its remaining cleanup, but one or more steps were unverified.",
                failures.Count == 1
                    ? failures[0]
                    : new AggregateException(
                        "Multiple application shutdown steps were unverified.", failures));
    }

    private void DisposeUiOwnedSessionResources()
    {
        lock (_configDebounceGate)
        {
            _configDebounce?.Dispose();
            _configDebounce = null;
        }
        _configWatcher?.Dispose();
        _configWatcher = null;
        _splash = null;
        MessageWindow? messageWindow = _messageWindow;
        if (messageWindow is not null)
        {
            messageWindow.SessionEnding -= OnSessionEnding;
            messageWindow.SessionLocked -= OnSessionLocked;
            messageWindow.SessionUnlocked -= OnSessionUnlocked;
            messageWindow.SystemSuspending -= OnSystemSuspending;
            messageWindow.SystemResumed -= OnSystemResumed;
        }
        _overlay?.Dispose();
        _overlay = null;
        _performanceOverlay?.Dispose();
        _performanceOverlay = null;
        _deviceOverlay?.Dispose();
        _deviceOverlay = null;
        _displayMute?.Dispose();
        _displayMute = null;
        _volumeButtons?.Dispose();
        _volumeButtons = null;
        _cardVolumes?.Dispose();
        _cardVolumes = null;
        _cardAcfWatcher?.Dispose();
        _cardAcfWatcher = null;
        _startupWatcher?.Dispose();
        _startupWatcher = null;
        if (_keepAwake is not null)
        {
            _keepAwake.DownloadActivityChanged -= OnDownloadActivityChanged;
            _keepAwake.Dispose();
            _keepAwake = null;
        }
        _monitor?.Dispose();
        _monitor = null;
        // Last: every service above deregisters its own native notification from this window.
        // Destroying the HWND first makes those orderly deregistrations race a dead handle.
        messageWindow?.Dispose();
        _messageWindow = null;
    }

    private void RetireTrayHostForShutdown()
    {
        // Every later cleanup is recoverable through process exit. Explorer restoration is not:
        // it must never run beside WSGM's Shell_TrayWnd and create two taskbar owners.
        _trayHost?.Dispose();
        _trayHost = null;
    }

    private async Task<bool> RestoreDesktopBeforeShutdownAsync(
        ApplicationShutdownReason reason,
        DateTimeOffset deadline)
    {
        ExplorerDesktopHost? desktopHost = _desktopHost;
        if (desktopHost is null || reason is ApplicationShutdownReason.SessionEnd)
        {
            return true;
        }

        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            Log.Warn("Application shutdown reached its deadline before Explorer desktop recovery.");
            return false;
        }

        try
        {
            // Reproduce the non-Explorer half of the ordinary desktop transition before the shell
            // appears. Update already asked Steam to exit so its mapped payload can be replaced;
            // never race that exit with a protocol URL that could start the client again.
            if (reason is not ApplicationShutdownReason.Update)
            {
                _modes?.ExitBigPicture();
            }
            DisplayScale.ApplyDesktopMode(_config);
        }
        catch (Exception ex)
        {
            // Explorer recovery is the higher-priority safety boundary. Program's final posture
            // cleanup gets another chance after Avalonia exits.
            Log.Error("Preparing desktop posture during application shutdown failed", ex);
        }

        remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            Log.Warn("Application shutdown reached its deadline before Explorer desktop recovery.");
            return false;
        }

        try
        {
            ExplorerDesktopResult result = await desktopHost.RestoreDesktopAsync(remaining)
                .ConfigureAwait(false);
            return result.Outcome is ExplorerDesktopOutcome.Normal
                or ExplorerDesktopOutcome.Degraded;
        }
        catch (Exception ex)
        {
            Log.Error("Application shutdown Explorer desktop recovery failed", ex);
            return false;
        }
    }

    private void ApplyDeviceConfig(AppConfig config)
    {
        DeviceCoordinator? coordinator = _deviceCoordinator;
        if (coordinator is null)
        {
            return;
        }

        // AutoTDP is applied before the coordinator: turning Device Integration off must stop
        // AutoTDP and restore the previous power limit while the capability is still writable.
        _autoTdp?.Apply(ShouldRunAutoTdp(config.DeviceIntegration));
        _ = ObserveDeviceConfigAsync(coordinator, config);
    }

    /// <summary>Applies the Device Integration master switch to AutoTDP at every entry point.</summary>
    internal static bool ShouldRunAutoTdp(DeviceIntegrationConfig config) =>
        config.Enabled && config.AutoTdpEnabled;

    private static bool GlyphsEnabled(AppConfig config) =>
        config.Cef.Enabled
        && config.DeviceIntegration.Enabled
        && config.DeviceIntegration.GlyphSelection is not DeviceGlyphSelection.NativeSteam;

    private void OnPhysicalGlyphProfilesChanged() => ApplyGlyphConfig(_config);

    private async Task ApplyRunningApplicationTargetAsync(
        RunningApplicationTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        _autoTdp?.ApplyRunningApplication(snapshot);
        if (_deviceCoordinator is { } coordinator)
        {
            await coordinator.ApplyRunningApplicationAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
            await ReconcileApplicationProfileAsync(snapshot.ApplicationId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restores the power limit and variable-refresh state the incoming application prefers, and takes
    /// back the ones the outgoing application imposed.
    /// </summary>
    /// <param name="applicationId">The canonical identity of the application now in front, or null.</param>
    /// <param name="cancellationToken">Cancels the device writes.</param>
    /// <remarks>
    /// The fix for a power limit or refresh mode set in a game leaking onto the desktop after the game
    /// closes. The per-game switch governs every performance value, so an application's own value
    /// applies only while its profile is enabled; otherwise it inherits the global one. When neither
    /// layer prefers a value, the outgoing application's is undone rather than left running — for
    /// power, automatic control resumes if it is on and the limit is otherwise released to the device
    /// ceiling; for variable refresh, it returns to off — but only when WSGM actually imposed the
    /// current one, so a session that never used the feature is never touched. Both decisions are pure
    /// and tested (<see cref="PerApplicationPowerPolicy"/>, <see cref="PerApplicationVrrPolicy"/>);
    /// this only reads the layers and carries them out.
    /// </remarks>
    private async Task ReconcileApplicationProfileAsync(
        string? applicationId,
        CancellationToken cancellationToken)
    {
        // The snapshot bumps when the foreground executable is enriched for the same running game;
        // a profile belongs to the application, not the focused window, so reconcile only when the
        // identity actually changes. A mid-game change reaches the device through the manual funnels,
        // not here.
        string identityKey = applicationId ?? string.Empty;
        if (string.Equals(identityKey, _lastReconciledApplicationId, StringComparison.Ordinal))
        {
            return;
        }

        if (_deviceCoordinator is not { } coordinator)
        {
            return;
        }

        DeviceCapabilityView? power = FindPowerLimitCapability();
        DeviceCapabilityView? vrr = FindVariableRefreshCapability();
        if (power is null && vrr is null)
        {
            // No manageable device value: nothing this transition can do. The identity is not
            // recorded, so if a plugin publishes a capability later a transition still reconciles.
            return;
        }

        _lastReconciledApplicationId = identityKey;

        PerformanceApplicationConfig? entry = applicationId is { Length: > 0 } id
            ? _config.Performance.Applications.Find(application => string.Equals(
                application.ApplicationId,
                id,
                StringComparison.Ordinal))
            : null;
        bool perGameActive = entry?.UsePerGameProfile ?? false;

        if (power is not null)
        {
            await ReconcileApplicationPowerLimitAsync(
                power,
                coordinator,
                entry,
                perGameActive,
                applicationId,
                cancellationToken).ConfigureAwait(false);
        }

        if (vrr is not null)
        {
            await ReconcileApplicationVariableRefreshAsync(
                entry,
                perGameActive,
                applicationId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileApplicationPowerLimitAsync(
        DeviceCapabilityView power,
        DeviceCoordinator coordinator,
        PerformanceApplicationConfig? entry,
        bool perGameActive,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        int? effective = PerApplicationPowerPolicy.ResolveEffective(
            _config.Performance.TdpWatts,
            entry?.TdpWatts,
            perGameActive);
        int ceiling = power.Descriptor.Maximum ?? 0;
        bool autoTdpEnabled = coordinator.AutoTdpEnabled;
        PerAppPowerDecision decision = PerApplicationPowerPolicy.DecideOnTargetChange(
            effective,
            _profilePowerImposed,
            autoTdpEnabled,
            ceiling);

        switch (decision.Action)
        {
            case PerAppPowerAction.Apply:
                if (await ApplyProfilePowerLimitAsync(power, decision.Watts, cancellationToken)
                    .ConfigureAwait(false))
                {
                    // An explicit limit overrides automatic control exactly as moving the slider
                    // does; pausing while it is applied keeps AutoTDP from writing over it next tick.
                    if (autoTdpEnabled)
                    {
                        _autoTdp?.NoteManualChange(decision.Watts);
                    }

                    _profilePowerImposed = true;
                    Log.Info(
                        $"Per-application power limit applied: {decision.Watts} W for "
                        + $"{applicationId ?? "the global profile"}.");
                }

                break;

            case PerAppPowerAction.ResumeAutomatic:
                _autoTdp?.ResumeAutomaticControl();
                _profilePowerImposed = false;
                Log.Info(
                    "Per-application power limit released; automatic control resumes for "
                    + $"{applicationId ?? "the global profile"}.");
                break;

            case PerAppPowerAction.ReleaseToCeiling:
                if (ceiling > 0
                    && await ApplyProfilePowerLimitAsync(power, ceiling, cancellationToken)
                        .ConfigureAwait(false))
                {
                    _profilePowerImposed = false;
                    Log.Info(
                        $"Per-application power limit released to the device ceiling {ceiling} W for "
                        + $"{applicationId ?? "the global profile"}.");
                }

                break;

            case PerAppPowerAction.Leave:
                break;
        }
    }

    private async Task ReconcileApplicationVariableRefreshAsync(
        PerformanceApplicationConfig? entry,
        bool perGameActive,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        bool? effective = PerApplicationVrrPolicy.ResolveEffective(
            _config.Performance.VariableRefreshRate,
            entry?.VariableRefreshRate,
            perGameActive);
        PerAppVrrDecision decision = PerApplicationVrrPolicy.DecideOnTargetChange(
            effective,
            _profileVrrImposed);
        if (decision.Action is not PerAppVrrAction.Apply)
        {
            return;
        }

        if (await ApplyVariableRefreshRateAsync(decision.Enabled, cancellationToken)
            .ConfigureAwait(false))
        {
            _profileVrrImposed = effective is not null;
            Log.Info(
                $"Per-application variable refresh {(decision.Enabled ? "enabled" : "disabled")} for "
                + $"{applicationId ?? "the global profile"}.");
        }
    }

    /// <summary>Persists a hand-set power limit to whichever profile layer is in force.</summary>
    /// <param name="watts">The limit the user just set, already applied to the device.</param>
    /// <remarks>
    /// Runs from the manual-power funnel, so the value has already reached the device and paused
    /// AutoTDP. This only records it as the user's preference for the running application's layer —
    /// its own when a per-game profile is enabled, the global layer otherwise — so the next launch
    /// restores it instead of the value leaking onto whatever runs next.
    /// </remarks>
    private void PersistManualPowerLimit(int watts)
    {
        string? applicationId = _performance?.Current.Target?.ApplicationId;
        PerformanceApplicationConfig? entry = applicationId is { Length: > 0 } id
            ? _config.Performance.Applications.Find(application => string.Equals(
                application.ApplicationId,
                id,
                StringComparison.Ordinal))
            : null;
        bool applicationLayer = entry is { UsePerGameProfile: true };
        int? current = applicationLayer ? entry!.TdpWatts : _config.Performance.TdpWatts;

        // The manual funnel fires on the value WSGM's own restore just wrote as well — its origin
        // keeps it out of here, but a value that already matches the layer is skipped regardless so
        // a drag that ends on the stored value writes no config.
        _profilePowerImposed = true;
        if (current == watts)
        {
            return;
        }

        ConfigStore.Mutate(config =>
        {
            if (applicationLayer)
            {
                PerformanceApplicationConfig? target = config.Performance.Applications.Find(
                    application => string.Equals(
                        application.ApplicationId,
                        applicationId,
                        StringComparison.Ordinal));
                if (target is not null)
                {
                    target.TdpWatts = watts;
                    return;
                }
            }

            config.Performance.TdpWatts = watts;
        });
        Log.Info(
            $"Power limit {watts} W saved to the "
            + (applicationLayer ? $"profile for {applicationId}." : "global profile."));
    }

    /// <summary>Applies a variable-refresh state the user set, and saves it to the layer in force.</summary>
    /// <param name="enabled">The state the user chose.</param>
    /// <param name="cancellationToken">Cancels the device write.</param>
    /// <returns>Whether the display is now in that state.</returns>
    /// <remarks>
    /// The user-facing counterpart to <see cref="ApplyVariableRefreshRateAsync"/>, which stays the
    /// bare device write the profile restore uses. This is what the native QAM's VRR control calls: it
    /// applies the state and, only if the device took it, records it as the running application's
    /// preference — its own layer when a per-game profile is enabled, the global layer otherwise — so
    /// it is restored on the next launch instead of leaking onto whatever runs next.
    /// </remarks>
    private async Task<bool> SetVariableRefreshRateFromUserAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!await ApplyVariableRefreshRateAsync(enabled, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        string? applicationId = _performance?.Current.Target?.ApplicationId;
        PerformanceApplicationConfig? entry = applicationId is { Length: > 0 } id
            ? _config.Performance.Applications.Find(application => string.Equals(
                application.ApplicationId,
                id,
                StringComparison.Ordinal))
            : null;
        bool applicationLayer = entry is { UsePerGameProfile: true };
        bool? current = applicationLayer ? entry!.VariableRefreshRate : _config.Performance.VariableRefreshRate;

        _profileVrrImposed = true;
        if (current == enabled)
        {
            return true;
        }

        ConfigStore.Mutate(config =>
        {
            if (applicationLayer)
            {
                PerformanceApplicationConfig? target = config.Performance.Applications.Find(
                    application => string.Equals(
                        application.ApplicationId,
                        applicationId,
                        StringComparison.Ordinal));
                if (target is not null)
                {
                    target.VariableRefreshRate = enabled;
                    return;
                }
            }

            config.Performance.VariableRefreshRate = enabled;
        });
        Log.Info(
            $"Variable refresh {(enabled ? "on" : "off")} saved to the "
            + (applicationLayer ? $"profile for {applicationId}." : "global profile."));
        return true;
    }

    private DeviceCapabilityView? FindPowerLimitCapability() =>
        _deviceCoordinator?.Capabilities.Snapshot().FirstOrDefault(view =>
            view.Descriptor.Role is CapabilityRole.PowerSustainedLimit
            && view.Descriptor.SupportsWrite
            && view.Descriptor.ValueKind is CapabilityValueKind.Integer);

    private DeviceCapabilityView? FindVariableRefreshCapability() =>
        _deviceCoordinator?.Capabilities.Snapshot().FirstOrDefault(view =>
            view.Descriptor.Role is CapabilityRole.VariableRefreshRate
            && view.Descriptor.SupportsWrite);

    private async Task<bool> ApplyProfilePowerLimitAsync(
        DeviceCapabilityView power,
        int watts,
        CancellationToken cancellationToken)
    {
        if (_deviceCoordinator is not { } coordinator)
        {
            return false;
        }

        CapabilityCommandResult result = await coordinator.ExecuteCapabilityAsync(
            power.Descriptor.CapabilityId,
            power.Descriptor.InstanceId,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = watts },
            TimeSpan.FromSeconds(5),
            // Not a user action: the value is already the saved preference, so it must not re-enter
            // the manual funnel and be persisted again or re-resolved into the wrong layer.
            CapabilityCommandOrigin.ProfileRestore,
            cancellationToken).ConfigureAwait(false);
        bool applied = result.Outcome
            is CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified;
        if (!applied)
        {
            Log.Warn(
                $"Per-application power limit {watts} W was not applied: "
                + (result.Reason?.Detail ?? result.Outcome.ToString()));
        }

        return applied;
    }

    /// <summary>
    /// The deadline AutoTDP judges frame delivery against.
    /// </summary>
    /// <remarks>
    /// The applied RTSS frame limit when there is one, because that is the rate the user asked for
    /// and delivering it is the whole goal. Without a limit the deadline falls back to 60 Hz rather
    /// than to the panel's maximum: chasing an uncapped refresh rate would push the power limit up
    /// for as long as the game could absorb it, which is the opposite of what AutoTDP is for.
    /// </remarks>
    private double TargetFrametimeMs()
    {
        PerformanceState? state = _performance?.Current;
        int limit = state?.Observed.FrameLimit ?? 0;
        if (limit <= 0)
        {
            limit = state?.Desired.FrameLimit ?? 0;
        }

        return limit > 0 ? 1000d / limit : 1000d / 60d;
    }

    /// <summary>
    /// Applies both halves of physical glyph presentation: whether it is on, and what to draw.
    /// </summary>
    /// <remarks>
    /// The selector alone changes nothing a user can see. Without the resolved profile the
    /// stylesheet has no rules, and the patch refuses to install an empty one — which is how
    /// physical glyphs were inert.
    /// </remarks>
    private void ApplyGlyphConfig(AppConfig config)
    {
        SteamUiSessionHost? steamUi = _steamUi;
        if (steamUi is null)
        {
            return;
        }

        bool enabled = GlyphsEnabled(config);
        steamUi.ApplyGlyphs(
            enabled,
            enabled ? _deviceCoordinator?.PhysicalGlyphSelectionSnapshot().Profile : null);
    }

    private void ApplyPerformanceConfig(AppConfig config)
    {
        PerformanceService? performance = _performance;
        if (performance is null)
        {
            return;
        }

        _refreshPairing?.SetStrategy(config.Performance.FrameLimitStrategy);
        performance.ApplyOsdCustomization(RtssOsdCustomSettings.FromConfig(config.Performance));
        _ = ObservePerformanceConfigAsync(
            performance,
            BuildPerformancePolicy(config, forceEnabled: _overlayTestOnly));
    }

    /// <remarks>
    /// Runs off the state event rather than inside <see cref="PerformanceService"/>, because that
    /// service owns RTSS profiles and this changes a display mode — two different pieces of hardware
    /// with different failure modes and different restore obligations.
    /// <para>
    /// Only an actual change is acted on. The state event fires on every poll, and re-applying the
    /// same mode repeatedly would put a driver round trip on a two-second timer forever.
    /// </para>
    /// </remarks>
    private void OnPerformanceStateForPairing(PerformanceState state)
    {
        if (_refreshPairing is not { } pairing)
        {
            return;
        }

        int limit = state.Desired.FrameLimit ?? 0;
        if (limit == _pairedFrameLimit)
        {
            return;
        }

        _pairedFrameLimit = limit;

        // Uncapped hands the display back: there is no cadence left to pair against, and holding a
        // reduced refresh rate after the cap is gone would cap frames by the back door.
        if (limit <= 0)
        {
            _ = pairing.Restore();
            return;
        }

        _ = pairing.ApplyForCap(limit);
    }

    private static async Task ObservePerformanceConfigAsync(
        PerformanceService performance,
        PerformancePolicy policy)
    {
        try
        {
            await performance.UpdatePolicyAsync(policy).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("RTSS performance config apply failed", ex);
        }
    }

    private static PerformancePolicy BuildPerformancePolicy(
        AppConfig config,
        bool forceEnabled)
    {
        List<PerformanceApplicationPolicy> applications = [];
        foreach (PerformanceApplicationConfig application in config.Performance.Applications)
        {
            if (!application.UsePerGameProfile)
            {
                continue;
            }

            applications.Add(new PerformanceApplicationPolicy(
                application.ApplicationId,
                application.RtssProfileName,
                new PerformanceValues(application.FrameLimit, application.OverlayLevel)));
        }

        return new PerformancePolicy(
            new PerformanceValues(
                config.Performance.FrameLimit,
                config.Performance.OverlayLevel),
            applications,
            forceEnabled || config.Performance.Enabled);
    }

    private static Task PersistPerformancePolicyAsync(
        PerformancePolicy policy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConfigStore.Mutate(config => MergePerformancePolicy(config.Performance, policy));
        return Task.CompletedTask;
    }

    private static async Task ObserveUiCaptureClaimAsync(
        DeviceCoordinator coordinator,
        string surfaceId)
    {
        try
        {
            await coordinator.ClaimUiAsync(surfaceId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"Managed controller capture failed for {surfaceId}", ex);
        }
    }

    private PluginSettingsScope? ActivePluginScope(Func<PluginSettingsScope, bool> predicate)
    {
        string? device = _deviceCoordinator?.ActiveDeviceDefinitionId;
        string? plugin = _deviceCoordinator?.InstalledPackage?.Manifest?.Id;
        if (device is null || plugin is null)
        {
            return null;
        }

        return _config.DeviceIntegration.PluginSettings.LastOrDefault(candidate =>
            string.Equals(candidate.DeviceDefinitionId, device, StringComparison.Ordinal)
            && string.Equals(candidate.PluginId, plugin, StringComparison.Ordinal)
            && predicate(candidate));
    }

    internal static void MergePerformancePolicy(
        PerformanceConfig destination,
        PerformancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(policy);
        Dictionary<string, PerformanceApplicationConfig> existing = destination.Applications
            .GroupBy(application => application.ApplicationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        destination.Enabled = policy.Enabled;
        destination.FrameLimit = policy.Global.FrameLimit;
        destination.OverlayLevel = policy.Global.OverlayLevel;
        List<PerformanceApplicationConfig> disabled = existing.Values
            .Where(application => !policy.Applications.Any(active => string.Equals(
                active.ApplicationId,
                application.ApplicationId,
                StringComparison.Ordinal)))
            .Select(application => new PerformanceApplicationConfig
            {
                ApplicationId = application.ApplicationId,
                RtssProfileName = application.RtssProfileName,
                UsePerGameProfile = false,
                FrameLimit = application.FrameLimit,
                OverlayLevel = application.OverlayLevel,
                TdpWatts = application.TdpWatts,
                VariableRefreshRate = application.VariableRefreshRate,
            })
            .ToList();
        destination.Applications.Clear();
        foreach (PerformanceApplicationPolicy application in policy.Applications)
        {
            existing.TryGetValue(application.ApplicationId, out PerformanceApplicationConfig? prior);
            destination.Applications.Add(new PerformanceApplicationConfig
            {
                ApplicationId = application.ApplicationId,
                RtssProfileName = application.RtssProfileName,
                FrameLimit = application.Values.FrameLimit,
                OverlayLevel = application.Values.OverlayLevel,
                UsePerGameProfile = true,
                TdpWatts = prior?.TdpWatts,
                VariableRefreshRate = prior?.VariableRefreshRate,
            });
        }
        destination.Applications.AddRange(disabled);
    }

    private static Task PersistSimulatedPerformancePolicyAsync(
        PerformancePolicy policy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static async Task ObserveDeviceConfigAsync(
        DeviceCoordinator coordinator,
        AppConfig config)
    {
        try
        {
            await coordinator.ApplyConfigAsync(config).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Device cycle config apply failed", ex);
        }
    }

    private async Task LaunchAppsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var haveApps = _config.StartupApps.Exists(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Path));
        if (haveApps && _config.StartupDelayMs > 0)
        {
            Log.Info($"Waiting {_config.StartupDelayMs} ms before the first startup app (boot settle).");
            await Task.Delay(_config.StartupDelayMs, cancellationToken);
        }

        foreach (var app in _config.StartupApps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!app.Enabled || string.IsNullOrWhiteSpace(app.Path))
            {
                continue;
            }
            // Explorer processed Run keys/Startup folder during the takeover's
            // settle window — tools registered in both places must not launch twice.
            if (_tookOverFromExplorer && IsAppAlreadyRunning(app.Path))
            {
                Log.Info($"Startup app already running (explorer autostart) — skipping: {app.Path}");
                continue;
            }
            Log.Info($"Starting startup app: {app.Path} {app.Args}{(app.Elevated ? " (elevated)" : "")}");
            AppLauncher.Start(app.Path, app.Args, app.Elevated);
            await Task.Delay(Math.Max(0, _config.StaggerDelayMs), cancellationToken);
        }

        if (_config.SteamDelayMs > 0)
        {
            await Task.Delay(_config.SteamDelayMs, cancellationToken);
        }

        // The splash's Switch-to-desktop (or the overlay's) may have fired while
        // this sequence was still sleeping — EnterDesktopMode paused the monitor,
        // and starting Big Picture now would slam it over the fresh desktop.
        if (_monitor is { Paused: true })
        {
            Log.Info("Skipping Steam start: desktop mode was requested during boot.");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Shared start + warning flow (also behind the overlay's Steam button);
        // boot surfaces failures itself because this runs off the UI thread.
        // (steam://open/bigpicture adopts a Steam that explorer's own autostart
        // already brought up, so no duplicate check is needed for Steam itself.)
        var warning = _modes!.StartBigPicture();
        if (warning is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_shutdownRequested)
                {
                    return;
                }
                _splash?.Dismiss("Steam start warning");
                _overlay?.SetWarning(warning);
                _overlay?.ShowOverlay();
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Inject the WSGM library tabs once Steam's UI has loaded, so they appear at
        // boot without the user opening the overlay. Fire-and-forget; self-limiting.
        _ = RunTabBootSyncAsync(_tabBootSyncCancellation);

    }

    private static async Task TrimAfterBootSettlesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            MemoryTrim.TrimBestEffort("boot settled");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application teardown deliberately suppresses the post-boot trim.
        }
    }
}

/// <summary>Outcome of the service-boot Explorer takeover phase.</summary>
internal enum BootTakeoverResult
{
    /// <summary>Explorer exited safely and game-mode shell resources were created.</summary>
    EnteredGameMode,
    /// <summary>The original desktop stayed intact and only the boot cover must be removed.</summary>
    DesktopPreserved,
    /// <summary>The exit boundary is uncertain and the verified desktop restoration must run.</summary>
    DesktopRestoreRequired,
}
