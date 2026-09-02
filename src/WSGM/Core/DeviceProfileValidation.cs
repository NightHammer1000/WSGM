using System;
using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>Why an authored profile cannot be applied to the device.</summary>
public enum DeviceProfileRejection
{
    /// <summary>The profile is usable.</summary>
    None,

    /// <summary>The device does not publish the capability this profile authors.</summary>
    CapabilityAbsent,

    /// <summary>The capability exists but does not take a curve.</summary>
    NotACurve,

    /// <summary>The profile carries no points, or more than the device accepts.</summary>
    PointCount,

    /// <summary>Inputs are not strictly ascending.</summary>
    NotAscending,

    /// <summary>A point sits outside the bounds the device declared.</summary>
    OutOfBounds,
}

/// <summary>
/// Checks an authored profile against the descriptor the device publishes right now.
/// </summary>
/// <remarks>
/// Pure, and deliberately not redundant with storage normalization; why it runs immediately before
/// apply is stated in <c>docs\device-integration.md</c> §Authored profiles.
/// </remarks>
public static class DeviceProfileValidation
{
    /// <summary>Most points a curve may carry, matching the device router's own limit.</summary>
    private const int MaximumPoints = 64;

    /// <summary>Checks a profile against the live descriptor.</summary>
    /// <param name="profile">The authored profile.</param>
    /// <param name="descriptor">The descriptor the device publishes now, or null when absent.</param>
    /// <param name="reason">What is wrong, when the result is not <see cref="DeviceProfileRejection.None"/>.</param>
    /// <returns>Why the profile cannot be applied, or <see cref="DeviceProfileRejection.None"/>.</returns>
    /// <remarks>
    /// Returns the reason rather than a bare false so the caller can log which bound was missed. A
    /// refusal without the value and the bound beside it cannot be diagnosed from a user's log.
    /// </remarks>
    public static DeviceProfileRejection Validate(
        DeviceAuthoredProfile profile,
        CapabilityDescriptor? descriptor,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(profile);
        reason = null;

        if (descriptor is null)
        {
            reason =
                $"the device does not publish capability '{profile.CapabilityId}'";
            return DeviceProfileRejection.CapabilityAbsent;
        }

        if (descriptor.ValueKind is not CapabilityValueKind.Curve)
        {
            reason =
                $"capability '{profile.CapabilityId}' takes {descriptor.ValueKind}, not a curve";
            return DeviceProfileRejection.NotACurve;
        }

        IReadOnlyList<AuthoredCurvePoint> curve = profile.Curve;
        if (curve.Count is 0 or > MaximumPoints)
        {
            reason = $"the curve has {curve.Count} points; 1 to {MaximumPoints} are accepted";
            return DeviceProfileRejection.PointCount;
        }

        for (int index = 0; index < curve.Count; index++)
        {
            AuthoredCurvePoint point = curve[index];
            if (index > 0 && point.Input <= curve[index - 1].Input)
            {
                reason =
                    $"input {point.Input} does not exceed the previous point's "
                    + $"{curve[index - 1].Input}";
                return DeviceProfileRejection.NotAscending;
            }

            // Only the bounds the device actually declared are enforced. A descriptor that leaves
            // one unset is saying it has no limit there, and inventing one would refuse a curve the
            // device would have accepted.
            if (descriptor.Minimum is { } minimum && point.Output < minimum)
            {
                reason = $"output {point.Output} is below the declared minimum {minimum}";
                return DeviceProfileRejection.OutOfBounds;
            }

            if (descriptor.Maximum is { } maximum && point.Output > maximum)
            {
                reason = $"output {point.Output} is above the declared maximum {maximum}";
                return DeviceProfileRejection.OutOfBounds;
            }
        }

        return DeviceProfileRejection.None;
    }
}
