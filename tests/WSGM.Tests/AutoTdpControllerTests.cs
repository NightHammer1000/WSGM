using WSGM.Core;

namespace WSGM.Tests;

public sealed class AutoTdpControllerTests
{
    private const string Game = "steam:70|1920x1080@60";
    private static readonly AutoTdpLimits Limits = new(8, 30, 2);

    [Fact]
    public void ASingleMissedWindowDoesNotRaisePower()
    {
        AutoTdpController controller = Started(15);

        IReadOnlyList<AutoTdpDecision> decisions = Replay(controller, Missing(2));

        Assert.All(decisions, decision => Assert.Equal(AutoTdpAction.Hold, decision.Action));
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void SustainedMissesRaisePowerOneStep()
    {
        AutoTdpController controller = Started(15);

        IReadOnlyList<AutoTdpDecision> decisions = Replay(controller, Missing(3));

        Assert.Equal(AutoTdpAction.Raise, decisions[^1].Action);
        Assert.Equal("sustained-miss", decisions[^1].Reason);
        Assert.Equal(17, controller.Watts);
    }

    [Fact]
    public void ContinuedMissesKeepRaisingUntilTheDeviceMaximum()
    {
        AutoTdpController controller = Started(26);

        IReadOnlyList<AutoTdpDecision> decisions = Replay(controller, Missing(60));

        Assert.Equal(30, controller.Watts);
        Assert.Contains(decisions, decision => decision.Reason == "at-maximum");
        // Nothing above the device maximum is ever requested, however long the misses continue.
        Assert.All(decisions, decision => Assert.True(decision.Watts <= 30));
    }

    [Fact]
    public void MeetingTheDeadlineWithoutHeadroomNeitherRaisesNorProbes()
    {
        AutoTdpController controller = Started(15);

        IReadOnlyList<AutoTdpDecision> decisions = Replay(controller, OnTarget(40));

        Assert.All(decisions, decision => Assert.Equal(AutoTdpAction.Hold, decision.Action));
        Assert.All(decisions, decision => Assert.Equal("on-target", decision.Reason));
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void ASettledPeriodOfHeadroomProbesOneStepDown()
    {
        AutoTdpController controller = Started(15);

        IReadOnlyList<AutoTdpDecision> decisions = Replay(controller, Comfortable(8));

        Assert.Equal(AutoTdpAction.Probe, decisions[^1].Action);
        Assert.Equal(13, controller.Watts);
        Assert.Equal(15, controller.LastGood);
        Assert.True(controller.IsProbing);
    }

    [Fact]
    public void AProbeThatKeepsDeliveringIsAcceptedAndLearned()
    {
        AutoTdpController controller = Started(15);
        Replay(controller, Comfortable(8));

        IReadOnlyList<AutoTdpDecision> decisions = Replay(
            controller,
            Comfortable(AutoTdpController.SettleWindows + AutoTdpController.ProbeWindows));

        Assert.Contains(decisions, decision => decision.Reason == "probe-accepted");
        Assert.False(controller.IsProbing);
        Assert.Equal(13, controller.Watts);
        Assert.Equal(13, controller.LearnedFloor(Game));
    }

    [Fact]
    public void AProbeThatCostsFramesRestoresTheLastGoodLimit()
    {
        AutoTdpController controller = Started(15);
        Replay(controller, Comfortable(8));
        Replay(controller, Comfortable(AutoTdpController.SettleWindows));

        IReadOnlyList<AutoTdpDecision> decisions = Replay(controller, Missing(1));

        Assert.Equal(AutoTdpAction.Restore, decisions[^1].Action);
        Assert.Equal("probe-rejected", decisions[^1].Reason);
        Assert.Equal(15, controller.Watts);
        Assert.Equal(15, controller.LearnedFloor(Game));
    }

    [Fact]
    public void ARejectedProbeIsNotRepeatedForTheSameContext()
    {
        AutoTdpController controller = Started(15);
        Replay(controller, Comfortable(8));
        Replay(controller, Comfortable(AutoTdpController.SettleWindows));
        Replay(controller, Missing(1));

        // A long settled period must not walk back into the limit that already stuttered.
        IReadOnlyList<AutoTdpDecision> decisions = Replay(controller, Comfortable(40));

        Assert.DoesNotContain(decisions, decision => decision.Action is AutoTdpAction.Probe);
        Assert.Contains(decisions, decision => decision.Reason == "below-learned-floor");
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void ACappedGameMeetingItsCapDescendsButNeverClimbs()
    {
        AutoTdpController controller = Started(20);

        IReadOnlyList<AutoTdpDecision> decisions = Replay(
            controller,
            AutoTdpReplay.Run(8, 16.6, 16.6, Game, capped: true));

        Assert.DoesNotContain(decisions, decision => decision.Action is AutoTdpAction.Raise);
        Assert.Equal(AutoTdpAction.Probe, decisions[^1].Action);
        Assert.Equal(18, controller.Watts);
    }

    [Fact]
    public void AMenuAtTheFrameCapIsNotTreatedAsAReasonToRaisePower()
    {
        AutoTdpController controller = Started(12);

        IReadOnlyList<AutoTdpDecision> decisions = Replay(
            controller,
            AutoTdpReplay.Run(30, 16.7, 16.6, Game, capped: true));

        Assert.DoesNotContain(decisions, decision => decision.Action is AutoTdpAction.Raise);
        Assert.True(controller.Watts <= 12);
    }

    [Fact]
    public void ATransientHeavySceneRecoversWithoutOscillating()
    {
        AutoTdpController controller = Started(15);

        // Settled, one heavy stretch, then settled again.
        Replay(controller, Comfortable(8));
        Replay(controller, Comfortable(AutoTdpController.SettleWindows));
        Replay(controller, Missing(1));
        IReadOnlyList<AutoTdpDecision> after = Replay(controller, Comfortable(30));

        Assert.Equal(15, controller.Watts);
        Assert.DoesNotContain(after, decision => decision.Action is AutoTdpAction.Raise);
        Assert.DoesNotContain(after, decision => decision.Action is AutoTdpAction.Probe);
    }

    [Fact]
    public void MissingTelemetryNeverCountsAsHeadroom()
    {
        AutoTdpController controller = Started(15);
        Replay(controller, Comfortable(7));

        Replay(controller, [new AutoTdpSample(double.NaN, 16.6, false, Game)]);
        IReadOnlyList<AutoTdpDecision> decisions = Replay(controller, Comfortable(1));

        Assert.Equal(AutoTdpAction.Hold, decisions[^1].Action);
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void AContextChangeDiscardsTheEvidenceGatheredForThePreviousOne()
    {
        AutoTdpController controller = Started(15);
        Replay(controller, Comfortable(7));

        IReadOnlyList<AutoTdpDecision> decisions = Replay(
            controller,
            AutoTdpReplay.Run(1, 10.0, 16.6, "steam:220|1920x1080@60"));

        Assert.Equal("context-changed", decisions[^1].Reason);
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void AManualChangeSuspendsControlAndAutomaticControlDoesNotResumeItself()
    {
        AutoTdpController controller = Started(15);
        controller.PauseForManualChange(22);

        IReadOnlyList<AutoTdpDecision> decisions = Replay(controller, Missing(30));

        Assert.True(controller.IsPaused);
        Assert.Equal(22, controller.Watts);
        Assert.All(decisions, decision => Assert.Equal("paused-manual", decision.Reason));
    }

    // Switching AutoTDP off and on is the only way control resumes after a manual change:
    // Start() is what clears the pause, and it starts from the manual value the user left.
    [Fact]
    public void SwitchingAutoTdpOffAndOnReturnsControlFromTheManualValue()
    {
        AutoTdpController controller = Started(15);
        controller.PauseForManualChange(22);
        controller.Start(22, Limits, Game);

        IReadOnlyList<AutoTdpDecision> decisions = Replay(controller, Missing(3));

        Assert.False(controller.IsPaused);
        Assert.Equal(AutoTdpAction.Raise, decisions[^1].Action);
        Assert.Equal(24, controller.Watts);
    }

    [Fact]
    public void StoppingRestoresTheLimitAutoTdpTookOverFrom()
    {
        AutoTdpController controller = Started(15);
        Replay(controller, Missing(3));

        AutoTdpDecision decision = controller.Stop(restoreTo: 15);

        Assert.Equal(AutoTdpAction.Release, decision.Action);
        Assert.Equal(15, decision.Watts);
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void AKnownContextStartsFromItsLearnedFloor()
    {
        AutoTdpController controller = Started(15);
        Replay(controller, Comfortable(8));
        Replay(controller, Comfortable(AutoTdpController.SettleWindows + AutoTdpController.ProbeWindows));

        int resumed = controller.Start(20, Limits, Game);

        Assert.Equal(13, resumed);
        Assert.Equal(13, controller.Watts);
    }

    [Fact]
    public void ALearnedFloorIsStillRaisedWhenTheContextGetsHeavier()
    {
        AutoTdpController controller = Started(15);
        Replay(controller, Comfortable(8));
        Replay(controller, Comfortable(AutoTdpController.SettleWindows + AutoTdpController.ProbeWindows));
        controller.Start(20, Limits, Game);

        IReadOnlyList<AutoTdpDecision> decisions = Replay(controller, Missing(3));

        Assert.Equal(AutoTdpAction.Raise, decisions[^1].Action);
        Assert.Equal(15, controller.Watts);
    }

    [Fact]
    public void UnusableDeviceBoundsProduceNoWrites()
    {
        AutoTdpController controller = new();
        AutoTdpLimits broken = new(0, 0, 0);
        controller.Start(15, broken, Game);

        IReadOnlyList<AutoTdpDecision> decisions =
            AutoTdpReplay.Run(controller, broken, Missing(30));

        Assert.All(decisions, decision => Assert.False(decision.RequiresWrite));
        Assert.All(decisions, decision => Assert.Equal("limits-unusable", decision.Reason));
    }

    [Fact]
    public void AWriteIsFollowedByASettlingWindowBeforeMoreEvidenceIsCounted()
    {
        AutoTdpController controller = Started(15);
        Replay(controller, Missing(3));

        IReadOnlyList<AutoTdpDecision> decisions =
            Replay(controller, Missing(AutoTdpController.SettleWindows));

        Assert.All(decisions, decision => Assert.Equal("settling", decision.Reason));
        Assert.Equal(17, controller.Watts);
    }

    private static AutoTdpController Started(int watts)
    {
        AutoTdpController controller = new();
        controller.Start(watts, Limits, Game);
        return controller;
    }

    private static IReadOnlyList<AutoTdpDecision> Replay(
        AutoTdpController controller,
        IEnumerable<AutoTdpSample> trace) =>
        AutoTdpReplay.Run(controller, Limits, trace);

    private static IEnumerable<AutoTdpSample> Missing(int count) =>
        AutoTdpReplay.Run(count, 22.0, 16.6, Game);

    private static IEnumerable<AutoTdpSample> OnTarget(int count) =>
        AutoTdpReplay.Run(count, 16.0, 16.6, Game);

    private static IEnumerable<AutoTdpSample> Comfortable(int count) =>
        AutoTdpReplay.Run(count, 12.0, 16.6, Game);
}
