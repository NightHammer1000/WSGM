using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using RadioPower = WindowsDeviceControl.WindowsRadio.Power;

namespace WSGM.Shell;

/// <summary>One Bluetooth device as Steam's own pairing panel renders it.</summary>
/// <remarks>
/// Field names are Steam's, not WSGM's, because this crosses straight into its store: the panel
/// reads <c>is_paired</c> and <c>is_connected</c> to decide which list a device belongs in, and
/// <c>etype</c> to choose its icon.
/// </remarks>
/// <param name="Id">Stable device identifier.</param>
/// <param name="Name">Device name, or its address when it reports none.</param>
/// <param name="Mac">Hardware address.</param>
/// <param name="EType">Steam's device-type enumeration value.</param>
/// <param name="IsPaired">Whether the device is paired.</param>
/// <param name="IsConnected">Whether it has a live connection.</param>
public readonly record struct SteamBluetoothDevice(
    string Id,
    string Name,
    string Mac,
    int EType,
    bool IsPaired,
    bool IsConnected
);

/// <summary>Bluetooth as Steam's own pairing panel expects to receive it.</summary>
/// <param name="Available">Whether Bluetooth can be observed and changed at all.</param>
/// <param name="Enabled">Whether the radio is on.</param>
/// <param name="Discovering">Whether a scan is running.</param>
/// <param name="Devices">Known devices, paired and discovered alike.</param>
public readonly record struct SteamBluetoothState(
    bool Available,
    bool Enabled,
    bool Discovering,
    IReadOnlyList<SteamBluetoothDevice> Devices
);

/// <summary>
/// The backend behind Steam's own Bluetooth pairing UI, reading and driving the session's radio
/// manager.
/// </summary>
internal sealed class NativeQamBluetoothService
{
    /// <summary>The Bluetooth commands, all answered by one handler that switches on the name.</summary>
    internal static readonly string[] Commands =
    [
        "setDiscovering",
        "pair",
        "cancelPair",
        "connect",
        "disconnect",
        "forget",
        "setTrusted",
        "setWakeAllowed",
    ];

    private readonly RadioManager _radios;

    /// <summary>Creates the service over the session's radio manager.</summary>
    internal NativeQamBluetoothService(RadioManager radios) => _radios = radios;

    /// <summary>
    /// Reads the radio manager's Bluetooth view into the shape Steam's panel consumes.
    /// </summary>
    /// <returns>The state to publish.</returns>
    /// <remarks>
    /// Reported unavailable when the radio is off rather than as an empty device list. Steam's panel
    /// distinguishes the two — "Bluetooth is off" is a state a user can act on, while an empty list
    /// reads as "nothing found" and invites them to keep waiting for devices that will never arrive.
    /// </remarks>
    internal async Task<SteamBluetoothState> ReadStateAsync()
    {
        List<SteamBluetoothDevice> devices = [];
        bool available = false;
        bool enabled = false;
        bool discovering = false;
        await NativeQamUi.RunAsync(() =>
        {
            // Available means "this machine has a Bluetooth radio WSGM can drive", never "the radio
            // is on". Wiring it to the on/off state made turning Bluetooth off remove the entire
            // settings page and the toggle with it — the exact control needed to turn it back on.
            available = _radios.BluetoothPower
                is not RadioPower.Absent and not RadioPower.Disabled;
            enabled = _radios.BluetoothOn;
            discovering = _radios.BluetoothScanning;
            foreach (BluetoothDeviceEntry entry in _radios.BluetoothDevices)
            {
                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    continue;
                }

                devices.Add(new SteamBluetoothDevice(
                    entry.Id,
                    string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
                    entry.Id,
                    // Steam's generic device type. WSGM does not classify Bluetooth devices, and a
                    // guessed class would put the wrong icon beside a real device.
                    0,
                    entry.Paired,
                    entry.Connected));
            }
        }).ConfigureAwait(false);

        return new SteamBluetoothState(available, enabled, discovering, devices);
    }

    /// <summary>
    /// Carries out one Bluetooth operation from Steam's own pairing UI.
    /// </summary>
    /// <param name="request">The bridge request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether it succeeded, and why not when it did not.</returns>
    /// <remarks>
    /// Pairing has no direct call: <see cref="RadioManager"/> drives it through a prompt the user
    /// answers, and inventing a headless pair here would either bypass a PIN confirmation the
    /// device requires or silently fail on one that does. Steam's Pair button therefore starts
    /// discovery and lets the existing prompt flow run, which is the same path the taskbar uses.
    /// <para>
    /// Trusted and wake-allowed are accepted and do nothing. They are Linux BlueZ concepts with no
    /// Windows equivalent, and refusing them would make Steam's UI report a failure for a control
    /// that was never going to change anything.
    /// </para>
    /// </remarks>
    internal async Task<SteamUiCommandResult> HandleAsync(
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Command is "setDiscovering")
        {
            if (!NativeQamPayload.TryReadEnabled(request.Payload, out bool discovering))
            {
                return new(false, "The discovery payload is invalid.");
            }

            // BluetoothScanning is manager-owned and driven by the same sweep as Wi-Fi, so
            // discovery goes through the scanning lifecycle rather than being set directly. One
            // sweep covering both radios is also what the taskbar's panel does.
            await NativeQamUi.RunAsync(() =>
            {
                if (discovering)
                {
                    _radios.StartScanning();
                }
                else
                {
                    _radios.StopScanning();
                }
            }).ConfigureAwait(false);
            return new(true, null);
        }

        if (request.Command is "setTrusted" or "setWakeAllowed")
        {
            Log.Info($"Bluetooth: '{request.Command}' accepted with no Windows equivalent.");
            return new(true, null);
        }

        if (!NativeQamPayload.TryReadBoundedString(request.Payload, "device", 256, out string deviceId))
        {
            return new(false, "The Bluetooth device payload is invalid.");
        }

        BluetoothDeviceEntry? device = null;
        await NativeQamUi.RunAsync(() => device = _radios.BluetoothDevices.FirstOrDefault(entry =>
            string.Equals(entry.Id, deviceId, StringComparison.Ordinal))).ConfigureAwait(false);
        if (device is null)
        {
            Log.Warn($"Bluetooth: '{deviceId}' is no longer present.");
            return new(false, "That device is no longer present.");
        }

        switch (request.Command)
        {
            case "connect":
                await _radios.SetAudioConnectionAsync(device, connect: true).ConfigureAwait(false);
                return new(true, null);
            case "disconnect":
                await _radios.SetAudioConnectionAsync(device, connect: false).ConfigureAwait(false);
                return new(true, null);
            case "forget":
                await _radios.UnpairAsync(device).ConfigureAwait(false);
                return new(true, null);
            case "pair":
                // Discovery drives the prompt; the user answers it exactly as they do from the
                // taskbar's radio panel.
                await NativeQamUi.RunAsync(_radios.StartScanning).ConfigureAwait(false);
                return new(true, null);
            case "cancelPair":
                await NativeQamUi.RunAsync(_radios.StopScanning).ConfigureAwait(false);
                return new(true, null);
            default:
                return new(false, "The requested semantic service is not active.");
        }
    }
}
