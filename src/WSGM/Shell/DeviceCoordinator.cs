using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Plugin;
using WSGM.Input;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Who asked for a capability command.</summary>
/// <remarks>
/// The only thing this decides is whether the manual-power funnel runs — pausing AutoTDP and
/// persisting the value as the user's preference. A limit the user moved is an instruction; the one
/// AutoTDP wrote itself is the controller's own output, and treating it as a manual override would
/// pause the feature on its first tick.
/// </remarks>
internal enum CapabilityCommandOrigin
{
    /// <summary>A person moved this control on a WSGM surface.</summary>
    User,

    /// <summary>An automatic controller inside WSGM wrote it.</summary>
    AutomaticControl,

    /// <summary>
    /// WSGM is re-applying a stored per-application or global preference on an application change.
    /// </summary>
    /// <remarks>
    /// Not <see cref="User"/>: the value is already the user's saved preference, so persisting it
    /// again is redundant and — on a release-to-ceiling or a fall back to the global layer — would
    /// write the wrong value into the layer the funnel resolves. The transition path pauses or
    /// resumes AutoTDP itself, so this origin deliberately skips the funnel.
    /// </remarks>
    ProfileRestore,
}

/// <summary>Authoritative process-long owner of the machine-wide hardware cycle.</summary>
public sealed class DeviceCoordinator : IAsyncDisposable
{
    internal const string ProductionOwnerName = @"Global\WSGM.DeviceOwner";
    private static readonly TimeSpan CanceledStartCleanupBudget = TimeSpan.FromSeconds(5);
    private readonly uint _sessionId;
    private readonly Mutex _ownerMutex;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly SemaphoreSlim _profileReconcileGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _backgroundGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly DeviceCapabilityRouter _capabilities;
    private readonly PluginSettingsCoordinator _pluginSettings;
    private readonly DeviceOemActionRouter _oemActions = new();
    private readonly DeviceCoordinatorDiagnosticsServer _diagnostics;
    private readonly PhysicalGlyphCatalog _physicalGlyphs = new();
    private readonly DeviceTeardownFailureTracker _teardownFailures = new();
    private readonly PluginHapticSink _hapticSink;
    private readonly ControllerManager _controllers;
    private DevicePackageDiscovery _packageDiscovery = new()
    {
        Inventory = new DevicePackageInventory { PackageRoots = [] },
    };
    private AppConfig _config;
    private DeviceIdentitySnapshot? _identity;
    private string? _deviceDefinitionId;
    private DevicePluginRuntime? _client;
    private long _cycleGeneration;
    private string? _runningApplicationId;
    private Action<int>? _autoTdpManualOverride;
    private bool _intentionalStop;
    private bool _faultRecoveryPending;
    private int _automaticRestartAttempts;
    private bool _disposed;

    private DeviceCoordinator(
        AppConfig config,
        uint sessionId,
        Mutex ownerMutex,
        Action<Action> postToUi)
    {
        _config = config;
        _sessionId = sessionId;
        _ownerMutex = ownerMutex;
        _capabilities = new DeviceCapabilityRouter(postToUi);
        _pluginSettings = new PluginSettingsCoordinator();
        _diagnostics = new DeviceCoordinatorDiagnosticsServer(sessionId, DiagnosticsSnapshot);
        _hapticSink = new PluginHapticSink(ApplyHapticOutputAsync);
        _controllers = new ControllerManager(
            new ViiperControllerBackend(),
            _hapticSink,
            new HidHideOwnedDeltaManager(
                new WindowsHidHideAdapter(),
                new FileHidHideOwnershipStore(
                    Path.Combine(Log.Directory, "hidhide-ownership.json"))),
            NativeStorage.FromDosPath(
                Environment.ProcessPath
                    ?? throw new InvalidOperationException("The WSGM executable path is unavailable.")));
    }

    private Task ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        DevicePluginRuntime? client = _client;
        return client is null
            ? Task.CompletedTask
            : client.ApplyHapticOutputAsync(frame, cancellationToken);
    }

    /// <summary>Current process-long lifecycle state.</summary>
    public DeviceCycleState State { get; private set; } = DeviceCycleState.Disabled;

    /// <summary>Whether the persisted master switch currently exposes the Device surface.</summary>
    internal bool IntegrationEnabled => _config.DeviceIntegration.Enabled;

    /// <summary>The sole installed package, including its validation result.</summary>
    internal InstalledDevicePackage? InstalledPackage => _packageDiscovery.InstalledPackage;

    /// <summary>The device definition matched by the active plugin cycle.</summary>
    internal string? ActiveDeviceDefinitionId => _deviceDefinitionId;

    /// <summary>The latest one-slot discovery result.</summary>
    internal DevicePackageDiscovery PackageDiscovery => _packageDiscovery;

    /// <summary>Raised after the authoritative lifecycle state changes.</summary>
    public event Action<DeviceCycleState>? StateChanged;

    /// <summary>Raised when settings change overlay visibility or desired presentation.</summary>
    /// <remarks>
    /// Glyph-profile selection also changes with configuration; consumers of the active profile
    /// subscribe to this and to <see cref="PhysicalGlyphCatalog"/>'s change event.
    /// </remarks>
    internal event Action? ConfigurationChanged;

    /// <summary>The capability router, for snapshots and change subscriptions.</summary>
    /// <remarks>
    /// Reads and events only. Writes go through <see cref="ExecuteCapabilityAsync"/>, which is the
    /// one path that lets a manual power change pause AutoTDP.
    /// </remarks>
    internal DeviceCapabilityRouter Capabilities => _capabilities;

    /// <summary>The controller manager, for status/sample subscriptions and reads.</summary>
    /// <remarks>
    /// Reads and events only. Lifecycle, UI capture, and the make-safe ordering stay behind this
    /// coordinator's methods so a consumer cannot order the manager's steps out of sequence.
    /// </remarks>
    internal ControllerManager Controllers => _controllers;

    /// <summary>
    /// Creates the one coordinator allowed to own hardware on this machine without blocking the UI.
    /// </summary>
    /// <param name="config">Initial normalized application configuration.</param>
    /// <param name="cancellationToken">Cancels admission before the coordinator is created.</param>
    /// <returns>The coordinator, or null when the process-wide device owner is already reserved.</returns>
    public static Task<DeviceCoordinator?> TryStartAsync(
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        Mutex? owner = TryCreateOwnerMutex(ProductionOwnerName);
        if (owner is null)
        {
            Log.Warn(
                "Device cycle: machine-wide ownership is already active or unavailable; no cycle started.");
            return Task.FromResult<DeviceCoordinator?>(null);
        }

        DeviceCoordinator coordinator;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint sessionId = (uint)Process.GetCurrentProcess().SessionId;
            coordinator = new DeviceCoordinator(
                config,
                sessionId,
                owner,
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        }
        catch
        {
            owner.Dispose();
            throw;
        }
        if (config.DeviceIntegration.Enabled)
        {
            coordinator.Observe(coordinator.StartCycleAsync(coordinator._lifetime.Token), "initial start");
        }
        else
        {
            Log.Info(
                $"Device cycle: coordinator ready for session {coordinator._sessionId}; integration disabled.");
        }

        return Task.FromResult<DeviceCoordinator?>(coordinator);
    }

    /// <summary>Creates one handle-owned machine marker. It is deliberately never mutex-owned, so
    /// coordinator disposal may close it from any continuation thread.</summary>
    internal static Mutex? TryCreateOwnerMutex(
        string name,
        Func<string, (Mutex Owner, bool CreatedNew)>? create = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            Func<string, (Mutex Owner, bool CreatedNew)> factory = create ?? CreateOwnerMutex;
            (Mutex owner, bool createdNew) = factory(name);
            if (createdNew)
            {
                return owner;
            }

            owner.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or WaitHandleCannotBeOpenedException)
        {
            Log.Warn($"Device cycle: owner marker '{name}' could not be created: {ex.Message}");
            return null;
        }
    }

    private static (Mutex Owner, bool CreatedNew) CreateOwnerMutex(string name)
    {
        var owner = new Mutex(initiallyOwned: false, name, out bool createdNew);
        return (owner, createdNew);
    }

    /// <summary>Applies a saved ownership configuration to this authoritative process.</summary>
    public async Task ApplyConfigAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppConfig previousConfig = _config;
            bool wasEnabled = _config.DeviceIntegration.Enabled;
            bool controllerWasEnabled = _config.DeviceIntegration.ControllerManagementEnabled;
            _config = config;
            bool controllerIsEnabled = config.DeviceIntegration.ControllerManagementEnabled;
            ConfigurationChanged?.Invoke();

            // Stored settings live in the configuration, so a reload can change what the plugin
            // should be running with even though the plugin itself never changed.
            _pluginSettings.ApplyConfig(config);
            UpdateCapabilityDesiredContext();
            UpdateOemConfiguration();
            await _controllers.ApplySelectionAsync(
                ControllerSelection.From(config.DeviceIntegration),
                _runningApplicationId,
                cancellationToken).ConfigureAwait(false);
            if (!wasEnabled && config.DeviceIntegration.Enabled)
            {
                _automaticRestartAttempts = 0;
                try
                {
                    await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    RestoreConfigAfterCanceledStart(previousConfig);
                    throw;
                }
                return;
            }

            if (wasEnabled && !config.DeviceIntegration.Enabled)
            {
                DeviceClientTeardownResult teardown = await StopCycleUnderGateAsync(
                    PluginStopReason.IntegrationDisabled,
                    NormalShutdownDeadline(),
                    cancellationToken).ConfigureAwait(false);
                _physicalGlyphs.ReplacePackageProfiles([]);
                ThrowIfDeviceTeardownIncomplete(teardown, cancellationToken);
                return;
            }

            if (config.DeviceIntegration.Enabled
                && controllerWasEnabled != controllerIsEnabled
                && _client is not null)
            {
                await SetControllerManagementUnderGateAsync(
                    controllerIsEnabled,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private void RestoreConfigAfterCanceledStart(AppConfig previousConfig)
    {
        _config = previousConfig;
        try
        {
            ConfigurationChanged?.Invoke();
            UpdateCapabilityDesiredContext();
            UpdateOemConfiguration();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Device cycle cancellation config restore notification failed", ex);
        }
    }

    /// <summary>Quiesces the active plugin for suspend or session lock.</summary>
    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DevicePluginRuntime? client = _client;
            if (client is null)
            {
                Log.Info("Device suspend skipped: no active plugin cycle exists.");
                return;
            }

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            if (_controllers.State is ControllerManagementState.Active
                or ControllerManagementState.Faulted)
            {
                await _controllers.BlockForwardingAsync("suspending", cancellationToken)
                    .ConfigureAwait(false);
                ControllerHandoff handoff = await _controllers.MakeSafeAsync(
                    HandoffScope.ControllerOnly,
                    token => client.ReleaseControllerAsync(
                        HandoffScope.ControllerOnly,
                        deadline,
                        token),
                    cancellationToken).ConfigureAwait(false);
                Log.Info(
                    $"Controller suspend handoff: step={handoff.Step}, result={handoff.Result}.");
            }
            DevicePluginState state = await client.SuspendAsync(deadline, cancellationToken)
                .ConfigureAwait(false);
            _oemActions.Reset();
            SetState(state.State);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Revalidates and resumes into a fresh device generation.</summary>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DevicePluginRuntime? client = _client;
            if (client is null)
            {
                Log.Info("Device resume skipped: no active plugin cycle exists.");
                return;
            }

            _identity = DeviceMachineIdentity.Collect();
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            long previousGeneration = Interlocked.Read(ref _cycleGeneration);
            long requestedGeneration = Interlocked.Increment(ref _cycleGeneration);
            DevicePluginState state;
            try
            {
                state = await client.ResumeAsync(
                    requestedGeneration,
                    deadline,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SynchronizeGenerationAfterLifecycleCall(client, previousGeneration);
            }
            SetState(state.State);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Starts one user-requested attempt after automatic recovery was exhausted.</summary>
    public async Task<bool> RetryAfterFaultAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not DeviceCycleState.Faulted)
            {
                Log.Info($"Device plugin retry ignored because state is {State}.");
                return false;
            }

            if (_teardownFailures.HasFailures)
            {
                Log.Warn("Device plugin retry refused because prior hardware cleanup was unverified.");
                return false;
            }

            _automaticRestartAttempts = 0;
            await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Stops the cycle under one caller-owned full-deactivation deadline.</summary>
    public async Task StopAsync(
        PluginStopReason reason,
        DateTimeOffset deadline,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeviceClientTeardownResult teardown = await StopCycleUnderGateAsync(
                reason,
                deadline,
                cancellationToken).ConfigureAwait(false);
            ThrowIfDeviceTeardownIncomplete(teardown, cancellationToken);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ShutdownAsync(
        PluginStopReason.WsgmExiting,
        NormalShutdownDeadline());

    /// <summary>Stops the device cycle under the process exit path's single outer deadline.</summary>
    internal async ValueTask ShutdownAsync(
        PluginStopReason reason,
        DateTimeOffset deadline)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception> shutdownFailures = [];
        try
        {
            await CancelLifetimeAndWaitForTransitionAsync(_lifetime, _transitionGate)
                .ConfigureAwait(false);
            try
            {
                DeviceClientTeardownResult teardown = await StopCycleUnderGateAsync(
                    reason,
                    deadline,
                    CancellationToken.None).ConfigureAwait(false);
                ThrowIfDeviceTeardownIncomplete(teardown, CancellationToken.None);
            }
            finally
            {
                shutdownFailures.AddRange(_teardownFailures.Drain());
                _transitionGate.Release();
            }
        }
        catch (Exception ex)
        {
            shutdownFailures.Add(ex);
            Log.Warn($"Device cycle shutdown was unverified: {ex.Message}");
        }

        Task[] background;
        lock (_backgroundGate)
        {
            background = _backgroundTasks.ToArray();
        }
        await RetainDeviceShutdownFailureAsync(
            shutdownFailures,
            "background task completion",
            () => new ValueTask(Task.WhenAll(background))).ConfigureAwait(false);
        await RetainDeviceShutdownFailureAsync(
            shutdownFailures,
            "diagnostics disposal",
            _diagnostics.DisposeAsync).ConfigureAwait(false);
        await RetainDeviceShutdownFailureAsync(
            shutdownFailures,
            "capability disposal",
            _capabilities.DisposeAsync).ConfigureAwait(false);
        await RetainDeviceShutdownFailureAsync(
            shutdownFailures,
            "controller management disposal",
            _controllers.DisposeAsync).ConfigureAwait(false);
        RetainDeviceShutdownFailure(shutdownFailures, "OEM action disposal", _oemActions.Dispose);
        RetainDeviceShutdownFailure(
            shutdownFailures,
            "plugin settings disposal",
            _pluginSettings.Dispose);
        RetainDeviceShutdownFailure(shutdownFailures, "glyph disposal", _physicalGlyphs.Dispose);
        RetainDeviceShutdownFailure(shutdownFailures, "lifetime disposal", _lifetime.Dispose);
        RetainDeviceShutdownFailure(shutdownFailures, "transition gate disposal", _transitionGate.Dispose);
        RetainDeviceShutdownFailure(shutdownFailures, "owner marker disposal", _ownerMutex.Dispose);
        if (shutdownFailures.Count > 0)
        {
            throw new InvalidOperationException(
                "Device cycle shutdown completed teardown, but hardware release was unverified.",
                shutdownFailures.Count == 1
                    ? shutdownFailures[0]
                    : new AggregateException(shutdownFailures));
        }
    }

    private static async ValueTask RetainDeviceShutdownFailureAsync(
        List<Exception> failures,
        string operation,
        Func<ValueTask> cleanupAsync)
    {
        try
        {
            await cleanupAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
            Log.Warn($"Device cycle {operation} was incomplete: {ex.Message}");
        }
    }

    private static void RetainDeviceShutdownFailure(
        List<Exception> failures,
        string operation,
        Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
            Log.Warn($"Device cycle {operation} was incomplete: {ex.Message}");
        }
    }

    private Task StartCycleAsync(CancellationToken cancellationToken) =>
        RunUnderTransitionGateAsync(StartCycleUnderGateAsync, cancellationToken);

    private async Task RunUnderTransitionGateAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task StartCycleUnderGateAsync(CancellationToken cancellationToken)
    {
        if (_client is not null || !_config.DeviceIntegration.Enabled)
        {
            return;
        }

        DeviceCycleState retryState = State;
        using CancellationTokenSource startLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        await RunCancellationSafeStartAsync(
            StartCycleCoreUnderGateAsync,
            CleanupCanceledStartAsync,
            () => SetState(retryState),
            startLifetime.Token).ConfigureAwait(false);
    }

    /// <summary>Runs one start attempt while guaranteeing that linked cancellation applies its
    /// ownership policy, restores the state from which the attempt may be retried, and is rethrown.</summary>
    internal static async Task RunCancellationSafeStartAsync(
        Func<CancellationToken, Task> operation,
        Func<ValueTask> cleanup,
        Action restoreRetryState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(restoreRetryState);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await cleanup().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Device cycle cancellation cleanup failed", ex);
            }

            try
            {
                restoreRetryState();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Device cycle cancellation state restore failed", ex);
            }

            throw;
        }
    }

    private async Task StartCycleCoreUnderGateAsync(CancellationToken cancellationToken)
    {
        _intentionalStop = false;
        SetState(DeviceCycleState.Detected);
        DevicePackageSlotGate? slotGate;
        try
        {
            slotGate = await DevicePackageSlotGate.TryAcquireAsync(
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScheduleStartFault(new InvalidOperationException(
                "The protected Device Plugin slot could not be locked for startup.",
                ex));
            return;
        }
        if (slotGate is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScheduleStartFault(new TimeoutException(
                "The protected Device Plugin slot remained busy during startup."));
            return;
        }

        InstalledDevicePackage package;
        long cycleGeneration;
        DevicePluginRuntime client;
        await using (slotGate)
        {
            try
            {
                // Maintenance and host startup share this gate. Reconcile the fixed recovery
                // sibling before discovery so a process death between the two atomic moves cannot
                // make the previously installed package disappear permanently.
                DevicePackageStager.ReconcileInstalledPackage(
                    DeviceInstallationPaths.InstalledPackageRoot);
                _identity = DeviceMachineIdentity.Collect();
                _packageDiscovery = await DiscoverPackageAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScheduleStartFault(new InvalidOperationException(
                    "The protected Device Plugin slot could not be reconciled or discovered.",
                    ex));
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            InstalledDevicePackage? discoveredPackage = InstalledPackage;
            _physicalGlyphs.ReplacePackageProfiles([]);
            if (discoveredPackage is null || !discoveredPackage.Valid)
            {
                SetState(DeviceCycleState.Passive);
                string refusal = _packageDiscovery.ErrorCode
                    ?? discoveredPackage?.RejectionCode
                    ?? "no-package-installed";
                Log.Warn(
                    $"Device cycle passive: {refusal}; packageRoots={_packageDiscovery.Inventory.PackageRoots.Count}.");
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            package = discoveredPackage;

            cycleGeneration = Interlocked.Increment(ref _cycleGeneration);
            SetState(DeviceCycleState.Activating);
            try
            {
                client = await DevicePluginRuntime.StartAsync(
                    package,
                    cycleGeneration,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScheduleStartFault(ex);
                return;
            }
        }
        _client = client;
        try
        {
            Attach(client);
            _capabilities.Attach(client, cycleGeneration);
            UpdateCapabilityDesiredContext();
            _oemActions.Attach(client, cycleGeneration);
            UpdateOemConfiguration();
            bool controllerManagement = _config.DeviceIntegration.ControllerManagementEnabled;
            // Before the plugin starts, because the plugin's first job is to find the physical
            // controller and it cannot find one that HidHide is hiding from this process. Doing it
            // afterwards is too late for the cycle that needed it.
            await _controllers.EnsureHidHideReadableAsync(controllerManagement, cancellationToken)
                .ConfigureAwait(false);
            DevicePluginState activation = await client.StartAsync(
                _identity,
                cycleGeneration,
                controllerManagement,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Before the profiles load: glyph selection is gated on the matched device definition,
            // and a catalog that arrives first would be selected against a null id and rejected.
            SetDeviceDefinitionId(activation.DeviceDefinitionId);

            // Attached after the definition is known, because stored values are keyed by it and by
            // the package: a value authored for one device must never be handed to another.
            _pluginSettings.Attach(
                client,
                activation.DeviceDefinitionId ?? string.Empty,
                package.Manifest?.Id ?? string.Empty,
                _config);
            LoadPhysicalGlyphProfiles(package);
            SetState(activation.State);
            _automaticRestartAttempts = 0;
            Log.Info(
                $"Device cycle active: package={package.Manifest?.Id}, "
                    + $"cycleGeneration={cycleGeneration}, "
                    + $"state={activation.State}.");
            Observe(ObserveRuntimeCompletionAsync(client), "plugin supervision");
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // The runtime owns a bounded start deadline. If it expires after the plugin entered
            // StartAsync, hardware may already be acquired even though the caller token is live;
            // run the same bounded make-safe path used by caller cancellation.
            await ScheduleStartFaultAfterCleanupAsync(
                ex,
                PluginStopReason.StartCanceled,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScheduleStartFaultAfterCleanupAsync(
                ex,
                PluginStopReason.StartFailed,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask CleanupCanceledStartAsync()
    {
        // A start fault can enqueue recovery while cancellation races its return. The recovery
        // worker is serialized behind this same transition, so clearing admission here guarantees
        // a canceled caller cannot be followed by an automatic restart.
        _faultRecoveryPending = false;

        return new ValueTask(RunCanceledStartCleanupPolicyAsync(
            _lifetime.IsCancellationRequested,
            () => CleanupAbortedStartAsync(PluginStopReason.StartCanceled)));
    }

    /// <summary>Preserves a possibly active runtime when shutdown canceled startup, because the
    /// shutdown owner must perform the bounded handoff. An independent caller cancellation runs
    /// its own fresh bounded teardown before the runtime can be disposed.</summary>
    internal static Task RunCanceledStartCleanupPolicyAsync(
        bool lifetimeCancellationRequested,
        Func<Task> callerCleanupAsync)
    {
        ArgumentNullException.ThrowIfNull(callerCleanupAsync);
        return lifetimeCancellationRequested
            ? Task.CompletedTask
            : callerCleanupAsync();
    }

    /// <summary>Closes a coordinator lifetime before waiting for its serialized transition. The
    /// ordering lets cancellation unwind an in-flight start that currently owns the gate.</summary>
    internal static Task CancelLifetimeAndWaitForTransitionAsync(
        CancellationTokenSource lifetime,
        SemaphoreSlim transitionGate)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(transitionGate);
        lifetime.Cancel();
        return transitionGate.WaitAsync(CancellationToken.None);
    }

    private async Task CleanupAbortedStartAsync(PluginStopReason reason)
    {
        bool teardownVerified = false;
        try
        {
            await RunFreshBoundedCleanupAsync(
                CanceledStartCleanupBudget,
                async (deadline, cancellationToken) =>
                {
                    DeviceClientTeardownResult teardown = await StopCycleUnderGateAsync(
                        reason,
                        deadline,
                        cancellationToken).ConfigureAwait(false);
                    teardownVerified = teardown.Verified;
                    ThrowIfDeviceTeardownIncomplete(teardown, cancellationToken);
                }).ConfigureAwait(false);
        }
        catch (Exception ex) when (!teardownVerified && ex is not OutOfMemoryException)
        {
            _teardownFailures.Retain(ex);
            throw;
        }
    }

    private async Task ScheduleStartFaultAfterCleanupAsync(
        Exception startFailure,
        PluginStopReason reason,
        CancellationToken startCancellationToken)
    {
        Exception failure = startFailure;
        try
        {
            await CleanupAbortedStartAsync(reason).ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (cleanupFailure is not OutOfMemoryException)
        {
            failure = new AggregateException(
                "Device startup failed and its bounded cleanup was unverified.",
                startFailure,
                cleanupFailure);
        }

        // Cancellation can race the original exception and the fresh cleanup. A canceled caller
        // still owns the outcome and must never be followed by an automatic restart.
        startCancellationToken.ThrowIfCancellationRequested();
        ScheduleStartFault(failure);
    }

    /// <summary>Creates a cleanup budget independent from the already-canceled start caller.</summary>
    internal static async Task RunFreshBoundedCleanupAsync(
        TimeSpan budget,
        Func<DateTimeOffset, CancellationToken, Task> cleanupAsync,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }
        ArgumentNullException.ThrowIfNull(cleanupAsync);
        utcNow ??= static () => DateTimeOffset.UtcNow;
        using var cleanupCancellation = new CancellationTokenSource(budget);
        await cleanupAsync(
            utcNow().Add(budget),
            cleanupCancellation.Token).ConfigureAwait(false);
    }

    private async Task ObserveRuntimeCompletionAsync(DevicePluginRuntime client)
    {
        DeviceRuntimeExit exit = await client.Completion.ConfigureAwait(false);
        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_client, client))
            {
                return;
            }

            _client = null;
            DateTimeOffset cleanupDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
            using CancellationTokenSource cleanupCancellation = new(TimeSpan.FromSeconds(15));
            DeviceClientTeardownResult cleanup = await RunClientTeardownAsync(
                token => _controllers.MakeSafeAsync(
                    HandoffScope.FullDeactivation,
                    inner => client.ReleaseControllerAsync(
                        HandoffScope.FullDeactivation,
                        cleanupDeadline,
                        inner),
                    token),
                token => client.StopAsync(
                    PluginStopReason.RuntimeFault,
                    cleanupDeadline,
                    token),
                () => DetachAsync(client),
                client.DisposeAsync,
                cleanupCancellation.Token).ConfigureAwait(false);
            foreach (Exception cleanupFailure in cleanup.Failures)
            {
                _teardownFailures.Retain(cleanupFailure);
            }
            if (!cleanup.Verified)
            {
                SetState(DeviceCycleState.Faulted);
                Log.Error(
                    "Device plugin fault cleanup was incomplete; restart is blocked",
                    cleanup.ToException());
                return;
            }

            _teardownFailures.ResolveAfterVerifiedOwnerTeardown();

            if (_intentionalStop
                || _disposed
                || !_config.DeviceIntegration.Enabled
                || exit.Reason is DeviceRuntimeExitReason.Intentional)
            {
                SetState(DeviceCycleState.Disabled);
                return;
            }

            Log.Warn(
                $"Device plugin fault: generation={_cycleGeneration}, reason={exit.Reason}, "
                    + $"detail={exit.Detail}.");
            ScheduleFaultRecovery();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetState(DeviceCycleState.Faulted);
            Log.Error("Device plugin restart failed; cycle faulted", ex);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private void ScheduleStartFault(Exception exception)
    {
        if (_faultRecoveryPending || _disposed || !_config.DeviceIntegration.Enabled)
        {
            Log.Warn(
                "Device plugin start fault recovery suppressed: "
                + $"pending={_faultRecoveryPending}, disposed={_disposed}, "
                + $"integrationEnabled={_config.DeviceIntegration.Enabled}, "
                + $"failure={exception.Message}");
            return;
        }

        _faultRecoveryPending = true;
        Log.Error("Device plugin start failed", exception);
        Observe(HandleStartFaultAsync(), "plugin start fault recovery");
    }

    private async Task HandleStartFaultAsync()
    {
        await _transitionGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!_faultRecoveryPending)
            {
                Log.Info("Device plugin start-fault worker stopped: recovery is no longer pending.");
                return;
            }

            _faultRecoveryPending = false;
            if (_disposed || !_config.DeviceIntegration.Enabled || _client is not null)
            {
                Log.Info(
                    "Device plugin start-fault worker stopped: "
                    + $"disposed={_disposed}, integrationEnabled={_config.DeviceIntegration.Enabled}, "
                    + $"runtimePresent={_client is not null}.");
                return;
            }

            ScheduleFaultRecovery();
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private void ScheduleFaultRecovery()
    {
        if (_automaticRestartAttempts >= 2)
        {
            SetState(DeviceCycleState.Faulted);
            Log.Error(
                $"Device cycle faulted after restart exhaustion: package={InstalledPackage?.Manifest?.Id}, "
                    + "the two automatic restart attempts were exhausted.");
            return;
        }

        TimeSpan backoff = _automaticRestartAttempts++ == 0
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromSeconds(4);
        SetState(DeviceCycleState.Activating);
        Log.Warn(
            $"Device plugin restart {_automaticRestartAttempts}/2 scheduled in "
                + $"{backoff.TotalSeconds:0.#} s.");
        Observe(RestartAfterDelayAsync(backoff), "delayed plugin restart");
    }

    private async Task RestartAfterDelayAsync(TimeSpan backoff)
    {
        await Task.Delay(backoff, _lifetime.Token).ConfigureAwait(false);
        if (!_disposed && _config.DeviceIntegration.Enabled && _client is null)
        {
            await StartCycleAsync(_lifetime.Token).ConfigureAwait(false);
            return;
        }

        Log.Info(
            "Device plugin delayed restart skipped: "
            + $"disposed={_disposed}, integrationEnabled={_config.DeviceIntegration.Enabled}, "
            + $"runtimePresent={_client is not null}.");
    }

    private async Task<DeviceClientTeardownResult> StopCycleUnderGateAsync(
        PluginStopReason reason,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        _intentionalStop = true;
        DevicePluginRuntime? client = _client;
        _client = null;
        if (client is null)
        {
            SetState(DeviceCycleState.Disabled);
            return DeviceClientTeardownResult.Clean;
        }

        DeviceClientTeardownResult? ownerTeardown = null;
        async Task<DeviceClientTeardownResult> TeardownOwnerAsync()
        {
            DeviceClientTeardownResult result = await RunClientTeardownAsync(
                token => _controllers.MakeSafeAsync(
                    HandoffScope.FullDeactivation,
                    inner => client.ReleaseControllerAsync(
                        HandoffScope.FullDeactivation,
                        deadline,
                        inner),
                    token),
                token => client.StopAsync(
                    reason,
                    deadline,
                    token),
                () => DetachAsync(client),
                client.DisposeAsync,
                cancellationToken).ConfigureAwait(false);
            ownerTeardown = result;
            return result;
        }

        DeviceClientTeardownResult teardown = await RunClientTeardownWithStateNotificationsAsync(
            _capabilities.CloseCommandAdmission,
            () => SetState(DeviceCycleState.Deactivating),
            TeardownOwnerAsync,
            () => SetState(DeviceCycleState.Disabled)).ConfigureAwait(false);
        if (ownerTeardown?.Verified is true
            && reason is not (PluginStopReason.StartCanceled or PluginStopReason.StartFailed))
        {
            _teardownFailures.ResolveAfterVerifiedOwnerTeardown();
        }
        return teardown;
    }

    internal static async Task<DeviceClientTeardownResult> RunClientTeardownWithStateNotificationsAsync(
        Action closeCommandAdmission,
        Action setDeactivating,
        Func<Task<DeviceClientTeardownResult>> teardownAsync,
        Action setDisabled)
    {
        ArgumentNullException.ThrowIfNull(closeCommandAdmission);
        ArgumentNullException.ThrowIfNull(setDeactivating);
        ArgumentNullException.ThrowIfNull(teardownAsync);
        ArgumentNullException.ThrowIfNull(setDisabled);
        List<Exception> failures = [];
        try
        {
            closeCommandAdmission();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
            Log.Warn($"Device command admission closure failed; cleanup continues: {ex.Message}");
        }

        try
        {
            setDeactivating();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
            Log.Warn($"Device deactivation state notification failed; cleanup continues: {ex.Message}");
        }

        try
        {
            DeviceClientTeardownResult teardown = await teardownAsync().ConfigureAwait(false);
            failures.AddRange(teardown.Failures);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failures.Add(ex);
            Log.Warn($"Device client teardown faulted before reporting its result: {ex.Message}");
        }
        finally
        {
            try
            {
                setDisabled();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Warn($"Device disabled-state notification failed after cleanup: {ex.Message}");
            }
        }

        return new DeviceClientTeardownResult(failures.ToArray());
    }

    /// <summary>Attempts controller and plugin cleanup before detaching and disposing the runtime.
    /// Every non-fatal unverified response or exception is retained while later phases continue.</summary>
    internal static async Task<DeviceClientTeardownResult> RunClientTeardownAsync(
        Func<CancellationToken, Task<ControllerHandoff>> releaseControllerAsync,
        Func<CancellationToken, Task<DevicePluginState>> stopAsync,
        Func<ValueTask> detachAsync,
        Func<ValueTask> disposeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(releaseControllerAsync);
        ArgumentNullException.ThrowIfNull(stopAsync);
        ArgumentNullException.ThrowIfNull(detachAsync);
        ArgumentNullException.ThrowIfNull(disposeAsync);
        List<Exception> failures = [];
        try
        {
            try
            {
                ControllerHandoff handoff = await releaseControllerAsync(
                    cancellationToken).ConfigureAwait(false);
                if (handoff.Result is ControllerHandoffResult.ReleasedVerified
                    && handoff.Step is (ControllerHandoffStep.TopologyVerified
                        or ControllerHandoffStep.WsgmStateRemoved))
                {
                    Log.Info($"Device controller release: {handoff.Step}, {handoff.Result}.");
                }
                else
                {
                    var failure = new InvalidOperationException(
                        $"Device controller release was unverified: {handoff.Step}, {handoff.Result}.");
                    failures.Add(failure);
                    Log.Warn(failure.Message);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Warn($"Device controller release unverified; cleanup continues: {ex.Message}");
            }

            try
            {
                DevicePluginState stopped = await stopAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (stopped.State is DeviceCycleState.Disabled && stopped.Reason is null)
                {
                    Log.Info($"Device hardware release: {stopped.State}, verified.");
                }
                else
                {
                    var failure = new InvalidOperationException(
                        $"Device hardware release was unverified: state={stopped.State}, "
                            + $"reason={stopped.Reason?.Code.ToString() ?? "none"}, "
                            + $"detail={stopped.Reason?.Detail ?? "none"}.");
                    failures.Add(failure);
                    Log.Warn(failure.Message);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex);
                Log.Warn($"Device hardware release unverified; host will be terminated: {ex.Message}");
            }
        }
        finally
        {
            try
            {
                try
                {
                    await detachAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failures.Add(ex);
                    Log.Warn($"Device client detach was incomplete: {ex.Message}");
                }
            }
            finally
            {
                try
                {
                    await disposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failures.Add(ex);
                    Log.Warn($"Device client disposal was incomplete: {ex.Message}");
                }
            }
        }

        return new DeviceClientTeardownResult(failures.ToArray());
    }

    internal static void ThrowIfDeviceTeardownIncomplete(
        DeviceClientTeardownResult teardown,
        CancellationToken cancellationToken)
    {
        if (teardown.Verified)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Device teardown observed caller cancellation after retaining unverified release results.",
                teardown.ToException(),
                cancellationToken);
        }

        throw new InvalidOperationException(
            "Device hardware teardown completed, but one or more release steps were unverified.",
            teardown.ToException());
    }

    private static DateTimeOffset NormalShutdownDeadline() =>
        DateTimeOffset.UtcNow.AddSeconds(15);

    private async Task SetControllerManagementUnderGateAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        DevicePluginRuntime? client = _client;
        if (client is null)
        {
            return;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        if (!enabled)
        {
            ControllerHandoff handoff = await _controllers.MakeSafeAsync(
                HandoffScope.ControllerOnly,
                token => client.ReleaseControllerAsync(
                    HandoffScope.ControllerOnly,
                    deadline,
                    token),
                cancellationToken).ConfigureAwait(false);
            Log.Info($"Controller management disabled: {handoff.Step}, {handoff.Result}.");

            // After the verified handoff, and never instead of it: the plugin remembers its
            // acquisition policy across suspend/resume. See docs\device-integration.md §Lifecycle
            // and recovery.
            try
            {
                await client.SetControllerManagementAsync(
                    enabled: false,
                    Interlocked.Read(ref _cycleGeneration),
                    DateTimeOffset.UtcNow.AddSeconds(6),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn(
                    "The plugin did not acknowledge controller management being disabled; "
                    + $"restarting the plugin with the persisted policy: {ex.Message}");
                DeviceClientTeardownResult teardown = await StopCycleUnderGateAsync(
                    PluginStopReason.RuntimeFault,
                    NormalShutdownDeadline(),
                    cancellationToken).ConfigureAwait(false);
                ThrowIfDeviceTeardownIncomplete(teardown, cancellationToken);
                await StartCycleUnderGateAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Before the plugin is asked to acquire, exactly as at cycle start: it cannot discover an
        // interface another application's HidHide allowlist is hiding from WSGM, and adding
        // the allowance afterwards does nothing for the acquisition that already failed.
        await _controllers.EnsureHidHideReadableAsync(true, cancellationToken).ConfigureAwait(false);
        long previousGeneration = Interlocked.Read(ref _cycleGeneration);
        long generation = Interlocked.Increment(ref _cycleGeneration);
        try
        {
            await client.SetControllerManagementAsync(
                enabled: true,
                generation,
                deadline,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SynchronizeGenerationAfterLifecycleCall(client, previousGeneration);
        }
        Log.Info($"Controller management enabled: cycleGeneration={generation}.");
    }

    private void SynchronizeGenerationAfterLifecycleCall(
        DevicePluginRuntime client,
        long previousGeneration)
    {
        long activeGeneration = client.CycleGeneration;
        Interlocked.Exchange(ref _cycleGeneration, activeGeneration);
        if (activeGeneration == previousGeneration)
        {
            return;
        }

        _capabilities.MarkCycleGenerationChanged(activeGeneration);
        UpdateCapabilityDesiredContext();
        _oemActions.Reset(activeGeneration);
    }

    private void Attach(DevicePluginRuntime client)
    {
        client.LifecycleStateReceived += OnLifecycleState;
        client.PhysicalIdentitiesReceived += OnPhysicalIdentities;
        client.ControllerSampleReceived += _controllers.Submit;
    }

    private async ValueTask DetachAsync(DevicePluginRuntime client)
    {
        client.LifecycleStateReceived -= OnLifecycleState;
        client.PhysicalIdentitiesReceived -= OnPhysicalIdentities;
        client.ControllerSampleReceived -= _controllers.Submit;
        // The plugin no longer owns the controller: withdraw before the routers are torn down, and
        // await so frames already admitted cannot land on a controller that was handed back.
        await _hapticSink.WithdrawAsync().ConfigureAwait(false);
        _capabilities.Detach();
        _pluginSettings.Detach();
        _oemActions.Detach();
    }

    /// <summary>
    /// Starts WSGM-side controller management for the controller the plugin just took.
    /// </summary>
    /// <remarks>
    /// Driven by the publication rather than by cycle start: WSGM may only hide a device and create
    /// a virtual target once the plugin has actually acquired the physical one, and the plugin
    /// republishes after a controller-management re-enable and after resume.
    /// </remarks>
    private void OnPhysicalIdentities(
        (IReadOnlyList<PhysicalDeviceIdentity> Devices, HapticCapabilities? Output) notification)
    {
        long generation = Interlocked.Read(ref _cycleGeneration);
        _hapticSink.Publish(notification.Output, generation);
        Observe(
            StartControllerManagementAsync(notification.Devices, generation),
            "controller management start");
    }

    private async Task StartControllerManagementAsync(
        IReadOnlyList<PhysicalDeviceIdentity> devices,
        long generation)
    {
        ControllerManagerStatus status = await _controllers.StartAsync(
            ControllerSelection.From(_config.DeviceIntegration),
            devices,
            _runningApplicationId,
            generation,
            _lifetime.Token).ConfigureAwait(false);
        Log.Info(
            $"Controller management: state={status.State}, target={status.Target}, "
            + $"source={status.TargetSource}, uiSource={status.UiSource}, detail={status.Detail}");
    }

    /// <summary>Applies a running-application change from the one shared monitor.</summary>
    /// <param name="snapshot">The canonical running-application snapshot.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>A task completing after the controller target is reconciled.</returns>
    internal async Task ApplyRunningApplicationAsync(
        RunningApplicationTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _runningApplicationId = snapshot.ApplicationId;
        await _controllers.ApplyRunningApplicationAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);

        // Authored profiles follow the same identity as everything else per-application: the fan
        // curve and the controller target can never disagree about which application is running.
        await ApplyAuthoredProfilesAsync(snapshot.ApplicationId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Current persisted physical-glyph presentation mode.</summary>
    internal DeviceGlyphSelection PhysicalGlyphSelection =>
        _config.DeviceIntegration.GlyphSelection;

    /// <summary>Resolves the current persisted mode against only the active package's safe profiles.</summary>
    internal PhysicalGlyphSelectionResult PhysicalGlyphSelectionSnapshot() =>
        _physicalGlyphs.SelectProfile(
            _config.DeviceIntegration.Enabled,
            _config.DeviceIntegration.GlyphSelection,
            _config.DeviceIntegration.ManualGlyphProfileId);

    /// <summary>Cycles the physical presentation policy and persists it without changing device ownership.</summary>
    internal async Task CyclePhysicalGlyphSelectionAsync(
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeviceGlyphSelection next = _config.DeviceIntegration.GlyphSelection switch
            {
                DeviceGlyphSelection.Automatic => DeviceGlyphSelection.NativeSteam,
                DeviceGlyphSelection.NativeSteam => DeviceGlyphSelection.ManualReviewedProfile,
                _ => DeviceGlyphSelection.Automatic,
            };
            await PersistConfigurationAsync(
                config => config.DeviceIntegration.GlyphSelection = next,
                cancellationToken).ConfigureAwait(false);
            Log.Info($"Physical glyph presentation changed: {next}.");
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>
    /// Attaches the hook that pauses AutoTDP after a user-originated power-limit write.
    /// </summary>
    /// <param name="note">Receives the accepted wattage, or null when AutoTDP is not running.</param>
    /// <remarks>
    /// Attached here because this is the one path every surface's power write already goes through:
    /// the overlay row and the native-QAM TDP control both call <see cref="ExecuteCapabilityAsync"/>,
    /// so this is the one place that sees every manual change.
    /// </remarks>
    internal void AttachAutoTdpManualOverride(Action<int>? note) => _autoTdpManualOverride = note;

    /// <summary>Whether AutoTDP is switched on in the persisted configuration.</summary>
    internal bool AutoTdpEnabled => _config.DeviceIntegration.AutoTdpEnabled;

    /// <summary>Turns AutoTDP on or off and persists the choice.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new setting is persisted.</returns>
    /// <remarks>
    /// Persisted rather than session-only, and applied by the ordinary configuration reload, so the
    /// overlay switch and the Settings checkbox are the same setting reached two ways.
    /// </remarks>
    internal Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default) =>
        SetAutoTdpEnabledAsync(!_config.DeviceIntegration.AutoTdpEnabled, cancellationToken);

    /// <summary>Sets AutoTDP to an explicit state and persists the choice.</summary>
    /// <param name="enabled">The state the caller asked for.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the setting is persisted.</returns>
    /// <remarks>
    /// The comparison happens inside the transition gate so concurrent surfaces cannot invert a
    /// newer persisted choice.
    /// </remarks>
    internal async Task SetAutoTdpEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_config.DeviceIntegration.AutoTdpEnabled == enabled)
            {
                Log.Info($"AutoTDP is already {(enabled ? "on" : "off")}; nothing to persist.");
                return;
            }

            await PersistConfigurationAsync(
                config => config.DeviceIntegration.AutoTdpEnabled = enabled,
                cancellationToken).ConfigureAwait(false);
            Log.Info($"AutoTDP switched {(enabled ? "on" : "off")} from the Device surface.");
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Claims managed controller input for one visible WSGM surface.</summary>
    internal Task ClaimUiAsync(string surfaceId, CancellationToken cancellationToken = default) =>
        _controllers.ClaimUiAsync(surfaceId, cancellationToken);

    /// <summary>Releases one visible WSGM surface's managed controller claim.</summary>
    internal void ReleaseUi(string surfaceId) => _controllers.ReleaseUi(surfaceId);

    /// <summary>Sends a bounded rear-button pulse through the managed virtual target.</summary>
    internal Task<bool> PulseRearButtonAsync(
        int button,
        CancellationToken cancellationToken = default) =>
        _controllers.PulseRearButtonAsync(button, cancellationToken);

    /// <summary>Whether controller management may run in this configuration.</summary>
    internal bool ControllerManagementEnabled =>
        _config.DeviceIntegration.ControllerManagementEnabled && _config.DeviceIntegration.Enabled;

    /// <summary>Changes the global default managed-controller target and persists the choice.</summary>
    /// <param name="target">The target to make the global default.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The controller state after the change was applied.</returns>
    /// <remarks>
    /// The stored setting is changed and then the manager is asked to re-resolve, in that order, so
    /// the persisted value and the running target cannot disagree if the apply fails — the setting
    /// is what the next reload and the Settings checkbox both read. Per-application overrides are
    /// deliberately untouched: this is the global default, and silently clearing an override the
    /// user set for one game would be a surprising side effect of changing the default.
    /// </remarks>
    internal async Task<ControllerManagerStatus> SetControllerTargetAsync(
        ManagedControllerTarget target,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppConfig persisted = await PersistConfigurationAsync(
                config => config.DeviceIntegration.ControllerTarget = target,
                cancellationToken).ConfigureAwait(false);
            Log.Info($"Controller target set to {target} from the Device surface.");
            return await _controllers.ApplySelectionAsync(
                ControllerSelection.From(persisted.DeviceIntegration),
                _runningApplicationId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Persists one configuration change and announces it to configuration consumers.</summary>
    /// <remarks>Called with the transition gate held, so concurrent surfaces cannot interleave.</remarks>
    private async Task<AppConfig> PersistConfigurationAsync(
        Action<AppConfig> mutate,
        CancellationToken cancellationToken)
    {
        AppConfig persisted = await Task.Run(
            () => ConfigStore.Mutate(mutate),
            cancellationToken).ConfigureAwait(false);
        _config = persisted;
        ConfigurationChanged?.Invoke();
        return persisted;
    }

    /// <summary>Routes one semantic capability command through current validation and serialization.</summary>
    /// <param name="capabilityId">The capability being commanded.</param>
    /// <param name="instanceId">Its instance, or null for a single-instance capability.</param>
    /// <param name="value">The requested value, or null for an action.</param>
    /// <param name="timeout">How long the command may take.</param>
    /// <param name="origin">Who asked for it, which decides whether AutoTDP steps aside.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The command result reported by the plugin.</returns>
    internal async Task<CapabilityCommandResult> ExecuteCapabilityAsync(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        TimeSpan timeout,
        CapabilityCommandOrigin origin = CapabilityCommandOrigin.User,
        CancellationToken cancellationToken = default)
    {
        CapabilityCommandResult result = await _capabilities.ExecuteAsync(
            capabilityId,
            instanceId,
            value,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (origin is CapabilityCommandOrigin.User)
        {
            NotifyManualPowerChange(capabilityId, instanceId, value, result);
        }

        return result;
    }

    private void NotifyManualPowerChange(
        string capabilityId,
        string? instanceId,
        CapabilityValue? value,
        CapabilityCommandResult result)
    {
        if (_autoTdpManualOverride is not { } note
            || value?.IntegerValue is not { } watts
            || result.Outcome is not (CommandOutcome.AppliedVerified
                or CommandOutcome.AppliedUnverified))
        {
            return;
        }

        bool primaryPowerLimit = _capabilities.Snapshot().Any(view =>
            view.Descriptor.Role is CapabilityRole.PowerSustainedLimit
            && string.Equals(view.Descriptor.CapabilityId, capabilityId, StringComparison.Ordinal)
            && string.Equals(view.Descriptor.InstanceId, instanceId, StringComparison.Ordinal));
        if (!primaryPowerLimit)
        {
            return;
        }

        // Permanent until the user resumes control, by specification: quietly taking the limit back
        // a few seconds after they set it by hand would make the manual control look broken.
        Log.Info($"AutoTDP paused: the sustained power limit was set to {watts} W by hand.");
        note(watts);
    }

    /// <summary>Attaches WSGM-owned UI and system actions after the shell surfaces exist.</summary>
    internal void ConfigureOemActions(DeviceOemActionServices actions) =>
        _oemActions.ConfigureActions(actions);

    /// <summary>The stored profile for the device this session is talking to, when there is one.</summary>
    /// <remarks>
    /// Keyed by the machine identity rather than by the package, so a user who swaps plugins keeps
    /// the values they set for this machine. Null before an identity is known, which is why every
    /// caller has to tolerate a missing profile rather than creating one eagerly.
    /// </remarks>
    private DeviceDesiredProfile? CurrentProfile
    {
        get
        {
            if (_identity is null)
            {
                return null;
            }

            string identityKey = DeviceMachineIdentity.StableKey(_identity);
            return _config.DeviceIntegration.Profiles.FirstOrDefault(item => string.Equals(
                item.DeviceIdentityKey,
                identityKey,
                StringComparison.Ordinal));
        }
    }

    /// <summary>The catalog holding the installed package's glyph profiles.</summary>
    /// <remarks>
    /// Exposed so one <c>PhysicalGlyphService</c> can be built over it and share its invalidation.
    /// The catalog is immutable data plus a change event; handing it out does not let a consumer
    /// load, replace or reach past a profile.
    /// </remarks>
    internal PhysicalGlyphCatalog PhysicalGlyphCatalog => _physicalGlyphs;

    /// <summary>The named hardware profiles this machine's stored values actually define.</summary>
    /// <remarks>
    /// Derived rather than declared. A profile exists exactly when some capability stores a value
    /// under its name, so there is no separate catalog to keep in step with the values — and a
    /// profile cannot be offered for selection while it would change nothing.
    /// </remarks>
    internal IReadOnlyList<string> HardwareProfileIds
    {
        get
        {
            DeviceDesiredProfile? profile = CurrentProfile;
            if (profile is null)
            {
                return [];
            }

            return profile.Capabilities
                .SelectMany(capability => capability.HardwareProfiles)
                .Select(value => value.ProfileId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .Take(32)
                .ToArray();
        }
    }

    /// <summary>The named hardware profile currently selected, or null for none.</summary>
    internal string? SelectedHardwareProfileId => CurrentProfile?.SelectedHardwareProfileId;

    /// <summary>Selects a named hardware profile, or none, and persists the choice.</summary>
    /// <param name="profileId">The profile to select, or null to select none.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the choice is persisted and applied.</returns>
    /// <remarks>
    /// The stored profile is created if this machine has none, because selecting is the first thing
    /// a user can do and refusing until some other write happened first would be arbitrary. Applying
    /// is `UpdateCapabilityDesiredContext`, which is the same path a configuration reload takes.
    /// </remarks>
    internal async Task SelectHardwareProfileAsync(
        string? profileId,
        CancellationToken cancellationToken = default)
    {
        if (_identity is null)
        {
            return;
        }

        string identityKey = DeviceMachineIdentity.StableKey(_identity);
        string? normalized = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim();
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistConfigurationAsync(
                config =>
                {
                    DeviceDesiredProfile? stored = config.DeviceIntegration.Profiles
                        .FirstOrDefault(item => string.Equals(
                            item.DeviceIdentityKey,
                            identityKey,
                            StringComparison.Ordinal));
                    if (stored is null)
                    {
                        stored = new DeviceDesiredProfile { DeviceIdentityKey = identityKey };
                        config.DeviceIntegration.Profiles.Add(stored);
                    }

                    stored.SelectedHardwareProfileId = normalized;
                },
                cancellationToken).ConfigureAwait(false);
            UpdateCapabilityDesiredContext();
            UpdateOemConfiguration();
            Log.Info($"Hardware profile selected: {normalized ?? "(none)"}.");
        }
        finally
        {
            _transitionGate.Release();
        }

        // Hardware reconciliation stays outside the transition gate: each capability write is
        // independently bounded and must not block unrelated lifecycle transitions.
        await ReconcileDesiredValuesAsync(
            $"hardware profile {normalized ?? "(none)"}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The authored fan profiles for the active device, the choice in force, and its scope.</summary>
    /// <returns>Null when the device has no authored profiles at all.</returns>
    /// <remarks>
    /// Read on every snapshot rather than cached: the answer follows both a configuration reload
    /// and a change of running application, and it is a handful of list lookups against objects
    /// already in memory.
    /// </remarks>
    internal (IReadOnlyList<DeviceAuthoredProfile> Profiles, string? SelectedProfileId, bool ApplicationScoped)?
        AuthoredProfileSelection()
    {
        PluginSettingsScope? scope = ActivePluginScope(candidate => candidate.Profiles.Count > 0);
        if (scope is null)
        {
            return null;
        }

        string? selected = DeviceProfileSelectionStore.ReadSelection(
            scope,
            DeviceAuthoredProfileCapabilities.FanCurve,
            _runningApplicationId,
            out bool applicationScoped);
        return (scope.Profiles, selected, applicationScoped);
    }

    /// <summary>Advances the authored fan profile and applies the new choice.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the selection is persisted and applied.</returns>
    /// <remarks>
    /// Scoped to the running application when there is one and global otherwise, because that is
    /// what a user means by changing this row: mid-game they are changing it for what they are
    /// playing, and on the desktop there is no per-game scope to mean.
    /// <para>
    /// Persisted first, then applied. The reverse order leaves the device running a profile the
    /// configuration does not name if the save fails, which survives into the next session as a
    /// device state nothing explains.
    /// </para>
    /// </remarks>
    internal async Task CycleAuthoredProfileAsync(CancellationToken cancellationToken = default)
    {
        PluginSettingsScope? current = ActivePluginScope(candidate => candidate.Profiles.Count > 0);
        if (current is null)
        {
            Log.Info("Fan profile cycle ignored: no profiles are authored for this device.");
            return;
        }

        string? applicationId = _runningApplicationId;
        string? selected = DeviceProfileSelectionStore.ReadSelection(
            current,
            DeviceAuthoredProfileCapabilities.FanCurve,
            applicationId,
            out _);

        // NextProfile's contract includes "none", so a user can cycle back off a profile without
        // opening Settings — the same wrap the hardware-profile row already offers.
        string? next = DeviceOverlayBridge.NextProfile(
            [.. current.Profiles.Select(profile => profile.ProfileId)],
            selected);

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistConfigurationAsync(
                config =>
                {
                    PluginSettingsScope? scope = config.DeviceIntegration.PluginSettings
                        .FirstOrDefault(candidate => string.Equals(
                            candidate.DeviceDefinitionId,
                            current.DeviceDefinitionId,
                            StringComparison.Ordinal)
                            && string.Equals(
                                candidate.PluginId,
                                current.PluginId,
                                StringComparison.Ordinal));
                    if (scope is not null)
                    {
                        DeviceProfileSelectionStore.SetSelection(
                            scope,
                            DeviceAuthoredProfileCapabilities.FanCurve,
                            next,
                            applicationId is { Length: > 0 }
                                ? DeviceProfileScope.Application
                                : DeviceProfileScope.Global,
                            applicationId);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }

        await ApplyAuthoredProfilesAsync(applicationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies the authored profile in force for the running application.</summary>
    /// <param name="applicationId">The running application identity, or null for none.</param>
    /// <param name="cancellationToken">Cancels the device writes.</param>
    /// <remarks>
    /// Every failure here is contained. A profile that cannot be applied is a degraded feature, not
    /// a reason to fault the session, and the applier already logs which step refused it.
    /// </remarks>
    private async Task ApplyAuthoredProfilesAsync(
        string? applicationId,
        CancellationToken cancellationToken)
    {
        PluginSettingsScope? scope = ActivePluginScope(
            candidate => candidate.ProfileSelections.Count > 0);
        if (scope is null)
        {
            return;
        }

        foreach (DeviceProfileSelection selection in scope.ProfileSelections)
        {
            try
            {
                await DeviceProfileApplier.ApplyAsync(
                    scope.ProfileSelections,
                    scope.Profiles,
                    selection.CapabilityId,
                    applicationId,
                    DescribeCapability,
                    (capabilityId, value, token) => ExecuteCapabilityAsync(
                        capabilityId,
                        null,
                        value,
                        TimeSpan.FromSeconds(5),
                        CapabilityCommandOrigin.AutomaticControl,
                        token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn(
                    $"Applying the device profile for '{selection.CapabilityId}' failed: "
                    + ex.Message);
            }
        }
    }

    /// <summary>Reads the descriptor the device publishes right now for one capability.</summary>
    /// <remarks>
    /// At apply time rather than cached: a plugin republishes its capabilities across a cycle, and a
    /// curve checked against a stale descriptor is exactly the case the pre-apply check exists for.
    /// </remarks>
    private CapabilityDescriptor? DescribeCapability(string capabilityId) =>
        _capabilities.Snapshot().FirstOrDefault(view => string.Equals(
            view.Descriptor.CapabilityId,
            capabilityId,
            StringComparison.Ordinal))?.Descriptor;

    /// <summary>The stored settings scope of the active device and plugin matching a predicate.</summary>
    private PluginSettingsScope? ActivePluginScope(Func<PluginSettingsScope, bool> predicate)
    {
        string? device = _deviceDefinitionId;
        string? plugin = InstalledPackage?.Manifest?.Id;
        if (device is null || plugin is null)
        {
            return null;
        }

        return _config.DeviceIntegration.PluginSettings.LastOrDefault(candidate =>
            string.Equals(candidate.DeviceDefinitionId, device, StringComparison.Ordinal)
            && string.Equals(candidate.PluginId, plugin, StringComparison.Ordinal)
            && predicate(candidate));
    }

    /// <summary>Writes every persistent desired value the hardware does not already hold.</summary>
    /// <param name="reason">What asked for the reconciliation, for the log.</param>
    /// <param name="cancellationToken">Cancels the remaining commands.</param>
    /// <returns>A task completing once every affected capability has been attempted.</returns>
    /// <remarks>
    /// Per-capability and independent: one refusal must not stop the rest, because a profile that
    /// applied its fan curve but not its power limit is still better than one that applied nothing.
    /// A value the device already reports is skipped, so reselecting the active profile is free.
    /// </remarks>
    private async Task ReconcileDesiredValuesAsync(string reason, CancellationToken cancellationToken)
    {
        await _profileReconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int applied = 0;
            int unchanged = 0;
            int refused = 0;
            int skipped = 0;
            foreach (DeviceCapabilityView view in _capabilities.Snapshot()
                .OrderBy(ReconciliationPriority)
                .ThenBy(view => view.Descriptor.CapabilityId, StringComparer.Ordinal)
                .ThenBy(view => view.Descriptor.InstanceId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!view.Descriptor.SupportsWrite
                    || view.Projection.DesiredValue is not { } desired
                    || view.Projection.DesiredSource is DeviceDesiredValueSource.None)
                {
                    continue;
                }

                if (!view.Projection.State.Available || view.Projection.DesiredValueOutOfRange)
                {
                    skipped++;
                    Log.Warn(
                        $"Desired value not applied for {view.Descriptor.CapabilityId}"
                        + $"{Instance(view.Descriptor.InstanceId)} ({reason}): available="
                        + $"{view.Projection.State.Available}, outOfRange="
                        + $"{view.Projection.DesiredValueOutOfRange}.");
                    continue;
                }

                if (view.Projection.State.ObservedValue is { } observed && SameValue(observed, desired))
                {
                    unchanged++;
                    continue;
                }

                CapabilityCommandResult result = await ExecuteCapabilityAsync(
                    view.Descriptor.CapabilityId,
                    view.Descriptor.InstanceId,
                    desired,
                    TimeSpan.FromSeconds(5),
                    // The user chose this profile, so its values are theirs: a power limit it carries
                    // overrides automatic control exactly as moving the slider would.
                    CapabilityCommandOrigin.User,
                    cancellationToken).ConfigureAwait(false);
                if (result.Outcome is CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified)
                {
                    applied++;
                    continue;
                }

                refused++;
                Log.Warn(
                    $"Desired value refused for {view.Descriptor.CapabilityId}"
                    + $"{Instance(view.Descriptor.InstanceId)} ({reason}): outcome={result.Outcome}, "
                    + $"{result.Reason?.Detail ?? "no detail"}.");
            }

            Log.Info(
                $"Desired-value reconciliation ({reason}): applied={applied}, unchanged={unchanged}, "
                + $"refused={refused}, skipped={skipped}.");
        }
        finally
        {
            _profileReconcileGate.Release();
        }
    }

    /// <summary>Orders coupled power writes so their transient pair remains valid.</summary>
    /// <param name="view">Capability and its observed and desired values.</param>
    /// <returns>Lower values are written first.</returns>
    internal static int ReconciliationPriority(DeviceCapabilityView view)
    {
        int? observed = view.Projection.State.ObservedValue?.IntegerValue;
        int? desired = view.Projection.DesiredValue?.IntegerValue;
        return view.Descriptor.Role switch
        {
            // Lower PL1 before lowering PL2, otherwise the new PL2 can fall below the old PL1.
            CapabilityRole.PowerSustainedLimit when desired < observed => 0,
            // Raise PL2 before raising PL1, otherwise the new PL1 can exceed the old PL2.
            CapabilityRole.PowerSlowLimit when desired > observed => 0,
            CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit => 1,
            _ => 2,
        };
    }

    private static string Instance(string? instanceId) =>
        instanceId is { Length: > 0 } id ? $"/{id}" : string.Empty;

    /// <summary>Compares two capability values, including curves, by content.</summary>
    /// <param name="observed">What the device reports.</param>
    /// <param name="desired">What WSGM wants.</param>
    /// <returns><see langword="true"/> when a write would change nothing.</returns>
    private static bool SameValue(CapabilityValue observed, CapabilityValue desired)
    {
        if (observed.Kind != desired.Kind)
        {
            return false;
        }

        // Field by field rather than record equality: CurveValue is compared by reference there,
        // which would report every curve as different and rewrite a fan table on each pass.
        return observed.Kind switch
        {
            CapabilityValueKind.Boolean => observed.BooleanValue == desired.BooleanValue,
            CapabilityValueKind.Integer => observed.IntegerValue == desired.IntegerValue,
            CapabilityValueKind.Choice => string.Equals(
                observed.ChoiceValue,
                desired.ChoiceValue,
                StringComparison.Ordinal),
            CapabilityValueKind.Color => observed.ColorValue == desired.ColorValue,
            CapabilityValueKind.Curve => observed.CurveValue.SequenceEqual(desired.CurveValue),
            _ => false,
        };
    }

    private void UpdateCapabilityDesiredContext()
    {
        DeviceDesiredProfile? profile = CurrentProfile;
        bool onAcPower = !NativeMethods.GetSystemPowerStatus(out NativeMethods.SystemPowerStatus power)
            || power.ACLineStatus != 0;
        _capabilities.UpdateDesiredContext(
            profile,
            onAcPower,
            profile?.SelectedHardwareProfileId,
            applicationId: null);
    }

    private void UpdateOemConfiguration()
    {
        DeviceDesiredProfile? profile = CurrentProfile;
        _oemActions.UpdateConfiguration(
            profile,
            _config.DeviceIntegration.ControllerManagementEnabled,
            _config.DeviceIntegration.ControllerTarget);
    }

    private void LoadPhysicalGlyphProfiles(InstalledDevicePackage package)
    {
        try
        {
            GlyphPackageImportResult imported = GlyphPackageImporter.Import(
                new ImmutableGlyphPackageDirectorySource(package.PackagePath));
            _physicalGlyphs.ReplacePackageProfiles(imported.Profiles);
            foreach (GlyphPackageImportError error in imported.Errors)
            {
                Log.Warn(
                    $"Device glyph profile rejected: profile={error.ProfileId}, code={error.Code}, "
                        + $"path={error.Path}, detail={error.Message}");
            }

            Log.Info(
                $"Device glyph catalog: package={package.Manifest?.Id}, "
                    + $"profiles={imported.Profiles.Count}, rejected={imported.Errors.Count}.");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            _physicalGlyphs.ReplacePackageProfiles([]);
            Log.Warn($"Device glyph catalog unavailable: {exception.Message}");
        }
    }

    private static Task<DevicePackageDiscovery> DiscoverPackageAsync(
        CancellationToken cancellationToken) =>
        Task.Run(
            () => DevicePackagePolicy.Discover(DeviceInstallationPaths.InstalledPackageRoot),
            cancellationToken);

    private DeviceCoordinatorDiagnosticsSnapshot DiagnosticsSnapshot()
    {
        IReadOnlyList<DeviceCapabilityView> capabilities = _capabilities.Snapshot();
        return new DeviceCoordinatorDiagnosticsSnapshot
        {
            State = State,
            InstalledPackage = InstalledPackage?.Manifest is { } manifest
                ? new DeviceInstalledPackageDiagnostic(manifest.Id, manifest.Version)
                : null,
            CycleGeneration = Interlocked.Read(ref _cycleGeneration),
            CapabilityCount = capabilities.Count,
            HealthyCapabilityCount = capabilities.Count(capability =>
                capability.Projection.State.Available
                && capability.Projection.State.Quality is HardwareStateQuality.Observed
                    or HardwareStateQuality.Verified),
            FaultedCapabilityCount = capabilities.Count(capability =>
                capability.Projection.State.Quality is HardwareStateQuality.Faulted),
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    private void OnLifecycleState(DevicePluginState state)
    {
        if (state.CycleGeneration != _cycleGeneration)
        {
            Log.Warn(
                $"Device lifecycle notification rejected as stale: "
                    + $"cycle={state.CycleGeneration}, current={_cycleGeneration}.");
            return;
        }

        SetDeviceDefinitionId(state.DeviceDefinitionId);
        SetState(state.State);
    }

    /// <summary>Records which device definition the plugin matched.</summary>
    /// <param name="deviceDefinitionId">The matched definition, or null when detection did not match.</param>
    /// <remarks>
    /// Every glyph surface — the Steam Input page, the overlay's glyph rows, and the navigation
    /// hints — resolves through <see cref="PhysicalGlyphSelectionSnapshot"/>, which will only return
    /// a profile that names the active device. The plugin publishes it with lifecycle state;
    /// retaining a prior cycle's value after a non-match would select artwork and
    /// authored profiles for hardware the active cycle did not identify.
    /// </remarks>
    private void SetDeviceDefinitionId(string? deviceDefinitionId)
    {
        string? normalized = string.IsNullOrWhiteSpace(deviceDefinitionId)
            ? null
            : deviceDefinitionId;
        if (string.Equals(_deviceDefinitionId, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _deviceDefinitionId = normalized;
        Log.Info(normalized is null
            ? "Device definition cleared: the active cycle did not match hardware."
            : $"Device definition matched: {normalized}.");
        _physicalGlyphs.SetActiveDevice(normalized);
    }

    private void SetState(DeviceCycleState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        Log.Info($"Device cycle: state={state}, cycleGeneration={_cycleGeneration}.");
        StateChanged?.Invoke(state);
    }

    private void Observe(Task task, string operation)
    {
        Task observed = CompleteObservedAsync(task, operation);
        lock (_backgroundGate)
        {
            _backgroundTasks.Add(observed);
        }
        _ = RemoveObservedAsync(observed);
    }

    private async Task CompleteObservedAsync(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"Device cycle {operation} failed", ex);
        }
    }

    private async Task RemoveObservedAsync(Task observed)
    {
        await observed.ConfigureAwait(false);
        lock (_backgroundGate)
        {
            _backgroundTasks.Remove(observed);
        }
    }
}

/// <summary>Complete retained outcome of controller handoff, plugin stop, detach, and disposal.</summary>
internal sealed record DeviceClientTeardownResult(IReadOnlyList<Exception> Failures)
{
    internal static DeviceClientTeardownResult Clean { get; } = new([]);

    internal bool Verified => Failures.Count == 0;

    internal Exception ToException() => Failures.Count == 1
        ? Failures[0]
        : new AggregateException("Multiple device teardown steps were unverified.", Failures);
}

internal sealed class DeviceTeardownFailureTracker
{
    private readonly object _gate = new();
    private readonly List<Exception> _failures = [];

    internal bool HasFailures
    {
        get
        {
            lock (_gate)
            {
                return _failures.Count > 0;
            }
        }
    }

    internal void Retain(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            _failures.Add(failure);
        }
    }

    internal void ResolveAfterVerifiedOwnerTeardown()
    {
        lock (_gate)
        {
            _failures.Clear();
        }
    }

    internal IReadOnlyList<Exception> Drain()
    {
        lock (_gate)
        {
            Exception[] retained = _failures.ToArray();
            _failures.Clear();
            return retained;
        }
    }
}
