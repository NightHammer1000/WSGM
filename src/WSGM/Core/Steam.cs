using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>A native keyboard shortcut understood by Steam's Big Picture UI.</summary>
public enum BigPictureShortcut
{
    /// <summary>Ctrl+1, equivalent to Steam's guide button and left-side menu.</summary>
    SteamMenu,

    /// <summary>Ctrl+2, equivalent to Steam's Quick Access button and right-side menu.</summary>
    QuickAccess,
}

/// <summary>Everything WSGM knows about Steam. WSGM is Steam-exclusive: Steam is
/// located via the registry (no path configuration), started/focused/closed via
/// steam:// protocol URLs (UIPI-proof, and the handler boots Steam when needed),
/// and its Big Picture window is recognized by class+process.</summary>
public static class Steam
{
    private static readonly TimeSpan UpdateGracefulExitBudget = TimeSpan.FromSeconds(5);

    /// <summary>steam.exe plus the process that owns the Big Picture window.</summary>
    public const string ProcessNames = "steam;steamwebhelper";

    /// <summary>Just steam.exe — deliberately narrower than <see cref="ProcessNames"/>:
    /// only the main client services steam:// protocol URLs, so a lingering
    /// steamwebhelper must not count as "Steam is running" for protocol callers.</summary>
    private const string MainProcessName = "steam";

    /// <summary>Big Picture window class (paired with the steamwebhelper process —
    /// SDL_app alone is not unique to Steam).</summary>
    public const string BigPictureWindowClass = "SDL_app";

    /// <summary>Protocol URL that opens Steam Big Picture mode.</summary>
    public const string OpenBigPictureUrl = "steam://open/bigpicture";

    /// <summary>Protocol URL that exits Steam Big Picture mode.</summary>
    public const string CloseBigPictureUrl = "steam://close/bigpicture";
    /// <summary>Graceful full Steam shutdown (verified client URL).</summary>
    public const string ExitUrl = "steam://exit";

    /// <summary>Gets the complete bounded pre-shutdown window used to release Steam and launch
    /// wrappers before WSGM starts its separate application-cleanup deadline.</summary>
    internal static TimeSpan UpdateStopBudget => TimeSpan.FromSeconds(10);

    private static string? _cachedExePath;

    /// <summary>Full path to steam.exe from the registry, or null when Steam is not
    /// installed. HKCU value uses forward slashes — normalized here. The registry+disk
    /// probe runs once; later reads only re-validate the cached path with File.Exists
    /// and re-probe when it went missing (uninstall/move).</summary>
    public static string? ExePath
    {
        get
        {
            var cached = _cachedExePath;
            if (cached is not null && File.Exists(cached))
            {
                return cached;
            }
            _cachedExePath = ResolveExePath();
            return _cachedExePath;
        }
    }

    private static string? ResolveExePath()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) is string exe
                && exe.Length > 0)
            {
                exe = exe.Replace('/', '\\');
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string dir
                && dir.Length > 0)
            {
                var fromInstallDir = Path.Combine(dir, "steam.exe");
                if (File.Exists(fromInstallDir))
                {
                    return fromInstallDir;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam registry lookup failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>Gets Steam's install directory, or <see langword="null"/> when Steam
    /// is not installed. Everything WSGM writes beside Steam - the CEF debug flag and
    /// the Steam Input proxy - resolves through here rather than repeating the
    /// directory split at each call site.</summary>
    public static string? InstallDirectory =>
        ExePath is { } exe ? Path.GetDirectoryName(exe) : null;

    /// <summary>Gets the full path of Steam's <c>config\libraryfolders.vdf</c> —
    /// the install-folder registry every card/library feature reads and edits —
    /// or <see langword="null"/> when Steam is not installed.</summary>
    public static string? LibraryFoldersConfigPath =>
        InstallDirectory is { } directory
            ? Path.Combine(directory, "config", "libraryfolders.vdf")
            : null;

    /// <summary>Reads <c>config\libraryfolders.vdf</c>. False when Steam is not
    /// installed or the file does not exist yet; <paramref name="path"/> still
    /// carries the resolved location when only the file is missing, so a caller
    /// can create it. Deliberately does NOT catch IO failures — the callers'
    /// policies for an unreadable config differ.</summary>
    /// <param name="path">The config path, or null when Steam is not installed.</param>
    /// <param name="text">The file text, or null when it could not be resolved.</param>
    public static bool TryReadLibraryFolders(out string? path, out string? text)
    {
        path = LibraryFoldersConfigPath;
        text = path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
        return text is not null;
    }

    /// <summary>Gets Steam's per-account data root (<c>userdata</c>), or
    /// <see langword="null"/> when Steam is not installed.</summary>
    public static string? UserDataDirectory =>
        InstallDirectory is { } directory ? Path.Combine(directory, "userdata") : null;

    /// <summary>Gets whether a usable Steam executable was found.</summary>
    public static bool IsInstalled => ExePath is not null;

    /// <summary>Gets whether a Steam client or Big Picture helper process is running.</summary>
    public static bool IsRunning => WindowFinder.FindProcessIds(ProcessNames).Count > 0;

    /// <summary>Gets whether Steam's process-owned Big Picture window exists. This is
    /// deliberately stronger than <see cref="IsRunning"/>: on a cold start Steam's
    /// process and headless CEF context exist before its UI is safe for autonomous
    /// mutation.</summary>
    public static bool IsBigPictureVisible =>
        WindowFinder.FindWindow(ProcessNames, BigPictureWindowClass) != IntPtr.Zero;

    /// <summary>Gets whether WSGM must match Steam's elevated integrity level so
    /// raw-touch gestures and overlay input are not blocked by UIPI.</summary>
    public static bool RequiresElevatedShell
    {
        get
        {
            foreach (var processId in WindowFinder.FindProcessIds(ProcessNames))
            {
                if (ElevationCheck.IsProcessElevated((uint)processId) == true)
                {
                    return true;
                }
            }

            var path = ExePath;
            return path is not null && HasRunAsAdminCompatibilityLayer(path);
        }
    }

    private static bool CompatibilityLayerRequiresElevation(string? layer)
    {
        if (string.IsNullOrWhiteSpace(layer))
        {
            return false;
        }
        foreach (var token in layer.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.TrimStart('~', '!', '#').Equals("RUNASADMIN", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasRunAsAdminCompatibilityLayer(string executablePath)
    {
        const string layersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        try
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(layersKey);
                    if (CompatibilityLayerRequiresElevation(key?.GetValue(executablePath) as string))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam compatibility-layer lookup failed: {ex.Message}");
        }
        return false;
    }

    /// <summary>Starts or focuses Big Picture the smooth way. Cold start passes the
    /// BP URL as a command-line ARGUMENT to steam.exe so Steam boots straight into
    /// Big Picture — fired as a protocol instead, the handler first brings Steam up
    /// in desktop mode and only switches after login (user-reported wonkiness).
    /// When Steam already runs, the protocol re-activates/enters BP (UIPI-proof).</summary>
    public static AppLauncher.LaunchResult LaunchBigPicture(bool unelevated = false)
    {
        if (!IsRunning && ExePath is { } exe)
        {
            // Steam is provably not running on this branch, which makes it the one
            // moment in a session when a stale Steam Input shim can actually be
            // replaced - anywhere else the image is mapped and the copy fails.
            SteamInputShim.Reconcile("steam-cold-start");
            // Enable Steam's CEF debug port before it starts so WSGM can add
            // libraries to the live client later without a restart. Only takes
            // effect on a fresh Steam start, which this cold path is.
            SteamCdp.EnsureRemoteDebuggingEnabled();
            // The de-elevating scheduled task is only meaningful from an elevated WSGM: started
            // from a medium-integrity process, the ordinary launch already produces a
            // medium-integrity Steam without the task-scheduler round trip.
            bool deElevate = unelevated && ElevationCheck.IsCurrentProcessElevated() is true;
            if (deElevate
                && UnelevatedLauncher.TryStartViaScheduledTask(exe, OpenBigPictureUrl))
            {
                Log.Info("Steam launch integrity: medium (de-elevated scheduled task).");
                return new AppLauncher.LaunchResult(null, true, false);
            }

            if (deElevate)
            {
                Log.Warn(
                    "Steam launch integrity: de-elevation was requested but unavailable; "
                    + "falling back to WSGM's own integrity.");
            }

            var result = AppLauncher.Start(exe, OpenBigPictureUrl, elevated: false);
            Log.Info(
                "Steam launch integrity: "
                + (ElevationCheck.IsCurrentProcessElevated() is true ? "elevated" : "medium")
                + " (matched to WSGM).");
            // Only when a vector is actually deployed, and worded as the EXPECTED
            // path. docs\steam-input.md tells the reader a missing file means the
            // gate worker never got past the loader — so naming a path for a Steam
            // with no shim, or for a pid Steam's bootstrapper then re-execs away
            // from, makes that diagnostic assert the opposite of the truth.
            if (result.Process is { } process
                && SteamInputShim.LastStatus.State == SteamInputShimState.Deployed)
            {
                Log.Info(
                    $"Steam Input shim startup trace expected for pid {process.Id}: "
                    + SteamInputShim.StartupTracePath(process.Id)
                    + " (absent if Steam re-execed into another pid)");
            }
            return result;
        }
        return AppLauncher.StartProtocol(OpenBigPictureUrl);
    }

    /// <summary>Sends one of Steam's own Big Picture keyboard shortcuts globally.
    /// This deliberately has no foreground-window gate: when a game is foreground,
    /// Steam uses the shortcut to bring its menu up over that game.</summary>
    /// <param name="shortcut">The Big Picture menu shortcut to send.</param>
    /// <returns>True when Windows accepted the complete synthetic key chord.</returns>
    public static bool TrySendBigPictureShortcut(BigPictureShortcut shortcut)
    {
        var virtualKey = ShortcutVirtualKey(shortcut);
        var result = KeyboardInput.SendControlChord(virtualKey);
        if (result.Sent != result.Requested)
        {
            Log.Warn(
                $"Steam Big Picture shortcut Ctrl+{(char)virtualKey} failed " +
                $"(sent {result.Sent}/{result.Requested}, Win32 error {result.Error}).");
            return false;
        }

        // SendInput has no window target: Windows delivers the chord to whatever holds
        // focus. Naming that window is the only way a pasted log distinguishes "Steam
        // ignored it" from "it went somewhere else entirely".
        Log.Info($"Steam Big Picture shortcut Ctrl+{(char)virtualKey} sent ({shortcut}) "
            + $"to foreground {WindowFinder.DescribeForeground()}.");
        return true;
    }

    /// <summary>Returns Steam's installed-client keyboard mapping for a Big Picture menu.</summary>
    /// <param name="shortcut">The menu shortcut to map.</param>
    /// <returns>The Win32 virtual key combined with Control.</returns>
    internal static ushort ShortcutVirtualKey(BigPictureShortcut shortcut) => shortcut switch
    {
        BigPictureShortcut.SteamMenu => 0x31,
        BigPictureShortcut.QuickAccess => 0x32,
        _ => throw new ArgumentOutOfRangeException(nameof(shortcut)),
    };

    /// <summary>Requests a graceful Steam shutdown for an application update.</summary>
    public static void StopForUpdate() => StopForUpdate(UpdateStopBudget);

    /// <summary>Stops Steam and launch wrappers without exceeding the updater-owned pre-shutdown
    /// window through any process-exit wait. The installer reserves this phase before WSGM's
    /// application cleanup budget.</summary>
    /// <param name="budget">Maximum combined graceful and forced-stop wait.</param>
    internal static void StopForUpdate(TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        Process[] runningSteam = CurrentSessionProcesses(MainProcessName);
        if (runningSteam.Length > 0)
        {
            foreach (Process process in runningSteam)
            {
                process.Dispose();
            }
            Log.Info("Update requested — closing Steam to release the Steam Input payload.");
            AppLauncher.StartProtocol(ExitUrl);
            TimeSpan gracefulDeadline = budget < UpdateGracefulExitBudget
                ? budget
                : UpdateGracefulExitBudget;
            while (elapsed.Elapsed < gracefulDeadline)
            {
                Process[] remaining = CurrentSessionProcesses(MainProcessName);
                if (remaining.Length == 0)
                {
                    Log.Info("Steam exited gracefully for update.");
                    break;
                }
                foreach (Process process in remaining)
                {
                    process.Dispose();
                }

                TimeSpan delay = gracefulDeadline - elapsed.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    Thread.Sleep(delay < TimeSpan.FromMilliseconds(250)
                        ? delay
                        : TimeSpan.FromMilliseconds(250));
                }
            }

            Process[] remainingSteam = CurrentSessionProcesses(MainProcessName);
            foreach (Process process in remainingSteam)
            {
                try
                {
                    Log.Warn(
                        $"Steam pid {process.Id} did not exit gracefully; setup will defer the "
                            + "update instead of terminating Steam or a running game.");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        TimeSpan helperBudget = budget - elapsed.Elapsed;
        if (helperBudget > TimeSpan.Zero)
        {
            LaunchWrapperCommand.StopRunningHelpers("update", helperBudget);
        }
        else
        {
            Log.Warn("Update-stop budget expired before launch wrappers could be ended.");
        }
    }

    private static Process[] CurrentSessionProcesses(string processName)
    {
        int sessionId = Process.GetCurrentProcess().SessionId;
        var matches = new List<Process>();
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.SessionId == sessionId)
                {
                    matches.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        return [.. matches];
    }

}
