using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class VolumeOsdVisibilityTests
{
    // QUNS_ACCEPTS_NOTIFICATIONS — the plain-desktop state.
    private const int QunsAcceptsNotifications = 5;

    [Fact]
    public void SteamAndBorderlessNotificationStatesAllowTheVolumeOsd()
    {
        Assert.True(VolumeOsdVisibility.AllowsVolumeOsd(0, QunsAcceptsNotifications));
        Assert.True(VolumeOsdVisibility.AllowsVolumeOsd(0, 2));
        Assert.True(VolumeOsdVisibility.AllowsVolumeOsd(0, 4));
        Assert.True(VolumeOsdVisibility.AllowsVolumeOsd(0, 7));
    }

    [Fact]
    public void ConfirmedExclusiveFullscreenSuppressesTheVolumeOsd()
    {
        Assert.False(VolumeOsdVisibility.AllowsVolumeOsd(0, NativeMethods.QunsRunningD3dFullScreen));
    }

    [Fact]
    public void LockedOrInactiveSessionSuppressesTheVolumeOsd()
    {
        Assert.False(VolumeOsdVisibility.AllowsVolumeOsd(0, NativeMethods.QunsNotPresent));
    }

    [Fact]
    public void FailedNotificationQuerySuppressesTheVolumeOsd()
    {
        Assert.False(VolumeOsdVisibility.AllowsVolumeOsd(unchecked((int)0x80004005), QunsAcceptsNotifications));
    }
}
