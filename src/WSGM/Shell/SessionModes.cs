using System;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Session-mode coordinator: owns the game/desktop mode transitions
/// (explorer, display scale, Steam open/close, monitor pause) and
/// the shared Steam start + warning flow. ShellSession uses it at boot, the
/// overlay's buttons drive it at runtime; OverlayController stays the UI owner
/// (lease lifecycle, window) and surfaces warnings via <see cref="SteamStartFailed"/>.</summary>
public sealed class SessionModes
{
    /// <summary>The warning shown when the required Steam installation cannot be found.</summary>
    public const string SteamNotFoundWarning = "Steam was not found on this PC. Install Steam — WSGM is Steam-exclusive.";

    /// <summary>The warning shown when Steam Big Picture could not be started.</summary>
    public const string BigPictureStartFailedWarning = "Couldn't start Steam Big Picture.";

    private AppConfig _config;
    private readonly SteamMonitor? _monitor;
    private readonly ExplorerDesktopHost? _desktopHost;
    private readonly object _homeLaunchGate = new();
    private bool _homeLaunchInProgress;
    private DateTime _lastHomeLaunchUtc;

    private static readonly TimeSpan HomeLaunchCooldown = TimeSpan.FromSeconds(5);
    // Budget for the WHOLE orderly exit: first attempt including ExplorerControl's
    // linger grace plus the respawn retry, which shares this deadline rather than
    // starting a fresh one (see docs\boot-and-shell.md). Fails open when explorer
    // is genuinely wedged.
    private static readonly TimeSpan ExplorerExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The warning shown when explorer refused its orderly exit and the
    /// session stayed in desktop mode (fail open, never a half game mode).</summary>
    public const string ExplorerExitFailedWarning =
        "Couldn't exit Windows Explorer safely. Desktop mode was preserved.";

    /// <summary>The warning shown when Explorer was recovered through the scheduler and is usable,
    /// but its process semantics do not support launchers that require job breakaway.</summary>
    public const string ExplorerDesktopDegradedWarning =
        "Windows Explorer was restored in recovery mode. Sign out or reboot before launching games from desktop tools.";

    /// <summary>The warning shown when no usable Explorer taskbar could be restored.</summary>
    public const string ExplorerDesktopStartFailedWarning =
        "Windows Explorer could not be restored. WSGM returned to Game Mode.";

    /// <summary>The warning shown when a dispatched Explorer may still be initializing, so WSGM
    /// deliberately avoids creating a competing replacement taskbar.</summary>
    public const string ExplorerDesktopPendingWarning =
        "Windows Explorer did not finish starting. WSGM will not create a competing taskbar; sign out or reboot to recover the desktop.";

    /// <summary>The warning shown when the current desktop cannot safely supply a normal shell
    /// launch owner, most commonly after upgrading beside an older job-bound Explorer.</summary>
    public const string ExplorerTakeoverRefusedWarning =
        "Game Mode could not safely take over this Windows Explorer. Desktop mode was preserved; sign out or reboot once before retrying.";

    /// <summary>Raised (on the caller's thread) when <see cref="StartOrFocusSteam"/>
    /// could not bring Steam up, with the user-facing warning text.</summary>
    public event Action<string>? SteamStartFailed;

    /// <summary>Raised (on the UI thread — the transition posts back there after the
    /// off-thread Big Picture close) during a desktop-mode transition, after Steam
    /// left Big Picture but BEFORE explorer starts. Listeners that own per-game-mode
    /// resources which must not coexist with explorer (the tray host's Shell_TrayWnd
    /// — explorer's taskbar creates its own) tear down here.</summary>
    public event Action? DesktopModeStarting;

    /// <summary>Raised (on the UI thread — the transition completes there after the
    /// off-thread explorer shutdown) after a game-mode transition has removed
    /// explorer from the session. Listeners recreate per-game-mode resources
    /// (tray host) here.</summary>
    public event Action? GameModeEntered;

    /// <summary>Awaited (bounded) immediately before a transition asks Steam for Big Picture, so
    /// the owner can retract injected Steam UI state and close its transport first: the request
    /// rebuilds Steam's whole front-end, and that rebuild must see stock client state (see
    /// <c>ShellSession.PrepareSteamUiForBigPictureAsync</c>). Null in preview coordinators.</summary>
    internal Func<System.Threading.Tasks.Task>? PrepareSteamUiForBigPictureAsync { get; set; }

    /// <summary>Invoked when a transition worker that may have requested Big Picture has settled,
    /// on every outcome path, so the owner can lift the hold above. Idempotent by contract; also
    /// invoked by transitions that never fired the request.</summary>
    internal Action? SteamUiBigPictureRequestSettled { get; set; }

    /// <summary>Surfaces a shell-transition warning through the overlay's existing warning path.</summary>
    internal void ReportWarning(string warning) => SteamStartFailed?.Invoke(warning);

    /// <summary>Creates a preview-only coordinator. Desktop/game transition requests are inert
    /// because Settings and other safe previews do not own an Explorer recovery host.</summary>
    /// <param name="config">The initial configuration controlling display posture and launch behavior.</param>
    /// <param name="monitor">The optional Steam monitor to pause or resume during transitions.</param>
    public SessionModes(AppConfig config, SteamMonitor? monitor)
    {
        _config = config;
        _monitor = monitor;
        _desktopHost = null;
    }

    /// <summary>Creates the coordinator with the session-owned verified Explorer launch path.</summary>
    internal SessionModes(
        AppConfig config,
        SteamMonitor? monitor,
        ExplorerDesktopHost desktopHost)
        : this(config, monitor)
    {
        ArgumentNullException.ThrowIfNull(desktopHost);
        _desktopHost = desktopHost;
    }

    /// <summary>Applies a freshly loaded config (settings saved in another process).
    /// Reloads replace the config wholesale, so no runtime state may live on it.</summary>
    public void ApplyConfig(AppConfig config)
    {
        _config = config;
    }

    /// <summary>Applies game mode's 100% display scaling. Windows exclusively
    /// owns device posture and touch-keyboard policy.</summary>
    public void ApplyGameModePosture()
    {
        DisplayScale.ApplyGameMode(_config);
    }

    private int _explorerTransition;
    private int _shutdownRequested;

    /// <summary>True while explorer is being brought up or down (mode switch or the
    /// boot takeover). Mode-switch requests arriving in that window are ignored —
    /// two concurrent explorer transitions produced exactly the device-observed
    /// mess of duplicate shutdowns and refused tray hosts (2026-08-07).</summary>
    public bool TransitionInProgress => System.Threading.Volatile.Read(ref _explorerTransition) != 0;

    /// <summary>Marks an explorer transition as running (boot takeover uses this
    /// directly; the mode switches go through <see cref="TryBeginTransition"/>).</summary>
    internal void BeginTransition() => System.Threading.Volatile.Write(ref _explorerTransition, 1);

    /// <summary>Clears the transition flag. Always pair with Begin/TryBegin.</summary>
    internal void EndTransition() => System.Threading.Volatile.Write(ref _explorerTransition, 0);

    /// <summary>Prevents another shell transition from starting during application teardown.</summary>
    internal void RequestShutdown() => System.Threading.Volatile.Write(ref _shutdownRequested, 1);

    /// <summary>Waits for the one already-running shell transition to leave its Explorer and UI
    /// boundaries. The application shutdown coordinator supplies the sole outer deadline.</summary>
    internal async System.Threading.Tasks.Task WaitForTransitionAsync()
    {
        while (TransitionInProgress)
        {
            await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(false);
        }
    }

    internal bool TryBeginTransition(string reason)
    {
        if (System.Threading.Volatile.Read(ref _shutdownRequested) != 0)
        {
            Log.Warn($"Ignoring {reason}: application shutdown is in progress.");
            return false;
        }
        if (System.Threading.Interlocked.CompareExchange(ref _explorerTransition, 1, 0) != 0)
        {
            Log.Warn($"Ignoring {reason}: an explorer transition is already in progress.");
            return false;
        }
        if (System.Threading.Volatile.Read(ref _shutdownRequested) != 0)
        {
            EndTransition();
            Log.Warn($"Ignoring {reason}: application shutdown is in progress.");
            return false;
        }
        return true;
    }

    /// <summary>Desktop mode: stop reacting to Steam (no auto-relaunch, no overlay
    /// pop), drop Steam out of Big Picture, bring the desktop up. Returns
    /// immediately — the blocking Big Picture close, display-scale restore and
    /// explorer start run off the UI thread so the overlay never freezes; only the
    /// monitor pause (before anything can react to Steam leaving) and
    /// <see cref="DesktopModeStarting"/> stay UI-thread work.</summary>
    /// <param name="startSteamDesktop">Whether to start windowed Steam after the verified desktop
    /// is ready; used by boot-splash recovery because that path skips the normal boot launch.</param>
    public void EnterDesktopMode(bool startSteamDesktop = false)
    {
        ExplorerDesktopHost? desktopHost = _desktopHost;
        if (desktopHost is null)
        {
            Log.Info("Ignoring desktop-mode switch in preview-only SessionModes.");
            return;
        }
        if (!TryBeginTransition("desktop-mode switch"))
        {
            return;
        }
        Log.Info("Entering desktop mode.");
        if (_monitor is not null)
        {
            _monitor.Paused = true;
        }
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                try
                {
                    ExitBigPicture();
                    DisplayScale.ApplyDesktopMode(_config);
                }
                catch (Exception ex)
                {
                    Log.Error("Leaving Big Picture / restoring the display scale failed", ex);
                }
                try
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        // UI-thread owned: the listeners destroy the tray host window,
                        // and that must still happen BEFORE explorer starts.
                        DesktopModeStarting?.Invoke();
                    });
                }
                catch (Exception ex)
                {
                    Log.Error("Desktop-mode teardown failed", ex);
                }

                ExplorerDesktopResult result = await RestoreDesktopSafelyAsync(
                    desktopHost, "Explorer desktop restoration failed").ConfigureAwait(false);

                string? rollbackSteamWarning = null;
                if (result.Outcome is ExplorerDesktopOutcome.Failed && result.CanResumeGameModeSafely)
                {
                    try
                    {
                        // Desktop mode closed Big Picture before the launch attempt. A safe rollback
                        // must restore the complete game-mode transaction, not only its taskbar.
                        rollbackSteamWarning =
                            await RequestBigPictureWhilePausedAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Restoring Big Picture after Explorer launch failure failed", ex);
                        rollbackSteamWarning = BigPictureStartFailedWarning;
                    }
                }

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (result.Outcome is ExplorerDesktopOutcome.Failed)
                    {
                        if (result.CanResumeGameModeSafely)
                        {
                            ApplyGameModePosture();
                            GameModeEntered?.Invoke();
                            if (_monitor is not null)
                            {
                                _monitor.Paused = false;
                            }
                            SteamStartFailed?.Invoke(rollbackSteamWarning is null
                                ? ExplorerDesktopStartFailedWarning
                                : $"{ExplorerDesktopStartFailedWarning} {rollbackSteamWarning}");
                        }
                        else
                        {
                            // A dispatched or already-visible shell can still publish its taskbar
                            // after our deadline. Leave game-only surfaces down to prevent dual
                            // Shell_TrayWnd owners and give one explicit recovery instruction.
                            SteamStartFailed?.Invoke(ExplorerDesktopPendingWarning);
                        }
                        return;
                    }

                    if (result.Outcome is ExplorerDesktopOutcome.Degraded)
                    {
                        SteamStartFailed?.Invoke(ExplorerDesktopDegradedWarning);
                    }
                    if (startSteamDesktop)
                    {
                        StartSteamDesktop();
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error("Desktop-mode transition failed", ex);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    SteamStartFailed?.Invoke(ExplorerDesktopPendingWarning));
            }
            finally
            {
                // The failed-restore rollback above may have requested Big Picture.
                SteamUiBigPictureRequestSettled?.Invoke();
                EndTransition();
            }
        });
    }

    /// <summary>Plain desktop Steam start — no Big Picture. Used by the boot
    /// splash's Switch-to-desktop: the boot sequence skips its Big Picture start
    /// once the monitor is paused, but the session should still end up with Steam
    /// available in windowed mode. No-op when Steam already runs.</summary>
    private void StartSteamDesktop()
    {
        if (System.Threading.Volatile.Read(ref _shutdownRequested) != 0)
        {
            Log.Info("Ignoring desktop Steam start: application shutdown is in progress.");
            return;
        }
        if (Steam.IsRunning)
        {
            Log.Info("Skipping desktop Steam start: Steam is already running.");
            return;
        }
        if (Steam.ExePath is { } exe)
        {
            Log.Info("Starting Steam (desktop mode, no Big Picture).");
            AppLauncher.Start(exe, "", elevated: false);
            return;
        }

        Log.Warn("Desktop Steam start skipped: no Steam installation was detected.");
    }

    /// <summary>Game mode: ask Steam to enter Big Picture immediately (the protocol
    /// also boots it if it exited while on the desktop) while Explorer's bounded
    /// orderly shutdown runs off the UI thread. Monitoring stays paused and
    /// game-mode resources are not created until Explorer is verifiably gone; if
    /// Explorer refuses to exit, Big Picture is closed again and desktop mode is
    /// preserved. Returns immediately.</summary>
    public void EnterGameMode()
    {
        ExplorerDesktopHost? desktopHost = _desktopHost;
        if (desktopHost is null)
        {
            Log.Info("Ignoring game-mode switch in preview-only SessionModes.");
            return;
        }
        if (!TryBeginTransition("game-mode switch"))
        {
            return;
        }
        Log.Info("Entering game mode.");
        if (_monitor is not null)
        {
            // Desktop mode already pauses it, but make the transition transactional:
            // no Steam lifecycle edge may react until Explorer is confirmed gone.
            _monitor.Paused = true;
        }
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            bool explorerWasRemoved = false;
            try
            {
                string? steamWarning;
                try
                {
                    // Fire exactly once before Explorer's linger/retry work. Steam can
                    // construct Big Picture during that wait; activating it again after
                    // the transition would interrupt its intro and steal focus again.
                    steamWarning = await RequestBigPictureWhilePausedAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error("Starting Steam Big Picture during game-mode transition failed", ex);
                    steamWarning = BigPictureStartFailedWarning;
                }

                var exited = false;
                // The normal desktop can be recreated only if its current taskbar owner is
                // captured while it still exists. A contaminated/unknown shell is preserved.
                ExplorerPreparationResult preparation = await desktopHost.PrepareForExplorerExitAsync()
                    .ConfigureAwait(false);
                try
                {
                    if (preparation.Prepared)
                    {
                        exited = ExplorerControl.ExitExplorerAndWait(ExplorerExitTimeout);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Explorer exit failed", ex);
                }

                // Decide once on the worker after the bounded exit finishes. Rolling
                // Steam back and then re-checking Explorer on the UI thread could race
                // a late process exit into committing game mode after BP was closed.
                var preserveDesktop = !preparation.Prepared;
                if (!exited && preparation.Prepared)
                {
                    try
                    {
                        preserveDesktop = ExplorerControl.IsRunningInSession();
                    }
                    catch (Exception ex)
                    {
                        // Failure cannot prove Explorer is absent. Preserve the usable
                        // desktop instead of risking a tray-host collision.
                        Log.Error("Checking Explorer state after its exit attempt failed", ex);
                        preserveDesktop = true;
                    }

                    if (!preserveDesktop)
                    {
                        // Exit returned failure without a living shell. Fail open by restoring through
                        // the already-captured anchor; never commit game mode on an unproven exit.
                        ExplorerDesktopResult restored = await desktopHost.RestoreDesktopAsync(
                            TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                        preserveDesktop = restored.Outcome is not ExplorerDesktopOutcome.Failed
                            || restored.LaunchDispatched
                            || restored.ShellSurfacePresent;
                    }
                }
                if (preserveDesktop)
                {
                    try
                    {
                        // The Big Picture request was speculative until Explorer left.
                        // Undo it before the warning overlay reopens on the desktop.
                        ExitBigPicture();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Rolling Steam back after Explorer exit failure failed", ex);
                    }
                }
                explorerWasRemoved = exited && !preserveDesktop;

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (preserveDesktop)
                    {
                        // Fail open (era-proven UX): a half-removed desktop with a
                        // refused tray host is strictly worse than staying put.
                        Log.Warn("Could not exit explorer safely — staying in desktop mode.");
                        SteamStartFailed?.Invoke(preparation.Prepared
                            ? ExplorerExitFailedWarning
                            : ExplorerTakeoverRefusedWarning);
                        return;
                    }
                    ApplyGameModePosture();
                    GameModeEntered?.Invoke();
                    if (_monitor is not null)
                    {
                        _monitor.Paused = false;
                    }
                    if (steamWarning is not null)
                    {
                        SteamStartFailed?.Invoke(steamWarning);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error("Game-mode transition failed", ex);
                if (explorerWasRemoved)
                {
                    await RecoverDesktopAfterFailedGameModeCommitAsync(desktopHost)
                        .ConfigureAwait(false);
                    return;
                }
                try
                {
                    ExitBigPicture();
                }
                catch (Exception rollbackEx)
                {
                    Log.Error("Rolling Steam back after game-mode transition failure failed", rollbackEx);
                }
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    SteamStartFailed?.Invoke(ExplorerExitFailedWarning));
            }
            finally
            {
                SteamUiBigPictureRequestSettled?.Invoke();
                EndTransition();
            }
        });
    }

    /// <summary>Runs the verified desktop restore, converting an exception into the fail-open
    /// result. The exception may have happened after an anchor/scheduler launch crossed its
    /// boundary, so it reports the launch as dispatched — Unknown is unsafe for recreating a
    /// competing Shell_TrayWnd.</summary>
    private static async System.Threading.Tasks.Task<ExplorerDesktopResult> RestoreDesktopSafelyAsync(
        ExplorerDesktopHost desktopHost,
        string failureContext)
    {
        try
        {
            return await desktopHost.RestoreDesktopAsync(TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(failureContext, ex);
            return new ExplorerDesktopResult(
                ExplorerDesktopOutcome.Failed,
                ExplorerDesktopRoute.ScheduledTaskRecovery,
                0,
                0,
                ex.Message,
                launchDispatched: true,
                shellSurfacePresent: false,
                elapsed: TimeSpan.Zero);
        }
    }

    private async System.Threading.Tasks.Task RecoverDesktopAfterFailedGameModeCommitAsync(
        ExplorerDesktopHost desktopHost)
    {
        try
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_monitor is not null)
                {
                    _monitor.Paused = true;
                }
                DesktopModeStarting?.Invoke();
            });
        }
        catch (Exception ex)
        {
            // Continue to the fail-open desktop restore even when one partially-created game-mode
            // surface fails to tear down. Leaving the session without Explorer is the worse state.
            Log.Error("Rolling back game-mode resources after commit failure failed", ex);
        }

        try
        {
            ExitBigPicture();
        }
        catch (Exception ex)
        {
            Log.Error("Rolling Steam back after game-mode commit failure failed", ex);
        }

        ExplorerDesktopResult restored = await RestoreDesktopSafelyAsync(
            desktopHost, "Restoring Explorer after game-mode commit failure failed")
            .ConfigureAwait(false);

        string warning = restored.Outcome switch
        {
            ExplorerDesktopOutcome.Degraded => ExplorerDesktopDegradedWarning,
            ExplorerDesktopOutcome.Failed => ExplorerDesktopPendingWarning,
            _ => ExplorerExitFailedWarning,
        };
        try
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                SteamStartFailed?.Invoke(warning));
        }
        catch (Exception ex)
        {
            Log.Error("Reporting game-mode rollback result failed", ex);
        }
    }

    /// <summary>Asks Steam to leave Big Picture (Steam keeps running). No-op if
    /// Steam isn't running.</summary>
    public void ExitBigPicture()
    {
        // Live check, not the up-to-5 s-stale monitor poll: entering desktop mode
        // right after Steam started must still send the close URL.
        if (!Steam.IsRunning)
        {
            return;
        }
        Log.Info("Exiting Steam Big Picture.");
        AppLauncher.StartProtocol(Steam.CloseBigPictureUrl);
    }

    /// <summary>Deliberately stops Steam (graceful steam://exit). Pauses the monitor
    /// first so neither auto-relaunch nor the exit-overlay reaction fires.</summary>
    public void CloseSteam()
    {
        if (_monitor is not null)
        {
            _monitor.Paused = true;
        }
        Log.Info("Closing Steam (steam://exit).");
        AppLauncher.StartProtocol(Steam.ExitUrl);
    }

    /// <summary>Start and focus are the same operation: steam://open/bigpicture
    /// re-activates a running Big Picture (UIPI-proof) and boots Steam when it
    /// isn't running. Re-arms the monitor (desktop mode and close-Steam pause it).
    /// Failures surface through <see cref="SteamStartFailed"/>.</summary>
    public void StartOrFocusSteam()
    {
        if (System.Threading.Volatile.Read(ref _shutdownRequested) != 0)
        {
            Log.Info("Ignoring Steam start/focus: application shutdown is in progress.");
            return;
        }
        if (_monitor is not null)
        {
            _monitor.Paused = false;
        }
        if (_monitor?.IsAlive == true)
        {
            FocusSteam();
            return;
        }

        if (!TryBeginHomeLaunch())
        {
            return;
        }

        try
        {
            var warning = StartBigPicture();
            if (warning is not null)
            {
                SteamStartFailed?.Invoke(warning);
            }
        }
        finally
        {
            EndHomeLaunch();
        }
    }

    /// <summary>Requests or focuses Big Picture without changing monitor state.
    /// The serialized game-mode transition uses this while the monitor remains
    /// paused, so it must not take the unrelated Home-button cooldown.</summary>
    /// <summary>How long the Steam UI retraction may delay the Big Picture request. Bounded so a
    /// broken CEF session can never block the mode switch itself.</summary>
    private static readonly TimeSpan SteamUiPrepareTimeout = TimeSpan.FromSeconds(5);

    private async System.Threading.Tasks.Task<string?> RequestBigPictureWhilePausedAsync()
    {
        if (PrepareSteamUiForBigPictureAsync is { } prepare)
        {
            try
            {
                System.Threading.Tasks.Task work = prepare();
                System.Threading.Tasks.Task first = await System.Threading.Tasks.Task
                    .WhenAny(work, System.Threading.Tasks.Task.Delay(SteamUiPrepareTimeout))
                    .ConfigureAwait(false);
                if (first == work)
                {
                    await work.ConfigureAwait(false);
                }
                else
                {
                    Log.Warn("Steam UI retraction did not finish before the Big Picture request; "
                        + "continuing with the transition.");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam UI retraction before the Big Picture request failed: {ex.Message}");
            }
        }
        if (_monitor?.IsAlive == true)
        {
            FocusSteam();
            return null;
        }
        return StartBigPicture();
    }

    /// <summary>The one Steam start + warning flow (shared by boot and the overlay):
    /// install check, then Big Picture launch. Returns the user-facing warning to
    /// surface, or null on success.</summary>
    public string? StartBigPicture()
    {
        if (System.Threading.Volatile.Read(ref _shutdownRequested) != 0)
        {
            Log.Info("Ignoring Big Picture start: application shutdown is in progress.");
            return null;
        }
        if (!Steam.IsInstalled)
        {
            Log.Warn("Steam is not installed — showing overlay instead.");
            return SteamNotFoundWarning;
        }
        Log.Info("Starting Steam Big Picture.");
        // Read at launch time, not captured: a config reload replaces _config wholesale, and both
        // the cold start and the auto-relaunch after Steam exits come through here.
        var result = Steam.LaunchBigPicture(_config.SteamLaunchUnelevated);
        return result.Started ? null : BigPictureStartFailedWarning;
    }

    /// <summary>Brings Steam Big Picture to the foreground when the monitor sees it alive.</summary>
    public void FocusSteam()
    {
        if (System.Threading.Volatile.Read(ref _shutdownRequested) != 0)
        {
            Log.Info("Ignoring Steam focus: application shutdown is in progress.");
            return;
        }
        if (_monitor?.IsAlive == true)
        {
            // Protocol re-activation self-focuses even against an elevated target.
            AppLauncher.StartProtocol(Steam.OpenBigPictureUrl);
        }
    }

    private bool TryBeginHomeLaunch()
    {
        lock (_homeLaunchGate)
        {
            if (_homeLaunchInProgress || DateTime.UtcNow - _lastHomeLaunchUtc < HomeLaunchCooldown)
            {
                Log.Warn("Skipping duplicate home-app start request.");
                return false;
            }
            _homeLaunchInProgress = true;
            return true;
        }
    }

    private void EndHomeLaunch()
    {
        lock (_homeLaunchGate)
        {
            _homeLaunchInProgress = false;
            _lastHomeLaunchUtc = DateTime.UtcNow;
        }
    }

}
