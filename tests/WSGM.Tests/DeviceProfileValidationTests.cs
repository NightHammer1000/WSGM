using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Tests;

public sealed class DeviceProfileValidationTests
{
    private static DeviceAuthoredProfile Profile(params (int Input, int Output)[] points) => new()
    {
        ProfileId = "quiet",
        Name = "Quiet",
        CapabilityId = "thermal.fan-curve",
        Curve = [.. points.Select(point => new AuthoredCurvePoint
        {
            Input = point.Input,
            Output = point.Output,
        })],
    };

    private static CapabilityDescriptor Descriptor(
        CapabilityValueKind kind = CapabilityValueKind.Curve,
        int? minimum = 0,
        int? maximum = 100) => new()
        {
            CapabilityId = "thermal.fan-curve",
            Role = CapabilityRole.FanCurve,
            ValueKind = kind,
            Display = new CapabilityDisplay { Key = DisplayKey.FanCurve },
            Minimum = minimum,
            Maximum = maximum,
            SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile,
        };

    [Fact]
    public void AProfileMatchingTheLiveDescriptorIsAccepted()
    {
        Assert.Equal(
            DeviceProfileRejection.None,
            DeviceProfileValidation.Validate(
                Profile((0, 20), (50, 60), (100, 100)),
                Descriptor(),
                out _));
    }

    [Fact]
    public void AnAbsentCapabilityIsNamedRatherThanReportedAsAGenericFailure()
    {
        // Authoring happens with no plugin running, so the device may have been swapped or
        // downgraded since the curve was built.
        DeviceProfileRejection rejection = DeviceProfileValidation.Validate(
            Profile((0, 20)),
            null,
            out string? reason);

        Assert.Equal(DeviceProfileRejection.CapabilityAbsent, rejection);
        Assert.Contains("thermal.fan-curve", reason);
    }

    [Fact]
    public void ACapabilityThatDoesNotTakeACurveIsRefused()
    {
        Assert.Equal(
            DeviceProfileRejection.NotACurve,
            DeviceProfileValidation.Validate(
                Profile((0, 20)),
                Descriptor(CapabilityValueKind.Integer),
                out _));
    }

    [Fact]
    public void AnEmptyCurveIsRefused()
    {
        Assert.Equal(
            DeviceProfileRejection.PointCount,
            DeviceProfileValidation.Validate(Profile(), Descriptor(), out _));
    }

    [Fact]
    public void MoreThanSixtyFourPointsIsRefused()
    {
        DeviceAuthoredProfile profile = Profile();
        for (int index = 0; index < 65; index++)
        {
            profile.Curve.Add(new AuthoredCurvePoint { Input = index, Output = 50 });
        }

        Assert.Equal(
            DeviceProfileRejection.PointCount,
            DeviceProfileValidation.Validate(profile, Descriptor(), out _));
    }

    [Fact]
    public void NonAscendingInputsAreRefusedWithBothValues()
    {
        DeviceProfileRejection rejection = DeviceProfileValidation.Validate(
            Profile((0, 20), (50, 40), (50, 60)),
            Descriptor(),
            out string? reason);

        Assert.Equal(DeviceProfileRejection.NotAscending, rejection);
        // A refusal without the values it was decided from cannot be acted on remotely.
        Assert.Contains("50", reason);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(140)]
    public void AnOutputOutsideTheDeclaredBoundsIsRefused(int output)
    {
        DeviceProfileRejection rejection = DeviceProfileValidation.Validate(
            Profile((0, output)),
            Descriptor(),
            out string? reason);

        Assert.Equal(DeviceProfileRejection.OutOfBounds, rejection);
        Assert.Contains(output.ToString(), reason);
    }

    [Fact]
    public void ABoundTheDeviceDidNotDeclareIsNotInvented()
    {
        // A descriptor that leaves a bound unset is saying it has no limit there; inventing one
        // would refuse a curve the device would have accepted.
        Assert.Equal(
            DeviceProfileRejection.None,
            DeviceProfileValidation.Validate(
                Profile((0, -500), (10, 9000)),
                Descriptor(minimum: null, maximum: null),
                out _));
    }
}
