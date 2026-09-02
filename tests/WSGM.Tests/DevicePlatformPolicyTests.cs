using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DevicePlatformPolicyTests
{
    [Fact]
    public void OldConfigurationDefaultsToDeviceIntegrationDisabled()
    {
        AppConfig config = ConfigStore.Normalize(new AppConfig { DeviceIntegration = null! });

        Assert.False(config.DeviceIntegration.Enabled);
        Assert.Equal(ManagedControllerTarget.SteamDeckComposite,
            config.DeviceIntegration.ControllerTarget);
    }

    [Fact]
    public void DisablingTheMasterDoesNotEraseTheControllerPreference()
    {
        AppConfig config = ConfigStore.Normalize(new AppConfig
        {
            DeviceIntegration = new DeviceIntegrationConfig
            {
                Enabled = false,
                ControllerManagementEnabled = true,
            },
        });

        Assert.False(config.DeviceIntegration.Enabled);
        Assert.True(config.DeviceIntegration.ControllerManagementEnabled);
    }

    [Fact]
    public void DesiredStateUsesTheFrozenLayerPrecedence()
    {
        DeviceCapabilityPreference preference = new()
        {
            CapabilityId = "power.primary-limit",
            GlobalDefault = Value(10),
            AcPolicy = Value(12),
            HardwareProfiles = [new DeviceNamedDesiredValue { ProfileId = "balanced", Value = Value(15) }],
            ApplicationOverrides = [new DeviceApplicationDesiredValue { ApplicationId = "game", Value = Value(18) }],
        };

        Assert.Equal(18, DeviceDesiredStateResolver.Resolve(
            preference, true, "balanced", "game").Value?.IntegerValue);
        Assert.Equal(15, DeviceDesiredStateResolver.Resolve(
            preference, true, "balanced", null).Value?.IntegerValue);
        Assert.Equal(12, DeviceDesiredStateResolver.Resolve(
            preference, true, null, null).Value?.IntegerValue);
        Assert.Equal(10, DeviceDesiredStateResolver.Resolve(
            preference, false, null, null).Value?.IntegerValue);
    }

    [Fact]
    public void DescriptorValidationRejectsDuplicateAndStaleShapes()
    {
        CapabilityDescriptor descriptor = Descriptor();
        CapabilityDescriptorSet duplicated = new()
        {
            Generation = 2,
            CycleGeneration = 3,
            Descriptors = [descriptor, descriptor],
        };

        Assert.False(DeviceCapabilityValidation.TryValidateDescriptorSet(
            duplicated, 3, 1, out _));
        Assert.False(DeviceCapabilityValidation.TryValidateDescriptorSet(
            duplicated with { Descriptors = [descriptor], Generation = 1 }, 3, 1, out _));
    }

    // A curve can be written straight through ExecuteCapabilityAsync without passing an authored
    // profile, so the declared output bounds have to be enforced on this path too — every other
    // numeric kind here is, and the refusal message promises "shape or bounds" for all of them.
    [Fact]
    public void CurveWritesAreHeldToTheDeclaredOutputBoundsLikeEveryOtherNumericKind()
    {
        CapabilityDescriptor fanCurve = Descriptor() with
        {
            CapabilityId = "fan.curve",
            Role = CapabilityRole.FanCurve,
            ValueKind = CapabilityValueKind.Curve,
            Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
            Minimum = 0,
            Maximum = 100,
        };

        Assert.True(DeviceCapabilityValidation.ValueMatches(Curve(0, 100), fanCurve, out _));
        Assert.False(DeviceCapabilityValidation.ValueMatches(Curve(0, 101), fanCurve, out _));
        Assert.False(DeviceCapabilityValidation.ValueMatches(Curve(-1, 100), fanCurve, out _));

        // An undeclared bound means the device has no limit there; inventing one would refuse a
        // curve it would have accepted.
        CapabilityDescriptor unbounded = fanCurve with { Minimum = null, Maximum = null };
        Assert.True(DeviceCapabilityValidation.ValueMatches(Curve(-500, 5000), unbounded, out _));
    }

    private static CapabilityValue Curve(int firstOutput, int secondOutput) => new()
    {
        Kind = CapabilityValueKind.Curve,
        CurveValue = [new CurvePoint(0, firstOutput), new CurvePoint(100, secondOutput)],
    };

    private static CapabilityValue Value(int value) => new()
    {
        Kind = CapabilityValueKind.Integer,
        IntegerValue = value,
    };

    private static CapabilityDescriptor Descriptor() => new()
    {
        CapabilityId = "power.primary-limit",
        Role = CapabilityRole.PowerSustainedLimit,
        ValueKind = CapabilityValueKind.Integer,
        Display = new CapabilityDisplay { Key = DisplayKey.Tdp },
        SupportsRead = true,
        SupportsWrite = true,
        Minimum = 8,
        Maximum = 30,
        Step = 1,
        Persistence = CapabilityPersistence.Volatile,
    };

}
