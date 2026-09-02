using WSGM.Shell;

namespace WSGM.Tests;

public sealed class SteamUiTransportGateTests
{
    [Fact]
    public void TransportShouldBeOpen_GameModeWithoutBigPictureWindow_HoldsTheTransportClosed()
    {
        Assert.False(SteamUiReadiness.TransportShouldBeOpen(
            cefMasterEnabled: true,
            inGameMode: true,
            gameModeTransitionPending: false,
            bigPictureReady: false));
    }

    [Fact]
    public void TransportShouldBeOpen_GameModeWithBigPictureWindow_Opens()
    {
        Assert.True(SteamUiReadiness.TransportShouldBeOpen(
            cefMasterEnabled: true,
            inGameMode: true,
            gameModeTransitionPending: false,
            bigPictureReady: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransportShouldBeOpen_DesktopMode_OpensOnTheMasterSwitchAlone(bool bigPictureReady)
    {
        Assert.True(SteamUiReadiness.TransportShouldBeOpen(
            cefMasterEnabled: true,
            inGameMode: false,
            gameModeTransitionPending: false,
            bigPictureReady: bigPictureReady));
    }

    [Fact]
    public void TransportShouldBeOpen_BigPictureRequestPendingInDesktopMode_HoldsTheTransportClosed()
    {
        // The desktop-to-game transition retracts and closes BEFORE steam://open/bigpicture
        // fires: Steam rebuilds its front-end for that request, and injected state left behind
        // stalled the gamepad UI bootstrap (device-diagnosed 2026-09-01).
        Assert.False(SteamUiReadiness.TransportShouldBeOpen(
            cefMasterEnabled: true,
            inGameMode: false,
            gameModeTransitionPending: true,
            bigPictureReady: false));
    }

    [Fact]
    public void TransportShouldBeOpen_BigPictureRequestPendingAndWindowUp_Opens()
    {
        Assert.True(SteamUiReadiness.TransportShouldBeOpen(
            cefMasterEnabled: true,
            inGameMode: false,
            gameModeTransitionPending: true,
            bigPictureReady: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TransportShouldBeOpen_MasterSwitchOff_NeverOpens(bool inGameMode, bool bigPictureReady)
    {
        Assert.False(SteamUiReadiness.TransportShouldBeOpen(
            cefMasterEnabled: false,
            inGameMode: inGameMode,
            gameModeTransitionPending: false,
            bigPictureReady: bigPictureReady));
    }
}
