using System;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Restores the per-user Winlogon Shell value and owns the anti-Xbox-FSE
/// StartupToGamingHome guard. HKCU only — no admin rights needed, other accounts
/// untouched. WSGM never registers itself as the shell; <see cref="Uninstall"/> exists
/// so recovery and the uninstaller can put back a snapshotted value exactly.</summary>
public static class ShellRegistration
{
    private const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string ShellValue = "Shell";
    private const string GamingConfigKey = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
    private const string StartupToGamingHome = "StartupToGamingHome";

    private static readonly RegistryValueSnapshot<string?> ShellSnapshot = new(
        ShellValue,
        absentValue: null,
        writeFallback: string.Empty,
        defaultKind: RegistryValueKind.String,
        coerce: static value => value as string ?? string.Empty,
        normalizeKind: static kind =>
            kind == RegistryValueKind.ExpandString ? RegistryValueKind.ExpandString : RegistryValueKind.String,
        load: static config => new(config.PreviousShellSnapshotCaptured, config.PreviousShellValueExists,
            config.PreviousShellValue, config.PreviousShellValueKind),
        store: static (config, state) =>
        {
            config.PreviousShellValue = state.Value;
            config.PreviousShellSnapshotCaptured = state.Captured;
            config.PreviousShellValueExists = state.Exists;
            config.PreviousShellValueKind = state.Kind;
        });

    private static readonly RegistryValueSnapshot<int> GamingHomeSnapshot = new(
        StartupToGamingHome,
        absentValue: 0,
        writeFallback: 0,
        defaultKind: RegistryValueKind.DWord,
        coerce: static value => value is int number ? number : 0,
        normalizeKind: static kind =>
            kind == RegistryValueKind.QWord ? RegistryValueKind.QWord : RegistryValueKind.DWord,
        load: static config => new(config.PreviousStartupToGamingHomeSnapshotCaptured,
            config.PreviousStartupToGamingHomeValueExists,
            config.PreviousStartupToGamingHomeValue,
            config.PreviousStartupToGamingHomeValueKind),
        store: static (config, state) =>
        {
            config.PreviousStartupToGamingHomeValue = state.Value;
            config.PreviousStartupToGamingHomeSnapshotCaptured = state.Captured;
            config.PreviousStartupToGamingHomeValueExists = state.Exists;
            config.PreviousStartupToGamingHomeValueKind = state.Kind;
        });

    /// <summary>Applies the anti-Xbox-FSE guard on its own: with explorer as the
    /// registered shell again, StartupToGamingHome=1 would boot the Xbox Full
    /// Screen Experience over WSGM's cover at sign-in. Captures the pre-existing
    /// value once (upgrades keep the original snapshot — Restore never clears the
    /// captured flag) and then writes 0.</summary>
    public static void ApplyGamingHomeGuard(AppConfig config)
    {
        try
        {
            // Read-only snapshot — OpenSubKey so a pure read can't materialize the key.
            using (var gamingSnapshot = Registry.CurrentUser.OpenSubKey(GamingConfigKey))
            {
                var current = GamingHomeSnapshot.ReadCurrent(gamingSnapshot);
                // The capture is persisted through Mutate rather than saving the caller's
                // instance: Mutate reloads strictly under the lock, so an unreadable
                // config.json aborts here instead of writing defaults over the recovery
                // snapshots — whichever way the caller obtained its own AppConfig. The
                // captured check lives inside the scope so disk, not the caller's
                // possibly stale copy, decides (upgrades keep the original snapshot).
                var persisted = ConfigStore.Mutate(c =>
                {
                    if (!GamingHomeSnapshot.IsCaptured(c))
                    {
                        GamingHomeSnapshot.Capture(c, current);
                    }
                });
                config.PreviousStartupToGamingHomeValue = persisted.PreviousStartupToGamingHomeValue;
                config.PreviousStartupToGamingHomeSnapshotCaptured =
                    persisted.PreviousStartupToGamingHomeSnapshotCaptured;
                config.PreviousStartupToGamingHomeValueExists = persisted.PreviousStartupToGamingHomeValueExists;
                config.PreviousStartupToGamingHomeValueKind = persisted.PreviousStartupToGamingHomeValueKind;
            }
            using var gaming = Registry.CurrentUser.CreateSubKey(GamingConfigKey);
            gaming.SetValue(StartupToGamingHome, 0, RegistryValueKind.DWord);
            Log.Info("StartupToGamingHome guard applied (0) — Xbox FSE will not contest sign-in.");
        }
        catch (Exception ex)
        {
            Log.Warn($"StartupToGamingHome guard failed: {ex.Message}");
        }
    }

    /// <summary>Restores the previous shell registration (delete our value, or write back
    /// the saved pre-existing one). Safe to call from a broken state — reads config
    /// defensively and never throws.</summary>
    public static void Uninstall()
    {
        try
        {
            var config = new AppConfig();
            try { config = ConfigStore.Load(); } catch { }

            // OpenSubKey (not CreateSubKey): a restore that finds nothing to restore
            // must not create keys as a side effect. Winlogon always exists; a null
            // here also means our value can't be registered there.
            using (var key = Registry.CurrentUser.OpenSubKey(WinlogonKey, writable: true))
            {
                if (key is not null && IsOwnedByThisExe(ShellSnapshot.ReadCurrent(key).Value))
                {
                    ShellSnapshot.Restore(key, config);
                }
            }

            using (var gaming = Registry.CurrentUser.OpenSubKey(GamingConfigKey, writable: true))
            {
                if (gaming is not null && GamingHomeSnapshot.IsCaptured(config))
                {
                    // Revert only while the value is still the 0 WSGM wrote in
                    // Install — anything else means the user (or the Xbox app)
                    // changed it since, and that change must win.
                    var currentGaming = GamingHomeSnapshot.ReadCurrent(gaming);
                    if (currentGaming.Exists && currentGaming.Value == 0)
                    {
                        GamingHomeSnapshot.Restore(gaming, config);
                    }
                }
            }
            Log.Info($"Shell registration restored (previous: {DisplayShellSnapshot(config)})");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restore shell registration", ex);
        }
    }

    private static bool IsOwnedByThisExe(string? value)
    {
        // Ours if the registered COMMAND'S EXECUTABLE is the running copy or the
        // installed copy. Path equality, not substring — a foreign command that
        // merely mentions our path (e.g. a wrapper passing it as an argument)
        // must not be treated as ours and deleted on uninstall.
        var registeredExe = ExtractExecutablePath(value);
        if (registeredExe is null)
        {
            return false;
        }
        var exe = Environment.ProcessPath;
        return (exe is not null && string.Equals(registeredExe, exe, StringComparison.OrdinalIgnoreCase))
            || string.Equals(registeredExe, Installer.InstalledExePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses the executable out of a Shell command line: the quoted token
    /// if the command starts with a quote, otherwise everything up to the first
    /// space (matching how Winlogon itself launches the value).</summary>
    internal static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var closing = command.IndexOf('"', 1);
            return closing > 1 ? command[1..closing] : null;
        }
        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private static string DisplayShellSnapshot(AppConfig config)
        => ShellSnapshot.HasValue(config)
            ? config.PreviousShellValue ?? string.Empty
            : "<absent>";
}
