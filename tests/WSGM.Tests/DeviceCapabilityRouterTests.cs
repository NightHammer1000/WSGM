using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DeviceCapabilityRouterTests
{
    [Fact]
    public async Task DisconnectedRouterRejectsACommandWithAnActionableReason()
    {
        await using DeviceCapabilityRouter router = new(action => action());

        CapabilityCommandResult result = await router.ExecuteAsync(
            "power.sustained",
            instanceId: null,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 18 },
            TimeSpan.FromSeconds(1));

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.HostUnavailable, result.Reason?.Code);
        Assert.True(result.Reason!.Retryable);
    }

    [Fact]
    public async Task OnlyTheNewestPostedSnapshotCanReachTheUi()
    {
        List<Action> posted = [];
        await using DeviceCapabilityRouter router = new(posted.Add);
        var notifications = 0;
        router.Changed += _ => notifications++;

        // Any two publications in a row will do; the point is that the older posted action
        // is superseded and must not raise Changed when it finally runs on the UI thread.
        router.UpdateDesiredContext(null, onAcPower: true, hardwareProfileId: null, applicationId: null);
        router.UpdateDesiredContext(null, onAcPower: false, hardwareProfileId: null, applicationId: null);

        Assert.Equal(2, posted.Count);
        posted[0]();
        Assert.Equal(0, notifications);
        posted[1]();
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task DisposalClosesCommandAdmissionWithoutDisposingAnOwnedGate()
    {
        DeviceCapabilityRouter router = new(action => action());
        await router.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => router.ExecuteAsync(
            "power.sustained",
            instanceId: null,
            new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 18 },
            TimeSpan.FromSeconds(1)));
    }
}
