using System;
using System.Linq;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>A read-only diagnostic that writes what the radio subsystem can
/// actually do on this machine into the log.
///
/// It exists because two things cannot be settled from documentation, and
/// this project's device is only reachable through pasted logs:
///
/// * whether WinRT radio control works from an elevated process with no
///   Explorer shell in the session,
/// * whether the Windows 11 24H2 precise-location gate blocks the Wi-Fi scan.
///
/// Strictly read-only. It never changes a radio's state and never writes a
/// consent value, so running it can never be what breaks a session.</summary>
public static class RadioProbe
{
    /// <summary>Runs every check and writes the result to the log.</summary>
    /// <returns>Zero. The verdict is the log, not the exit code — the point is
    /// to gather evidence, not to gate anything on it.</returns>
    public static int Run()
    {
        Log.Info("---- radio probe ----");
        // Both are the conditions the open questions are about, so they belong
        // in the same log block as the answers.
        Log.Info($"Radio probe: elevated={ElevationCheck.IsCurrentProcessElevated()}, "
            + $"explorer={ExplorerControl.IsRunningInSession()}");

        ProbeRadio("Wi-Fi", WindowsRadio.RadioKind.WiFi);
        ProbeRadio("Bluetooth", WindowsRadio.RadioKind.Bluetooth);
        ProbeAccess();
        ProbeConsent("location");
        ProbeConsent("radios");
        ProbeWifi();
        ProbeBluetooth();

        Log.Info("---- radio probe done ----");
        return 0;
    }

    private static void ProbeRadio(string label, WindowsRadio.RadioKind kind)
    {
        try
        {
            var state = WindowsRadio.GetPower(kind);
            Log.Info($"Radio probe: {label} radio power={state}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio probe: {label} radio power threw: {ex.Message}");
        }
    }

    private static void ProbeAccess()
    {
        try
        {
            var access = WindowsRadio.RequestAccess();
            Log.Info($"Radio probe: radio control access={access}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio probe: radio access threw: {ex.Message}");
        }
    }

    private static void ProbeConsent(string capability)
    {
        try
        {
            var consent = WindowsRadio.GetConsent(capability);
            Log.Info($"Radio probe: consent {capability} user={consent.User} machine={consent.Machine}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio probe: consent {capability} threw: {ex.Message}");
        }
    }

    private static void ProbeWifi()
    {
        try
        {
            var status = WindowsRadio.GetWifiStatus();
            Log.Info($"Radio probe: wlan interface state={status.State} "
                + "(0 connected, 1 connecting, 2 disconnected, 3 unavailable)");

            WindowsRadio.RequestWifiScan();
            Log.Info("Radio probe: wlan scan accepted");

            var networks = WindowsRadio.ListWifiNetworks();
            Log.Info($"Radio probe: wlan network list={networks.Count} network(s)");
            foreach (var network in networks.Take(8))
            {
                Log.Info($"Radio probe:   \"{network.Ssid}\" {network.Signal}% "
                    + $"security={network.Security} saved={network.Saved}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio probe: wlan threw: {ex.Message}");
        }
    }

    private static void ProbeBluetooth()
    {
        try
        {
            var devices = WindowsRadio.ListBluetoothDevices(pairedOnly: false);
            Log.Info($"Radio probe: bluetooth list={devices.Count} device(s)");
            foreach (var device in devices.Take(12))
            {
                Log.Info($"Radio probe:   \"{device.Name}\" paired={device.Paired} "
                    + $"canPair={device.CanPair}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Radio probe: bluetooth threw: {ex.Message}");
        }
    }
}
