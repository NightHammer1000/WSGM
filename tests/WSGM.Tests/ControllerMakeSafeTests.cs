using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class ControllerMakeSafeTests
{
    [Fact]
    public void SequenceRefusesTargetRemovalBeforeThePhysicalReleaseConcludes()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(verified: true);

        Assert.False(sequence.CanRemoveTarget);
        Assert.True(sequence.HidHideMustRemain);
        Assert.Throws<InvalidOperationException>(
            () => sequence.RecordTargetRemoved(verified: true));
    }

    [Fact]
    public void SequenceRefusesHidHideRemovalWhileTheTargetStillExists()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyVerified);

        Assert.False(sequence.CanRemoveHidHide);
        Assert.True(sequence.HidHideMustRemain);
        Assert.Throws<InvalidOperationException>(() => sequence.RecordHidHideRemoved(verified: true));
    }

    [Fact]
    public void CompleteVerifiedSequenceReportsAVerifiedRelease()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyVerified);
        sequence.RecordTargetRemoved(verified: true);
        sequence.RecordHidHideRemoved(verified: true);

        Assert.Equal(ControllerHandoffResult.ReleasedVerified, sequence.Complete());
        Assert.Equal(ControllerHandoffStep.WsgmStateRemoved, sequence.Step);
        Assert.False(sequence.HidHideMustRemain);
    }

    [Fact]
    public void AnUnverifiedPluginTopologyDowngradesTheResultButStillRemovesWsgmState()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyUnverified);
        sequence.RecordTargetRemoved(verified: true);
        sequence.RecordHidHideRemoved(verified: true);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
        Assert.True(sequence.TargetRemoved);
        Assert.True(sequence.HidHideRemoved);
    }

    [Fact]
    public void AnUnobservedPluginReleaseStillPermitsRemovalAndReportsUnverified()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(verified: true);
        sequence.RecordPluginReleaseUnobserved();

        Assert.True(sequence.CanRemoveTarget);
        Assert.Equal(ControllerHandoffStep.TopologyUnverified, sequence.Step);
        sequence.RecordTargetRemoved(verified: true);
        sequence.RecordHidHideRemoved(verified: true);
        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
    }

    [Fact]
    public void AnUnverifiedHidHideRemovalDowngradesAnOtherwiseCleanSequence()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyVerified);
        sequence.RecordTargetRemoved(verified: true);
        sequence.RecordHidHideRemoved(verified: false);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
    }

    [Fact]
    public void APluginReportingAVerifiedTopologyWithAnUnverifiedResultIsNotTreatedAsClean()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(verified: true);
        sequence.RecordPluginRelease(
            ControllerHandoffStep.TopologyVerified,
            ControllerHandoffResult.ReleasedUnverified);
        sequence.RecordTargetRemoved(verified: true);
        sequence.RecordHidHideRemoved(verified: true);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
    }

    [Fact]
    public void SequenceRefusesASecondPluginReleaseAndAWsgmOwnedStepFromThePlugin()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyVerified);
        Assert.Throws<InvalidOperationException>(() => sequence.RecordPluginRelease(
            ControllerHandoffStep.TopologyVerified,
            ControllerHandoffResult.ReleasedVerified));

        ControllerMakeSafeSequence fresh = new();
        fresh.RecordNeutralized(verified: true);
        Assert.Throws<InvalidOperationException>(() => fresh.RecordPluginRelease(
            ControllerHandoffStep.VirtualTargetNeutralized,
            ControllerHandoffResult.ReleasedVerified));
    }

    [Fact]
    public void SequenceRefusesCompletionBeforeWsgmStateIsRemoved()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyVerified);
        sequence.RecordTargetRemoved(verified: true);

        Assert.Throws<InvalidOperationException>(() => sequence.Complete());
        Assert.Equal(ControllerHandoffResult.InProgress, sequence.Result);
    }

    [Fact]
    public void SequenceRefusesNeutralizingTwice()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(verified: true);

        Assert.Throws<InvalidOperationException>(
            () => sequence.RecordNeutralized(verified: true));
    }

    [Fact]
    public void AnUnverifiedNeutralizationDowngradesAnOtherwiseCleanSequence()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(verified: false);
        sequence.RecordPluginRelease(
            ControllerHandoffStep.TopologyVerified,
            ControllerHandoffResult.ReleasedVerified);
        sequence.RecordTargetRemoved(verified: true);
        sequence.RecordHidHideRemoved(verified: true);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
    }

    [Fact]
    public void AFailedTargetRemovalStillRemovesHidHideButIsNeverReportedAsVerified()
    {
        ControllerMakeSafeSequence sequence = Released(ControllerHandoffStep.TopologyVerified);
        sequence.RecordTargetRemoved(verified: false);
        sequence.RecordHidHideRemoved(verified: true);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, sequence.Complete());
        Assert.True(sequence.HidHideRemoved);
    }

    private static ControllerMakeSafeSequence Released(ControllerHandoffStep step)
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(verified: true);
        sequence.RecordPluginRelease(
            step,
            step is ControllerHandoffStep.TopologyVerified
                ? ControllerHandoffResult.ReleasedVerified
                : ControllerHandoffResult.ReleasedUnverified);
        return sequence;
    }
}
