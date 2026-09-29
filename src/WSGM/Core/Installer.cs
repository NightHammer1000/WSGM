using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Install-lifecycle helpers behind the Inno installer: the per-user install
/// directory layout and the machine-setting rollback the uninstaller drives through
/// <c>--uninstall-restore</c>.</summary>
public static class Installer
{
    /// <summary>Gets the stable per-user directory that holds the installed application files.</summary>
    public static string InstallDir => Path.Combine(Log.Directory, "bin");

    /// <summary>Gets the installed WSGM executable path.</summary>
    public static string InstalledExePath => Path.Combine(InstallDir, "WSGM.exe");

    /// <summary>Prepares the install directory for the files Inno just laid down.
    /// Returns the installed exe path.</summary>
    public static string InstallApp()
    {
        Directory.CreateDirectory(InstallDir);
        Log.Info($"Installed to {InstalledExePath}");
        return InstalledExePath;
    }

    /// <summary>Best-effort rollback of every machine/user setting WSGM changed
    /// outside its own directory: display scaling, UAC prompt level, and
    /// lock-on-wake. Called by --uninstall-restore, which the elevated Inno
    /// uninstaller runs (PrivilegesRequired=admin) so the HKLM writes succeed
    /// directly; each step is isolated so one failure cannot stop the rest.</summary>
    public static void RestoreMachineSettings()
    {
        try
        {
            var config = ConfigStore.Load();
            DisplayScale.RestoreSaved(config);
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: display scaling failed: {ex.Message}");
        }

        try
        {
            var config = ConfigStore.Load();
            if (config.PreviousUacSnapshotCaptured && UacSettings.Read().PromptsDisabled)
            {
                Log.Info("Uninstall restore: restoring UAC prompt level.");
                UacSettings.ApplyDirect(disablePrompts: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: UAC failed: {ex.Message}");
        }

        try
        {
            var config = ConfigStore.Load();
            if (config.PreviousLockOnWakeSnapshotCaptured && LockScreenSettings.SignInOnWakeDisabled())
            {
                Log.Info("Uninstall restore: restoring lock-on-wake.");
                LockScreenSettings.ApplyDirect(disableSignInOnWake: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: lock-on-wake failed: {ex.Message}");
        }
    }
}
