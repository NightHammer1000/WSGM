using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>Detect/start/kill explorer.exe within the current session.</summary>
public static class ExplorerControl
{
    /// <summary>Gets whether Explorer is running in the current interactive session.</summary>
    public static bool IsRunningInSession() => WindowFinder.FindProcessIds("explorer").Count > 0;

    /// <summary>Starts Explorer for the current session when it is not already running.</summary>
    public static void StartExplorer() => StartExplorerCore(waitForElevationRepair: false);

    /// <summary>Starts Explorer and, when this process is elevated, BLOCKS until the
    /// de-elevation check has run and repaired Explorer if needed.
    /// <para>For terminal recovery paths only — the crash-loop disarm and
    /// <c>--restore-shell</c> both hand the user a desktop and then exit the process,
    /// so the fire-and-forget verification <see cref="StartExplorer"/> queues would be
    /// torn down before it ever ran, leaving an ELEVATED Explorer behind (which breaks
    /// UWP: touch keyboard, Store apps — invariant 5). Costs the verification delay,
    /// which is why the normal transition path keeps using
    /// <see cref="StartExplorer"/>. <c>Panic()</c> deliberately does NOT use this: that
    /// process is already dying.</para></summary>
    public static void StartExplorerAndVerify() => StartExplorerCore(waitForElevationRepair: true);

    private static void StartExplorerCore(bool waitForElevationRepair)
    {
        try
        {
            var weAreElevated = ElevationCheck.IsCurrentProcessElevated() == true;

            Process.Start(new ProcessStartInfo(ExplorerPath) { UseShellExecute = true });
            Log.Info("Started explorer.exe");

            if (weAreElevated)
            {
                // Win11 explorer normally de-elevates itself through its own
                // scheduled task — but whether that survives a custom shell
                // registration is undocumented. Verify, and repair once if not:
                // an elevated explorer breaks UWP (touch keyboard, store apps).
                if (waitForElevationRepair)
                {
                    // Blocking on purpose, and via Task.Run so the wait can never
                    // deadlock against a captured context: the callers are terminal
                    // recovery paths that exit the process immediately afterwards, so
                    // an un-awaited verification would be torn down before it ran.
                    System.Threading.Tasks.Task.Run(VerifyAndRepairElevation).GetAwaiter().GetResult();
                }
                else
                {
                    System.Threading.Tasks.Task.Run(VerifyAndRepairElevation);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start explorer.exe", ex);
        }
    }

    private static async System.Threading.Tasks.Task VerifyAndRepairElevation()
    {
        try
        {
            // The de-elevation hop goes through Task Scheduler; give it time to land.
            await System.Threading.Tasks.Task.Delay(5000);

            var elevated = false;
            var undetermined = false;
            var seen = false;
            foreach (var pid in WindowFinder.FindProcessIds("explorer"))
            {
                try
                {
                    seen = true;
                    // Three states, not two: IsProcessElevated returns null when
                    // Windows would not answer, and folding that into "unelevated"
                    // reports a repair that never happened.
                    var state = ElevationCheck.IsProcessElevated(pid);
                    if (state == true)
                    {
                        elevated = true;
                    }
                    else if (state is null)
                    {
                        undetermined = true;
                    }
                }
                catch (Exception ex)
                {
                    undetermined = true;
                    Log.Warn($"Explorer elevation query failed: {ex.Message}");
                }
            }

            if (!seen)
            {
                Log.Warn("Explorer verification: no explorer process found 5 s after start.");
                return;
            }
            if (!elevated && undetermined)
            {
                // Restarting a shell we cannot even classify is worse than living
                // with the possibility: leave it alone, but say so in the log.
                Log.Warn("Explorer elevation could not be determined — leaving it alone.");
                return;
            }
            if (!elevated)
            {
                Log.Info("Explorer is running unelevated (self-demotion worked).");
                return;
            }

            Log.Warn("Explorer is running ELEVATED — restarting it via de-elevating scheduled task.");
            KillElevatedExplorerAndWait();
            if (!UnelevatedLauncher.TryStartViaScheduledTask(ExplorerPath))
            {
                // Last resort: an elevated desktop beats no desktop.
                Log.Warn("De-elevated restart failed — starting explorer elevated. " +
                         "UWP features (touch keyboard, store apps) may misbehave.");
                Process.Start(new ProcessStartInfo(ExplorerPath) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Explorer elevation verification failed", ex);
        }
    }

    // Explorer's own Ctrl+Shift taskbar "Exit Explorer" command — the ONLY exit
    // mechanism Winlogon accepts without an AutoRestartShell respawn. Undocumented,
    // so every use is bounded and fails open. The device evidence (kills and
    // Restart Manager both device-DISPROVEN) lives in docs\boot-and-shell.md.
    private const uint ExitExplorerMessage = 0x05B4;

    private static readonly TimeSpan StableAbsence = TimeSpan.FromMilliseconds(500);

    // How long a snapshotted remnant may outlive the destroyed taskbar before it
    // is terminated. Never shortened to fit the remaining budget; the derivation
    // and device evidence live in docs\boot-and-shell.md.
    private static readonly TimeSpan LingerGrace = TimeSpan.FromMilliseconds(8000);

    // How long the respawn-retry waits for the replacement explorer to put up
    // its taskbar before giving up (device logs: it is there in ~3 s).
    private static readonly TimeSpan RespawnTaskbarWait = TimeSpan.FromSeconds(5);
    private static readonly object ExitGate = new();

    /// <summary>Set by the exit core when its failure was a Winlogon respawn —
    /// the one failure mode a single retry reliably recovers (device-observed:
    /// every manual second attempt succeeded). Guarded by <see cref="ExitGate"/>.</summary>
    private static bool _respawnCancelled;

    /// <summary>The pid Winlogon respawned, so the retry can wait for THAT
    /// shell's taskbar. Guarded by <see cref="ExitGate"/>.</summary>
    private static uint _respawnProcessId;

    /// <summary>Requests Explorer's orderly shell exit and verifies boundedly that
    /// no current-session Explorer remains before a replacement tray is created.
    /// Fails OPEN: on refusal, timeout, or a Winlogon respawn the caller must
    /// preserve desktop mode — a replacement explorer is never killed (fighting
    /// AutoRestartShell just loops). Lingering snapshotted processes are
    /// terminated only after explorer already destroyed its taskbar (a shell
    /// extension can hold the process open — device-observed). Serialized so the
    /// boot takeover and an overlay mode switch can never race two exits.</summary>
    /// <param name="timeout">Total budget for the exit and the stability check.</param>
    /// <returns><see langword="true"/> only when Explorer exited without Winlogon
    /// immediately replacing it.</returns>
    public static bool ExitExplorerAndWait(TimeSpan timeout)
    {
        lock (ExitGate)
        {
            // ONE deadline for the whole operation, retry included (a fresh full
            // budget for the retry more than doubled the caller's wait).
            var deadline = DateTime.UtcNow + timeout;
            if (ExitExplorerAndWaitCore(timeout))
            {
                return true;
            }
            // A Winlogon respawn is the one failure a single retry reliably
            // recovers; a second respawn ends the attempt for good, and a
            // replacement is never killed (see docs\boot-and-shell.md).
            if (!_respawnCancelled)
            {
                return false;
            }
            var remaining = deadline - DateTime.UtcNow;
            var taskbarWait = remaining < RespawnTaskbarWait ? remaining : RespawnTaskbarWait;
            if (taskbarWait <= TimeSpan.Zero)
            {
                Log.Warn("No budget left to retry the orderly Explorer exit; staying in desktop mode.");
                return false;
            }
            if (!WaitForReplacementTaskbar(_respawnProcessId, taskbarWait))
            {
                Log.Warn("Respawned Explorer showed no taskbar to retry against; staying in desktop mode.");
                return false;
            }
            remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                Log.Warn("No budget left to retry the orderly Explorer exit; staying in desktop mode.");
                return false;
            }
            Log.Info($"Retrying orderly Explorer exit once against the respawned shell (pid {_respawnProcessId}, {remaining.TotalSeconds:F0}s left).");
            return ExitExplorerAndWaitCore(remaining);
        }
    }

    /// <summary>Waits until the taskbar belongs to the REPLACEMENT explorer, plus a
    /// short settle. The owning-pid check is what makes this safe: the original
    /// shell can still own a dying <c>Shell_TrayWnd</c>, and posting the retry into
    /// the process that was already leaving did nothing.</summary>
    /// <param name="replacementProcessId">The pid Winlogon started.</param>
    /// <param name="timeout">How long to wait for its taskbar.</param>
    private static bool WaitForReplacementTaskbar(uint replacementProcessId, TimeSpan timeout)
    {
        if (replacementProcessId == 0)
        {
            return false;
        }
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var taskbar = Interop.NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (IsWindowOwnedByProcess(taskbar, replacementProcessId)
                && IsCurrentSessionWindow(taskbar))
            {
                // Freshly created; give the message loop a moment before the
                // exit command lands in it.
                System.Threading.Thread.Sleep(500);
                return true;
            }
            System.Threading.Thread.Sleep(200);
        }
        return false;
    }

    private static bool ExitExplorerAndWaitCore(TimeSpan timeout)
    {
        _respawnCancelled = false;
        _respawnProcessId = 0;
        var initialProcessIds = ExplorerProcessIdsInSession();
        if (initialProcessIds.Count == 0)
        {
            return true;
        }

        var taskbar = Interop.NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (!IsCurrentSessionWindow(taskbar))
        {
            Log.Warn("Cannot request orderly Explorer exit: current-session taskbar was not found.");
            return false;
        }
        Interop.NativeMethods.GetWindowThreadProcessId(taskbar, out var taskbarProcessId);
        if (!initialProcessIds.Contains(checked((int)taskbarProcessId)))
        {
            Log.Warn($"Cannot request orderly Explorer exit: taskbar owner pid {taskbarProcessId} is not Explorer.");
            return false;
        }

        Log.Info($"Requesting orderly Explorer exit (pid {taskbarProcessId}).");
        if (!Interop.NativeMethods.PostMessageW(taskbar, ExitExplorerMessage, 0, 0))
        {
            Log.Warn($"Orderly Explorer exit request failed (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var currentProcessIds = ExplorerProcessIdsInSession();
            var replacementProcessId = FindReplacementProcessId(initialProcessIds, currentProcessIds);
            if (replacementProcessId != 0)
            {
                _respawnCancelled = true;
                _respawnProcessId = checked((uint)replacementProcessId);
                Log.Warn($"Winlogon restarted Explorer as pid {replacementProcessId}; takeover cancelled.");
                return false;
            }
            if (currentProcessIds.Count == 0)
            {
                return WaitForStableExplorerAbsence(initialProcessIds, deadline);
            }
            if (!IsWindowOwnedByProcess(taskbar, taskbarProcessId))
            {
                Log.Info("Explorer acknowledged orderly exit and removed its taskbar.");
                break;
            }
            System.Threading.Thread.Sleep(100);
        }

        var taskbarStillPresent = IsWindowOwnedByProcess(taskbar, taskbarProcessId);
        var afterTaskbar = ExplorerProcessIdsInSession();
        var replacementAfterTaskbar = FindReplacementProcessId(initialProcessIds, afterTaskbar);
        // Lingering originals may be terminated only when the orderly exit was
        // acknowledged (taskbar destroyed) and Winlogon has not respawned a shell.
        if (taskbarStillPresent || replacementAfterTaskbar != 0)
        {
            _respawnCancelled = !taskbarStillPresent;
            if (_respawnCancelled)
            {
                _respawnProcessId = checked((uint)replacementAfterTaskbar);
            }
            Log.Warn(taskbarStillPresent
                ? "Explorer did not honor the orderly exit request before timeout."
                : $"Winlogon restarted Explorer as pid {replacementAfterTaskbar}; takeover cancelled.");
            return false;
        }

        // Explorer acknowledged the orderly exit and is winding the shell down, but
        // a shell extension or open folder window can keep the ORIGINAL process
        // alive a moment. Let it leave on its own first: killing it mid-shutdown is
        // what Winlogon respawns (the "two tries" symptom). Only terminate a remnant
        // still present after the grace period. A replacement is never killed.
        var graceStart = DateTime.UtcNow;
        var graceDeadline = graceStart + LingerGrace;
        // The caller's budget can expire first — then the grace was NOT served
        // and the remnant must not be killed (see below).
        if (graceDeadline > deadline)
        {
            graceDeadline = deadline;
        }
        while (DateTime.UtcNow < graceDeadline)
        {
            afterTaskbar = ExplorerProcessIdsInSession();
            replacementAfterTaskbar = FindReplacementProcessId(initialProcessIds, afterTaskbar);
            if (replacementAfterTaskbar != 0)
            {
                _respawnCancelled = true;
                _respawnProcessId = checked((uint)replacementAfterTaskbar);
                Log.Warn($"Winlogon restarted Explorer as pid {replacementAfterTaskbar}; takeover cancelled.");
                return false;
            }
            if (afterTaskbar.Count == 0)
            {
                // Left on its own — the graceful path Winlogon accepts.
                return WaitForStableExplorerAbsence(initialProcessIds, deadline);
            }
            System.Threading.Thread.Sleep(100);
        }

        afterTaskbar = ExplorerProcessIdsInSession();
        replacementAfterTaskbar = FindReplacementProcessId(initialProcessIds, afterTaskbar);
        if (replacementAfterTaskbar != 0)
        {
            _respawnCancelled = true;
            _respawnProcessId = checked((uint)replacementAfterTaskbar);
            Log.Warn($"Winlogon restarted Explorer as pid {replacementAfterTaskbar}; takeover cancelled.");
            return false;
        }
        if (afterTaskbar.Count > 0)
        {
            var lingered = DateTime.UtcNow - graceStart;
            if (lingered < LingerGrace)
            {
                // The budget ran out before the full grace was served. Killing a
                // remnant that never got its grace is exactly what Winlogon
                // respawns, so fail open and let the stability check report it.
                Log.Warn($"Explorer taskbar exited but original process(es) {string.Join(", ", afterTaskbar)} " +
                         $"were still present after {lingered.TotalMilliseconds:F0} ms and the budget ran out " +
                         $"before the {LingerGrace.TotalMilliseconds:F0} ms grace; not terminating them.");
            }
            else
            {
                Log.Warn($"Explorer taskbar exited but original process(es) {string.Join(", ", afterTaskbar)} " +
                         $"lingered past {lingered.TotalMilliseconds:F0} ms; terminating them.");
                TerminateOriginalExplorerProcesses(initialProcessIds);
            }
        }
        return WaitForStableExplorerAbsence(initialProcessIds, deadline);
    }

    /// <summary>Success only after half a second of continuous explorer absence —
    /// a Winlogon respawn shows up within that window and cancels the takeover.</summary>
    private static bool WaitForStableExplorerAbsence(IReadOnlyCollection<int> initialProcessIds, DateTime deadline)
    {
        var stableSinceUtc = (DateTime?)null;
        while (DateTime.UtcNow < deadline)
        {
            var currentProcessIds = ExplorerProcessIdsInSession();
            var replacementProcessId = FindReplacementProcessId(initialProcessIds, currentProcessIds);
            if (replacementProcessId != 0)
            {
                _respawnCancelled = true;
                _respawnProcessId = checked((uint)replacementProcessId);
                Log.Warn($"Winlogon restarted Explorer as pid {replacementProcessId}; takeover cancelled.");
                return false;
            }
            if (currentProcessIds.Count == 0)
            {
                stableSinceUtc ??= DateTime.UtcNow;
                if (DateTime.UtcNow - stableSinceUtc.Value >= StableAbsence)
                {
                    Log.Info("Explorer exited cleanly without replacement.");
                    return true;
                }
            }
            else
            {
                stableSinceUtc = null;
            }
            System.Threading.Thread.Sleep(100);
        }
        Log.Warn("Explorer processes did not exit cleanly before timeout.");
        return false;
    }

    private static void TerminateOriginalExplorerProcesses(IEnumerable<int> originalProcessIds)
    {
        foreach (var processId in originalProcessIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.SessionId != WindowFinder.CurrentSessionId ||
                    !process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                Log.Info($"Terminating lingering explorer.exe (pid {processId}) after orderly shell exit.");
                process.Kill();
            }
            catch (ArgumentException)
            {
                // Exited between enumeration and open.
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not terminate lingering explorer pid {processId}: {ex.Message}");
            }
        }
    }

    /// <summary>Explorer pids of the CURRENT session only (other RDP/FUS sessions
    /// run their own).</summary>
    private static List<int> ExplorerProcessIdsInSession()
    {
        var ids = new List<int>();
        foreach (var pid in WindowFinder.FindProcessIds("explorer"))
        {
            ids.Add(checked((int)pid));
        }
        return ids;
    }

    /// <summary>Any current explorer PID that was not in the initial snapshot is a
    /// Winlogon replacement (0 = none).</summary>
    private static int FindReplacementProcessId(
        IReadOnlyCollection<int> initialProcessIds, IReadOnlyCollection<int> currentProcessIds)
    {
        foreach (var id in currentProcessIds)
        {
            if (!initialProcessIds.Contains(id))
            {
                return id;
            }
        }
        return 0;
    }

    private static bool IsWindowOwnedByProcess(nint window, uint processId)
    {
        if (window == 0 || !Interop.NativeMethods.IsWindow(window))
        {
            return false;
        }
        Interop.NativeMethods.GetWindowThreadProcessId(window, out var currentOwner);
        return currentOwner == processId;
    }

    private static bool IsCurrentSessionWindow(nint window)
    {
        if (window == 0)
        {
            return false;
        }
        Interop.NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.SessionId == WindowFinder.CurrentSessionId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Repair path only: kills the ELEVATED instances (an unelevated one is
    /// what we want to keep) and waits — bounded — for them to actually die. Kill is
    /// asynchronous, and explorer is a per-session singleton: starting the
    /// replacement while the old instance still lives makes the new one open a
    /// folder window instead of becoming the shell.</summary>
    private static void KillElevatedExplorerAndWait()
    {
        var killed = new List<Process>();
        foreach (var pid in WindowFinder.FindProcessIds("explorer"))
        {
            var isElevated = false;
            try
            {
                isElevated = ElevationCheck.IsProcessElevated(pid) == true;
            }
            catch { }
            if (!isElevated)
            {
                continue;
            }
            Process? p = null;
            try
            {
                p = Process.GetProcessById(checked((int)pid));
                Log.Info($"Killing ELEVATED explorer.exe (pid {pid})");
                p.Kill();
                killed.Add(p);
            }
            catch (ArgumentException)
            {
                // Exited between enumeration and open.
                p?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not kill explorer pid {pid}: {ex.Message}");
                p?.Dispose();
            }
        }
        foreach (var p in killed)
        {
            try
            {
                if (!p.WaitForExit(5000))
                {
                    Log.Warn($"Explorer pid {p.Id} did not exit within 5 s — replacement may race it.");
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    /// <summary>The canonical Windows Explorer image path, shared by every launcher
    /// and image-identity check.</summary>
    internal static string ExplorerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
}
