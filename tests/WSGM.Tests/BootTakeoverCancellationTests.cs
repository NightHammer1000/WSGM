using WSGM.Shell;

namespace WSGM.Tests;

public sealed class BootTakeoverCancellationTests
{
    [Fact]
    public void RequestDesktop_WhileTakeoverActive_CancelsAndRecordsRecovery()
    {
        using var takeover = new BootTakeoverCancellation();

        var accepted = takeover.RequestDesktop();

        Assert.True(accepted);
        Assert.True(takeover.DesktopRequested);
        Assert.True(takeover.Token.IsCancellationRequested);
    }

    [Fact]
    public void RequestDesktop_AfterTakeoverCompleted_LeavesOrdinaryTransitionInControl()
    {
        using var takeover = new BootTakeoverCancellation();
        takeover.Complete();

        var accepted = takeover.RequestDesktop();

        Assert.False(accepted);
        Assert.False(takeover.DesktopRequested);
        Assert.False(takeover.Token.IsCancellationRequested);
    }

    [Fact]
    public void Complete_AfterDesktopRequest_PreservesAcceptedRecovery()
    {
        using var takeover = new BootTakeoverCancellation();
        takeover.RequestDesktop();

        takeover.Complete();

        Assert.True(takeover.DesktopRequested);
        Assert.True(takeover.Token.IsCancellationRequested);
    }

    [Fact]
    public void RequestShutdown_CancelsWithoutStartingTheSplashDesktopPath()
    {
        using var takeover = new BootTakeoverCancellation();
        takeover.RequestDesktop();

        bool accepted = takeover.RequestShutdown();

        Assert.True(accepted);
        Assert.True(takeover.ShutdownRequested);
        Assert.False(takeover.DesktopRequested);
        Assert.True(takeover.Token.IsCancellationRequested);
    }
}
