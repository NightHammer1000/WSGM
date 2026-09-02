using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using WSGM.Device.Sdk.Identity;

namespace WSGM.Core;

/// <summary>Read-only machine identity used before plugin code is loaded.</summary>
public static class DeviceMachineIdentity
{
    /// <summary>Reads stable SMBIOS values exposed by Windows in the hardware registry hive.</summary>
    public static DeviceIdentitySnapshot Collect()
    {
        using RegistryKey? bios = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\BIOS",
            writable: false);
        using RegistryKey? cpu = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            writable: false);
        return new DeviceIdentitySnapshot
        {
            SystemManufacturer = Normalize(bios?.GetValue("SystemManufacturer") as string),
            SystemProduct = Normalize(bios?.GetValue("SystemProductName") as string),
            SystemSku = Normalize(bios?.GetValue("SystemSKU") as string),
            SystemFamily = Normalize(bios?.GetValue("SystemFamily") as string),
            BaseboardProduct = Normalize(bios?.GetValue("BaseBoardProduct") as string),
            BaseboardVersion = Normalize(bios?.GetValue("BaseBoardVersion") as string),
            BiosVersion = Normalize(bios?.GetValue("BIOSVersion") as string),
            CpuIdentity = Normalize(cpu?.GetValue("Identifier") as string),
        };
    }

    /// <summary>Builds a stable, non-secret local key for persisted per-device intent.</summary>
    public static string StableKey(DeviceIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string material = string.Join('|',
            identity.SystemManufacturer ?? string.Empty,
            identity.BaseboardProduct ?? string.Empty,
            identity.BaseboardVersion ?? string.Empty,
            identity.SystemSku ?? string.Empty).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..24];
    }

    private static string? Normalize(string? value) => IdentityText.Normalize(value);
}
