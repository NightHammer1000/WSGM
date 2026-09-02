using System;
using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Which stored layer supplied the effective managed-controller target.</summary>
internal enum ControllerTargetSource
{
    /// <summary>The single global default.</summary>
    GlobalDefault,

    /// <summary>An override stored for the running application.</summary>
    ApplicationOverride,
}

/// <summary>
/// The complete stored controller-management selection, projected through the release gate.
/// </summary>
/// <remarks>
/// Two settings and one gate, resolved once here so no consumer re-derives them. The compile-time
/// release gate belongs in this projection rather than inside
/// <see cref="ControllerManager"/>: the manager's behaviour with management enabled has to stay
/// testable while the shipped gate is closed.
/// </remarks>
/// <param name="Enabled">Whether controller management may run at all.</param>
/// <param name="GlobalDefault">The global default target.</param>
/// <param name="Overrides">Stored per-application overrides.</param>
/// <param name="DisabledDetail">Why management is off, when it is.</param>
internal sealed record ControllerSelection(
    bool Enabled,
    ManagedControllerTarget GlobalDefault,
    IReadOnlyList<DeviceApplicationTargetOverride> Overrides,
    string DisabledDetail)
{
    /// <summary>Projects stored device-integration settings through the release gate.</summary>
    /// <param name="config">The stored device-integration configuration.</param>
    /// <returns>The selection in effect.</returns>
    internal static ControllerSelection From(DeviceIntegrationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        bool enabled = config.Enabled && config.ControllerManagementEnabled;
        string detail = enabled ? string.Empty : "Controller management is off.";
        return new(enabled, config.ControllerTarget, config.ControllerTargets, detail);
    }
}

/// <summary>The managed-controller target in effect and where it came from.</summary>
internal sealed record ResolvedControllerTarget(
    ManagedControllerTarget Target,
    ControllerTargetSource Source,
    string? ApplicationId);

/// <summary>
/// The complete controller-target policy: one global default plus per-application overrides.
/// </summary>
/// <remarks>
/// Two layers, resolved here and nowhere else. The semantic capabilities have a five-layer desired
/// state (temporary, application, profile, AC/DC, global) because hardware limits genuinely differ
/// on battery and per profile; the controller target does not — a game either wants a DualShock or
/// it does not, and running it on mains power does not change the answer. Reusing that resolver
/// would add four layers no one can set and a projection stack between the setting and the target.
/// <para>
/// Overrides are keyed by the canonical running-application identity produced by the one
/// <see cref="RunningApplicationMonitor"/>, which is also what resolves the RTSS profile. Matching on
/// the executable path instead would never fire: the monitor only resolves an executable for an
/// application it has already identified.
/// </para>
/// </remarks>
internal static class ControllerTargetSelection
{
    /// <summary>Resolves the target for the running application.</summary>
    /// <param name="globalDefault">The global default target.</param>
    /// <param name="overrides">Stored per-application overrides.</param>
    /// <param name="applicationId">Canonical identity of the running application, when one is known.</param>
    /// <returns>The effective target and the layer that supplied it.</returns>
    internal static ResolvedControllerTarget Resolve(
        ManagedControllerTarget globalDefault,
        IReadOnlyList<DeviceApplicationTargetOverride> overrides,
        string? applicationId)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            foreach (DeviceApplicationTargetOverride candidate in overrides)
            {
                if (string.Equals(candidate.ApplicationId, applicationId, StringComparison.Ordinal))
                {
                    return new(candidate.Target, ControllerTargetSource.ApplicationOverride, applicationId);
                }
            }
        }

        return new(globalDefault, ControllerTargetSource.GlobalDefault, null);
    }
}
