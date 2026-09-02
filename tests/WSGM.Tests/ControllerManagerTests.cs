using WSGM.Core;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class ControllerManagerTests
{
    private const string HostApplication = @"C:\Program Files\WSGM\WSGM.exe";

    [Fact]
    public async Task DisabledSelectionStartsOffAndTouchesNoHidHideOrBackendState()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;

        ControllerManagerStatus status = await manager.StartAsync(
            Disabled("Controller management is off."),
            [Device()],
            applicationId: null,
            sourceGeneration: 5,
            CancellationToken.None);

        Assert.Equal(ControllerManagementState.Off, status.State);
        Assert.Equal("Controller management is off.", status.Detail);
        Assert.Equal(UiInputSource.SdlWithSteamLease, status.UiSource);
        Assert.Null(status.Target);
        Assert.Empty(harness.Backend.Operations);
        Assert.Equal(0, harness.HidHide.MutationCount);
        Assert.Null(harness.Store.Ledger);
    }

    [Fact]
    public async Task EnabledSelectionHidesTheDeviceAndCreatesTheSelectedTarget()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;

        ControllerManagerStatus status = await manager.StartAsync(
            Enabled(ManagedControllerTarget.Xbox360),
            [Device()],
            applicationId: null,
            sourceGeneration: 5,
            CancellationToken.None);

        Assert.Equal(ControllerManagementState.Active, status.State);
        Assert.Equal(ManagedControllerTarget.Xbox360, status.Target);
        Assert.Equal(ControllerTargetSource.GlobalDefault, status.TargetSource);
        Assert.Equal(UiInputSource.ManagedCanonical, status.UiSource);
        Assert.Contains("create:1:neutral", harness.Backend.Operations);
        HidHideExactSnapshot hidHide = await harness.HidHide.ReadAsync(CancellationToken.None);
        Assert.Contains(HostApplication, hidHide.Applications);
        Assert.Contains(Device().InstancePath, hidHide.Devices);
    }

    [Fact]
    public async Task AnApplicationOverrideChoosesTheTargetAtStart()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;

        ControllerManagerStatus status = await manager.StartAsync(
            Enabled(
                ManagedControllerTarget.SteamDeckComposite,
                Override("steam:70", ManagedControllerTarget.DualShock4)),
            [Device()],
            "steam:70",
            sourceGeneration: 5,
            CancellationToken.None);

        Assert.Equal(ManagedControllerTarget.DualShock4, status.Target);
        Assert.Equal(ControllerTargetSource.ApplicationOverride, status.TargetSource);
        Assert.Equal("steam:70", status.ApplicationId);
    }

    [Fact]
    public async Task AnUnavailableBackendLeavesHidHideUntouchedAndFallsBackToSdl()
    {
        const string unavailableDetail = "The controller backend is not usable on this system.";
        Harness harness = new();
        harness.Backend.Health = new(HidBackendHealthState.Incompatible, unavailableDetail);
        await using ControllerManager manager = harness.Manager;

        ControllerManagerStatus status = await manager.StartAsync(
            Enabled(ManagedControllerTarget.Xbox360),
            [Device()],
            applicationId: null,
            sourceGeneration: 5,
            CancellationToken.None);

        Assert.Equal(ControllerManagementState.Unavailable, status.State);
        Assert.Equal(unavailableDetail, status.Detail);
        Assert.Equal(UiInputSource.SdlWithSteamLease, status.UiSource);
        Assert.Equal(0, harness.HidHide.MutationCount);
        Assert.DoesNotContain(harness.Backend.Operations, operation => operation.StartsWith("create"));
    }

    [Fact]
    public async Task UnhealthyHidHideRefusesActivationWithoutCreatingATarget()
    {
        Harness harness = new();
        harness.HidHide.Health = HidHideHealthState.Inactive;
        harness.HidHide.Active = false;
        await using ControllerManager manager = harness.Manager;

        ControllerManagerStatus status = await manager.StartAsync(
            Enabled(ManagedControllerTarget.Xbox360),
            [Device()],
            applicationId: null,
            sourceGeneration: 5,
            CancellationToken.None);

        Assert.Equal(ControllerManagementState.Unavailable, status.State);
        Assert.DoesNotContain(harness.Backend.Operations, operation => operation.StartsWith("create"));
    }

    [Fact]
    public async Task ABackendWithoutTheSelectedTargetReportsThatExactReason()
    {
        Harness harness = new(ManagedControllerTarget.Xbox360);
        await using ControllerManager manager = harness.Manager;

        ControllerManagerStatus status = await manager.StartAsync(
            Enabled(ManagedControllerTarget.DualShock4),
            [Device()],
            applicationId: null,
            sourceGeneration: 5,
            CancellationToken.None);

        Assert.Equal(ControllerManagementState.Unavailable, status.State);
        Assert.Contains("DualShock4", status.Detail);
        Assert.Equal(0, harness.HidHide.MutationCount);
    }

    [Fact]
    public async Task ARunningApplicationOverrideReplacesTheTargetExactlyOnce()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await manager.StartAsync(
            Enabled(
                ManagedControllerTarget.SteamDeckComposite,
                Override("steam:70", ManagedControllerTarget.DualShock4)),
            [Device()],
            applicationId: null,
            sourceGeneration: 5,
            CancellationToken.None);

        ControllerManagerStatus status = await manager.ApplyRunningApplicationAsync(
            Running("steam:70"),
            CancellationToken.None);

        Assert.Equal(ManagedControllerTarget.DualShock4, status.Target);
        Assert.Equal(ControllerTargetSource.ApplicationOverride, status.TargetSource);
        // The old target is removed before the replacement is created, so the two are never
        // enumerated at the same time.
        List<string> operations = [.. harness.Backend.Operations];
        Assert.InRange(
            operations.IndexOf("remove:1"),
            0,
            operations.IndexOf("create:2:neutral") - 1);
    }

    [Fact]
    public async Task AnApplicationWithNoOverrideKeepsTheGlobalTargetWithoutReplacement()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await manager.StartAsync(
            Enabled(
                ManagedControllerTarget.SteamDeckComposite,
                Override("steam:70", ManagedControllerTarget.DualShock4)),
            [Device()],
            applicationId: null,
            sourceGeneration: 5,
            CancellationToken.None);

        ControllerManagerStatus status = await manager.ApplyRunningApplicationAsync(
            Running("steam:220"),
            CancellationToken.None);

        Assert.Equal(ManagedControllerTarget.SteamDeckComposite, status.Target);
        Assert.DoesNotContain("remove:1", harness.Backend.Operations);
    }

    [Fact]
    public async Task ADisabledSelectionIsNotReconciledIntoAnUnorderedTargetRemoval()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await manager.StartAsync(
            Enabled(ManagedControllerTarget.Xbox360),
            [Device()],
            applicationId: null,
            sourceGeneration: 5,
            CancellationToken.None);

        ControllerManagerStatus status = await manager.ApplySelectionAsync(
            Disabled("Controller management is off."),
            applicationId: null,
            CancellationToken.None);

        Assert.Equal(ControllerManagementState.Active, status.State);
        Assert.DoesNotContain("remove:1", harness.Backend.Operations);
    }

    [Fact]
    public async Task SamplesReachTheVirtualTargetWhileNoSurfaceHoldsCapture()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);

        Assert.True(await manager.RouteAsync(Sample(1, CanonicalButtons.A), CancellationToken.None));
        Assert.Contains("publish:1:live", harness.Backend.Operations);
    }

    [Fact]
    public async Task CapturedSamplesReachTheUiAndLeaveTheTargetNeutral()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);
        List<CanonicalControllerSample> ui = [];
        manager.UiSampleReceived += ui.Add;

        await manager.RouteAsync(Sample(1, CanonicalButtons.A), CancellationToken.None);
        await manager.ClaimUiAsync("overlay", CancellationToken.None);
        bool routed = await manager.RouteAsync(
            Sample(2, CanonicalButtons.Y),
            CancellationToken.None);

        Assert.False(routed);
        Assert.Equal(CanonicalButtons.Y, Assert.Single(ui).Buttons);
        List<string> operations = [.. harness.Backend.Operations];
        Assert.Equal(operations.Count - 1, operations.IndexOf("neutralize:1"));
    }

    [Fact]
    public async Task TheChordThatOpenedASurfaceIsSuppressedUntilItIsReleased()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);
        List<CanonicalControllerSample> ui = [];
        manager.UiSampleReceived += ui.Add;

        // The chord is held as the surface opens.
        await manager.RouteAsync(Sample(1, CanonicalButtons.Guide), CancellationToken.None);
        await manager.ClaimUiAsync("overlay", CancellationToken.None);
        await manager.RouteAsync(Sample(2, CanonicalButtons.Guide), CancellationToken.None);

        Assert.Equal(CanonicalButtons.None, Assert.Single(ui).Buttons);
    }

    [Fact]
    public async Task ForwardingResumesOnlyAfterEveryHeldControlIsReleased()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.RouteAsync(Sample(1, CanonicalButtons.Guide), CancellationToken.None);
        await manager.ClaimUiAsync("overlay", CancellationToken.None);
        manager.ReleaseUi("overlay");

        Assert.False(await manager.RouteAsync(
            Sample(2, CanonicalButtons.Guide),
            CancellationToken.None));
        Assert.True(await manager.RouteAsync(
            Sample(3, CanonicalButtons.None),
            CancellationToken.None));
    }

    [Fact]
    public async Task AControlPressedInsideTheSurfaceCannotLeakIntoTheGameWhenItCloses()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.ClaimUiAsync("overlay", CancellationToken.None);
        Assert.False(await manager.RouteAsync(
            Sample(1, CanonicalButtons.A),
            CancellationToken.None));
        manager.ReleaseUi("overlay");

        Assert.False(await manager.RouteAsync(
            Sample(2, CanonicalButtons.A),
            CancellationToken.None));
        Assert.True(await manager.RouteAsync(
            Sample(3, CanonicalButtons.None),
            CancellationToken.None));
    }

    [Fact]
    public async Task NestedSurfacesKeepCaptureUntilTheLastOneCloses()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.ClaimUiAsync("overlay", CancellationToken.None);
        await manager.ClaimUiAsync("taskbar", CancellationToken.None);
        manager.ReleaseUi("taskbar");

        Assert.False(await manager.RouteAsync(Sample(1, CanonicalButtons.None), CancellationToken.None));
        manager.ReleaseUi("overlay");
        Assert.True(await manager.RouteAsync(Sample(2, CanonicalButtons.None), CancellationToken.None));
    }

    [Fact]
    public async Task LifecycleBlockNeutralizesOnceAndRoutesLaterSamplesOnlyToTheUi()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);
        List<CanonicalControllerSample> ui = [];
        manager.UiSampleReceived += ui.Add;

        await manager.RouteAsync(Sample(1, CanonicalButtons.A), CancellationToken.None);
        await manager.BlockForwardingAsync("suspending", CancellationToken.None);
        bool routed = await manager.RouteAsync(
            Sample(2, CanonicalButtons.None),
            CancellationToken.None);

        Assert.False(routed);
        Assert.Single(ui);
        Assert.Single(harness.Backend.Operations, operation => operation == "neutralize:1");
    }

    [Fact]
    public async Task AVerifiedMakeSafeRemovesTheTargetAndOnlyWsgmOwnedHidHideEntries()
    {
        Harness harness = new(
            existingApplications: [@"C:\External\Manager.exe"],
            existingDevices: ["HID\\EXTERNAL"]);
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);

        ControllerHandoff response = await manager.MakeSafeAsync(
            HandoffScope.ControllerOnly,
            _ => Task.FromResult(PluginRelease(ControllerHandoffStep.TopologyVerified)),
            CancellationToken.None);

        Assert.Equal(ControllerHandoffStep.WsgmStateRemoved, response.Step);
        Assert.Equal(ControllerHandoffResult.ReleasedVerified, response.Result);
        Assert.Contains("remove:1", harness.Backend.Operations);
        HidHideExactSnapshot hidHide = await harness.HidHide.ReadAsync(CancellationToken.None);
        Assert.Equal([@"C:\External\Manager.exe"], hidHide.Applications);
        Assert.Equal(["HID\\EXTERNAL"], hidHide.Devices);
        Assert.Null(harness.Store.Ledger);
        Assert.Equal(ControllerManagementState.Idle, manager.State);
    }

    [Fact]
    public async Task MakeSafeRemovesTheTargetBeforeTheHidHideEntriesItOwns()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);
        int mutationsBeforeRelease = 0;
        bool targetRemovedBeforeRelease = true;
        IReadOnlyList<string> hiddenAtRelease = [];

        await manager.MakeSafeAsync(
            HandoffScope.FullDeactivation,
            async token =>
            {
                mutationsBeforeRelease = harness.HidHide.MutationCount;
                targetRemovedBeforeRelease = harness.Backend.Operations.Contains("remove:1");
                hiddenAtRelease = (await harness.HidHide.ReadAsync(token)).Devices;
                return PluginRelease(ControllerHandoffStep.TopologyVerified);
            },
            CancellationToken.None);

        // While the plugin is still letting go, the physical device stays hidden and WSGM's target
        // still exists. Un-hiding earlier would expose a device the plugin is still holding.
        Assert.Equal(2, mutationsBeforeRelease);
        Assert.False(targetRemovedBeforeRelease);
        Assert.Contains(Device().InstancePath, hiddenAtRelease);
        Assert.Equal(4, harness.HidHide.MutationCount);
        Assert.Contains("remove:1", harness.Backend.Operations);
    }

    [Fact]
    public async Task AFailedPluginReleaseStillRemovesWsgmStateAndReportsUnverified()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);

        ControllerHandoff response = await manager.MakeSafeAsync(
            HandoffScope.FullDeactivation,
            _ => Task.FromException<ControllerHandoff>(
                new TimeoutException("The plugin never answered.")),
            CancellationToken.None);

        Assert.Equal(ControllerHandoffStep.WsgmStateRemoved, response.Step);
        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, response.Result);
        Assert.Contains("remove:1", harness.Backend.Operations);
        Assert.Null(harness.Store.Ledger);
        Assert.Equal(ControllerManagementState.Off, manager.State);
    }

    [Fact]
    public async Task AnUnverifiedPluginTopologyIsNotReportedAsACleanRelease()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);

        ControllerHandoff response = await manager.MakeSafeAsync(
            HandoffScope.ControllerOnly,
            _ => Task.FromResult(PluginRelease(ControllerHandoffStep.TopologyUnverified)),
            CancellationToken.None);

        Assert.Equal(ControllerHandoffResult.ReleasedUnverified, response.Result);
        Assert.Null(harness.Store.Ledger);
    }

    [Fact]
    public async Task MakeSafeCarriesThePluginReleasedDevicesBackToTheCaller()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);

        ControllerHandoff response = await manager.MakeSafeAsync(
            HandoffScope.ControllerOnly,
            _ => Task.FromResult(PluginRelease(
                ControllerHandoffStep.TopologyVerified,
                [Device()])),
            CancellationToken.None);

        Assert.Equal(Device().InstancePath, Assert.Single(response.ReleasedDevices).InstancePath);
    }

    [Fact]
    public async Task SamplesAreRefusedAfterMakeSafeUntilManagementStartsAgain()
    {
        Harness harness = new();
        await using ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);
        await manager.MakeSafeAsync(
            HandoffScope.ControllerOnly,
            _ => Task.FromResult(PluginRelease(ControllerHandoffStep.TopologyVerified)),
            CancellationToken.None);

        Assert.False(await manager.RouteAsync(
            Sample(9, CanonicalButtons.A),
            CancellationToken.None));
    }

    [Fact]
    public async Task DisposeRemovesTheTargetAndThenWsgmOwnedHidHideEntries()
    {
        Harness harness = new();
        ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.DisposeAsync();

        Assert.Contains("remove:1", harness.Backend.Operations);
        HidHideExactSnapshot hidHide = await harness.HidHide.ReadAsync(CancellationToken.None);
        Assert.Empty(hidHide.Applications);
        Assert.Empty(hidHide.Devices);
        Assert.Null(harness.Store.Ledger);
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        Harness harness = new();
        ControllerManager manager = harness.Manager;
        await StartActiveAsync(manager);

        await manager.DisposeAsync();
        await manager.DisposeAsync();

        Assert.Single(harness.Backend.Operations, operation => operation == "remove:1");
    }

    private static async Task StartActiveAsync(ControllerManager manager)
    {
        ControllerManagerStatus status = await manager.StartAsync(
            Enabled(ManagedControllerTarget.Xbox360),
            [Device()],
            applicationId: null,
            sourceGeneration: 5,
            CancellationToken.None);
        Assert.Equal(ControllerManagementState.Active, status.State);
    }

    private static ControllerSelection Enabled(
        ManagedControllerTarget target,
        params DeviceApplicationTargetOverride[] overrides) =>
        new(Enabled: true, target, overrides, "Controller management is off.");

    private static ControllerSelection Disabled(string detail) =>
        new(Enabled: false, ManagedControllerTarget.SteamDeckComposite, [], detail);

    private static DeviceApplicationTargetOverride Override(
        string applicationId,
        ManagedControllerTarget target) =>
        new() { ApplicationId = applicationId, Target = target };

    private static PhysicalDeviceIdentity Device() => new()
    {
        InstancePath = @"HID\VID_0DB0&PID_1901\7&CLAW",
        RequiresHiding = true,
    };

    private static RunningApplicationTargetSnapshot Running(string applicationId) => new(
        1,
        1,
        RunningApplicationTargetState.Active,
        applicationId,
        70,
        @"C:\Games\game.exe",
        "game",
        DateTimeOffset.UtcNow,
        null);

    private static CanonicalControllerSample Sample(long sequence, CanonicalButtons buttons) => new()
    {
        Sequence = sequence,
        CycleGeneration = 5,
        Timestamp = DateTimeOffset.UtcNow,
        Buttons = buttons,
    };

    private static ControllerHandoff PluginRelease(
        ControllerHandoffStep step,
        IReadOnlyList<PhysicalDeviceIdentity>? released = null) => new()
        {
            Step = step,
            Result = step is ControllerHandoffStep.TopologyVerified
                ? ControllerHandoffResult.ReleasedVerified
                : ControllerHandoffResult.ReleasedUnverified,
            ReleasedDevices = released ?? [],
        };

    private sealed class Harness
    {
        internal Harness(
            ManagedControllerTarget? onlyTarget = null,
            IEnumerable<string>? existingApplications = null,
            IEnumerable<string>? existingDevices = null)
        {
            Backend = onlyTarget is { } kind
                ? new DeterministicFakeHidBackend(kind)
                : new DeterministicFakeHidBackend();
            HidHide = new(existingApplications, existingDevices);
            Store = new();
            Manager = new(
                Backend,
                new DeterministicFakeHapticSink(5),
                new HidHideOwnedDeltaManager(HidHide, Store),
                HostApplication);
        }

        internal DeterministicFakeHidBackend Backend { get; }

        internal DeterministicFakeHidHideAdapter HidHide { get; }

        internal InMemoryHidHideOwnershipStore Store { get; }

        internal ControllerManager Manager { get; }
    }
}
