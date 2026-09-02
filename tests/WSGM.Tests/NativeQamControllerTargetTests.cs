using System.Text.Json;
using WSGM.Core;
using WSGM.Input;
using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>
/// The controller target Steam's native quick-access menu shows is WSGM's own setting.
/// </summary>
/// <remarks>
/// These pin the projection, which is the whole of the menu's truthfulness: which targets it offers,
/// which one it says is selected, which one it says is actually up, and whether a running game has
/// to be restarted before a change reaches it.
/// </remarks>
public sealed class NativeQamControllerTargetTests
{
    [Fact]
    public void ManagementSwitchedOffOffersNothingAndSaysWhy()
    {
        NativeQamControllerTargetState state = Project(
            enabled: false,
            Status(ControllerManagementState.Off, null, "Controller management is off."));

        Assert.False(state.Available);
        Assert.Empty(state.Targets);
        Assert.Empty(state.SelectedTarget);
        // Surfaced verbatim rather than replaced with a generic message, so a user reading native
        // QAM learns why the control is not there.
        Assert.Equal("Controller management is off.", state.StatusText);
    }

    [Fact]
    public void EveryTargetTheBackendCanBuildIsOfferedOnceManagementRuns()
    {
        NativeQamControllerTargetState state = Project(
            enabled: true,
            Status(ControllerManagementState.Idle, ManagedControllerTarget.Xbox360),
            supportedTargets:
            [
                ManagedControllerTarget.SteamDeckComposite,
                ManagedControllerTarget.Xbox360,
                ManagedControllerTarget.DualShock4,
            ]);

        Assert.True(state.Available);
        Assert.Collection(
            state.Targets,
            target => Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), target.Id),
            target => Assert.Equal(nameof(ManagedControllerTarget.Xbox360), target.Id),
            target => Assert.Equal(nameof(ManagedControllerTarget.DualShock4), target.Id));
        Assert.All(state.Targets, target => Assert.True(target.Available));
    }

    [Fact]
    public void ATargetTheBackendCannotBuildIsNotOffered()
    {
        // Offering one is worse than offering fewer: the selection persists, target creation is
        // refused, and controller management reports itself unavailable until the user finds the
        // setting again. The production backend supports only the Deck composite today.
        NativeQamControllerTargetState state = Project(
            enabled: true,
            Status(ControllerManagementState.Idle, ManagedControllerTarget.SteamDeckComposite),
            supportedTargets: [ManagedControllerTarget.SteamDeckComposite]);

        Assert.True(state.Available);
        Assert.Collection(
            state.Targets,
            target => Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), target.Id));
    }

    [Fact]
    public void ATargetChosenButNotYetUpIsNotReportedAsObserved()
    {
        // Idle means the selection is stored and nothing is present for it. Echoing the selection
        // back as observed would make a target that never came up look like it had.
        NativeQamControllerTargetState state = Project(
            enabled: true,
            Status(ControllerManagementState.Idle, ManagedControllerTarget.DualShock4));

        Assert.Equal(nameof(ManagedControllerTarget.DualShock4), state.SelectedTarget);
        Assert.Empty(state.ObservedTarget);
    }

    [Fact]
    public void AnActiveTargetIsReportedAsBothSelectedAndObserved()
    {
        NativeQamControllerTargetState state = Project(
            enabled: true,
            Status(ControllerManagementState.Active, ManagedControllerTarget.SteamDeckComposite));

        Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), state.SelectedTarget);
        Assert.Equal(nameof(ManagedControllerTarget.SteamDeckComposite), state.ObservedTarget);
        Assert.Equal("completed", state.Progress);
    }

    [Fact]
    public void AFaultedManagerIsUnavailableRatherThanQuietlySelectable()
    {
        NativeQamControllerTargetState state = Project(
            enabled: true,
            Status(
                ControllerManagementState.Faulted,
                ManagedControllerTarget.Xbox360,
                "The virtual controller could not be attached."));

        Assert.False(state.Available);
        Assert.Equal("failed", state.Progress);
        Assert.Equal("The virtual controller could not be attached.", state.StatusText);
    }

    [Fact]
    public void ARunningGameIsToldItNeedsARestart()
    {
        // A game holds the target it launched with, so a change reaches it only next launch. Saying
        // so is the difference between a control that looks broken and one the user understands.
        NativeQamControllerTargetState state = Project(
            enabled: true,
            Status(
                ControllerManagementState.Active,
                ManagedControllerTarget.Xbox360,
                applicationId: "steam:70"));

        Assert.True(state.ApplicationRestartRequired);
    }

    [Fact]
    public void NoRunningGameNeedsNoRestart()
    {
        NativeQamControllerTargetState state = Project(
            enabled: true,
            Status(ControllerManagementState.Active, ManagedControllerTarget.Xbox360));

        Assert.False(state.ApplicationRestartRequired);
    }

    [Fact]
    public void AMissingDevicePackageIsExplainedRatherThanLeftBlank()
    {
        // Controller management runs without a plugin, but with nothing capturing the physical
        // controller the result is a target that never moves. That is worth saying.
        NativeQamControllerTargetState state = Project(
            enabled: true,
            Status(ControllerManagementState.Idle, ManagedControllerTarget.Xbox360, detail: string.Empty),
            packageInstalled: false);

        Assert.True(state.Available);
        Assert.Contains("No device package", state.StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Xbox360", true)]
    [InlineData("SteamDeckComposite", true)]
    [InlineData("DualShock4", true)]
    [InlineData("xbox360", false)]
    [InlineData("", false)]
    [InlineData("3", false)]
    [InlineData("NotATarget", false)]
    public void OnlyTheExactNamesTheMenuWasGivenParse(string candidate, bool expected)
    {
        // Case-sensitive on purpose: these names come from the projection, so anything else is a
        // caller defect rather than user input to be forgiving about. "3" is rejected even though
        // Enum.TryParse accepts numeric text, which would otherwise let an out-of-range value in.
        Assert.Equal(
            expected,
            DeviceCoordinatorNativeQamControllerTargetService.TryParseTarget(candidate, out _));
    }

    [Fact]
    public void EveryProjectedTargetSurvivesTheHostPayloadBoundary()
    {
        foreach (ManagedControllerTarget target in Enum.GetValues<ManagedControllerTarget>())
        {
            using JsonDocument payload = JsonDocument.Parse($$"""{"target":"{{target}}"}""");

            Assert.True(NativeQamPayload.TryReadTarget(payload.RootElement, out string parsed));
            Assert.Equal(target.ToString(), parsed);
        }
    }

    [Fact]
    public void TheUnavailableServiceStaysTheProjectionForASessionWithNoDevicePlatform()
    {
        using DeviceCoordinatorNativeQamControllerTargetService service = new(null);

        Assert.False(service.Current.Available);
        Assert.Empty(service.Current.Targets);
    }

    private static NativeQamControllerTargetState Project(
        bool enabled,
        ControllerManagerStatus status,
        bool packageInstalled = true,
        IReadOnlyList<ManagedControllerTarget>? supportedTargets = null) =>
        DeviceCoordinatorNativeQamControllerTargetService.Project(
            enabled,
            status,
            packageInstalled,
            supportedTargets ?? Enum.GetValues<ManagedControllerTarget>());

    private static ControllerManagerStatus Status(
        ControllerManagementState state,
        ManagedControllerTarget? target,
        string detail = "",
        string? applicationId = null) =>
        new(
            state,
            target,
            ControllerTargetSource.GlobalDefault,
            applicationId,
            UiInputSource.SdlWithSteamLease,
            detail);
}
