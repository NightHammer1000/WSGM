using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
using StatePublication = SteamUiToolkit.SteamUiStatePublication;

namespace WSGM.Shell;

/// <summary>
/// Owns the narrow bridge and registered patches over the injected process-long Steam UI transport.
/// </summary>
/// <remarks>
/// Session lifetime only: which patches are applied when, generation changes, synchronization, and
/// publication gating. Each surface's handlers, readers and timers live with the service that owns
/// its backend.
/// </remarks>
internal sealed class SteamUiSessionHost : IAsyncDisposable
{
    private const string ShellPatchId = "wsgm.native-qam.shell";
    private readonly ISteamUiTransport _transport;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _synchronizeSignal = new(0, 1);
    private readonly object _observationGate = new();
    private readonly SteamUiModuleSet _modules;
    private readonly SteamUiModuleRuntime _runtime;
    private readonly Func<CancellationToken, Task<bool>> _toggleQuickAccess;
    private readonly DeviceCoordinatorNativeQamTdpService _tdp;
    private readonly DeviceCoordinatorNativeQamDeviceControlsService _deviceControls;
    private readonly DeviceCoordinatorNativeQamAutoTdpService _autoTdp;
    private readonly DeviceCoordinatorNativeQamControllerTargetService _controllerTarget;
    private readonly NativeQamBrightnessService _brightness;

    /// <summary>
    /// Null when no audio manager exists for this session, which is the overlay-test case.
    /// </summary>
    /// <remarks>
    /// Unlike the semantic services above there is no "unavailable" stand-in, because audio is
    /// supplied as a namespace rather than drawn as a row: with nothing to supply, the right
    /// behaviour is to leave the namespace absent so Steam's own store stays unavailable, not to
    /// install one that answers with nothing.
    /// </remarks>
    private readonly AudioManagerNativeQamAudioService? _audio;

    /// <summary>The Wi-Fi surface, or null when this session has no radio manager.</summary>
    private readonly NativeQamNetworkService? _network;

    /// <summary>The Bluetooth surface, riding the same radio-manager condition.</summary>
    private readonly NativeQamBluetoothService? _bluetooth;

    private readonly PerformanceService _performanceService;
    private readonly PerformanceServiceNativeQamAdapter _performance;
    private readonly Action<PerformanceState> _onPerformanceStateChanged;

    /// <summary>
    /// The display-resolution row's backend, or null when this session must not move the display.
    /// </summary>
    /// <remarks>
    /// Null in overlay-test, which runs without a real display to change. The patch is not
    /// registered at all in that case, so the row cannot appear and offer a control with nothing
    /// behind it.
    /// </remarks>
    private readonly NativeQamResolutionService? _resolution;
    private readonly SteamInputGlyphDeliveryState _glyphDeliveryState = new();
    private readonly SteamUiBridgeHost _bridge;
    private readonly SteamUiPatchManager _patches;
    private readonly Task _synchronization;
    private int _signalPending;
    private IDisposable? _performanceObservation;
    private volatile bool _enabled;
    private volatile bool _networkIndicatorEnabled;
    private volatile bool _downloadSortEnabled;
    private volatile bool _glyphsEnabled;
    private volatile bool _glyphDeliveryEnabled;
    private volatile bool _disposed;

    /// <summary>Creates the host and its surface services.</summary>
    /// <param name="transport">The one process-long Steam UI transport.</param>
    /// <param name="toggleQuickAccess">Opens or closes WSGM's overlay.</param>
    /// <param name="deviceCoordinator">The device platform, or null when integration is off.</param>
    /// <param name="performance">The RTSS-backed performance service.</param>
    /// <param name="audio">The session's audio manager, or null in overlay-test.</param>
    /// <param name="radios">The session's radio manager, borrowed, or null in overlay-test.</param>
    /// <param name="resolution">The display-resolution backend, or null.</param>
    /// <param name="autoTdp">The session's AutoTDP service, or null when it is not running.</param>
    /// <param name="perfSupport">
    /// What the device can back, for the reactivated performance panel. Supplied by the session
    /// because the frame-limit options come from display-mode discovery and the VRR flag from the
    /// device plugin, and this host owns neither. Null hides every performance control, which is
    /// the correct state for a session that cannot yet say what it can honour.
    /// </param>
    /// <param name="applyRefreshRate">Applies a manually chosen refresh rate, or null.</param>
    /// <param name="applyVariableRefreshRate">Applies the VRR flag, or null.</param>
    internal SteamUiSessionHost(
        ISteamUiTransport transport,
        Func<CancellationToken, Task<bool>> toggleQuickAccess,
        DeviceCoordinator? deviceCoordinator,
        PerformanceService performance,
        AudioManager? audio = null,
        RadioManager? radios = null,
        DisplayResolutionService? resolution = null,
        AutoTdpService? autoTdp = null,
        Func<NativeQamPerfSupport>? perfSupport = null,
        Func<int, bool>? applyRefreshRate = null,
        Func<bool, CancellationToken, Task<bool>>? applyVariableRefreshRate = null)
    {
        _resolution = resolution is null ? null : new NativeQamResolutionService(resolution);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(toggleQuickAccess);
        _toggleQuickAccess = toggleQuickAccess;
        _tdp = new DeviceCoordinatorNativeQamTdpService(deviceCoordinator);
        _deviceControls = new DeviceCoordinatorNativeQamDeviceControlsService(deviceCoordinator);
        _performanceService = performance;
        _performance = new PerformanceServiceNativeQamAdapter(performance)
        {
            PerfSupport = perfSupport,
            ApplyRefreshRate = applyRefreshRate,
            ApplyVariableRefreshRate = applyVariableRefreshRate,
        };
        _autoTdp = new DeviceCoordinatorNativeQamAutoTdpService(deviceCoordinator, autoTdp);
        _controllerTarget = new DeviceCoordinatorNativeQamControllerTargetService(deviceCoordinator);
        _audio = audio is null ? null : new AudioManagerNativeQamAudioService(audio);
        _network = radios is null
            ? null
            : new NativeQamNetworkService(
                radios,
                () => !_disposed && _networkIndicatorEnabled,
                QueueStatePublication);
        _bluetooth = radios is null ? null : new NativeQamBluetoothService(radios);
        _brightness = new NativeQamBrightnessService(
            () => !_disposed && _enabled,
            QueueStatePublication);
        _modules = new SteamUiModuleSet(CreateModules());
        // WSGM's own asset and module-derived vocabulary, named here rather than reached for from
        // inside the bridge. The toolkit has no WSGM patch ids of its own.
        _bridge = new SteamUiBridgeHost(
            _transport,
            new SteamUiInjectedAsset(
                SteamUiAssetCatalog.LoadNativeQamBootstrap(),
                SteamUiAssetCatalog.NativeQamBootstrapSha256),
            _modules.AllowedCommands);
        _patches = new SteamUiPatchManager(_transport);
        _patches.Register(new NativeQamBootstrapPatch(_bridge));
        _modules.RegisterPatches(_patches);
        SetPatchStates(bootstrap: false, components: false);
        SetGlyphDeliveryPatchStates();
        _patches.SetGlobalEnabled(false);
        // Traffic in both directions is the runtime's; which patches are applied when stays here,
        // because that is this application's policy and not a general rule.
        _runtime = new SteamUiModuleRuntime(
            _bridge,
            _modules,
            commandsEnabled: () => _enabled,
            publishEnabled: () => _enabled || _networkIndicatorEnabled);
        _transport.GenerationChanged += OnGenerationChanged;
        _tdp.StateChanged += OnSemanticStateChanged;
        _deviceControls.StateChanged += OnSemanticStateChanged;
        _autoTdp.StateChanged += OnSemanticStateChanged;
        _onPerformanceStateChanged = _ => QueueStatePublication();
        _performanceService.StateChanged += _onPerformanceStateChanged;
        _controllerTarget.StateChanged += OnSemanticStateChanged;
        if (_audio is not null)
        {
            _audio.StateChanged += OnSemanticStateChanged;
        }

        _synchronization = Task.Run(SynchronizeLoopAsync);
    }

    internal void Apply(bool enabled)
    {
        if (_disposed || _enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        if (enabled)
        {
            _patches.SetGlobalEnabled(true);
            SetPatchStates(bootstrap: true, components: true);
        }
        else
        {
            CancelAllInflightRequests();
            ReleasePerformanceObservation();
            SetPatchStates(bootstrap: _networkIndicatorEnabled, components: false);
        }
        QueueSynchronization();
    }

    /// <summary>Feeds Steam's header and Internet page through the registered network gate.</summary>
    /// <param name="enabled">Whether the game-mode Wi-Fi projection is active.</param>
    internal void ApplyNetworkIndicator(bool enabled)
    {
        if (_disposed || _networkIndicatorEnabled == enabled)
        {
            return;
        }

        _networkIndicatorEnabled = enabled;
        if (enabled)
        {
            _patches.SetGlobalEnabled(true);
        }
        else if (!_enabled && _network is { } network)
        {
            network.PostStopScanning();
        }
        SetPatchStates(bootstrap: _enabled || enabled, components: _enabled);
        QueueSynchronization();
        QueueStatePublication();
    }

    /// <summary>Applies download-queue sorting through the shared patch lifecycle.</summary>
    /// <param name="enabled">Whether the MainWindow wrapper should be installed.</param>
    internal void ApplyDownloadSort(bool enabled)
    {
        if (_disposed || _downloadSortEnabled == enabled)
        {
            return;
        }

        _downloadSortEnabled = enabled;
        if (enabled)
        {
            _patches.SetGlobalEnabled(true);
        }
        SetPatchStates(bootstrap: _enabled || _networkIndicatorEnabled, components: _enabled);
        QueueSynchronization();
    }

    /// <summary>Returns the immutable patch-registry view used by diagnostics and isolated tests.</summary>
    internal IReadOnlyList<SteamUiPatchSnapshot> GetPatchSnapshots() => _patches.GetSnapshots();

    /// <summary>
    /// Applies handheld glyph presentation: whether it is on, and what to draw.
    /// </summary>
    /// <param name="enabled">Whether WSGM presents handheld glyphs at all.</param>
    /// <param name="profile">The resolved plugin profile, or null for native Steam glyphs.</param>
    /// <remarks>
    /// One call because there is one thing to install. The profile is the plugin's and is the only
    /// source of artwork; WSGM turns it into a stylesheet. Either switch off, or a profile that
    /// supplies nothing to draw, removes WSGM's stylesheet and leaves native Valve glyphs in place.
    /// </remarks>
    internal void ApplyGlyphs(bool enabled, ImportedGlyphProfile? profile)
    {
        if (_disposed)
        {
            return;
        }

        _glyphsEnabled = enabled;
        _glyphDeliveryState.Update(enabled ? profile : null);
        SetGlyphDeliveryPatchStates();
        if (_glyphDeliveryEnabled)
        {
            _patches.SetGlobalEnabled(true);
        }

        QueueSynchronization();
    }

    internal async Task DisableAsync()
    {
        if (_disposed)
        {
            return;
        }

        _enabled = false;
        _networkIndicatorEnabled = false;
        _downloadSortEnabled = false;
        _glyphsEnabled = false;
        CancelAllInflightRequests();
        ReleasePerformanceObservation();
        if (_network is { } network)
        {
            await network.StopScanningAsync().ConfigureAwait(false);
        }
        SetPatchStates(bootstrap: true, components: false);
        SetGlyphDeliveryPatchStates();
        await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
        _glyphDeliveryState.Update(null);
        SetPatchStates(bootstrap: false, components: false);
        _patches.SetGlobalEnabled(false);
        await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
    }

    private void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
    {
        if (snapshot.Role == SteamUiTargetRole.SharedJsContext)
        {
            // A semantic operation is authorized against one execution-context/document pair.
            // Letting it continue after either generation moved could apply a result for a page
            // that can no longer receive its response, so replacement is cancellation just like
            // an explicit bridge cancel.
            CancelAllInflightRequests();
            ReleasePerformanceObservation();
        }

        // The patch manager marks patches for every changed target role, so every role change must
        // queue synchronization.
        if (_enabled
            || _networkIndicatorEnabled
            || _downloadSortEnabled
            || _glyphsEnabled
            || _glyphDeliveryEnabled)
        {
            QueueSynchronization();
        }
    }

    private void QueueSynchronization()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _signalPending, 1) == 0)
        {
            _synchronizeSignal.Release();
        }
    }

    private async Task SynchronizeLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _synchronizeSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _signalPending, 0);
                await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
                if (_enabled || _networkIndicatorEnabled)
                {
                    if (_enabled)
                    {
                        UpdatePerformanceObservation();
                    }
                    else
                    {
                        ReleasePerformanceObservation();
                    }
                    QueueStatePublication();
                }
                else
                {
                    ReleasePerformanceObservation();
                    SetPatchStates(bootstrap: false, components: false);
                    _patches.SetGlobalEnabled(
                        _downloadSortEnabled || _glyphsEnabled || _glyphDeliveryEnabled);
                    await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam UI patch synchronization failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Every Steam UI surface this session offers, one declaration each: the patches that install
    /// it, the state it publishes, and the commands it answers.
    /// </summary>
    /// <remarks>
    /// A surface used to be four separate edits in this file — a registration, a publication row, a
    /// command row and an id constant — which is how one could be added with a control that
    /// rendered and did nothing. Here the whole surface is one entry, and a module whose backend is
    /// absent is simply not declared.
    /// </remarks>
    private IReadOnlyList<ISteamUiModule> CreateModules()
    {
        List<ISteamUiModule> modules =
        [
            new SteamUiModule(
                "shell",
                commands: [new(ShellPatchId, "toggleQuickAccess", HandleToggleQuickAccessAsync)]),

            // The SteamOS Manager RPC seam and the rows it reveals: the gate supplies the answer
            // and watches the client settings Valve's rows write; both halves share the
            // wsgm.native-qam.tdp id and its published state. See SteamGatePatches.SteamOsManager.
            new SteamUiModule(
                "tdp",
                patches: [SteamGatePatches.SteamOsManager, NativeQamComponentPatches.ValveTdp],
                publications: [Publish(
                    SteamGatePatches.SteamOsManager.Id,
                    () => JsonSerializer.SerializeToElement(
                        _tdp.Current,
                        NativeQamSemanticJsonContext.Default.NativeQamTdpState))],
                commands:
                [
                    new(
                        SteamGatePatches.SteamOsManager.Id,
                        "setPrimaryLimit",
                        _tdp.HandleSetPrimaryLimitAsync),
                ]),

            new SteamUiModule(
                "auto-tdp",
                patches: [NativeQamComponentPatches.AutoTdp],
                publications: [Publish(
                    NativeQamComponentPatches.AutoTdp.Id,
                    () => JsonSerializer.SerializeToElement(
                        _autoTdp.Current,
                        NativeQamSemanticJsonContext.Default.NativeQamAutoTdpState))],
                commands:
                [
                    new(
                        NativeQamComponentPatches.AutoTdp.Id,
                        "setAutoTdp",
                        _autoTdp.HandleSetAutoTdpAsync),
                ]),

            // The frame limit is WSGM's own row, deliberately, and the Q12 retirement does not
            // apply here: Valve's component is a notch slider fed by fps_limit_options, and a free
            // 30-120 range made it unusable. The unified-row shape it takes instead is on
            // NativeQamFrameLimitState's remarks.
            new SteamUiModule(
                "frame-limit",
                patches: [NativeQamComponentPatches.FrameLimit],
                publications: [Publish(
                    NativeQamComponentPatches.FrameLimit.Id,
                    () => JsonSerializer.SerializeToElement(
                        _performance.FrameLimit,
                        NativeQamSemanticJsonContext.Default.NativeQamFrameLimitState))],
                commands:
                [
                    new(
                        NativeQamComponentPatches.FrameLimit.Id,
                        "setFrameLimit",
                        _performance.HandleFrameLimitAsync),
                    new(
                        NativeQamComponentPatches.FrameLimit.Id,
                        "setRefreshRate",
                        _performance.HandleRefreshRateAsync),
                ]),

            // The overlay level stays Valve's: it is genuinely five discrete levels.
            new SteamUiModule(
                "overlay-level",
                patches: [NativeQamComponentPatches.ValveOverlayLevel]),

            new SteamUiModule(
                "controller-target",
                patches: [NativeQamComponentPatches.ControllerTarget],
                publications:
                [
                    Publish(
                        NativeQamComponentPatches.ControllerTarget.Id,
                        () => JsonSerializer.SerializeToElement(
                            _controllerTarget.Current,
                            NativeQamSemanticJsonContext.Default.NativeQamControllerTargetState)),
                ],
                commands:
                [
                    new(
                        NativeQamComponentPatches.ControllerTarget.Id,
                        "setControllerTarget",
                        _controllerTarget.HandleSetControllerTargetAsync),
                ]),

            // WSGM's own VRR switch, not Valve's. Valve's component is gated on a react-query over
            // SteamClient.System.DisplayManager, which this client does not define: the query never
            // succeeds and the component returns null before it reads anything WSGM publishes, so
            // the row was simply absent. Declared unconditionally — whether it appears is decided
            // by whether the device publishes a variable-refresh capability, which the state
            // carries.
            new SteamUiModule(
                "vrr",
                patches:
                [
                    NativeQamComponentPatches.Vrr,
                    NativeQamComponentPatches.ValveProfileHeader,
                    NativeQamComponentPatches.ValveReset,
                    NativeQamComponentPatches.ValveRefreshRate,
                ],
                publications: [Publish(
                    NativeQamComponentPatches.Vrr.Id,
                    () => JsonSerializer.SerializeToElement(
                        _performance.Vrr,
                        NativeQamSemanticJsonContext.Default.NativeQamVrrState))],
                commands:
                [
                    new(
                        NativeQamComponentPatches.Vrr.Id,
                        "setVariableRefreshRate",
                        _performance.HandleVrrAsync),
                ]),

            // The backend behind Valve's own Performance tab. Declared unconditionally because the
            // performance service always exists; what the panel then shows is decided entirely by
            // which fields the projected state carry, not by whether this patch installed.
            new SteamUiModule(
                "perf",
                patches: [SteamGatePatches.Perf],
                publications: [Publish(
                    SteamGatePatches.Perf.Id,
                    () => JsonSerializer.SerializeToElement(
                        _performance.PerfState,
                        NativeQamSemanticJsonContext.Default.NativeQamPerfState))],
                commands:
                [
                    new(
                        SteamGatePatches.Perf.Id,
                        "updateSettings",
                        _performance.HandlePerformanceDeltaAsync),
                ]),

            // No backend of WSGM's behind it: Steam's own brightness backend already works on
            // Windows, and only its availability flag says otherwise. Declared unconditionally for
            // that reason — it depends on nothing WSGM has to supply.
            new SteamUiModule(
                "brightness",
                patches: [SteamGatePatches.Brightness],
                publications:
                [
                    new(
                        SteamGatePatches.Brightness.Id,
                        () => _enabled,
                        NativeQamBrightnessService.ReadPublication),
                ],
                commands:
                [
                    new(
                        SteamGatePatches.Brightness.Id,
                        "setBrightness",
                        _brightness.HandleSetBrightnessAsync),
                ]),

            new SteamUiModule(
                "device-controls",
                patches: [NativeQamComponentPatches.DeviceControls],
                publications: [Publish(
                    NativeQamComponentPatches.DeviceControls.Id,
                    () => JsonSerializer.SerializeToElement(
                        _deviceControls.Current,
                        NativeQamSemanticJsonContext.Default.NativeQamDeviceControlsState))],
                commands:
                [
                    new(
                        NativeQamComponentPatches.DeviceControls.Id,
                        "setChargeLimit",
                        _deviceControls.HandleSetChargeLimitAsync),
                    new(
                        NativeQamComponentPatches.DeviceControls.Id,
                        "setLightingBrightness",
                        _deviceControls.HandleSetLightingBrightnessAsync),
                    new(
                        NativeQamComponentPatches.DeviceControls.Id,
                        "setLightingColor",
                        _deviceControls.HandleSetLightingColorAsync),
                ]),

            new SteamUiModule("download-sort", patches: [new SteamDownloadSortPatch()]),

            new SteamUiModule(
                "glyph-style",
                patches: [new SteamInputGlyphStylePatch(_glyphDeliveryState)]),
        ];

        if (_resolution is { } resolution)
        {
            modules.Add(new SteamUiModule(
                "resolution",
                patches: [NativeQamComponentPatches.Resolution],
                publications: [Publish(
                    NativeQamComponentPatches.Resolution.Id,
                    () => JsonSerializer.SerializeToElement(
                        resolution.Current,
                        NativeQamSemanticJsonContext.Default.NativeQamResolutionState))],
                commands:
                [
                    new(
                        NativeQamComponentPatches.Resolution.Id,
                        "setResolution",
                        resolution.HandleSetResolutionAsync),
                ]));
        }

        if (_audio is { } audio)
        {
            // Publishing once after injection updates the store whose availability was cached when
            // Steam started before the replacement namespace existed.
            modules.Add(new SteamUiModule(
                "audio",
                patches: [SteamGatePatches.Audio],
                publications:
                [
                    Publish(
                        SteamGatePatches.Audio.Id,
                        () => AudioManagerNativeQamAudioService.SerializeState(audio.Current)),
                ],
                commands:
                [
                    new(SteamGatePatches.Audio.Id, "getDevices", audio.HandleGetDevicesAsync),
                    new(
                        SteamGatePatches.Audio.Id,
                        "setDefaultDevice",
                        audio.HandleSetDefaultDeviceAsync),
                    new(SteamGatePatches.Audio.Id, "setVolume", audio.HandleSetVolumeAsync),
                ]));
        }

        // The gate reveals Steam's Wi-Fi surface, and the surface is only worth revealing if
        // something can populate it — which is the radio manager. Bluetooth rides the same
        // condition for the same reason.
        if (_network is { } network)
        {
            modules.Add(new SteamUiModule(
                "network",
                patches: [SteamGatePatches.Network],
                publications:
                [
                    // The one publication not gated on _enabled alone: the header Wi-Fi indicator
                    // is shown on the desktop side too, where the rest of the QAM is not.
                    new(SteamGatePatches.Network.Id,
                        () => _enabled || _networkIndicatorEnabled,
                        async () => JsonSerializer.SerializeToElement(
                            await network.ReadStateAsync(_networkIndicatorEnabled)
                                .ConfigureAwait(false),
                            NativeQamSemanticJsonContext.Default.SteamNetworkState)),
                ],
                commands:
                [
                    new(SteamGatePatches.Network.Id, "startScan", network.HandleScanStartAsync),
                    new(SteamGatePatches.Network.Id, "stopScan", network.HandleScanStopAsync),
                ]));
        }

        if (_bluetooth is { } bluetooth)
        {
            modules.Add(new SteamUiModule(
                "bluetooth",
                patches: [SteamGatePatches.Bluetooth],
                publications:
                [
                    new(SteamGatePatches.Bluetooth.Id, () => _enabled, async () =>
                        JsonSerializer.SerializeToElement(
                            await bluetooth.ReadStateAsync().ConfigureAwait(false),
                            NativeQamSemanticJsonContext.Default.SteamBluetoothState)),
                ],
                commands:
                [
                    .. NativeQamBluetoothService.Commands.Select(command =>
                        new SteamUiCommandHandler(
                            SteamGatePatches.Bluetooth.Id,
                            command,
                            bluetooth.HandleAsync)),
                ]));
        }

        return modules;
    }

    /// <summary>Publishes a value that is always readable while the session is enabled.</summary>
    private StatePublication Publish(string patchId, Func<JsonElement> read) =>
        new(patchId, () => _enabled, () => ValueTask.FromResult<JsonElement?>(read()));

    private async Task<SteamUiCommandResult> HandleToggleQuickAccessAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        bool succeeded = await _toggleQuickAccess(cancellationToken).ConfigureAwait(false);
        return succeeded
            ? SteamUiCommandResult.Applied
            : new(false, "Quick access is not currently available.");
    }

    private void OnSemanticStateChanged() => QueueStatePublication();

    private void QueueStatePublication() => _runtime?.QueuePublication();

    private void SetPatchStates(bool bootstrap, bool components)
    {
        // The registry is the source of truth for which patches exist; a hand-kept id list here
        // drifts. Glyphs and download sorting have independent switches; the network gate may also
        // outlive native QAM to keep the configured header indicator.
        foreach (SteamUiPatchSnapshot patch in _patches.GetSnapshots())
        {
            if (patch.Id == SteamInputGlyphStylePatch.PatchId)
            {
                continue;
            }

            _patches.SetPatchEnabled(
                patch.Id,
                patch.Id == SteamDownloadSortPatch.PatchId
                    ? _downloadSortEnabled
                    : patch.Id == NativeQamBootstrapPatch.PatchId
                        ? bootstrap
                        : patch.Id == SteamGatePatches.Network.Id
                            ? components || _networkIndicatorEnabled
                            : components);
        }
    }

    /// <summary>
    /// Enables the one glyph stylesheet when the active plugin profile supplies something to draw.
    /// </summary>
    /// <remarks>
    /// One switch, because there is one stylesheet. The previous four independent tier switches
    /// existed to gate four separate mapping namespaces; a single stylesheet either has rules or it
    /// does not, and the patch itself refuses to apply an empty one.
    /// </remarks>
    private void SetGlyphDeliveryPatchStates()
    {
        SteamInputGlyphPresentation? presentation = _glyphDeliveryState.Current;
        // Absent controls count as rules. A reviewed profile may legitimately carry nothing but
        // them — hiding trackpad or extra-paddle rows on a handheld that has neither, while keeping
        // Valve's own artwork — and SteamGlyphCss.Build emits real hiding rules for exactly that.
        // Requiring a resource or an image left those profiles with no stylesheet at all, so the
        // controls the device does not have stayed on screen.
        bool deliver = _glyphsEnabled
            && presentation is not null
            && (presentation.StableResources.Count > 0
                || presentation.ControllerImages.Count > 0
                || presentation.AbsentControls.Count > 0);

        // Three independent conditions, and failing any of them leaves the Steam Input page showing
        // Valve's Steam Deck artwork instead of the handheld's own. The patch then reports itself
        // Disabled, which is honest but says nothing about which condition was missing — the
        // setting, a profile that never resolved, or a profile that resolved with nothing to draw.
        Log.Change(
            "steam.ui.glyphs",
            $"Steam Input glyph delivery {(deliver ? "enabled" : "disabled")}: "
                + $"setting={_glyphsEnabled}, profile={presentation is not null}, "
                + $"stableResources={presentation?.StableResources.Count ?? 0}, "
                + $"controllerImages={presentation?.ControllerImages.Count ?? 0}, "
                + $"absentControls={presentation?.AbsentControls.Count ?? 0}",
            deliver ? "info " : "warn ");
        _patches.SetPatchEnabled(SteamInputGlyphStylePatch.PatchId, deliver);
        _glyphDeliveryEnabled = deliver;
    }

    private void UpdatePerformanceObservation()
    {
        // RTSS polling exists for rendered native controls, not merely for the session. A failed
        // fingerprint or lost bridge generation therefore releases the shared service lease.
        IReadOnlyList<SteamUiPatchSnapshot> snapshots = _patches.GetSnapshots();
        bool performancePatchVerified = false;
        foreach (SteamUiPatchSnapshot snapshot in snapshots)
        {
            // The rows that actually render, whichever they are — WSGM's own frame limit and
            // Valve's overlay level. Observation must follow the mounted rows or it never starts.
            performancePatchVerified |= (snapshot.Id == NativeQamComponentPatches.FrameLimit.Id
                || snapshot.Id == NativeQamComponentPatches.ValveOverlayLevel.Id)
                && snapshot.State == SteamUiPatchState.Verified;
        }
        bool shouldObserve = _enabled && _bridge.IsReady && performancePatchVerified;
        if (!shouldObserve)
        {
            ReleasePerformanceObservation();
            return;
        }

        lock (_observationGate)
        {
            if (!_enabled || !_bridge.IsReady)
            {
                return;
            }

            _performanceObservation ??= _performanceService.AcquireObservation();
        }
    }

    private void ReleasePerformanceObservation()
    {
        IDisposable? observation;
        lock (_observationGate)
        {
            observation = _performanceObservation;
            _performanceObservation = null;
        }
        observation?.Dispose();
    }

    private void CancelAllInflightRequests() => _runtime.CancelAllInflight();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisableAsync().ConfigureAwait(false);
        _disposed = true;
        _brightness.Dispose();
        // A session that ends while Steam's network page is open would otherwise leave the radio
        // sweeping and this host subscribed to a collection it no longer publishes.
        if (_network is { } network)
        {
            await network.DisposeAsync().ConfigureAwait(false);
        }
        _transport.GenerationChanged -= OnGenerationChanged;
        _tdp.StateChanged -= OnSemanticStateChanged;
        _deviceControls.StateChanged -= OnSemanticStateChanged;
        _performanceService.StateChanged -= _onPerformanceStateChanged;
        _autoTdp.StateChanged -= OnSemanticStateChanged;
        _controllerTarget.StateChanged -= OnSemanticStateChanged;
        if (_audio is not null)
        {
            _audio.StateChanged -= OnSemanticStateChanged;
        }

        _enabled = false;
        ReleasePerformanceObservation();
        // The runtime first: it stops answering, cancels what is in flight and drains its own
        // request tasks, so nothing is still writing to the bridge when that is disposed below.
        await _runtime.DisposeAsync().ConfigureAwait(false);
        _shutdown.Cancel();
        try
        {
            await _synchronization.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _patches.DisposeAsync().ConfigureAwait(false);
        await _bridge.DisposeAsync().ConfigureAwait(false);
        _autoTdp.Dispose();
        _audio?.Dispose();
        _controllerTarget.Dispose();
        _deviceControls.Dispose();
        _tdp.Dispose();
        _synchronizeSignal.Dispose();
        _shutdown.Dispose();
    }
}
