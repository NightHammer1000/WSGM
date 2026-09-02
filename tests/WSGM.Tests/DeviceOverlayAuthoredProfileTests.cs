using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DeviceOverlayAuthoredProfileTests
{
    private static DeviceAuthoredProfile Profile(string id, string name) => new()
    {
        ProfileId = id,
        Name = name,
        CapabilityId = "thermal.fan-curve",
    };

    [Fact]
    public void NoAuthoredProfilesShowsNoRowAtAll()
    {
        // Unlike the hardware-profile row, which is always present because the user cannot create
        // one. These are created in Settings, so a row offering a choice between nothing would
        // invite a press that cannot do anything.
        Assert.Null(DeviceOverlayBridge.AuthoredProfileView([], null, false));
    }

    [Fact]
    public void AProfileChosenForEverythingSaysSo()
    {
        DescriptorRow? row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet"), Profile("loud", "Loud")],
            "quiet",
            applicationScoped: false);

        Assert.Equal("QUIET", row?.TrailingText);
        Assert.Contains("everything", row?.Description);
    }

    [Fact]
    public void AProfileChosenForOneGameSaysThatInstead()
    {
        // The same word with very different consequences: this is the difference the user opens the
        // row to check mid-game.
        DescriptorRow? row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "quiet",
            applicationScoped: true);

        Assert.Contains("this game only", row?.Description);
    }

    [Fact]
    public void NothingChosenReadsAsNoneRatherThanEmpty()
    {
        DescriptorRow? row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            null,
            applicationScoped: false);

        Assert.Equal("NONE", row?.TrailingText);
        Assert.Equal(DescriptorStatus.None, row?.Status);
    }

    [Fact]
    public void ASelectionNamingADeletedProfileIsSaidPlainlyNotShownAsNone()
    {
        // None is a state the user chose; this is not, and showing them identically hides a
        // selection that has silently stopped working.
        DescriptorRow? row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "deleted",
            applicationScoped: true);

        Assert.Equal("MISSING", row?.TrailingText);
        Assert.Equal(DescriptorStatus.Warning, row?.Status);
    }

    [Fact]
    public void TheRowOffersACycleOnceThereIsSomethingToCycleTo()
    {
        DescriptorRow? row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "quiet",
            applicationScoped: false);

        Assert.True(row?.CanInvoke);
    }

    [Fact]
    public void ADeletedSelectionStaysCyclableSoTheUserCanGetOutOfIt()
    {
        // Pressing it moves to a profile that does exist, which is the fastest way out of the state
        // for someone mid-game.
        DescriptorRow? row = DeviceOverlayBridge.AuthoredProfileView(
            [Profile("quiet", "Quiet")],
            "deleted",
            applicationScoped: false);

        Assert.True(row?.CanInvoke);
    }

    [Fact]
    public void CyclingWrapsThroughNoneSoAProfileCanBeTurnedOffWithoutSettings()
    {
        // The same wrap the hardware-profile row already offers: past the last profile is "none",
        // so a user can turn one off mid-game without opening Settings.
        Assert.Equal("loud", DeviceOverlayBridge.NextProfile(["quiet", "loud"], "quiet"));
        Assert.Null(DeviceOverlayBridge.NextProfile(["quiet", "loud"], "loud"));
        Assert.Equal("quiet", DeviceOverlayBridge.NextProfile(["quiet", "loud"], null));
    }
}
