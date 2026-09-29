using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>Controls whether Windows demands a sign-in after the screen turns off or
/// the device wakes from standby — the "Require a password on wakeup" power setting
/// (CONSOLELOCK). On modern-standby handhelds this setting is hidden from the classic
/// power UI but still applies, so it is written two ways:
///
///  1. The active power scheme's value (via powercfg, AC and DC).
///  2. The matching Power *policy* values in HKLM, which override the scheme and —
///     importantly on handhelds — survive vendor software switching power schemes.
///
/// This does not remove the lock screen you get from pressing Win+L; it stops the
/// device from locking itself when the screen sleeps.</summary>
public static class LockScreenSettings
{
    private const string ConsoleLockGuid = "0e796bdb-100d-47d6-a2d5-f7d2daa51f51";
    private const string SubNoneGuid = "fea3413e-7e05-4911-9a71-700331f1c294";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\" + ConsoleLockGuid;
    private const string SchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string PersonalizationKey = @"SOFTWARE\Policies\Microsoft\Windows\Personalization";
    private const string NoLockScreen = "NoLockScreen";

    /// <summary>True when waking the device does NOT require signing in again — for
    /// EVERY power scheme, since vendor tools switch schemes at will.</summary>
    public static bool SignInOnWakeDisabled()
    {
        try
        {
            // Policy wins over the per-scheme values when present.
            using (var policy = Registry.LocalMachine.OpenSubKey(PolicyKey))
            {
                if (policy?.GetValue("ACSettingIndex") is int policyAc)
                {
                    var policyDc = policy.GetValue("DCSettingIndex") as int? ?? policyAc;
                    return policyAc == 0 && policyDc == 0;
                }
            }

            using var schemes = Registry.LocalMachine.OpenSubKey(SchemesKey);
            if (schemes is null)
            {
                return false;
            }

            var any = false;
            foreach (var scheme in EnumerateSchemeGuids())
            {
                using var setting = schemes.OpenSubKey($@"{scheme}\{SubNoneGuid}\{ConsoleLockGuid}");
                // Absent = Windows default = require sign-in.
                var ac = setting?.GetValue("ACSettingIndex") as int? ?? 1;
                var dc = setting?.GetValue("DCSettingIndex") as int? ?? 1;
                if (ac != 0 || dc != 0)
                {
                    return false;
                }
                any = true;
            }
            return any;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read lock-on-wake setting: {ex.Message}");
            return false;
        }
    }

    /// <summary>Runs in the ELEVATED instance.</summary>
    public static bool ApplyDirect(bool disableSignInOnWake)
    {
        try
        {
            // Strict load: both branches below are read-modify-write transactions on
            // config.json. A lenient load would hand an unreadable file back as
            // defaults, and this method would then capture the ALREADY MODIFIED
            // registry state as the "pre-WSGM" snapshot and save those defaults over
            // every other recovery snapshot. A throw aborts the change instead — the
            // catch below logs it and reports failure.
            var config = ConfigStore.LoadForMutation();

            if (disableSignInOnWake)
            {
                if (!config.PreviousLockOnWakeSnapshotCaptured)
                {
                    // Faithful snapshot BEFORE any write: per-scheme AC/DC values,
                    // the pre-existing policy values, and whether the policy key
                    // existed at all (if not, restore removes the whole key).
                    config.PreviousLockOnWakeSnapshotCaptured = true;
                    config.PreviousConsoleLockSchemeValues = CaptureSchemeValues();
                    using (var policy = Registry.LocalMachine.OpenSubKey(PolicyKey))
                    {
                        config.PreviousConsoleLockPolicyKeyExisted = policy is not null;
                        config.PreviousConsoleLockPolicyAc = policy?.GetValue("ACSettingIndex") as int? ?? -1;
                        config.PreviousConsoleLockPolicyDc = policy?.GetValue("DCSettingIndex") as int? ?? -1;
                    }
                    config.PreviousNoLockScreen = ReadNoLockScreen();
                    ConfigStore.Save(config);
                }

                using (var policy = Registry.LocalMachine.CreateSubKey(PolicyKey))
                {
                    policy.SetValue("ACSettingIndex", 0, RegistryValueKind.DWord);
                    policy.SetValue("DCSettingIndex", 0, RegistryValueKind.DWord);
                }
                SetSchemeValue(0);
                SetNoLockScreen(true);
                Log.Info("Sign-in on wake disabled (CONSOLELOCK=0, policy + active scheme, NoLockScreen=1).");
            }
            else
            {
                RestorePolicyValues(config);

                if (config.PreviousLockOnWakeSnapshotCaptured && config.PreviousConsoleLockSchemeValues.Count > 0)
                {
                    RestoreSchemeValues(config.PreviousConsoleLockSchemeValues);
                }
                else
                {
                    // No snapshot (or an empty scheme capture): restore Windows'
                    // default (require sign-in).
                    SetSchemeValue(1);
                    Log.Info("Sign-in on wake restored (CONSOLELOCK=1).");
                }
                RestoreNoLockScreen(config.PreviousNoLockScreen);

                config.PreviousLockOnWakeSnapshotCaptured = false;
                config.PreviousConsoleLockSchemeValues = [];
                config.PreviousConsoleLockPolicyKeyExisted = false;
                config.PreviousConsoleLockPolicyAc = -1;
                config.PreviousConsoleLockPolicyDc = -1;
                config.PreviousNoLockScreen = -1;
                ConfigStore.Save(config);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to change lock-on-wake setting", ex);
            return false;
        }
    }

    /// <summary>Current HKLM Personalization\NoLockScreen value, or -1 when absent.</summary>
    private static int ReadNoLockScreen()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PersonalizationKey);
            return key?.GetValue(NoLockScreen) as int? ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Removes the lock screen UI itself. Note: Windows 11 Home ignores this
    /// policy on several builds — treated as best-effort, never fatal.</summary>
    private static void SetNoLockScreen(bool disable)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(PersonalizationKey);
            key.SetValue(NoLockScreen, disable ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not set NoLockScreen: {ex.Message}");
        }
    }

    private static void RestoreNoLockScreen(int previous)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PersonalizationKey, writable: true);
            if (key is null)
            {
                return;
            }
            if (previous < 0)
            {
                key.DeleteValue(NoLockScreen, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(NoLockScreen, previous, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not restore NoLockScreen: {ex.Message}");
        }
    }

    /// <summary>Snapshot of every scheme's CONSOLELOCK AC/DC values (-1 = absent),
    /// taken before WSGM writes anything so the restore can be exact.</summary>
    private static List<PowerSchemeConsoleLock> CaptureSchemeValues()
    {
        var result = new List<PowerSchemeConsoleLock>();
        try
        {
            using var schemes = Registry.LocalMachine.OpenSubKey(SchemesKey);
            if (schemes is null)
            {
                return result;
            }
            foreach (var scheme in EnumerateSchemeGuids())
            {
                using var setting = schemes.OpenSubKey($@"{scheme}\{SubNoneGuid}\{ConsoleLockGuid}");
                result.Add(new PowerSchemeConsoleLock
                {
                    SchemeGuid = scheme,
                    AcValue = setting?.GetValue("ACSettingIndex") as int? ?? -1,
                    DcValue = setting?.GetValue("DCSettingIndex") as int? ?? -1,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not capture per-scheme CONSOLELOCK values: {ex.Message}");
        }
        return result;
    }

    /// <summary>Writes back exactly what CaptureSchemeValues recorded: powercfg for
    /// values that existed, registry delete for values that were absent.</summary>
    private static void RestoreSchemeValues(List<PowerSchemeConsoleLock> saved)
    {
        var applied = 0;
        foreach (var entry in saved)
        {
            if (!Guid.TryParse(entry.SchemeGuid, out _))
            {
                continue;   // never feed a hand-edited config value to powercfg's command line
            }
            var ok = entry.AcValue >= 0
                ? RunPowerCfg($"/setacvalueindex {entry.SchemeGuid} SUB_NONE CONSOLELOCK {entry.AcValue}")
                : DeleteSchemeValue(entry.SchemeGuid, "ACSettingIndex");
            ok &= entry.DcValue >= 0
                ? RunPowerCfg($"/setdcvalueindex {entry.SchemeGuid} SUB_NONE CONSOLELOCK {entry.DcValue}")
                : DeleteSchemeValue(entry.SchemeGuid, "DCSettingIndex");
            if (ok)
            {
                applied++;
            }
        }
        // Re-apply the active scheme so the change takes effect immediately.
        RunPowerCfg("/setactive SCHEME_CURRENT");
        Log.Info($"Sign-in on wake restored per scheme (CONSOLELOCK on {applied}/{saved.Count} power scheme(s)).");
    }

    /// <summary>powercfg cannot remove a value, so "absent before WSGM" is restored
    /// by deleting the registry values directly (we run elevated here).</summary>
    private static bool DeleteSchemeValue(string scheme, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{SchemesKey}\{scheme}\{SubNoneGuid}\{ConsoleLockGuid}", writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete CONSOLELOCK {valueName} for scheme {scheme}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Restores the CONSOLELOCK policy exactly: the whole key is deleted
    /// only when WSGM created it; a pre-existing key gets its captured values back
    /// (or the values deleted when they were absent).</summary>
    private static void RestorePolicyValues(AppConfig config)
    {
        if (config.PreviousLockOnWakeSnapshotCaptured &&
            !config.PreviousConsoleLockPolicyKeyExisted)
        {
            try
            {
                Registry.LocalMachine.DeleteSubKey(PolicyKey, throwOnMissingSubKey: false);
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not delete WSGM-created CONSOLELOCK policy key: {ex.Message}");
                // Fall through and at least clear the values WSGM wrote.
            }
        }
        using var policy = Registry.LocalMachine.OpenSubKey(PolicyKey, writable: true);
        if (policy is null)
        {
            return;
        }
        if (config.PreviousLockOnWakeSnapshotCaptured && config.PreviousConsoleLockPolicyAc >= 0)
        {
            policy.SetValue("ACSettingIndex", config.PreviousConsoleLockPolicyAc, RegistryValueKind.DWord);
        }
        else
        {
            policy.DeleteValue("ACSettingIndex", throwOnMissingValue: false);
        }
        if (config.PreviousLockOnWakeSnapshotCaptured && config.PreviousConsoleLockPolicyDc >= 0)
        {
            policy.SetValue("DCSettingIndex", config.PreviousConsoleLockPolicyDc, RegistryValueKind.DWord);
        }
        else
        {
            policy.DeleteValue("DCSettingIndex", throwOnMissingValue: false);
        }
    }

    /// <summary>Applies the value to EVERY power scheme, not just the active one:
    /// handheld vendor software (Handheld Companion, Armoury Crate, MSI Center)
    /// switches power plans aggressively, and this setting is stored per scheme.
    /// The HKLM policy above still covers schemes created later.</summary>
    private static void SetSchemeValue(int index)
    {
        var applied = 0;
        var seen = 0;
        foreach (var scheme in EnumerateSchemeGuids())
        {
            seen++;
            var ok = RunPowerCfg($"/setacvalueindex {scheme} SUB_NONE CONSOLELOCK {index}");
            ok &= RunPowerCfg($"/setdcvalueindex {scheme} SUB_NONE CONSOLELOCK {index}");
            if (ok)
            {
                applied++;
            }
        }
        if (seen == 0)
        {
            RunPowerCfg($"/setacvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK {index}");
            RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT SUB_NONE CONSOLELOCK {index}");
        }
        // Re-apply the active scheme so the change takes effect immediately.
        RunPowerCfg("/setactive SCHEME_CURRENT");
        Log.Info($"CONSOLELOCK={index} applied to {applied} of {seen} power scheme(s).");
    }

    private static List<string> EnumerateSchemeGuids()
    {
        var result = new List<string>();
        try
        {
            using var schemes = Registry.LocalMachine.OpenSubKey(SchemesKey);
            if (schemes is null)
            {
                return result;
            }
            foreach (var name in schemes.GetSubKeyNames())
            {
                if (Guid.TryParse(name, out _))
                {
                    result.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not enumerate power schemes: {ex.Message}");
        }
        return result;
    }

    /// <summary>Exit-code-checked: a failed powercfg must never count as applied
    /// (the "applied to N scheme(s)" log line is the only remote diagnosis signal).</summary>
    private static bool RunPowerCfg(string arguments) => ConsoleTool.Run(ConsoleTool.System32("powercfg.exe"), arguments);

    /// <summary>Requests the change from the non-elevated UI (one elevation prompt).</summary>
    public static bool RequestChange(bool disableSignInOnWake) =>
        SelfElevation.RunElevatedAction(
            disableSignInOnWake ? "--disable-lock-on-wake" : "--restore-lock-on-wake",
            "Lock-on-wake change");
}
