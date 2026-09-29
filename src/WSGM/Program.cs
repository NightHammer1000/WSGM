using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM;

/// <summary>The intentionally narrow operating modes accepted by the executable.</summary>
public enum RunMode
{
    /// <summary>Runs the game-mode shell session (service boot or --shell). Explorer
    /// stays the registered Windows shell; this session ends it and takes the screen.</summary>
    Shell,

    /// <summary>Runs the settings or welcome UI without changing shell state.</summary>
    Settings,

    /// <summary>Runs the manual overlay smoke-test session.</summary>
    OverlayTest,
}

internal enum DevicePluginMaintenanceMode
{
    None,
    Install,
    Remove,
    Invalid,
}

/// <summary>Defines the safe command-line entry points and application bootstrap.</summary>
public static class Program
{
    /// <summary>Gets the mode selected from the current command line.</summary>
    public static RunMode Mode { get; private set; } = RunMode.Settings;

    /// <summary>Gets whether this shell process was launched by the logon service
    /// (--boot): the session boots over a live, still-initializing explorer that
    /// the takeover flow waits out and then cleanly shuts down.</summary>
    public static bool ServiceBoot { get; private set; }

    private static Mutex? _shellMutex;

    /// <summary>Starts the selected supported application mode.</summary>
    /// <param name="args">The command-line arguments passed to the executable.</param>
    /// <returns>The process exit code.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        // Roslyn implements an async Main through a separate synchronous entry point and does not
        // carry STAThread onto that synthesized method. Keep the real process entry point
        // synchronous so normal startup and Avalonia begin on the Windows STA thread; command-only
        // maintenance may still yield inside the private body while this wrapper owns its result.
        return MainAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> MainAsync(string[] args)
    {
        // Hidden fixed-purpose process used only to preserve normal Explorer parent/job
        // semantics across a shell transition. It must run before logging, package discovery,
        // elevation, Avalonia, or any other WSGM service. The mode accepts no executable or
        // argument input and can start only the canonical Windows Explorer path.
        if (ExplorerShellAnchor.TryRunProcessMode(args, out int anchorExitCode))
        {
            return anchorExitCode;
        }

        // Recovery path: must work even when Avalonia/GPU/config are broken.
        // Keep this ahead of logging too: a broken profile directory must never
        // prevent the user from getting their desktop back.
        if (args.Contains("--restore-shell", StringComparer.OrdinalIgnoreCase))
        {
            ShellRegistration.Uninstall();
            // The user is escaping game mode: also disarm the service boot so the
            // next sign-in is a plain desktop (re-enable in Settings). Best effort —
            // this path must survive a broken profile, and logging is not up yet.
            // boot.json is projected from a defensive load so the disarm still lands
            // when config.json cannot be read; clearing the flag INSIDE config.json is
            // a read-modify-write and goes through the strict mutation path, which
            // aborts rather than replacing the registry recovery snapshots with
            // defaults.
            try { BootManifestWriter.WriteDisabled(ConfigStore.Load()); } catch { }
            try { ConfigStore.Mutate(static c => c.GameModeBootEnabled = false); } catch { }
            // Verify-and-wait: this path returns out of Main straight afterwards, so a
            // queued de-elevation check would be torn down before it ran and the user
            // would be left with an ELEVATED explorer (breaks UWP — invariant 5).
            ExplorerControl.StartExplorerAndVerify();
            // A lease is pipe-backed, so a crashed shell releases it when Windows
            // closes its handles. A live shell can still be releasing normally.
            SteamInputBlocker.ReleaseBestEffort("restore-shell");
            RestoreDisplayScalesBestEffort();
            return 0;
        }

        // Quiet shell-registration restore for the Inno uninstaller: no explorer
        // start, no UI — the uninstaller drives everything else.
        if (args.Contains("--unregister-shell", StringComparer.OrdinalIgnoreCase))
        {
            ShellRegistration.Uninstall();
            SteamInputBlocker.ReleaseBestEffort("unregister-shell");
            return 0;
        }

        DevicePluginMaintenanceMode pluginMaintenance = ParseDevicePluginMaintenance(args);
        if (pluginMaintenance is not DevicePluginMaintenanceMode.None)
        {
            Log.Init();
            return await RunDevicePluginMaintenanceAsync(pluginMaintenance, args)
                .ConfigureAwait(false);
        }

        Log.Init();
        // The Steam UI machinery writes through its own sink so it carries no dependency on this
        // application's logger. Installed here, right after Log.Init, because remote diagnosis of
        // the CEF surface is a pasted wsgm.log and a missed install would silently empty it.
        WsgmSteamUiLog.Install();

        // Elevated one-shots for the UAC prompt-level toggle (see UacSettings).
        if (args.Contains("--set-uac-silent", StringComparer.OrdinalIgnoreCase))
        {
            return UacSettings.ApplyDirect(disablePrompts: true) ? 0 : 1;
        }
        if (args.Contains("--restore-uac", StringComparer.OrdinalIgnoreCase))
        {
            return UacSettings.ApplyDirect(disablePrompts: false) ? 0 : 1;
        }
        if (args.Contains("--disable-lock-on-wake", StringComparer.OrdinalIgnoreCase))
        {
            return LockScreenSettings.ApplyDirect(disableSignInOnWake: true) ? 0 : 1;
        }
        if (args.Contains("--restore-lock-on-wake", StringComparer.OrdinalIgnoreCase))
        {
            return LockScreenSettings.ApplyDirect(disableSignInOnWake: false) ? 0 : 1;
        }

        // Elevated one-shots for the Steam Input shim. Steam normally lives under
        // Program Files, which a desktop-mode Settings process cannot write, so the
        // Settings save path re-runs itself through these when a write is refused.
        if (args.Contains("--apply-steam-input-shim", StringComparer.OrdinalIgnoreCase))
        {
            SteamInputShim.SetEnabled(true);
            return SteamInputShim.Reconcile("elevated-apply").State
                is SteamInputShimState.Deployed or SteamInputShimState.UpdatePending
                ? 0
                : 1;
        }

        if (args.Contains("--remove-steam-input-shim", StringComparer.OrdinalIgnoreCase))
        {
            SteamInputShim.Remove("uninstall");
            return 0;
        }

        // Read-only radio diagnostic. Run it on the device, in the session being
        // diagnosed, and read the verdict out of wsgm.log — it answers what the
        // documentation cannot: whether radio control works elevated with no
        // shell, and whether the location gate blocks the Wi-Fi scan.
        if (args.Contains("--radio-probe", StringComparer.OrdinalIgnoreCase))
        {
            return RadioProbe.Run();
        }

        // Elevated one-shot for the uninstaller: puts back every machine-level
        // setting WSGM changed (display scaling, UAC, lock-on-wake).
        if (args.Contains("--uninstall-restore", StringComparer.OrdinalIgnoreCase))
        {
            Installer.RestoreMachineSettings();
            return 0;
        }

        if (args.Contains("--setup", StringComparer.OrdinalIgnoreCase))
        {
            // The gaming-home guard captures a registry snapshot INTO this config and
            // saves it, so it is loaded strictly: an unreadable config.json aborts the
            // capture instead of recording the already-modified value as the pre-WSGM
            // one and persisting defaults over every other recovery snapshot. Setup
            // itself must still complete, so the failure only logs.
            AppConfig? config = null;
            try
            {
                config = ConfigStore.LoadForMutation();
            }
            catch (Exception ex)
            {
                Log.Error("Setup: config.json is unreadable — skipping the gaming-home guard and the boot manifest", ex);
            }
            Installer.InstallApp();
            // Deploy the Steam Input shim only after the payload exists in the
            // install directory. Default-on when config.json is unreadable, because
            // on is the default the property itself carries.
            SteamInputShim.SetEnabled(config?.SteamInputManagementEnabled ?? true);
            SteamInputShim.Reconcile("setup");
            // Self-guarding no-op unless a snapshotted shell value needs restoring —
            // WSGM boots via the logon service over an explorer shell.
            ShellRegistration.Uninstall();
            if (config is not null)
            {
                ShellRegistration.ApplyGamingHomeGuard(config);
                BootManifestWriter.WriteCurrent(config);
            }
            return 0;
        }

        if (ShouldEnforceDevicePackageCardinality(args))
        {
            DevicePackageInventory? inventory;
            try
            {
                inventory = InventoryDevicePackagesForStartup(
                    DeviceInstallationPaths.InstalledPackageRoot,
                    TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (IsDevicePackageSlotGateFailure(ex))
            {
                Log.Error("Device plugin startup inventory failed", ex);
                ShowDevicePackageStartupRefusal(
                    "WSGM could not inspect the protected Device Plugin slot. "
                        + "Use setup or --remove-device-plugin to repair it.\n\n"
                        + ex.Message);
                return 2;
            }
            if (inventory is null)
            {
                const string detail = "The protected Device Plugin slot remained busy during "
                    + "startup. Close Device Plugin maintenance and start WSGM again.";
                Log.Error(detail);
                ShowDevicePackageStartupRefusal(detail);
                return 2;
            }

            Log.Info($"Device plugin startup inventory: {inventory.Cardinality}, "
                + $"roots={inventory.PackageRoots.Count}.");
            if (inventory.Cardinality is DevicePackageCardinality.Multiple)
            {
                string packages = string.Join(
                    Environment.NewLine,
                    inventory.PackageRoots.Select(path => $"- {Path.GetFileName(path)}: {path}"));
                string detail = "WSGM found more than one Device Plugin package root and refused "
                    + "normal startup. No package was opened or selected. Remove the extra package "
                    + "with setup or --remove-device-plugin, then start WSGM again."
                    + Environment.NewLine + Environment.NewLine + packages;
                Log.Error(detail);
                ShowDevicePackageStartupRefusal(detail);
                return 2;
            }
        }

        ServiceBoot = IsServiceBoot(args);
        Mode = DecideMode(args);
        if (ServiceBoot)
        {
            Log.Info($"Run mode: {Mode} (service boot, elevated={ElevationCheck.IsCurrentProcessElevated()}, " +
                     $"session {System.Diagnostics.Process.GetCurrentProcess().SessionId})");
        }
        else
        {
            Log.Info($"Run mode: {Mode}");
        }

        if (Mode == RunMode.Shell)
        {
            // Shell only — --overlay-test is a dev-machine surface and must not
            // trigger a UAC prompt or relaunch elevated.
            // Must run before the shell mutex: the elevated copy takes the mutex,
            // this process only lingers as Winlogon's watched shell process.
            var handedOver = SelfElevation.EnsureElevatedIfConfigured(args);
            if (handedOver is not null)
            {
                return handedOver.Value;
            }
        }

        if (Mode == RunMode.Shell)
        {
            if (!AcquireShellMutex())
            {
                Log.Warn("Another WSGM shell instance is running; exiting.");
                return 0;
            }
            // Record this start BEFORE deciding, so the breaker fires on the
            // 3rd start within 2 minutes (this one included) as documented.
            CrashLoopBreaker.RecordStart();
            if (CrashLoopBreaker.IsLooping())
            {
                Log.Error("Crash loop detected (3+ shell starts within 2 minutes) — " +
                          "game-mode boot DISABLED (re-enable in WSGM settings).");
                // Disarm the service boot: the manifest write works even when
                // config.json cannot be saved, so the next sign-in stays a desktop.
                try
                {
                    BootManifestWriter.WriteDisabled(ConfigStore.Load());
                }
                catch (Exception ex)
                {
                    Log.Warn($"Crash-loop disarm: boot manifest write failed: {ex.Message}");
                }
                try
                {
                    // Read-modify-write, so the strict mutation load: an unreadable
                    // config.json aborts here instead of overwriting the registry
                    // recovery snapshots with defaults. boot.json above already
                    // disarmed the next sign-in either way.
                    ConfigStore.Mutate(static c => c.GameModeBootEnabled = false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Crash-loop disarm: could not clear the game-mode boot flag: {ex.Message}");
                }
                ShellRegistration.Uninstall();
                if (!ExplorerControl.IsRunningInSession())
                {
                    // Same reason as --restore-shell: the disarm exits immediately after
                    // this, so the elevation repair has to complete before we return.
                    ExplorerControl.StartExplorerAndVerify();
                }
                // Lease release first (invariant: fires on EVERY recovery path,
                // ahead of cosmetic restores) — same ordering as --restore-shell.
                SteamInputBlocker.ReleaseBestEffort("crash-loop");
                RestoreDisplayScalesBestEffort();
                // Clear the marker so the next manual start isn't instantly disarmed.
                CrashLoopBreaker.Reset();
                return 1;
            }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Panic("UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        UpdateExitWatcher.Start(
            () => RequestInstallerExit(ApplicationShutdownReason.Update),
            () => RequestInstallerExit(ApplicationShutdownReason.Uninstall));

        try
        {
            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            // Normal shutdown. Settings-only processes skip release unless they
            // acquired a lease themselves (overlay test).
            if (Mode is RunMode.Shell or RunMode.OverlayTest || SteamInputBlocker.IsApplied)
            {
                SteamInputBlocker.ReleaseBestEffort("shutdown");
            }
            if (Mode == RunMode.Shell)
            {
                RestoreDisplayScalesBestEffort();
                // A clean exit is NOT a crash: without this, two update restarts
                // plus a sign-in inside 2 minutes read as a crash loop and disarm
                // the shell (device-observed). Only dirty deaths — which never
                // reach this line — may accumulate toward the breaker.
                CrashLoopBreaker.Reset();
            }
            return exitCode;
        }
        catch (Exception ex)
        {
            Panic("Avalonia lifetime crashed", ex);
            return 1;
        }
    }

    private static void RequestInstallerExit(ApplicationShutdownReason reason) =>
        // Posted jobs only run once StartWithClassicDesktopLifetime pumps the dispatcher.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RunInstallerExitRequest(
            reason,
            Steam.StopForUpdate,
            ApplicationShutdownRequest.Request,
            ApplicationShutdownRequest.ShutdownLifetime));

    /// <summary>The installer-exit ordering, separated from the dispatcher and from Steam so it
    /// can be proven without either.</summary>
    /// <remarks>
    /// Update reserves one bounded Steam/wrapper pre-stop window before the application's own
    /// cleanup deadline; the installer waits for both windows plus handoff margin before its force
    /// fallback. The try/finally is the contract: a failed pre-stop can never prevent WSGM cleanup
    /// from starting. Uninstall deliberately does not stop Steam.
    /// </remarks>
    internal static void RunInstallerExitRequest(
        ApplicationShutdownReason reason,
        Action stopForUpdate,
        Action<ApplicationShutdownReason> requestShutdown,
        Action shutdownLifetime)
    {
        try
        {
            if (reason is ApplicationShutdownReason.Update)
            {
                stopForUpdate();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Steam/update-helper pre-stop failed; WSGM cleanup will still run", ex);
        }
        finally
        {
            requestShutdown(reason);
            shutdownLifetime();
        }
    }

    /// <summary>Resolves the requested mode from explicit flags. No flag means the
    /// safe Settings surface; shell mode is only ever explicit (--shell/--boot).</summary>
    internal static RunMode DecideMode(string[] args)
    {
        if (args.Contains("--shell", StringComparer.OrdinalIgnoreCase) || IsServiceBoot(args))
        {
            return RunMode.Shell;
        }

        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            return RunMode.Settings;
        }

        if (args.Contains("--overlay-test", StringComparer.OrdinalIgnoreCase))
        {
            return RunMode.OverlayTest;
        }

        return RunMode.Settings;
    }

    /// <summary>True when the command line carries the logon service's --boot flag
    /// (kept pure so mode precedence stays testable without a live session).</summary>
    internal static bool IsServiceBoot(string[] args)
        => args.Contains("--boot", StringComparer.OrdinalIgnoreCase);

    private static async Task<int> RunDevicePluginMaintenanceAsync(
        DevicePluginMaintenanceMode mode,
        string[] args)
    {
        if (mode is DevicePluginMaintenanceMode.Invalid)
        {
            Log.Error("Device plugin maintenance: use exactly "
                + "--install-device-plugin <expanded-package-directory> or "
                + "--remove-device-plugin, without other arguments.");
            return 1;
        }

        string? sourceDirectory = null;
        string elevatedArguments = "--remove-device-plugin";
        string operation = "removal";
        if (mode is DevicePluginMaintenanceMode.Install)
        {
            try
            {
                sourceDirectory = Path.GetFullPath(args[1]);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                Log.Error("Device plugin maintenance: source path is invalid", ex);
                return 1;
            }

            elevatedArguments = "--install-device-plugin "
                + SelfElevation.Quote(sourceDirectory);
            operation = "installation";
        }

        bool? elevated = ElevationCheck.IsCurrentProcessElevated();
        if (elevated is false)
        {
            return SelfElevation.RunElevatedAction(
                elevatedArguments,
                $"Device plugin {operation}",
                Timeout.Infinite)
                ? 0
                : 1;
        }
        if (elevated is null)
        {
            Log.Error($"Device plugin maintenance: current elevation could not be verified; {operation} refused.");
            return 1;
        }

        DevicePackageSlotGate? slotGate;
        try
        {
            slotGate = await DevicePackageSlotGate.TryAcquireAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDevicePackageSlotGateFailure(ex))
        {
            Log.Error($"Device plugin maintenance: package-slot ownership could not be verified; {operation} refused.", ex);
            return 1;
        }
        if (slotGate is null)
        {
            Log.Error($"Device plugin maintenance: package-slot startup activity did not settle; {operation} refused.");
            return 1;
        }

        await using (slotGate)
        {
            return await RunDevicePluginMaintenanceUnderGateAsync(
                mode,
                sourceDirectory,
                operation).ConfigureAwait(false);
        }
    }

    private static Task<int> RunDevicePluginMaintenanceUnderGateAsync(
        DevicePluginMaintenanceMode mode,
        string? sourceDirectory,
        string operation) =>
        RunDevicePluginMaintenanceWithOwnerReservationAsync(
            DeviceCoordinator.ProductionOwnerName,
            operation,
            () => RunDevicePluginMaintenanceUnderOwnerAsync(mode, sourceDirectory, operation));

    /// <summary>Runs one package-slot mutation while holding the machine-wide device-owner
    /// marker, so plugin code can never load beside a slot that is being replaced.</summary>
    /// <remarks>
    /// The reservation is held for the WHOLE operation rather than taken per step: a stage that
    /// released it between validation and the swap would let a coordinator start against a slot
    /// that is halfway replaced. Separated from the maintenance body so that "held throughout"
    /// can be proven against a private marker name instead of the production one.
    /// </remarks>
    internal static async Task<int> RunDevicePluginMaintenanceWithOwnerReservationAsync(
        string ownerName,
        string operation,
        Func<Task<int>> maintenance)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        using Mutex? ownerReservation = DeviceCoordinator.TryCreateOwnerMutex(ownerName);
        if (ownerReservation is null)
        {
            Log.Error($"Device plugin maintenance: machine-wide device ownership is active or "
                + $"could not be reserved; {operation} refused.");
            return 1;
        }

        return await maintenance().ConfigureAwait(false);
    }

    private static async Task<int> RunDevicePluginMaintenanceUnderOwnerAsync(
        DevicePluginMaintenanceMode mode,
        string? sourceDirectory,
        string operation)
    {
        try
        {
            if (mode is DevicePluginMaintenanceMode.Remove)
            {
                DevicePackageStager.RemoveInstalledPackage(
                    DeviceInstallationPaths.InstalledPackageRoot);
                Log.Info("Device plugin maintenance: installed slot removed.");
                return 0;
            }

            InstalledDevicePackage installed = await DevicePackageStager.StageAsync(
                sourceDirectory!,
                DeviceInstallationPaths.InstalledPackageRoot).ConfigureAwait(false);
            Log.Info("Device plugin maintenance: installed "
                + $"{installed.Manifest?.Id ?? Path.GetFileName(installed.PackagePath)} "
                + $"into the protected slot at {installed.PackagePath}.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException)
        {
            Log.Error($"Device plugin maintenance: {operation} failed", ex);
            return 1;
        }
    }

    internal static DevicePluginMaintenanceMode ParseDevicePluginMaintenance(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool hasInstall = args.Contains("--install-device-plugin", StringComparer.OrdinalIgnoreCase);
        bool hasRemove = args.Contains("--remove-device-plugin", StringComparer.OrdinalIgnoreCase);
        if (!hasInstall && !hasRemove)
        {
            return DevicePluginMaintenanceMode.None;
        }

        if (args.Length == 1
            && string.Equals(args[0], "--remove-device-plugin", StringComparison.OrdinalIgnoreCase))
        {
            return DevicePluginMaintenanceMode.Remove;
        }

        if (args.Length == 2
            && string.Equals(args[0], "--install-device-plugin", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(args[1])
            && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return DevicePluginMaintenanceMode.Install;
        }

        return DevicePluginMaintenanceMode.Invalid;
    }

    private static DevicePackageInventory? InventoryDevicePackagesForStartup(
        string packageRoot,
        TimeSpan timeout) =>
        DevicePackageSlotGate.TryRunSynchronously(
            timeout,
            () => DevicePackageStager.InventoryEffectiveInstalledPackage(packageRoot));

    /// <summary>Returns whether startup must fail closed for a named-object, filesystem, or
    /// ambiguous/unsafe recovery-slot inspection error.</summary>
    internal static bool IsDevicePackageSlotGateFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or WaitHandleCannotBeOpenedException;
    }

    /// <summary>
    /// Returns whether this invocation is a normal startup that must enforce the one-plugin slot.
    /// Recovery, setup, update/uninstall helpers, and the simulated overlay test never start device
    /// code and therefore bypass the refusal.
    /// </summary>
    internal static bool ShouldEnforceDevicePackageCardinality(string[] args)
    {
        // Overlay test is the only simulated UI root, and it bypasses package discovery only when
        // it is the whole invocation. A mixed command such as --shell --overlay-test resolves to
        // the real shell and must not smuggle that startup past the hard one-plugin gate.
        if (args.Length == 1
            && string.Equals(args[0], "--overlay-test", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] bypass =
        [
            "--restore-shell",
            "--unregister-shell",
            "--set-uac-silent",
            "--restore-uac",
            "--disable-lock-on-wake",
            "--restore-lock-on-wake",
            "--apply-steam-input-shim",
            "--remove-steam-input-shim",
            "--radio-probe",
            "--uninstall-restore",
            "--setup",
            "--install-device-plugin",
            "--remove-device-plugin",
        ];
        return !args.Any(argument => bypass.Contains(argument, StringComparer.OrdinalIgnoreCase));
    }

    private static void ShowDevicePackageStartupRefusal(string detail)
    {
        try
        {
            _ = Interop.NativeMethods.MessageBoxW(
                0,
                detail,
                "WSGM Device Plugin startup refused",
                Interop.NativeMethods.MbOk | Interop.NativeMethods.MbIconError);
        }
        catch
        {
            // The full refusal and absolute paths are already in wsgm.log. A service-boot desktop
            // may not yet permit an interactive user32 surface, but that must not weaken the gate.
        }
    }

    private static bool AcquireShellMutex()
    {
        _shellMutex = new Mutex(initiallyOwned: true, @"Local\WSGM.Shell", out var createdNew);
        if (createdNew)
        {
            return true;
        }
        // The named object survives while ANY handle to it is open (installer
        // probe, diagnostic tool), so createdNew=false only proves it existed —
        // try to actually take ownership before concluding a shell is running.
        try
        {
            return _shellMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died without releasing; ownership passed to us.
            return true;
        }
    }

    /// <summary>Fatal-error handler for shell mode: make sure the session has a
    /// desktop again, then die. The logon service watchdog is the robust outer
    /// recovery layer; this is in-process best effort.</summary>
    private static void Panic(string context, Exception? ex)
    {
        Log.Error($"PANIC ({context})", ex ?? new Exception("unknown"));
        if (Mode == RunMode.Shell)
        {
            ShellRegistration.Uninstall();
            // Best-effort (fails from a non-UI thread, and the dying process
            // destroys the window anyway): don't leave our Shell_TrayWnd up while
            // explorer's taskbar comes back.
            try
            {
                Shell.TrayHost.DestroyActive();
            }
            catch { /* recovery must not throw */ }
            if (!ExplorerControl.IsRunningInSession())
            {
                if (ExplorerShellAnchor.HasRecoveryOwner(WindowFinder.CurrentSessionId))
                {
                    // The verified medium/jobless anchor restores Explorer after this process
                    // actually exits. Starting one here would race that exact owner and the
                    // service watchdog, and could recreate the job-bound desktop Q02 removes.
                    Log.Info("Panic recovery delegated to the verified Explorer shell anchor.");
                }
                else
                {
                    ExplorerControl.StartExplorer();
                }
            }
            RestoreDisplayScalesBestEffort();
        }
        // Same guard as normal shutdown: a crashing settings process must not
        // release a still-running shell's lease.
        if (Mode is RunMode.Shell or RunMode.OverlayTest || SteamInputBlocker.IsApplied)
        {
            SteamInputBlocker.ReleaseBestEffort("panic");
        }
    }

    /// <summary>Game mode forces 100% scaling and that persists in the registry —
    /// every way out of shell mode must put the captured values back.</summary>
    private static void RestoreDisplayScalesBestEffort()
    {
        try
        {
            DisplayScale.RestoreSaved(ConfigStore.Load());
        }
        catch
        {
            // Recovery paths must never be blocked by scaling cleanup.
        }
    }

    /// <summary>Builds the Avalonia application configuration used by all UI modes.</summary>
    /// <returns>The configured Avalonia application builder.</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>Disarms WSGM if the shell process keeps dying at logon: 3 or more
/// shell-mode starts within 2 minutes disarm the service boot automatically
/// (boot.json disabled plus the config flag cleared), so the next sign-in is a plain
/// Explorer desktop. Dropping a legacy shell registration is a migration remnant of
/// the same disarm, not its primary action.</summary>
internal static class CrashLoopBreaker
{
    private static string MarkerPath => Path.Combine(Log.Directory, "shell-starts.txt");

    public static void RecordStart()
    {
        try
        {
            File.AppendAllText(MarkerPath, DateTime.UtcNow.ToString("O") + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>Call AFTER RecordStart so the current start counts toward the 3.</summary>
    public static bool IsLooping()
    {
        try
        {
            if (!File.Exists(MarkerPath))
            {
                return false;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            var all = File.ReadAllLines(MarkerPath)
                .Select(l => DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
                .Where(t => t != DateTime.MinValue)
                .ToArray();
            var recent = all.Count(t => t > cutoff);
            if (recent >= 3)
            {
                return true;
            }
            // Trim stale entries so the file doesn't grow forever.
            if (recent < all.Length)
            {
                File.WriteAllLines(MarkerPath, all.Where(t => t > cutoff).Select(t => t.ToString("O")));
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Clears the marker after the breaker fired, so the next manual
    /// shell start begins with a clean slate instead of being disarmed again.</summary>
    public static void Reset()
    {
        try
        {
            File.Delete(MarkerPath);
        }
        catch { }
    }
}
