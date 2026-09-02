using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Which scope a profile choice applies to.</summary>
public enum DeviceProfileScope
{
    /// <summary>Everything without an override of its own.</summary>
    Global,

    /// <summary>Only the application that is running now.</summary>
    Application,
}

/// <summary>Which authored profile is in force, and where the choice came from.</summary>
/// <param name="Profile">The profile to apply, or null when none is in force.</param>
/// <param name="ApplicationScoped">
/// Whether an application override supplied it rather than the global choice.
/// </param>
/// <param name="Diagnostic">
/// Why nothing is in force, or why a selection was ignored. Null when a profile resolved cleanly.
/// </param>
public readonly record struct DeviceProfileResolution(
    DeviceAuthoredProfile? Profile,
    bool ApplicationScoped,
    string? Diagnostic);

/// <summary>
/// Reads, writes, and resolves which authored profile is in force.
/// </summary>
/// <remarks>
/// This writes only which profile is chosen and never a profile's contents; the precedence and
/// dangling-selection rules are stated in <c>docs\device-integration.md</c> §Authored profiles.
/// Every write goes through the caller's own configuration mutation, so this holds no state and the
/// cross-process config lock stays owned by <c>ConfigStore</c> rather than being taken twice.
/// </remarks>
public static class DeviceProfileSelectionStore
{
    /// <summary>Reads the profile id currently chosen for a capability.</summary>
    /// <param name="scope">The device scope holding the selections.</param>
    /// <param name="capabilityId">The capability being read.</param>
    /// <param name="applicationId">The running application, or null for none.</param>
    /// <param name="applicationScoped">
    /// Whether the answer came from an application override rather than the global choice.
    /// </param>
    /// <returns>The chosen profile id, or null when nothing is chosen.</returns>
    public static string? ReadSelection(
        PluginSettingsScope scope,
        string capabilityId,
        string? applicationId,
        out bool applicationScoped)
    {
        ArgumentNullException.ThrowIfNull(scope);
        applicationScoped = false;
        DeviceProfileSelection? selection = Find(scope.ProfileSelections, capabilityId);
        return selection is null
            ? null
            : ReadSelection(selection, applicationId, out applicationScoped);
    }

    /// <summary>Resolves which authored profile applies to the running application.</summary>
    /// <param name="selections">Selections stored for the device.</param>
    /// <param name="profiles">Profiles authored for the device.</param>
    /// <param name="capabilityId">The capability being resolved.</param>
    /// <param name="applicationId">
    /// The canonical running-application identity, or null when none is running.
    /// </param>
    /// <returns>The profile to apply and where the choice came from.</returns>
    /// <remarks>
    /// Selections reference a profile by id, so this is also where a reference to a profile the user
    /// has since deleted is caught. It resolves to nothing and says so, because applying a stale
    /// profile would be worse than applying none and silently applying none is what makes it
    /// undiagnosable.
    /// </remarks>
    public static DeviceProfileResolution Resolve(
        IReadOnlyList<DeviceProfileSelection> selections,
        IReadOnlyList<DeviceAuthoredProfile> profiles,
        string capabilityId,
        string? applicationId)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(profiles);

        DeviceProfileSelection? selection = Find(selections, capabilityId);
        if (selection is null)
        {
            return new DeviceProfileResolution(null, false, null);
        }

        string? profileId = ReadSelection(selection, applicationId, out bool applicationScoped);
        if (applicationScoped)
        {
            return Find(profiles, profileId!, applicationScoped: true, applicationId);
        }

        return profileId is { Length: > 0 }
            ? Find(profiles, profileId, applicationScoped: false, applicationId: null)
            : new DeviceProfileResolution(null, false, null);
    }

    /// <summary>Chooses a profile, or clears the choice.</summary>
    /// <param name="scope">The device scope to write into.</param>
    /// <param name="capabilityId">The capability being set.</param>
    /// <param name="profileId">The profile to choose, or null to clear.</param>
    /// <param name="target">Whether this is the global choice or an application override.</param>
    /// <param name="applicationId">
    /// The application the override belongs to. Required for
    /// <see cref="DeviceProfileScope.Application"/>.
    /// </param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// Clearing an application override falls back to the global choice, which is the difference
    /// between "this game uses the default" and "this game uses nothing" — the first is what a user
    /// clearing an override means, and there is no way to express the second on purpose.
    /// </remarks>
    public static bool SetSelection(
        PluginSettingsScope scope,
        string capabilityId,
        string? profileId,
        DeviceProfileScope target,
        string? applicationId = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(capabilityId))
        {
            return false;
        }

        if (target is DeviceProfileScope.Application && string.IsNullOrWhiteSpace(applicationId))
        {
            // Refused rather than quietly written as the global choice: silently widening a
            // per-game change to every game is the worst possible reading of the user's intent.
            Log.Warn(
                $"Device profile selection for '{capabilityId}' was refused: an application-scoped "
                + "choice needs a running application.");
            return false;
        }

        DeviceProfileSelection? selection = Find(scope.ProfileSelections, capabilityId);
        if (selection is null)
        {
            if (profileId is null)
            {
                return false;
            }

            selection = new DeviceProfileSelection { CapabilityId = capabilityId };
            scope.ProfileSelections.Add(selection);
        }

        if (target is DeviceProfileScope.Global)
        {
            if (string.Equals(selection.GlobalProfileId, profileId, StringComparison.Ordinal))
            {
                return false;
            }

            selection.GlobalProfileId = profileId;
            return true;
        }

        List<DeviceApplicationProfileSelection> overrides = selection.ApplicationOverrides;
        DeviceApplicationProfileSelection? existing = overrides.FirstOrDefault(entry =>
            string.Equals(entry.ApplicationId, applicationId, StringComparison.Ordinal));

        if (profileId is null)
        {
            return existing is not null && overrides.Remove(existing);
        }

        if (existing is not null)
        {
            if (string.Equals(existing.ProfileId, profileId, StringComparison.Ordinal))
            {
                return false;
            }

            existing.ProfileId = profileId;
            return true;
        }

        overrides.Add(new DeviceApplicationProfileSelection
        {
            ApplicationId = applicationId!,
            ProfileId = profileId,
        });
        return true;
    }

    private static string? ReadSelection(
        DeviceProfileSelection selection,
        string? applicationId,
        out bool applicationScoped)
    {
        applicationScoped = false;
        if (applicationId is { Length: > 0 })
        {
            DeviceApplicationProfileSelection? overridden = selection.ApplicationOverrides
                .FirstOrDefault(entry => string.Equals(
                    entry.ApplicationId,
                    applicationId,
                    StringComparison.Ordinal));
            if (overridden is not null)
            {
                applicationScoped = true;
                return overridden.ProfileId;
            }
        }

        return selection.GlobalProfileId;
    }

    private static DeviceProfileSelection? Find(
        IReadOnlyList<DeviceProfileSelection> selections,
        string capabilityId) =>
        selections.FirstOrDefault(candidate =>
            string.Equals(candidate.CapabilityId, capabilityId, StringComparison.Ordinal));

    private static DeviceProfileResolution Find(
        IReadOnlyList<DeviceAuthoredProfile> profiles,
        string profileId,
        bool applicationScoped,
        string? applicationId)
    {
        DeviceAuthoredProfile? profile = profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.ProfileId, profileId, StringComparison.Ordinal));
        if (profile is not null)
        {
            return new DeviceProfileResolution(profile, applicationScoped, null);
        }

        // Named, and never silently downgraded to the global choice. A per-application selection
        // pointing at a deleted profile means the user's intent for that application is gone, and
        // quietly running the global profile instead hides it.
        return new DeviceProfileResolution(
            null,
            applicationScoped,
            applicationScoped
                ? $"application '{applicationId}' selects profile '{profileId}', which no longer exists"
                : $"the global selection names profile '{profileId}', which no longer exists");
    }
}
