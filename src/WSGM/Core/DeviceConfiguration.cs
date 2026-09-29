using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Core;

/// <summary>Shared shape check for plugin-supplied identifiers.</summary>
public static class DeviceIdentifier
{
    /// <summary>Whether a plugin-supplied identifier is non-empty, bounded, and uses only ASCII
    /// letters, digits, '.', '-' and '_'.</summary>
    /// <param name="value">The identifier to check.</param>
    /// <param name="maximumLength">Longest accepted identifier.</param>
    /// <returns><see langword="true"/> when the identifier is safe to store, log, and use as a key.</returns>
    public static bool IsValid(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');
}

/// <summary>Capability ids the authored-profile chain targets.</summary>
public static class DeviceAuthoredProfileCapabilities
{
    /// <summary>The fan-curve capability authored curve profiles apply to.</summary>
    public const string FanCurve = "fan.curve";

    /// <summary>The lighting zone-colour capability authored colour profiles apply to.</summary>
    public const string Lighting = "lighting.zone-color";
}

/// <summary>Persisted settings for the optional production device platform.</summary>
public sealed class DeviceIntegrationConfig
{
    /// <summary>Master ownership switch. Older configurations default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>Remembered child preference for physical-controller management.</summary>
    /// <remarks>
    /// Turning the master switch off makes this ineffective but does not erase it. A later master
    /// re-enable restores the user's previous choice after all safety gates are checked again.
    /// </remarks>
    public bool ControllerManagementEnabled { get; set; }

    /// <summary>Global managed-controller target.</summary>
    public ManagedControllerTarget ControllerTarget { get; set; } =
        ManagedControllerTarget.SteamDeckComposite;

    /// <summary>Per-application managed-controller target overrides.</summary>
    /// <remarks>
    /// Stored beside the global default rather than under a per-device profile. There is one
    /// installed plugin and therefore one device, so nesting the controller target under a device
    /// identity would add a layer nothing can vary and a projection between the setting and the
    /// virtual target.
    /// </remarks>
    public List<DeviceApplicationTargetOverride> ControllerTargets { get; set; } = [];

    /// <summary>Whether AutoTDP controls the primary power limit from frame delivery.</summary>
    /// <remarks>
    /// Requires Device Integration, because the limit it moves is a plugin capability. Off leaves
    /// the power limit entirely to manual control and profiles.
    /// </remarks>
    public bool AutoTdpEnabled { get; set; }

    /// <summary>How the active handheld glyph profile is selected.</summary>
    public DeviceGlyphSelection GlyphSelection { get; set; } = DeviceGlyphSelection.Automatic;

    /// <summary>Manual reviewed glyph profile when <see cref="GlyphSelection"/> is manual.</summary>
    public string? ManualGlyphProfileId { get; set; }

    /// <summary>Desired semantic profiles keyed by stable local device identity.</summary>
    public List<DeviceDesiredProfile> Profiles { get; set; } = [];

    /// <summary>Stored values for the settings a plugin declares for itself.</summary>
    /// <remarks>
    /// Keyed by device definition and plugin, so a value authored for one plugin never reaches
    /// another that happens to reuse the setting identifier. Values are revalidated against the
    /// current manifest on load, because a plugin update can narrow a range or drop an option.
    /// </remarks>
    public List<PluginSettingsScope> PluginSettings { get; set; } = [];
}

/// <summary>The stored settings of one plugin on one device definition.</summary>
public sealed class PluginSettingsScope
{
    /// <summary>Device definition the values were authored against.</summary>
    public string DeviceDefinitionId { get; set; } = string.Empty;

    /// <summary>Plugin that declared the settings.</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>The values, one per declared setting the user has changed.</summary>
    public List<PluginSettingValue> Values { get; set; } = [];

    /// <summary>
    /// The manifest the plugin published when it last ran, or null when none has been seen.
    /// </summary>
    /// <remarks>
    /// Cached because Settings has to draw the page without activating device hardware. The
    /// declaration is published by plugin code rather than stored in <c>plugin.wsgm.json</c>, so
    /// there is nothing equivalent to read from the installed package at rest.
    /// <para>
    /// It is a cache and never the authority. The shell replaces it whenever a running plugin
    /// publishes, and stored values are still reconciled against the live declaration when one
    /// exists — this only decides what can be <em>drawn</em> when no plugin is running, never what
    /// is legal to send one.
    /// </para>
    /// <para>
    /// Stale by construction: a plugin uninstalled or downgraded between sessions leaves a manifest
    /// describing settings that no longer exist. That is why it is dropped when it fails its own
    /// validation on load, and why the page it produces is editable but the values still go through
    /// reconciliation before they reach a plugin.
    /// </para>
    /// </remarks>
    public PluginSettingsManifest? Declaration { get; set; }

    /// <summary>Named fan curves and lighting profiles the user authored for this device.</summary>
    /// <remarks>
    /// Device-keyed and stored beside the plugin's settings because they are authored the same way
    /// and become meaningless against a different device. Authoring lives in Settings; choosing
    /// which one is in force is the overlay's job (D22b), so nothing here records a selection.
    /// </remarks>
    public List<DeviceAuthoredProfile> Profiles { get; set; } = [];

    /// <summary>Which authored profile is in force, globally and per application.</summary>
    /// <remarks>
    /// Selections reference a profile by id rather than copying its curve, so editing a profile
    /// changes every application already using it. Copying would silently strand every override on
    /// the shape the profile had when it was chosen.
    /// </remarks>
    public List<DeviceProfileSelection> ProfileSelections { get; set; } = [];
}

/// <summary>Which authored profile is in force for one capability.</summary>
/// <remarks>
/// The same two layers, and the same precedence, as
/// <see cref="DeviceCapabilityPreference"/>: an application override outranks the global choice.
/// This is deliberately not a second per-application mechanism — it stores a profile reference
/// where that one stores a value, and both resolve against the same running-application identity.
/// </remarks>
public sealed class DeviceProfileSelection
{
    /// <summary>The capability the selection applies to.</summary>
    public string CapabilityId { get; set; } = string.Empty;

    /// <summary>Profile in force when no application override matches, or null for none.</summary>
    public string? GlobalProfileId { get; set; }

    /// <summary>Per-application selections, at the higher precedence.</summary>
    public List<DeviceApplicationProfileSelection> ApplicationOverrides { get; set; } = [];
}

/// <summary>One per-application profile choice.</summary>
public sealed class DeviceApplicationProfileSelection
{
    /// <summary>The canonical running-application identity this applies to.</summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>The authored profile chosen for it.</summary>
    public string ProfileId { get; set; } = string.Empty;
}

/// <summary>One named profile the user authored for a device capability.</summary>
/// <remarks>
/// A profile is not a setting. A setting is one value WSGM keeps and hands the plugin; a profile is
/// a named shape the user builds and then applies, globally or per application, from the overlay.
/// That is why curves are refused as settings and live here instead — one home each.
/// </remarks>
public sealed class DeviceAuthoredProfile
{
    /// <summary>Longest accepted <see cref="Name"/>.</summary>
    public const int MaxNameLength = 48;

    /// <summary>Stable identifier the overlay selects by.</summary>
    /// <remarks>
    /// Separate from <see cref="Name"/> so renaming a profile does not detach every application
    /// override that pointed at it.
    /// </remarks>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>What the user called it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The capability this profile authors, for example a fan curve or a lighting colour.</summary>
    public string CapabilityId { get; set; } = string.Empty;

    /// <summary>Curve points, ascending by input. Empty for a profile that is not a curve.</summary>
    public List<AuthoredCurvePoint> Curve { get; set; } = [];

    /// <summary>Packed colour for a lighting profile, or null when this is not one.</summary>
    public int? Color { get; set; }
}

/// <summary>One authored curve point.</summary>
/// <remarks>
/// A mutable configuration class rather than the SDK's <c>CurvePoint</c> struct, matching every
/// other stored shape in this file: configuration is deserialized, normalized in place, and
/// re-serialized, while the SDK value is an immutable runtime contract.
/// </remarks>
public sealed class AuthoredCurvePoint
{
    /// <summary>The input, for a fan curve a temperature.</summary>
    public int Input { get; set; }

    /// <summary>The output, for a fan curve a duty percentage.</summary>
    public int Output { get; set; }
}

/// <summary>One stored plugin setting value.</summary>
/// <remarks>
/// Mirrors the value shapes the SDK allows a setting to take. There is no curve field: a curve is
/// authored as a named profile with its own storage, so a curve-shaped setting is refused at
/// declaration rather than given a second home here.
/// </remarks>
public sealed class PluginSettingValue
{
    /// <summary>Which declared setting this is the value of.</summary>
    public string SettingId { get; set; } = string.Empty;

    /// <summary>Value of a boolean setting.</summary>
    public bool? Boolean { get; set; }

    /// <summary>Value of an integer setting.</summary>
    public int? Integer { get; set; }

    /// <summary>Selected option of a choice setting.</summary>
    public string? Choice { get; set; }

    /// <summary>Packed 24-bit RGB of a colour setting.</summary>
    public int? Color { get; set; }

    /// <summary>Value of a text setting.</summary>
    public string? Text { get; set; }
}

/// <summary>Controller identity exposed to applications while management is active.</summary>
public enum ManagedControllerTarget
{
    /// <summary>Rich Steam Deck-compatible composite target.</summary>
    SteamDeckComposite,

    /// <summary>Widely compatible Xbox 360 target.</summary>
    Xbox360,

    /// <summary>DualShock 4 target with native motion where supported.</summary>
    DualShock4,
}

/// <summary>How WSGM chooses a device glyph profile.</summary>
public enum DeviceGlyphSelection
{
    /// <summary>Use only the exact verified profile advertised by the device definition.</summary>
    Automatic,

    /// <summary>Leave Steam's own glyphs untouched.</summary>
    NativeSteam,

    /// <summary>Use an explicitly selected reviewed catalog profile.</summary>
    ManualReviewedProfile,
}

/// <summary>All persistent desired state for one local device identity.</summary>
public sealed class DeviceDesiredProfile
{
    /// <summary>Stable local device identity key.</summary>
    public string DeviceIdentityKey { get; set; } = string.Empty;

    /// <summary>Currently selected named hardware profile, when any.</summary>
    public string? SelectedHardwareProfileId { get; set; }

    /// <summary>Desired values by semantic capability and optional instance.</summary>
    public List<DeviceCapabilityPreference> Capabilities { get; set; } = [];

    /// <summary>Allowlisted assignments for logical OEM controls.</summary>
    public List<DeviceOemAssignment> OemAssignments { get; set; } = [];
}

/// <summary>Persistent desired-state layers for one semantic capability instance.</summary>
public sealed class DeviceCapabilityPreference
{
    /// <summary>Semantic capability identifier.</summary>
    public string CapabilityId { get; set; } = string.Empty;

    /// <summary>Optional descriptor instance identifier.</summary>
    public string? InstanceId { get; set; }

    /// <summary>Lowest-precedence global default.</summary>
    public CapabilityValue? GlobalDefault { get; set; }

    /// <summary>Desired value while on AC power.</summary>
    public CapabilityValue? AcPolicy { get; set; }

    /// <summary>Desired value while on battery.</summary>
    public CapabilityValue? DcPolicy { get; set; }

    /// <summary>Values supplied by named hardware profiles.</summary>
    public List<DeviceNamedDesiredValue> HardwareProfiles { get; set; } = [];

    /// <summary>Per-application values at the highest persistent precedence.</summary>
    public List<DeviceApplicationDesiredValue> ApplicationOverrides { get; set; } = [];
}

/// <summary>One named-hardware-profile value.</summary>
public sealed class DeviceNamedDesiredValue
{
    /// <summary>Stable profile identifier.</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>Desired semantic value.</summary>
    public CapabilityValue? Value { get; set; }
}

/// <summary>One per-application desired semantic value.</summary>
public sealed class DeviceApplicationDesiredValue
{
    /// <summary>Stable application identity owned by WSGM.</summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>Desired semantic value.</summary>
    public CapabilityValue? Value { get; set; }
}

/// <summary>Closed WSGM-owned actions that an OEM control may invoke.</summary>
/// <remarks>
/// There is deliberately no executable, script, shell-command, text-macro, or arbitrary-key action.
/// Keeping the vocabulary here, rather than in the plugin SDK, prevents hardware packages from
/// defining WSGM application policy or turning OEM assignment into a general remapper.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OemAction>))]
public enum OemAction
{
    /// <summary>Do nothing.</summary>
    Disabled,

    /// <summary>Open or close the WSGM overlay.</summary>
    ToggleWsgmOverlay,

    /// <summary>Open or close Steam's native Quick Access Menu.</summary>
    ToggleSteamQuickAccess,

    /// <summary>Open the overlay directly on the Device page.</summary>
    ShowWsgmDevicePage,

    /// <summary>Open the quick access sheet on its Open apps strip, or close it when it is up —
    /// what the taskbar button did before the strip moved into the sheet.</summary>
    ToggleWsgmTaskbar,

    /// <summary>Switch between Desktop and Game Mode.</summary>
    ToggleDesktopGameMode,

    /// <summary>Show or hide the on-screen keyboard.</summary>
    ToggleOnScreenKeyboard,

    /// <summary>Move to the next performance profile.</summary>
    CyclePerformanceProfile,

    /// <summary>Move to the next performance-overlay level.</summary>
    CyclePerformanceOverlayLevel,

    /// <summary>Forward as the current target's first rear control. Rear placement only.</summary>
    VirtualTargetRearButton1,

    /// <summary>Forward as the current target's second rear control. Rear placement only.</summary>
    VirtualTargetRearButton2,
}

/// <summary>One allowlisted OEM-control assignment.</summary>
public sealed class DeviceOemAssignment
{
    /// <summary>Stable logical control identifier from the plugin descriptor.</summary>
    public string ControlId { get; set; } = string.Empty;

    /// <summary>Closed WSGM-owned action.</summary>
    public OemAction Action { get; set; } = OemAction.Disabled;
}

/// <summary>One per-application controller target override.</summary>
public sealed class DeviceApplicationTargetOverride
{
    /// <summary>Stable application identity owned by WSGM.</summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>Managed target selected for that application.</summary>
    public ManagedControllerTarget Target { get; set; }
}

/// <summary>The desired-state layer that supplied an effective value.</summary>
/// <remarks>
/// Ordered lowest to highest precedence. Captured hardware state is deliberately absent: it is
/// restoration-only and never competes with what the user asked for, because adopting an observed
/// value as a desired one would silently turn whatever the device happened to be doing into policy.
/// </remarks>
public enum DeviceDesiredValueSource
{
    /// <summary>No desired value exists; the device keeps whatever it has.</summary>
    None,

    /// <summary>Global per-device default.</summary>
    GlobalDefault,

    /// <summary>AC/DC policy for the current power source.</summary>
    PowerPolicy,

    /// <summary>Selected named hardware profile.</summary>
    HardwareProfile,

    /// <summary>Matched application override.</summary>
    ApplicationOverride,
}

/// <summary>Result of resolving the frozen desired-state precedence.</summary>
public sealed record ResolvedDeviceDesiredValue(
    CapabilityValue? Value,
    DeviceDesiredValueSource Source);

/// <summary>Pure desired-state precedence policy.</summary>
public static class DeviceDesiredStateResolver
{
    /// <summary>Resolves the application, profile, power, and global layers.</summary>
    /// <param name="preference">Persistent capability layers.</param>
    /// <param name="onAcPower">Current power state.</param>
    /// <param name="hardwareProfileId">Selected named profile.</param>
    /// <param name="applicationId">Matched application identity.</param>
    /// <returns>The highest-precedence available value and its source.</returns>
    public static ResolvedDeviceDesiredValue Resolve(
        DeviceCapabilityPreference preference,
        bool onAcPower,
        string? hardwareProfileId,
        string? applicationId)
    {
        ArgumentNullException.ThrowIfNull(preference);
        CapabilityValue? application = preference.ApplicationOverrides.FirstOrDefault(
            value => string.Equals(value.ApplicationId, applicationId, StringComparison.Ordinal))?.Value;
        if (application is not null)
        {
            return new(application, DeviceDesiredValueSource.ApplicationOverride);
        }

        CapabilityValue? profile = preference.HardwareProfiles.FirstOrDefault(
            value => string.Equals(value.ProfileId, hardwareProfileId, StringComparison.Ordinal))?.Value;
        if (profile is not null)
        {
            return new(profile, DeviceDesiredValueSource.HardwareProfile);
        }

        CapabilityValue? power = onAcPower ? preference.AcPolicy : preference.DcPolicy;
        if (power is not null)
        {
            return new(power, DeviceDesiredValueSource.PowerPolicy);
        }

        return preference.GlobalDefault is null
            ? new(null, DeviceDesiredValueSource.None)
            : new(preference.GlobalDefault, DeviceDesiredValueSource.GlobalDefault);
    }
}
