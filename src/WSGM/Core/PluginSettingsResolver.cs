using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Core;

/// <summary>Why an effective plugin setting value ended up as it did.</summary>
public enum PluginSettingOrigin
{
    /// <summary>The user has never changed it, so the plugin's declared default applies.</summary>
    Default,

    /// <summary>A stored value was restored unchanged.</summary>
    Stored,

    /// <summary>A stored value no longer fits the declaration and the default replaced it.</summary>
    Rejected,
}

/// <summary>One effective plugin setting value and how it was arrived at.</summary>
/// <param name="SettingId">The setting the value belongs to.</param>
/// <param name="Value">The value the plugin will actually be given.</param>
/// <param name="Origin">Whether it was defaulted, restored, or replaced after rejection.</param>
/// <param name="Reason">
/// Why a stored value was rejected, naming the value and the declared bound. Null unless
/// <paramref name="Origin"/> is <see cref="PluginSettingOrigin.Rejected"/>.
/// </param>
public readonly record struct EffectivePluginSetting(
    string SettingId,
    CapabilityValue Value,
    PluginSettingOrigin Origin,
    string? Reason
);

/// <summary>The result of reconciling stored values against a plugin's current declaration.</summary>
/// <param name="Values">One entry per declared setting, in declaration order.</param>
/// <param name="Orphans">
/// Stored settings the manifest no longer declares, named so they can be logged and dropped rather
/// than silently carried forever.
/// </param>
public readonly record struct PluginSettingsResolution(
    IReadOnlyList<EffectivePluginSetting> Values,
    IReadOnlyList<string> Orphans
);

/// <summary>
/// Reconciles stored plugin setting values with the manifest the plugin declares now.
/// </summary>
/// <remarks>
/// A plugin update can narrow a range, drop a choice, shorten a text bound, or remove a setting
/// outright, and the values on disk were written against whatever it declared before. Restoring one
/// blindly would hand the plugin a value it no longer considers legal.
/// <para>
/// This is a pure decision so it can be tested without a device: it reports what it rejected and
/// why, and the caller logs it. A rejection that is not logged with the stored value and the
/// declared bound beside it cannot be diagnosed from a user's log.
/// </para>
/// </remarks>
public static class PluginSettingsResolver
{
    /// <summary>
    /// Produces the effective value of every declared setting.
    /// </summary>
    /// <param name="manifest">What the plugin declares now.</param>
    /// <param name="stored">The values previously written for this plugin, possibly empty.</param>
    /// <returns>The effective values, and any stored settings the manifest no longer declares.</returns>
    public static PluginSettingsResolution Resolve(
        PluginSettingsManifest manifest,
        IReadOnlyList<PluginSettingValue>? stored
    )
    {
        Dictionary<string, PluginSettingValue> storedById = new(System.StringComparer.Ordinal);
        foreach (PluginSettingValue value in stored ?? [])
        {
            if (!string.IsNullOrWhiteSpace(value.SettingId))
            {
                storedById[value.SettingId] = value;
            }
        }

        List<EffectivePluginSetting> effective = new(manifest.Settings.Count);
        foreach (PluginSettingDescriptor descriptor in manifest.Settings)
        {
            if (!storedById.TryGetValue(descriptor.SettingId, out PluginSettingValue? entry))
            {
                effective.Add(new EffectivePluginSetting(
                    descriptor.SettingId,
                    descriptor.Default,
                    PluginSettingOrigin.Default,
                    null));
                continue;
            }

            CapabilityValue candidate = ToCapabilityValue(entry, descriptor.ValueKind);
            if (descriptor.TryValidateValue(candidate, out string? error))
            {
                effective.Add(new EffectivePluginSetting(
                    descriptor.SettingId,
                    candidate,
                    PluginSettingOrigin.Stored,
                    null));
                continue;
            }

            effective.Add(new EffectivePluginSetting(
                descriptor.SettingId,
                descriptor.Default,
                PluginSettingOrigin.Rejected,
                error));
        }

        HashSet<string> declared = new(
            manifest.Settings.Select(setting => setting.SettingId),
            System.StringComparer.Ordinal);
        List<string> orphans = [.. storedById.Keys.Where(id => !declared.Contains(id))];

        return new PluginSettingsResolution(effective, orphans);
    }

    /// <summary>
    /// Reads a stored entry into the value shape the declaration expects.
    /// </summary>
    /// <param name="entry">The stored entry.</param>
    /// <param name="kind">The kind the setting currently declares.</param>
    /// <returns>The value, which the caller still validates.</returns>
    /// <remarks>
    /// Deliberately reads only the field matching the declared kind. A setting whose kind changed
    /// between plugin versions therefore produces an empty value and is rejected with a reason,
    /// rather than silently reinterpreting an integer as a colour.
    /// </remarks>
    internal static CapabilityValue ToCapabilityValue(PluginSettingValue entry, CapabilityValueKind kind) =>
        kind switch
        {
            CapabilityValueKind.Boolean => new CapabilityValue
            {
                Kind = kind,
                BooleanValue = entry.Boolean,
            },
            CapabilityValueKind.Integer => new CapabilityValue
            {
                Kind = kind,
                IntegerValue = entry.Integer,
            },
            CapabilityValueKind.Choice => new CapabilityValue
            {
                Kind = kind,
                ChoiceValue = entry.Choice,
            },
            CapabilityValueKind.Color => new CapabilityValue { Kind = kind, ColorValue = entry.Color },
            CapabilityValueKind.Text => new CapabilityValue { Kind = kind, TextValue = entry.Text },
            _ => new CapabilityValue { Kind = kind },
        };
}
