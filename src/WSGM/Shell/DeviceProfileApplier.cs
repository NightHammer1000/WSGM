using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>What happened when an authored profile was applied.</summary>
public enum DeviceProfileApplyOutcome
{
    /// <summary>The profile was sent to the device.</summary>
    Applied,

    /// <summary>No profile is selected for this capability; nothing was changed.</summary>
    NoSelection,

    /// <summary>A profile is selected but cannot be applied to the device as it is now.</summary>
    Refused,

    /// <summary>The device accepted the command but reported failure.</summary>
    Failed,
}

/// <summary>
/// Applies the authored profile in force for the running application to the device.
/// </summary>
/// <remarks>
/// Three steps, each of which can stop the chain for a different reason worth logging separately:
/// resolve which profile the selection points at, check it against the descriptor the device
/// publishes right now, and only then send it. The pre-apply check reads the live descriptor on
/// purpose; see <c>docs\device-integration.md</c> §Authored profiles.
/// </remarks>
internal static class DeviceProfileApplier
{
    /// <summary>Applies the profile in force for one capability.</summary>
    /// <param name="selections">Selections stored for the device.</param>
    /// <param name="profiles">Profiles authored for the device.</param>
    /// <param name="capabilityId">The capability to apply.</param>
    /// <param name="applicationId">The running application identity, or null for none.</param>
    /// <param name="describe">Reads the descriptor the device publishes for a capability.</param>
    /// <param name="execute">Sends a value to the device and reports the command result.</param>
    /// <param name="cancellationToken">Cancels the device write.</param>
    /// <returns>What happened, for the caller to act on and for the log.</returns>
    internal static async Task<DeviceProfileApplyOutcome> ApplyAsync(
        IReadOnlyList<DeviceProfileSelection> selections,
        IReadOnlyList<DeviceAuthoredProfile> profiles,
        string capabilityId,
        string? applicationId,
        Func<string, CapabilityDescriptor?> describe,
        Func<string, CapabilityValue, CancellationToken, Task<CapabilityCommandResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(describe);
        ArgumentNullException.ThrowIfNull(execute);
        DeviceProfileResolution resolution = DeviceProfileSelectionStore.Resolve(
            selections,
            profiles,
            capabilityId,
            applicationId);

        if (resolution.Profile is not { } profile)
        {
            // A dangling reference and no selection at all are different facts. The first is a
            // mistake the user can fix once they know; the second is the normal state.
            if (resolution.Diagnostic is { } diagnostic)
            {
                Log.Warn($"Device profile for '{capabilityId}' not applied: {diagnostic}.");
                return DeviceProfileApplyOutcome.Refused;
            }

            Log.Change(
                $"device-profile/{capabilityId}",
                $"Device profile for '{capabilityId}': no selection is in force.");
            return DeviceProfileApplyOutcome.NoSelection;
        }

        CapabilityDescriptor? descriptor = describe(capabilityId);
        DeviceProfileRejection rejection = DeviceProfileValidation.Validate(
            profile,
            descriptor,
            out string? reason);
        if (rejection is not DeviceProfileRejection.None)
        {
            Log.Warn(
                $"Device profile '{profile.ProfileId}' refused for '{capabilityId}' "
                + $"({rejection}): {reason}.");
            return DeviceProfileApplyOutcome.Refused;
        }

        CapabilityValue value = new()
        {
            Kind = CapabilityValueKind.Curve,
            CurveValue =
            [
                .. profile.Curve.Select(point => new CurvePoint(point.Input, point.Output)),
            ],
        };

        CapabilityCommandResult result = await execute(capabilityId, value, cancellationToken)
            .ConfigureAwait(false);
        // Unverified counts as applied: many EC writes have no readback, and treating the absence
        // of confirmation as failure would report every one of them as broken. A timeout does not
        // count — whether it was written is unknown, and claiming success there is the one answer
        // that misleads.
        bool applied = result.Outcome
            is CommandOutcome.AppliedVerified
            or CommandOutcome.AppliedUnverified;
        if (!applied)
        {
            Log.Warn(
                $"Device profile '{profile.ProfileId}' was accepted for '{capabilityId}' but the "
                + "device reported failure.");
            return DeviceProfileApplyOutcome.Failed;
        }

        Log.Info(
            $"Device profile '{profile.ProfileId}' applied to '{capabilityId}' "
            + (resolution.ApplicationScoped
                ? $"for application '{applicationId}'."
                : "as the global selection."));
        return DeviceProfileApplyOutcome.Applied;
    }
}
