using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class NativeQamSemanticServicesTests
{
    [Fact]
    public void TdpProjectionUsesTheAuthoritativeDesiredObservedAndProgressState()
    {
        DeviceCapabilityView view = PrimaryLimitView("pl1");

        DeviceCoordinatorNativeQamTdpService.TdpProjection projection =
            DeviceCoordinatorNativeQamTdpService.Project([view]);

        Assert.True(projection.State.Available);
        Assert.Equal("pl1", projection.InstanceId);
        Assert.Equal(8, projection.State.MinimumWatts);
        Assert.Equal(30, projection.State.MaximumWatts);
        Assert.Equal(1, projection.State.StepWatts);
        Assert.Equal(18, projection.State.DesiredWatts);
        Assert.Equal(17, projection.State.ObservedWatts);
        Assert.Equal("applying", projection.State.Progress);
    }

    [Fact]
    public void TdpProjectionFailsClosedWhenPrimaryLimitIsAmbiguous()
    {
        DeviceCoordinatorNativeQamTdpService.TdpProjection projection =
            DeviceCoordinatorNativeQamTdpService.Project(
                [PrimaryLimitView("first"), PrimaryLimitView("second")]);

        Assert.False(projection.State.Available);
        Assert.Null(projection.State.MinimumWatts);
        Assert.Contains("ambiguous", projection.State.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeviceControlsProjectionUsesSemanticRolesAndIndependentLightingZones()
    {
        NativeQamDeviceControlsState state =
            DeviceCoordinatorNativeQamDeviceControlsService.Project(
            [
                IntegerDeviceView(
                    "vendor.charge",
                    CapabilityRole.ChargeLimit,
                    DisplayKey.ChargeLimit,
                    60,
                    100,
                    desired: 80,
                    observed: 79),
                IntegerDeviceView(
                    "vendor.brightness",
                    CapabilityRole.LightingBrightness,
                    DisplayKey.Brightness,
                    0,
                    100,
                    desired: 50,
                    observed: 45),
                ColorDeviceView("vendor.color", "right-ring", "Right ring", 0xFF8000),
                ColorDeviceView("vendor.color", "buttons", "Buttons", 0x0080FF),
            ]);

        Assert.True(state.ChargeLimit?.Available);
        Assert.Equal(60, state.ChargeLimit?.Minimum);
        Assert.Equal(80, state.ChargeLimit?.Desired);
        Assert.Equal(79, state.ChargeLimit?.Observed);
        Assert.True(state.LightingBrightness?.Available);
        Assert.Equal(2, state.LightingZones.Count);
        Assert.Contains(state.LightingZones, zone =>
            zone.Id == "right-ring"
            && zone.Label == "Right ring"
            && zone.ObservedColor == 0xFF8000);
        Assert.Contains(state.LightingZones, zone =>
            zone.Id == "buttons"
            && zone.Label == "Buttons"
            && zone.ObservedColor == 0x0080FF);
    }

    [Fact]
    public void DeviceControlsProjectionFailsClosedForAmbiguousChargeRole()
    {
        DeviceCapabilityView first = IntegerDeviceView(
            "first",
            CapabilityRole.ChargeLimit,
            DisplayKey.ChargeLimit,
            60,
            100,
            desired: 80,
            observed: 80);
        DeviceCapabilityView second = IntegerDeviceView(
            "second",
            CapabilityRole.ChargeLimit,
            DisplayKey.ChargeLimit,
            60,
            100,
            desired: 80,
            observed: 80);

        NativeQamDeviceControlsState state =
            DeviceCoordinatorNativeQamDeviceControlsService.Project([first, second]);

        Assert.False(state.ChargeLimit?.Available);
        Assert.Contains(
            "ambiguous",
            state.ChargeLimit?.StatusText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmbiguousLightingZoneIsDroppedWithoutHidingIndependentControls()
    {
        NativeQamDeviceControlsState state =
            DeviceCoordinatorNativeQamDeviceControlsService.Project(
            [
                IntegerDeviceView(
                    "vendor.charge",
                    CapabilityRole.ChargeLimit,
                    DisplayKey.ChargeLimit,
                    60,
                    100,
                    desired: 80,
                    observed: 80),
                ColorDeviceView("first", "ring", "First ring", 0xFF0000),
                ColorDeviceView("second", "ring", "Second ring", 0x0000FF),
                ColorDeviceView("buttons", "button zone", "Buttons", 0x00FF00),
            ]);

        Assert.True(state.ChargeLimit?.Available);
        NativeQamLightingZoneState zone = Assert.Single(state.LightingZones);
        Assert.Equal("button zone", zone.Id);
    }

    [Fact]
    public void UnavailableControllerServicePublishesNoSelectableTargets()
    {
        using var service = new DeviceCoordinatorNativeQamControllerTargetService(null);

        NativeQamControllerTargetState state = service.Current;

        Assert.False(state.Available);
        Assert.Empty(state.Targets);
        Assert.Empty(state.SelectedTarget);
        Assert.Empty(state.ObservedTarget);
        // The reason is surfaced verbatim rather than replaced with a generic message, so a user
        // reading native QAM learns why controller management is off.
        Assert.Equal(
            DeviceCoordinatorNativeQamControllerTargetService.UnavailableDetail,
            state.StatusText);
    }

    [Fact]
    public void PerformanceProjectionPublishesExactAdapterCapabilitiesAndReadback()
    {
        PerformanceState state = PerformanceStateFixture(
            new HashSet<int> { 0, 1 },
            PerformanceCommandState.Idle);

        NativeQamFrameLimitState frame =
            PerformanceServiceNativeQamAdapter.ProjectFrameLimit(state, enabled: true);
        Assert.True(frame.Available);
        Assert.Equal(0, frame.MinimumFps);
        Assert.Equal(1000, frame.MaximumFps);
        Assert.Equal(45, frame.DesiredFps);
        Assert.Equal(44, frame.ObservedFps);
    }

    [Fact]
    public void PerformanceFaultIsPublishedOnlyForItsCommandedControl()
    {
        PerformanceCommandState command = new(
            7,
            "native-qam",
            "native-qam:4:5:6:7",
            PerformanceControl.FrameLimit,
            60,
            PerformanceCommandPhase.TimedOut,
            "RTSS readback timed out.");
        PerformanceState state = PerformanceStateFixture(new HashSet<int> { 0, 1 }, command);

        NativeQamFrameLimitState frame =
            PerformanceServiceNativeQamAdapter.ProjectFrameLimit(state, enabled: true);
        Assert.Equal("timed-out", frame.Progress);
        Assert.Equal("RTSS readback timed out.", frame.Fault);
    }

    private static PerformanceState PerformanceStateFixture(
        IReadOnlySet<int> overlayLevels,
        PerformanceCommandState command) => new(
            new RtssProbe(
                RtssAvailability.Ready,
                "7.3.6",
                "RTSS.exe",
                3,
                new RtssCapabilities(0, 1000, overlayLevels, true, true),
                null),
            new PerformanceApplicationTarget("steam:123", 123, "game.exe", 123),
            true,
            PerformancePolicyLayer.Application,
            PerformancePolicyLayer.Global,
            new PerformanceValues(45, 1),
            new PerformanceValues(44, 0),
            PerformanceReadbackQuality.Verified,
            PerformanceReadbackQuality.Verified,
            DateTimeOffset.UtcNow,
            command);

    private static DeviceCapabilityView PrimaryLimitView(string instanceId)
    {
        CapabilityDescriptor descriptor = new()
        {
            CapabilityId = "power.primary-limit",
            InstanceId = instanceId,
            Role = CapabilityRole.PowerSustainedLimit,
            ValueKind = CapabilityValueKind.Integer,
            Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
            SupportsRead = true,
            SupportsWrite = true,
            Minimum = 8,
            Maximum = 30,
            Step = 1,
            Unit = CapabilityUnit.Watt,
            Persistence = CapabilityPersistence.Volatile,
        };
        CapabilityState state = new()
        {
            CapabilityId = descriptor.CapabilityId,
            InstanceId = descriptor.InstanceId,
            Available = true,
            ObservedValue = Integer(17),
            Quality = HardwareStateQuality.Verified,
            ObservedAt = DateTimeOffset.UtcNow,
            DescriptorGeneration = 4,
            CycleGeneration = 3,
        };
        return new DeviceCapabilityView(
            descriptor,
            new CapabilityProjection
            {
                State = state,
                DesiredValue = Integer(18),
                DesiredSource = DeviceDesiredValueSource.ApplicationOverride,
                PendingValue = Integer(19),
                Progress = CommandProgress.Pending,
            },
            null);
    }

    private static DeviceCapabilityView IntegerDeviceView(
        string capabilityId,
        CapabilityRole role,
        DisplayKey display,
        int minimum,
        int maximum,
        int desired,
        int observed)
    {
        CapabilityDescriptor descriptor = new()
        {
            CapabilityId = capabilityId,
            Role = role,
            ValueKind = CapabilityValueKind.Integer,
            Display = new CapabilityDisplay { Key = display },
            SupportsRead = true,
            SupportsWrite = true,
            Minimum = minimum,
            Maximum = maximum,
            Step = 1,
            Unit = CapabilityUnit.Percent,
            Persistence = CapabilityPersistence.DevicePersistent,
        };
        return DeviceView(descriptor, Integer(desired), Integer(observed));
    }

    private static DeviceCapabilityView ColorDeviceView(
        string capabilityId,
        string instanceId,
        string label,
        int color)
    {
        CapabilityDescriptor descriptor = new()
        {
            CapabilityId = capabilityId,
            InstanceId = instanceId,
            Role = CapabilityRole.LightingZoneColor,
            ValueKind = CapabilityValueKind.Color,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label },
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent,
        };
        CapabilityValue value = new()
        {
            Kind = CapabilityValueKind.Color,
            ColorValue = color,
        };
        return DeviceView(descriptor, value, value);
    }

    private static DeviceCapabilityView DeviceView(
        CapabilityDescriptor descriptor,
        CapabilityValue desired,
        CapabilityValue observed)
    {
        CapabilityState state = new()
        {
            CapabilityId = descriptor.CapabilityId,
            InstanceId = descriptor.InstanceId,
            Available = true,
            ObservedValue = observed,
            Quality = HardwareStateQuality.Verified,
            ObservedAt = DateTimeOffset.UtcNow,
            DescriptorGeneration = 4,
            CycleGeneration = 3,
        };
        return new DeviceCapabilityView(
            descriptor,
            new CapabilityProjection
            {
                State = state,
                DesiredValue = desired,
                DesiredSource = DeviceDesiredValueSource.GlobalDefault,
                Progress = CommandProgress.Idle,
            },
            null);
    }

    private static CapabilityValue Integer(int value) => new()
    {
        Kind = CapabilityValueKind.Integer,
        IntegerValue = value,
    };
}
