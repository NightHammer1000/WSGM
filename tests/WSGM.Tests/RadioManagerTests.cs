using WindowsDeviceControl;
using WSGM.Shell;
using PairingOutcome = WindowsDeviceControl.WindowsRadio.PairingOutcome;
using RadioPower = WindowsDeviceControl.WindowsRadio.Power;
using WifiConnectionState = WindowsDeviceControl.WindowsRadio.WifiConnectionState;
using WifiFailureKind = WindowsDeviceControl.WindowsRadio.WifiFailureKind;
using WifiSecurity = WindowsDeviceControl.WindowsRadio.WifiSecurity;

namespace WSGM.Tests;

public class RadioManagerTests
{
    [Theory]
    [InlineData(RadioPower.Off, WifiConnectionState.Connected, "Off")]
    [InlineData(RadioPower.Disabled, WifiConnectionState.Connected, "Blocked by Windows")]
    [InlineData(RadioPower.Absent, WifiConnectionState.Connected, "No Wi-Fi adapter")]
    [InlineData(RadioPower.Unknown, WifiConnectionState.Connected, "State unavailable")]
    [InlineData(RadioPower.On, WifiConnectionState.Connected, "Connected")]
    [InlineData(RadioPower.On, WifiConnectionState.Connecting, "Connecting...")]
    [InlineData(RadioPower.On, WifiConnectionState.Disconnected, "Not connected")]
    public void WifiWordingCoversEveryRadioAndInterfaceState(
        RadioPower power, WifiConnectionState state, string expected)
        => Assert.Equal(expected, RadioManager.DescribeWifi(power, state));

    [Fact]
    public void APoweredOffWifiRadioNeverClaimsAConnection()
    {
        // The interface can still report "connected" for a moment after the radio
        // goes down; the radio state has to win or the tile lies.
        Assert.Equal(
            "Off",
            RadioManager.DescribeWifi(RadioPower.Off, WifiConnectionState.Connected));
    }

    [Theory]
    [InlineData(RadioPower.Off, 3, "Off")]
    [InlineData(RadioPower.Absent, 0, "No Bluetooth adapter")]
    [InlineData(RadioPower.On, 0, "On")]
    [InlineData(RadioPower.On, 2, "On, 2 device(s)")]
    public void BluetoothWordingCoversEveryRadioState(
        RadioPower power, int devices, string expected)
        => Assert.Equal(expected, RadioManager.DescribeBluetooth(power, devices));

    [Theory]
    [InlineData(RadioPower.Off, "is off")]
    [InlineData(RadioPower.Disabled, "blocked")]
    [InlineData(RadioPower.Absent, "no Wi-Fi adapter")]
    [InlineData(RadioPower.Unknown, "unavailable")]
    public void AnUnusableRadioSaysWhyRatherThanJustOff(RadioPower power, string expected)
    {
        // "Off" for a policy-blocked or missing adapter leaves the user
        // pressing a switch that cannot do anything.
        Assert.Contains(expected, RadioManager.DescribeUnavailable(power, "Wi-Fi"));
    }

    [Fact]
    public void OnlyARejectedKeyAsksTheUserToRetypeThePassword()
    {
        Assert.Contains(
            "password",
            RadioManager.DescribeConnectFailure(WifiFailureKind.KeyRejected, 0, ""));
    }

    [Fact]
    public void AnUnreachableNetworkNeverBlamesThePassword()
    {
        // Re-prompting here would make the user retype a password that was never
        // even tried, which is worse than saying the network was not reachable.
        var message = RadioManager.DescribeConnectFailure(WifiFailureKind.Unreachable, 0, "");
        Assert.DoesNotContain("password", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("range", message);
    }

    [Fact]
    public void AnUnknownFailureFallsBackToWindowsMessage()
    {
        Assert.Equal(
            "boom",
            RadioManager.DescribeConnectFailure(WifiFailureKind.Unknown, 0, "boom"));
        // ...and still says something when there is no message at all.
        Assert.False(string.IsNullOrWhiteSpace(
            RadioManager.DescribeConnectFailure(WifiFailureKind.Unknown, 0, "")));
    }

    [Fact]
    public void TheLocationConsentGateIsNamedRatherThanShownAsARawError()
    {
        // Win32 5 from a scan is the 24H2 consent gate, not something elevating
        // or retrying can fix, so it must not read as a generic failure.
        var message = RadioManager.DescribeScanFailure("WlanScan failed (Win32 5)");
        Assert.Contains("location", message, StringComparison.OrdinalIgnoreCase);

        var other = RadioManager.DescribeScanFailure("WlanScan failed (Win32 1168)");
        Assert.DoesNotContain("location", other, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PairingOutcome.Paired, "Pad is paired.")]
    [InlineData(PairingOutcome.AlreadyPaired, "Pad was already paired.")]
    [InlineData(PairingOutcome.Cancelled, "Pairing with Pad was cancelled.")]
    public void PairOutcomeWordingNamesTheDevice(PairingOutcome outcome, string expected)
        => Assert.Equal(expected, RadioManager.DescribePairOutcome(outcome, "Pad", ""));

    [Fact]
    public void AFailedPairingSuggestsPairingMode()
        => Assert.Contains(
            "pairing mode",
            RadioManager.DescribePairOutcome(PairingOutcome.Failed, "Pad", ""));

    [Fact]
    public void AStartupErrorUsesTheWindowsMessageWhenThereIsOne()
    {
        // A null outcome is the attempt that threw before Windows produced one.
        Assert.Equal("no such device", RadioManager.DescribePairOutcome(null, "Pad", "no such device"));
        Assert.Contains("Pad", RadioManager.DescribePairOutcome(null, "Pad", ""));
    }

    [Fact]
    public void OneLiveRadioWinsTheAggregateState()
        => Assert.Equal(
            WindowsRadio.Power.On,
            WindowsRadio.AggregatePower([WindowsRadio.Power.Off, WindowsRadio.Power.On]));

    [Fact]
    public void NoRadioIsReportedAsAbsent()
        => Assert.Equal(WindowsRadio.Power.Absent, WindowsRadio.AggregatePower([]));

    [Theory]
    [InlineData(294932u, WifiFailureKind.KeyRejected)] // MSMSEC_PSK_MISMATCH_SUSPECTED
    [InlineData(262148u, WifiFailureKind.SecurityMismatch)] // MSMSEC_PROFILE_PSK_LENGTH
    [InlineData(196614u, WifiFailureKind.Unreachable)] // any MSM association failure
    [InlineData(1u, WifiFailureKind.Unknown)]
    public void WlanReasonFamiliesKeepPasswordAndReachabilityFailuresDistinct(
        uint reason,
        WifiFailureKind expected)
        => Assert.Equal(expected, WindowsRadio.GetReasonVerdict(reason));

    [Fact]
    public void ARawPskUsesTheNetworkKeyProfileShape()
    {
        var xml = WifiProfile.CreatePsk(
            "Cafe", "Cafe", "Cafe"u8.ToArray(), string.Concat(Enumerable.Repeat("a1B2", 16)),
            WifiProfile.PskFlavor.Wpa3Transition);
        Assert.Contains("<keyType>networkKey</keyType>", xml);
        Assert.Contains("profile/v4", xml);
    }

    [Fact]
    public void AProfileRoundTripsEscapedAndHexSsids()
    {
        var escaped = WifiProfile.CreateOpen("A&B", "A&B", "A&B"u8.ToArray(), false);
        Assert.Equal("A&B"u8.ToArray(), WifiProfile.TryReadSsid(escaped));

        var raw = new byte[] { 0x41, 0xff, 0x42 };
        var hex = WifiProfile.CreatePsk(
            "A?B", "A?B", raw, "password1", WifiProfile.PskFlavor.Wpa2Aes);
        Assert.Equal(raw, WifiProfile.TryReadSsid(hex));
    }

    [Fact]
    public void ProfileAuthoringPreservesEveryWindowsSpecificShape()
    {
        var escaped = WifiProfile.CreatePsk(
            "A&B<C>",
            "A&B<C>",
            "A&B<C>"u8.ToArray(),
            "pw\"&<>'x",
            WifiProfile.PskFlavor.Wpa3Transition);
        Assert.Contains("<name>A&amp;B&lt;C&gt;</name>", escaped);
        Assert.Contains("<keyMaterial>pw&quot;&amp;&lt;&gt;&apos;x</keyMaterial>", escaped);
        Assert.Contains(
            "<transitionMode xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v4\">true</transitionMode>",
            escaped);

        var enhancedOpen = WifiProfile.CreateOpen("Cafe", "Cafe", "Cafe"u8.ToArray(), true);
        Assert.Contains("<authentication>OWE</authentication>", enhancedOpen);
        Assert.DoesNotContain("<encryption>none</encryption>", enhancedOpen);

        var legacy = WifiProfile.CreatePsk(
            "Old", "Old", "Old"u8.ToArray(), "password1", WifiProfile.PskFlavor.WpaTkip);
        Assert.Contains("<authentication>WPAPSK</authentication>", legacy);
        Assert.Contains("<encryption>TKIP</encryption>", legacy);
    }

    [Fact]
    public void AProfileNameNeverReplacesTheNetworkIdentity()
    {
        var xml = WifiProfile.CreatePsk(
            "Cafe 2", " Cafe ", " Cafe "u8.ToArray(), "password1",
            WifiProfile.PskFlavor.Wpa2Aes);
        Assert.Contains("<name>Cafe 2</name>", xml);
        Assert.Equal(" Cafe "u8.ToArray(), WifiProfile.TryReadSsid(xml));
        Assert.Null(WifiProfile.TryReadSsid("<WLANProfile />"));
        Assert.Null(WifiProfile.TryReadSsid(
            "<SSIDConfig><SSID><hex>ABC</hex></SSID></SSIDConfig>"));
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("12345678", true)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]
    [InlineData("pass\tword", false)]
    [InlineData("pässword", false)]
    public void PassphraseValidationUses80211Bounds(string passphrase, bool expected)
        => Assert.Equal(expected, WifiProfile.PassphraseIsValid(passphrase));
}

public class RadioEntryTests
{
    [Fact]
    public void ASecuredNetworkWithoutASavedProfileAsksForAPassword()
    {
        var entry = new WifiNetworkEntry("Cafe") { Security = WifiSecurity.PersonalPsk };
        Assert.True(entry.NeedsPassword);
    }

    [Fact]
    public void ASavedNetworkNeverAsksForAPasswordAgain()
    {
        var entry = new WifiNetworkEntry("Cafe")
        {
            Security = WifiSecurity.PersonalPsk,
            Saved = true,
        };
        Assert.False(entry.NeedsPassword);
    }

    [Fact]
    public void AnOpenNetworkNeverAsksForAPassword()
    {
        var entry = new WifiNetworkEntry("Cafe") { Security = WifiSecurity.Open };
        Assert.False(entry.NeedsPassword);
    }

    [Fact]
    public void NeedsPasswordRaisesChangeNotificationWhenTheSavedFlagFlips()
    {
        var entry = new WifiNetworkEntry("Cafe") { Security = WifiSecurity.PersonalPsk };
        var raised = new List<string?>();
        entry.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        entry.Saved = true;

        // Without this the password prompt would keep appearing for a network
        // that has just been saved.
        Assert.Contains(nameof(WifiNetworkEntry.NeedsPassword), raised);
    }

    [Fact]
    public void ADeviceWithoutANameStillShowsSomethingSelectable()
    {
        var entry = new BluetoothDeviceEntry("BT#1");
        Assert.False(string.IsNullOrWhiteSpace(entry.Name));
    }

    [Fact]
    public void TheRowActionFollowsPairedAndBusyState()
    {
        var entry = new BluetoothDeviceEntry("BT#1");
        Assert.Equal("Pair", entry.ActionText);

        // Paired is where the primary action becomes the SOFT one. Unpairing
        // lives on its own button, so a tap meant as "disconnect" can never
        // destroy the pairing.
        entry.Paired = true;
        entry.AudioConnectable = true;
        Assert.Equal("Connect", entry.ActionText);

        entry.AudioActive = true;
        Assert.Equal("Disconnect", entry.ActionText);

        entry.Busy = true;
        Assert.Equal("Working...", entry.ActionText);
    }

    [Fact]
    public void TheConnectActionFollowsTheAudioEndpointsNotTheAssociation()
    {
        // A headset can hold an association for another profile while its audio
        // endpoints are unplugged. Reading the broader state would label the
        // button Disconnect and then send the opposite one-shot.
        var entry = new BluetoothDeviceEntry("BT#1")
        {
            Paired = true,
            AudioConnectable = true,
            Connected = true,
            AudioActive = false,
        };
        Assert.Equal("Connect", entry.ActionText);
    }

    [Fact]
    public void APairedDeviceWithNoConnectActionOffersOnlyRemove()
    {
        // Mice and gamepads reconnect on their own initiative when used; there
        // is no host-side connect for them, and Windows shows none either.
        var entry = new BluetoothDeviceEntry("BT#1") { Paired = true };
        Assert.False(entry.PrimaryActionVisible);
        Assert.True(entry.RemoveVisible);

        // An unpaired stranger offers Pair only while Windows says pairing is
        // actually possible — a stale endpoint would fail every time.
        var stranger = new BluetoothDeviceEntry("BT#2");
        Assert.False(stranger.PrimaryActionVisible);
        stranger.CanPair = true;
        Assert.True(stranger.PrimaryActionVisible);
        Assert.False(stranger.RemoveVisible);
    }

    [Fact]
    public void ActionTextIsRepublishedWhenPairedOrBusyChanges()
    {
        var entry = new BluetoothDeviceEntry("BT#1");
        var raised = new List<string?>();
        entry.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        entry.Paired = true;
        entry.Busy = true;

        // The button label is derived, so it needs its own notification or the
        // row keeps offering "Pair" for an already-paired device.
        Assert.Equal(2, raised.FindAll(n => n == nameof(BluetoothDeviceEntry.ActionText)).Count);
    }
}
