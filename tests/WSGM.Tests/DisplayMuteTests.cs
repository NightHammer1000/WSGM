using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Tests;

public class DisplayMuteTests
{
    [Fact]
    public void DownloadCompletionRestoreDelay_IsTenSeconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            DisplayMuteDecider.DownloadCompletionRestoreDelay);
    }

    [Fact]
    public void Reconcile_DarkDisplayWithActiveDownload_Mutes()
    {
        var action = DisplayMuteDecider.Reconcile(
            enabled: true,
            displayOff: true,
            downloadActive: true,
            mutedByUs: false);

        Assert.Equal(DisplayMuteAction.Mute, action);
    }

    [Fact]
    public void Reconcile_DarkDisplayWithoutActiveDownload_DoesNothing()
    {
        var action = DisplayMuteDecider.Reconcile(
            enabled: true,
            displayOff: true,
            downloadActive: false,
            mutedByUs: false);

        Assert.Equal(DisplayMuteAction.NoChange, action);
    }

    [Fact]
    public void Reconcile_LitDisplayWithActiveDownload_DoesNothing()
    {
        var action = DisplayMuteDecider.Reconcile(
            enabled: true,
            displayOff: false,
            downloadActive: true,
            mutedByUs: false);

        Assert.Equal(DisplayMuteAction.NoChange, action);
    }

    [Fact]
    public void Reconcile_DisabledSettingWithDarkDownload_DoesNothing()
    {
        var action = DisplayMuteDecider.Reconcile(
            enabled: false,
            displayOff: true,
            downloadActive: true,
            mutedByUs: false);

        Assert.Equal(DisplayMuteAction.NoChange, action);
    }

    [Fact]
    public void Reconcile_LastDownloadFinishesWhileDark_DelaysRestore()
    {
        var action = DisplayMuteDecider.Reconcile(
            enabled: true,
            displayOff: true,
            downloadActive: false,
            mutedByUs: true);

        Assert.Equal(DisplayMuteAction.DelayRestore, action);
    }

    [Fact]
    public void Reconcile_DisplayReturnsWhileMuted_RestoresImmediately()
    {
        var action = DisplayMuteDecider.Reconcile(
            enabled: true,
            displayOff: false,
            downloadActive: true,
            mutedByUs: true);

        Assert.Equal(DisplayMuteAction.Restore, action);
    }

    [Fact]
    public void Reconcile_DownloadRestartsDuringDelayedRestore_KeepsMute()
    {
        var action = DisplayMuteDecider.Reconcile(
            enabled: true,
            displayOff: true,
            downloadActive: true,
            mutedByUs: true);

        Assert.Equal(DisplayMuteAction.NoChange, action);
    }

    [Fact]
    public void Reconcile_SettingDisabledWhileMuted_RestoresImmediately()
    {
        var action = DisplayMuteDecider.Reconcile(
            enabled: false,
            displayOff: true,
            downloadActive: true,
            mutedByUs: true);

        Assert.Equal(DisplayMuteAction.Restore, action);
    }

    [Fact]
    public void IsDisplayOff_Off_IsTrue()
    {
        Assert.True(DisplayMuteDecider.IsDisplayOff(DisplayMuteDecider.DisplayOff));
    }

    [Theory]
    [InlineData(DisplayMuteDecider.DisplayOn)]
    [InlineData(DisplayMuteDecider.DisplayDimmed)]
    public void IsDisplayOff_LitDisplay_IsFalse(int state)
    {
        Assert.False(DisplayMuteDecider.IsDisplayOff(state));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(-1)]
    public void IsDisplayOff_UnknownState_IsFalseRatherThanLeavingTheDeviceSilent(int state)
    {
        Assert.False(DisplayMuteDecider.IsDisplayOff(state));
    }

    [Fact]
    public void MayReportDark_SessionDisplayStatus_IsTrusted()
    {
        Assert.True(DisplayMuteDecider.MayReportDark(DisplayStateSource.Session));
    }

    [Theory]
    [InlineData(DisplayStateSource.Console)]
    [InlineData(DisplayStateSource.LegacyMonitor)]
    public void MayReportDark_RedundantWakeSources_NeverStartAMute(DisplayStateSource source)
    {
        // They exist so a missed wake still restores; a cross-session or stale "off" from
        // them must not be able to silence a device whose own display is lit.
        Assert.False(DisplayMuteDecider.MayReportDark(source));
    }

    [Fact]
    public void HasInputSince_NoNewInput_IsFalse()
    {
        Assert.False(DisplayMuteDecider.HasInputSince(1_000, 1_000));
    }

    [Fact]
    public void HasInputSince_LaterTick_IsTrue()
    {
        Assert.True(DisplayMuteDecider.HasInputSince(1_000, 1_001));
    }

    [Fact]
    public void HasInputSince_TickCountWrapAround_StillDetectsNewInput()
    {
        // GetLastInputInfo reports a 32-bit tick count that wraps roughly every 49 days;
        // a plain > comparison would report "no input" for the whole wrap.
        Assert.True(DisplayMuteDecider.HasInputSince(uint.MaxValue - 500, 250));
    }

    [Fact]
    public void HasInputSince_StaleReadBeforeTheBaseline_IsFalse()
    {
        Assert.False(DisplayMuteDecider.HasInputSince(5_000, 4_000));
    }
}
