using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>
/// With Device Integration off, WSGM must be invisible to the hardware.
/// </summary>
/// <remarks>
/// This is the promise that lets someone run another manager — MSI Center, HandheldCompanion — beside
/// WSGM. It is not enough that WSGM stops writing: nothing may be created, claimed, hidden or
/// reconfigured either, because anything left behind is something the other manager then fights.
/// <para>
/// These pin the decisions that are pure. Observing on a real Claw that nothing moves while another
/// manager drives it is the attended half and stays in its own item.
/// </para>
/// </remarks>
public sealed class DeviceIntegrationOffTests
{
    [Fact]
    public void TheMasterSwitchOffMeansNoControllerManagementWhateverElseIsStored()
    {
        // The child preference is deliberately remembered rather than erased, so it has to be the
        // master that decides — otherwise turning integration off would leave WSGM still creating a
        // virtual controller and hiding the physical one.
        ControllerSelection selection = ControllerSelection.From(new DeviceIntegrationConfig
        {
            Enabled = false,
            ControllerManagementEnabled = true,
            ControllerTarget = ManagedControllerTarget.SteamDeckComposite,
        });

        Assert.False(selection.Enabled);
    }

    [Fact]
    public void TurningTheMasterOffKeepsTheChildPreferenceForNextTime()
    {
        // The remembered preference is what makes the switch reversible without the user having to
        // set everything up again, and it is only safe to keep because the test above holds.
        DeviceIntegrationConfig config = new()
        {
            Enabled = false,
            ControllerManagementEnabled = true,
        };

        Assert.True(config.ControllerManagementEnabled);
        Assert.False(ControllerSelection.From(config).Enabled);
    }

    [Fact]
    public void ADisabledSelectionCarriesNoTargetForAnythingToCreate()
    {
        ControllerSelection selection = ControllerSelection.From(new DeviceIntegrationConfig
        {
            Enabled = false,
            ControllerManagementEnabled = true,
        });

        // Nothing downstream may read a target out of a disabled selection and act on it.
        Assert.False(selection.Enabled);
        Assert.Equal("Controller management is off.", selection.DisabledDetail);
    }

    [Fact]
    public void NothingIsRemovedBeforeThePluginHasFinishedHandingItsDevicesBack()
    {
        // Switching integration off runs this. Pulling the virtual target while the plugin is still
        // releasing leaves the plugin talking to something that no longer exists.
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(verified: true);

        Assert.False(sequence.CanRemoveTarget);
        Assert.False(sequence.CanRemoveHidHide);

        // And WSGM's hiding must stay while the target is still there: removing it first would
        // expose the physical controller alongside the virtual one, so whatever takes over next
        // would see both at once.
        Assert.True(sequence.HidHideMustRemain);
    }

    [Fact]
    public void HidHideOutlivesTheTargetAndBothAreGoneAtTheEnd()
    {
        ControllerMakeSafeSequence sequence = new();
        sequence.RecordNeutralized(verified: true);
        sequence.RecordPluginRelease(
            ControllerHandoffStep.TopologyVerified,
            ControllerHandoffResult.ReleasedVerified);

        Assert.True(sequence.CanRemoveTarget);
        Assert.False(sequence.CanRemoveHidHide);

        sequence.RecordTargetRemoved(verified: true);
        Assert.True(sequence.TargetRemoved);
        Assert.True(sequence.CanRemoveHidHide);
        Assert.False(sequence.HidHideMustRemain);

        sequence.RecordHidHideRemoved(verified: true);
        Assert.True(sequence.HidHideRemoved);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void AutoTdpStartupAndReloadShareTheMasterSwitchPolicy(
        bool integrationEnabled,
        bool autoTdpEnabled,
        bool expected)
    {
        DeviceIntegrationConfig config = new()
        {
            Enabled = integrationEnabled,
            AutoTdpEnabled = autoTdpEnabled,
        };

        Assert.Equal(expected, ShellSession.ShouldRunAutoTdp(config));
    }
}
