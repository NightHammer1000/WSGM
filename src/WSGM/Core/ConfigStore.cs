using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Core;

/// <summary>Loads and atomically saves WSGM's shared per-user configuration file.</summary>
public static class ConfigStore
{
    /// <summary>Absolute path of the persisted configuration file.</summary>
    public static string ConfigPath => Path.Combine(Log.Directory, "config.json");

    // Shell, settings window, and elevated one-shots all load-modify-save the same
    // file; the named mutex serializes the individual Load/Save calls so they never
    // interleave. It CANNOT merge: saving an AppConfig loaded long ago overwrites
    // every field another process persisted in between, so long-lived holders must
    // re-load and re-apply only their own fields before saving (see
    // SettingsViewModel.Save). Read-only startup may degrade after the short timeout; every
    // write and read-modify-write transaction fails closed instead of risking a lost update.
    private const string MutexName = @"Local\WSGM.Config";
    private const int MutexTimeoutMs = 2000;

    /// <summary>Loads the current configuration, returning safe defaults when the
    /// file is absent, malformed, or inaccessible.</summary>
    /// <returns>A normalized configuration that callers can use without null checks.</returns>
    public static AppConfig Load()
    {
        using var guard = ConfigMutex.Acquire(requireExclusive: false);
        try
        {
            return LoadCurrentDocument();
        }
        catch (Exception ex)
        {
            // The file holds the previous-shell/UAC/lock-screen registry snapshots;
            // set the corrupt file aside so they stay manually recoverable instead
            // of being clobbered when the next Save writes blank defaults.
            Log.Error("Failed to load config, using defaults", ex);
            PreserveCorruptFile();
        }
        return new AppConfig();
    }

    /// <summary>Loads configuration for a read-modify-write transaction. Unlike
    /// <see cref="Load"/>, an existing unreadable file is never converted to defaults:
    /// the exception aborts the mutation so registry recovery snapshots cannot be erased.</summary>
    /// <returns>The normalized configuration, or defaults only when no file exists.</returns>
    internal static AppConfig LoadForMutation()
    {
        using var guard = ConfigMutex.Acquire(requireExclusive: true);
        return LoadCurrentDocument();
    }

    private static AppConfig LoadCurrentDocument()
    {
        if (!File.Exists(ConfigPath))
        {
            return new AppConfig();
        }
        var json = File.ReadAllText(ConfigPath);
        var config = DeserializeConfig(json);
        return Normalize(config);
    }

    /// <summary>Deserializes one configuration document, repairing unknown enum names first.</summary>
    /// <param name="json">The raw file contents.</param>
    /// <returns>The configuration, before normalization.</returns>
    /// <remarks>
    /// Internal because the repair pass is the part worth testing: a value it fails to repair makes
    /// the retry throw, and <see cref="Load"/> then sets the entire file aside.
    /// </remarks>
    internal static AppConfig DeserializeConfig(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)
                ?? throw new JsonException("Configuration JSON contained null instead of an object.");
        }
        catch (JsonException)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                ?? throw new JsonException("Configuration root was not an object.");
            RepairEnum(root, "GlyphStyle", Defaults.GlyphStyle);
            RepairEnum(root, "DisplayManagement", Defaults.DisplayManagement);
            if (root["Splash"] is JsonObject splash)
            {
                RepairEnum(splash, "SpinnerStyle", Defaults.Splash.SpinnerStyle);
                RepairEnum(splash, "SweepEdge", Defaults.Splash.SweepEdge);
                RepairPlacement(splash["TextPlacement"] as JsonObject);
                RepairPlacement(splash["SpinnerPlacement"] as JsonObject);
                RepairPlacement(splash["LogoPlacement"] as JsonObject);
            }
            if (root["CustomTabs"] is JsonArray tabs)
            {
                foreach (var tab in tabs.OfType<JsonObject>())
                {
                    RepairFilterJson(tab["FilterTree"] as JsonObject);
                }
            }
            if (root["LaunchWrappers"] is JsonArray wrappers)
            {
                foreach (var wrapper in wrappers.OfType<JsonObject>())
                {
                    RepairEnum(wrapper, "Mode", default(LaunchWrapperMode));
                    RepairEnum(wrapper, "Kind", default(LaunchConfigurationKind));
                }
            }
            if (root["Performance"] is JsonObject performance)
            {
                RepairEnum(performance, "FrameLimitStrategy", Defaults.Performance.FrameLimitStrategy);
            }
            // Every enum in this file is written by name (UseStringEnumConverter), so an unknown
            // name throws here before Normalize can apply its Enum.IsDefined fallbacks. Repairing
            // only the older fields meant one mistyped or unrecognised device value — from a hand
            // edit, or from a configuration written by a newer build — made the retry throw as
            // well, and Load then moved the whole otherwise-valid file aside, taking the registry
            // recovery snapshots and every unrelated setting with it.
            if (root["DeviceIntegration"] is JsonObject device)
            {
                RepairEnum(device, "ControllerTarget", Defaults.DeviceIntegration.ControllerTarget);
                RepairEnum(device, "GlyphSelection", Defaults.DeviceIntegration.GlyphSelection);
                if (device["ControllerTargets"] is JsonArray targets)
                {
                    foreach (var target in targets.OfType<JsonObject>())
                    {
                        RepairEnum(target, "Target", Defaults.DeviceIntegration.ControllerTarget);
                    }
                }
                if (device["Profiles"] is JsonArray profiles)
                {
                    foreach (var profile in profiles.OfType<JsonObject>())
                    {
                        RepairDeviceProfileJson(profile);
                    }
                }
                if (device["PluginSettings"] is JsonArray settings)
                {
                    foreach (var scope in settings.OfType<JsonObject>())
                    {
                        RepairPluginSettingsDeclarationJson(scope["Declaration"] as JsonObject);
                    }
                }
            }
            return JsonSerializer.Deserialize(root.ToJsonString(), ConfigJsonContext.Default.AppConfig)
                ?? throw new JsonException("Configuration JSON contained null instead of an object.");
        }
    }

    /// <summary>
    /// Repairs enum names in one cached plugin settings manifest so a declaration written by a
    /// newer plugin can be discarded independently instead of quarantining the entire config.
    /// </summary>
    /// <param name="declaration">The cached declaration, or null when the scope has none.</param>
    private static void RepairPluginSettingsDeclarationJson(JsonObject? declaration)
    {
        if (declaration is null)
        {
            return;
        }

        foreach (var section in (declaration["Sections"] as JsonArray ?? []).OfType<JsonObject>())
        {
            RepairEnum(section, "Key", SettingSectionKey.General);
        }

        foreach (var setting in (declaration["Settings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            RepairEnum(setting, "ValueKind", CapabilityValueKind.None);
            RepairEnum(setting, "Unit", CapabilityUnit.None);
            if (setting["Display"] is JsonObject display)
            {
                RepairEnum(display, "Key", DisplayKey.Custom);
            }
            if (setting["Default"] is JsonObject defaultValue)
            {
                RepairEnum(defaultValue, "Kind", CapabilityValueKind.None);
            }
        }
    }

    /// <summary>Repairs the enum-bearing members of one persisted device profile.</summary>
    /// <param name="profile">The stored profile object, straight from the file.</param>
    private static void RepairDeviceProfileJson(JsonObject profile)
    {
        if (profile["OemAssignments"] is JsonArray assignments)
        {
            foreach (var assignment in assignments.OfType<JsonObject>())
            {
                RepairEnum(assignment, "Action", OemAction.Disabled);
            }
        }

        if (profile["Capabilities"] is not JsonArray capabilities)
        {
            return;
        }

        foreach (var capability in capabilities.OfType<JsonObject>())
        {
            RepairCapabilityValueJson(capability["GlobalDefault"] as JsonObject);
            RepairCapabilityValueJson(capability["AcPolicy"] as JsonObject);
            RepairCapabilityValueJson(capability["DcPolicy"] as JsonObject);
            foreach (var named in (capability["HardwareProfiles"] as JsonArray ?? [])
                .OfType<JsonObject>())
            {
                RepairCapabilityValueJson(named["Value"] as JsonObject);
            }
            foreach (var application in (capability["ApplicationOverrides"] as JsonArray ?? [])
                .OfType<JsonObject>())
            {
                RepairCapabilityValueJson(application["Value"] as JsonObject);
            }
        }
    }

    /// <summary>Repairs the value-kind discriminator of one stored capability value.</summary>
    /// <param name="value">The stored value object, or null when the layer carries none.</param>
    /// <remarks>
    /// Repaired to <see cref="CapabilityValueKind.None"/> rather than guessing from the populated
    /// field: a value whose kind cannot be read is not a value, and the desired-state resolver
    /// treats it as absent instead of writing something arbitrary to hardware.
    /// </remarks>
    private static void RepairCapabilityValueJson(JsonObject? value)
    {
        if (value is not null)
        {
            RepairEnum(value, "Kind", CapabilityValueKind.None);
        }
    }

    private static void RepairPlacement(JsonObject? placement)
    {
        if (placement is null) { return; }
        RepairEnum(placement, "Mode", PlacementDefaults.Mode);
        RepairEnum(placement, "Anchor", PlacementDefaults.Anchor);
    }

    private static void RepairFilterJson(JsonObject? filter)
    {
        if (filter is null) { return; }
        RepairEnum(filter, "Kind", FilterDefaults.Kind);
        RepairEnum(filter, "Mode", FilterDefaults.Mode);
        RepairEnum(filter, "Condition", FilterDefaults.Condition);
        RepairEnum(filter, "Platform", FilterDefaults.Platform);
        RepairEnum(filter, "ScoreType", FilterDefaults.ScoreType);
        RepairEnum(filter, "Units", FilterDefaults.Units);
        RepairEnum(filter, "CardScope", FilterDefaults.CardScope);
        if (filter["Children"] is JsonArray children)
        {
            foreach (var child in children.OfType<JsonObject>())
            {
                RepairFilterJson(child);
            }
        }
    }

    private static void RepairEnum<T>(JsonObject value, string property, T fallback)
        where T : struct, Enum
    {
        if (value[property] is JsonValue node
            && node.TryGetValue<string>(out var text)
            && !Enum.TryParse<T>(text, ignoreCase: true, out _))
        {
            value[property] = fallback.ToString();
        }
    }

    // The single source for every persisted default is the config classes' own
    // property initializers; these read-only templates hand them to the JSON
    // repair pass (unknown enum NAME) and to Normalize (unknown enum NUMBER, null
    // string) alike, so the two passes cannot drift apart. Never mutate them and
    // never hand them to a caller.
    private static readonly AppConfig Defaults = new();
    private static readonly SplashElementPlacement PlacementDefaults = new();
    // Spelled out rather than left to the property initializers: a repaired filter falls back to
    // the neutral filter a user would recognise ("installed", ANDed, inserted cards), which is not
    // the same as enum member zero. Both repair passes read this one instance, so they cannot
    // disagree about what an unreadable value becomes.
    private static readonly FilterNode FilterDefaults = new()
    {
        Kind = FilterKind.Installed,
        Mode = FilterMode.And,
        Condition = ThresholdCondition.Above,
        Platform = PlatformKind.Steam,
        ScoreType = ReviewScoreType.SteamPercent,
        Units = TimeUnit.Hours,
        CardScope = SdCardScope.Inserted,
    };

    /// <summary>An unknown enum NUMBER ("SpinnerStyle": 999 deserializes into the
    /// enum unchecked) falls back to the field's default rather than to whatever
    /// neighbouring member a clamp would land on.</summary>
    private static T Definite<T>(T value, T fallback) where T : struct, Enum =>
        Enum.IsDefined(value) ? value : fallback;

    /// <summary>An explicit JSON null ("StartupApps": null) deserializes over the
    /// property initializer; replace nulls with fresh defaults so a hand-edited
    /// config can never NRE the shell later (which would kill it before the panic
    /// handler runs). New nested object/list members belong in this list too.</summary>
    internal static AppConfig Normalize(AppConfig config)
    {
        config.DisplayManagement = Definite(config.DisplayManagement, Defaults.DisplayManagement);
        config.StartupApps ??= [];
        config.DeviceIntegration ??= new DeviceIntegrationConfig();
        NormalizeDeviceIntegration(config.DeviceIntegration);
        config.Performance ??= new PerformanceConfig();
        NormalizePerformance(config.Performance);
        config.Cef ??= new CefConfig();
        config.Hotkey ??= new HotkeyConfig();
        config.GamepadChord ??= new GamepadChordConfig();
        config.Gestures ??= new GestureConfig();
        config.QuickAccessPins ??= [];
        config.QuickAccessPins = config.QuickAccessPins
            .Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        config.SavedDisplayScaleEntries ??= [];
        config.DisplayProfiles ??= [];
        config.PreviousConsoleLockSchemeValues ??= [];
        config.CardLibraries ??= [];
        config.ForgottenInsertedCardIds ??= [];
        config.ForgottenInsertedCardIds = config.ForgottenInsertedCardIds
            .Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        config.CustomTabs ??= [];
        config.LibraryTabOrder ??= [];
        config.HiddenNativeTabs ??= [];
        config.KnownNativeTabs ??= [];
        config.SteamGridDbApiKey ??= "";
        config.SgdbLinks ??= [];
        config.LaunchWrappers ??= [];
        // A null ELEMENT ("StartupApps": [null]) survives the list-level ??= above and
        // would NRE in SelfElevation before the crash-loop breaker has recorded the
        // start — the shell would then die at every sign-in with nothing disarming it.
        // RemoveAll repairs in place: Normalize must hand back the caller's own list
        // instances (RegressionCoverageTests pins that), and rebuilding them would
        // allocate on every config load just to drop elements that are almost never there.
        config.StartupApps.RemoveAll(static app => app is null);
        foreach (var app in config.StartupApps)
        {
            app.Path ??= "";
            app.Args ??= "";
        }
        config.LaunchWrappers.RemoveAll(static w => w is null);
        foreach (var wrapper in config.LaunchWrappers)
        {
            wrapper.OriginalTarget ??= "";
            wrapper.OriginalLaunchOptions ??= "";
            wrapper.OriginalStartDir ??= "";
            wrapper.Name ??= "";
            wrapper.CustomActionPath ??= "";
            wrapper.CustomArguments ??= "";
        }
        config.CardLibraries = config.CardLibraries.Where(static card => card is not null).ToList();
        foreach (var card in config.CardLibraries)
        {
            card.ContentId ??= "";
            card.Name ??= "";
            card.AppIds ??= [];
        }
        config.CustomTabs = config.CustomTabs.Where(static tab => tab is not null).ToList();
        foreach (var tab in config.CustomTabs)
        {
            tab.Id = string.IsNullOrWhiteSpace(tab.Id) ? Guid.NewGuid().ToString("N") : tab.Id;
            tab.Name ??= "";
            tab.FilterTree ??= new FilterNode { Kind = FilterKind.Merge };
            NormalizeFilter(tab.FilterTree);
        }
        config.LibraryTabOrder = config.LibraryTabOrder
            .Where(static key => key is not null).ToList();
        config.HiddenNativeTabs = config.HiddenNativeTabs
            .Where(static id => id is not null).ToList();
        config.KnownNativeTabs = config.KnownNativeTabs
            .Where(static tab => tab is not null).ToList();
        foreach (var native in config.KnownNativeTabs)
        {
            native.Id ??= "";
            native.Title ??= "";
        }
        config.SavedDisplayScaleEntries.RemoveAll(static entry => entry is null);
        foreach (var entry in config.SavedDisplayScaleEntries)
        {
            entry.DeviceName ??= "";
        }
        config.DisplayProfiles.RemoveAll(static profile => profile is null);
        foreach (var profile in config.DisplayProfiles)
        {
            profile.MonitorId ??= "";
            profile.DeviceName ??= "";
            profile.DisplayName ??= "";
            profile.Desktop ??= new DisplayModeValues();
            profile.Game ??= new DisplayModeValues();
            NormalizeDisplayMode(profile.Desktop);
            NormalizeDisplayMode(profile.Game);
        }
        config.PreviousConsoleLockSchemeValues.RemoveAll(static scheme => scheme is null);
        foreach (var scheme in config.PreviousConsoleLockSchemeValues)
        {
            scheme.SchemeGuid ??= "";
        }
        config.SgdbLinks.RemoveAll(static link => link is null);
        foreach (var link in config.SgdbLinks)
        {
            link.Name ??= "";
        }
        config.AccentColor ??= Defaults.AccentColor;
        config.AccentColor = Truncate(config.AccentColor, MaxColorLength, "Accent color");
        config.Splash ??= new SplashConfig();
        NormalizeSplash(config.Splash);
        return config;
    }

    /// <summary>
    /// Brings device-integration configuration into a shape the rest of WSGM can rely on.
    /// </summary>
    /// <param name="device">The section to normalize in place.</param>
    /// <remarks>
    /// Internal rather than private so its rules can be tested directly. It touches only the object
    /// handed to it and reads no file, which is what keeps a test off the developer's real
    /// configuration.
    /// </remarks>
    internal static void NormalizeDeviceIntegration(DeviceIntegrationConfig device)
    {
        device.ControllerTarget = Definite(
            device.ControllerTarget, Defaults.DeviceIntegration.ControllerTarget);
        device.GlyphSelection = Definite(
            device.GlyphSelection, Defaults.DeviceIntegration.GlyphSelection);
        device.ManualGlyphProfileId = string.IsNullOrWhiteSpace(device.ManualGlyphProfileId)
            ? null
            : device.ManualGlyphProfileId.Trim();
        device.ControllerTargets ??= [];
        device.ControllerTargets.RemoveAll(static target => target is null
            || string.IsNullOrWhiteSpace(target.ApplicationId)
            || !Enum.IsDefined(target.Target));
        HashSet<string> controllerApplications = new(StringComparer.Ordinal);
        device.ControllerTargets.RemoveAll(
            target => !controllerApplications.Add(target.ApplicationId.Trim()));
        foreach (DeviceApplicationTargetOverride target in device.ControllerTargets)
        {
            target.ApplicationId = target.ApplicationId.Trim();
        }

        // A scope with no device, no plugin, or no values keys nothing and can never be matched, so
        // it would sit in the file forever growing it. Values are only shape-checked here; whether
        // one still satisfies its declared bounds is decided against the live manifest on load,
        // because a plugin update can narrow a range after the value was stored.
        device.PluginSettings ??= [];
        device.PluginSettings.RemoveAll(static scope => scope is null
            || string.IsNullOrWhiteSpace(scope.DeviceDefinitionId)
            || string.IsNullOrWhiteSpace(scope.PluginId));
        foreach (PluginSettingsScope scope in device.PluginSettings)
        {
            scope.DeviceDefinitionId = scope.DeviceDefinitionId.Trim();
            scope.PluginId = scope.PluginId.Trim();

            // Settings renders the cached declaration without activating plugin code. Drop malformed
            // declarations so every rendered control has the bounds required by the SDK contract.
            if (scope.Declaration is { } declaration && !declaration.TryValidate(out string? reason))
            {
                Log.Warn(
                    $"Plugin settings: cached declaration for {scope.PluginId} on "
                    + $"{scope.DeviceDefinitionId} was dropped: {reason}");
                scope.Declaration = null;
            }

            // A profile with no id keys nothing and can never be selected, and one whose curve is
            // not strictly ascending is refused by the device router on apply — keeping either
            // would leave the user a profile that silently does nothing when chosen.
            scope.Profiles ??= [];
            scope.Profiles.RemoveAll(static profile => profile is null
                || string.IsNullOrWhiteSpace(profile.ProfileId)
                || string.IsNullOrWhiteSpace(profile.CapabilityId));
            HashSet<string> profileIds = new(StringComparer.Ordinal);
            scope.Profiles.RemoveAll(profile => !profileIds.Add(profile.ProfileId.Trim()));
            foreach (DeviceAuthoredProfile profile in scope.Profiles)
            {
                profile.ProfileId = profile.ProfileId.Trim();
                profile.CapabilityId = profile.CapabilityId.Trim();
                profile.Name = string.IsNullOrWhiteSpace(profile.Name)
                    ? profile.ProfileId
                    : profile.Name.Trim();
                if (profile.Name.Length > DeviceAuthoredProfile.MaxNameLength)
                {
                    profile.Name = profile.Name[..DeviceAuthoredProfile.MaxNameLength];
                }

                profile.Curve ??= [];
                profile.Curve.RemoveAll(static point => point is null);
            }

            // A selection naming no capability resolves nothing. One naming a profile that no
            // longer exists is deliberately KEPT: the resolver reports it by name, and dropping it
            // here would turn a diagnosable mistake into a per-application override that vanished
            // without explanation.
            scope.ProfileSelections ??= [];
            scope.ProfileSelections.RemoveAll(static selection => selection is null
                || string.IsNullOrWhiteSpace(selection.CapabilityId));
            HashSet<string> selectionCapabilities = new(StringComparer.Ordinal);
            scope.ProfileSelections.RemoveAll(
                selection => !selectionCapabilities.Add(selection.CapabilityId.Trim()));
            foreach (DeviceProfileSelection selection in scope.ProfileSelections)
            {
                selection.CapabilityId = selection.CapabilityId.Trim();
                selection.ApplicationOverrides ??= [];
                selection.ApplicationOverrides.RemoveAll(static entry => entry is null
                    || string.IsNullOrWhiteSpace(entry.ApplicationId)
                    || string.IsNullOrWhiteSpace(entry.ProfileId));
                HashSet<string> applications = new(StringComparer.Ordinal);
                selection.ApplicationOverrides.RemoveAll(
                    entry => !applications.Add(entry.ApplicationId.Trim()));
                foreach (DeviceApplicationProfileSelection entry in selection.ApplicationOverrides)
                {
                    entry.ApplicationId = entry.ApplicationId.Trim();
                    entry.ProfileId = entry.ProfileId.Trim();
                }
            }

            scope.Profiles.RemoveAll(static profile =>
            {
                if (profile.Curve.Count == 0)
                {
                    return false;
                }

                for (int index = 1; index < profile.Curve.Count; index++)
                {
                    if (profile.Curve[index].Input <= profile.Curve[index - 1].Input)
                    {
                        Log.Warn(
                            $"Device profile '{profile.ProfileId}' was dropped: its curve inputs "
                            + "are not strictly ascending.");
                        return true;
                    }
                }

                return false;
            });
            scope.Values ??= [];
            scope.Values.RemoveAll(static value => value is null
                || string.IsNullOrWhiteSpace(value.SettingId));
            HashSet<string> settingIds = new(StringComparer.Ordinal);
            scope.Values.RemoveAll(value => !settingIds.Add(value.SettingId.Trim()));
            foreach (PluginSettingValue value in scope.Values)
            {
                value.SettingId = value.SettingId.Trim();
            }
        }

        HashSet<(string DeviceDefinitionId, string PluginId)> scopeKeys = [];
        device.PluginSettings.RemoveAll(
            scope => !scopeKeys.Add((scope.DeviceDefinitionId, scope.PluginId)));

        device.Profiles ??= [];
        device.Profiles.RemoveAll(static profile => profile is null
            || string.IsNullOrWhiteSpace(profile.DeviceIdentityKey));
        foreach (DeviceDesiredProfile profile in device.Profiles)
        {
            profile.DeviceIdentityKey = profile.DeviceIdentityKey.Trim();
            profile.SelectedHardwareProfileId = string.IsNullOrWhiteSpace(profile.SelectedHardwareProfileId)
                ? null
                : profile.SelectedHardwareProfileId.Trim();
            profile.Capabilities ??= [];
            profile.OemAssignments ??= [];
            profile.Capabilities.RemoveAll(static capability => capability is null
                || string.IsNullOrWhiteSpace(capability.CapabilityId));
            foreach (DeviceCapabilityPreference capability in profile.Capabilities)
            {
                capability.CapabilityId = capability.CapabilityId.Trim();
                capability.InstanceId = string.IsNullOrWhiteSpace(capability.InstanceId)
                    ? null
                    : capability.InstanceId.Trim();
                capability.HardwareProfiles ??= [];
                capability.ApplicationOverrides ??= [];
                capability.HardwareProfiles.RemoveAll(static value => value is null
                    || string.IsNullOrWhiteSpace(value.ProfileId));
                capability.ApplicationOverrides.RemoveAll(static value => value is null
                    || string.IsNullOrWhiteSpace(value.ApplicationId));
            }

            profile.OemAssignments.RemoveAll(static assignment => assignment is null
                || string.IsNullOrWhiteSpace(assignment.ControlId)
                || !Enum.IsDefined(assignment.Action));
        }
    }

    private static void NormalizePerformance(PerformanceConfig performance)
    {
        performance.FrameLimitStrategy = Definite(
            performance.FrameLimitStrategy, Defaults.Performance.FrameLimitStrategy);
        performance.Applications ??= [];
        performance.Applications.RemoveAll(static application => application is null
            || string.IsNullOrWhiteSpace(application.ApplicationId));
        HashSet<string> identities = new(StringComparer.Ordinal);
        performance.Applications.RemoveAll(application =>
            !identities.Add(application.ApplicationId.Trim()));
        foreach (PerformanceApplicationConfig application in performance.Applications)
        {
            application.ApplicationId = application.ApplicationId.Trim();
            application.RtssProfileName ??= string.Empty;
            application.RtssProfileName = application.RtssProfileName.Trim();
            if (application.RtssProfileName.Length > 128
                || !string.Equals(
                    Path.GetFileName(application.RtssProfileName),
                    application.RtssProfileName,
                    StringComparison.Ordinal)
                || !application.RtssProfileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                application.RtssProfileName = string.Empty;
            }
        }

        // An entry is no longer only an RTSS profile: it also carries the per-game performance
        // profile, its power limit and its refresh preference. Dropping one for having no RTSS
        // profile name would silently delete a game profile the user set up, so an entry survives
        // as long as it still says something.
        performance.Applications.RemoveAll(static application =>
            string.IsNullOrWhiteSpace(application.RtssProfileName)
            && !application.UsePerGameProfile
            && application.FrameLimit is null
            && application.OverlayLevel is null
            && application.TdpWatts is null
            && application.VariableRefreshRate is null);
    }

    private static void NormalizeDisplayMode(DisplayModeValues mode)
    {
        mode.Width = Math.Clamp(mode.Width, 0, 16384);
        mode.Height = Math.Clamp(mode.Height, 0, 16384);
        mode.RefreshRate = Math.Clamp(mode.RefreshRate, 0, 1000);
        mode.DpiPercent = DisplayScale.NormalizeConfiguredPercent(Math.Clamp(mode.DpiPercent, 100, 500));
    }

    private static void NormalizeFilter(FilterNode node)
    {
        node.Kind = Definite(node.Kind, FilterDefaults.Kind);
        node.Mode = Definite(node.Mode, FilterDefaults.Mode);
        node.Condition = Definite(node.Condition, FilterDefaults.Condition);
        node.Platform = Definite(node.Platform, FilterDefaults.Platform);
        node.ScoreType = Definite(node.ScoreType, FilterDefaults.ScoreType);
        node.Units = Definite(node.Units, FilterDefaults.Units);
        node.CardScope = Definite(node.CardScope, FilterDefaults.CardScope);
        node.CollectionId ??= "";
        node.Pattern ??= "";
        node.ContentId ??= "";
        node.Children = (node.Children ?? []).Where(static child => child is not null).ToList();
        node.TagIds ??= [];
        node.AppIds ??= [];
        foreach (var child in node.Children)
        {
            NormalizeFilter(child);
        }
    }

    // The single source of the editor bounds: AppearancePage.axaml binds its
    // NumericUpDown Minimum/Maximum and TextBox MaxLength values to these via
    // x:Static, and normalization applies the same limits to config load and
    // theme import, so the renderer sees the same bounded values regardless of
    // their source.
    /// <summary>Smallest splash font size the editor and normalization accept.</summary>
    public const int MinFontSize = 1;
    /// <summary>Largest splash title font size.</summary>
    public const int MaxTitleFontSize = 400;
    /// <summary>Largest splash caption font size.</summary>
    public const int MaxCaptionFontSize = 200;
    /// <summary>Smallest spinner size in logical pixels.</summary>
    public const int MinSpinnerSize = 1;
    /// <summary>Largest spinner size in logical pixels.</summary>
    public const int MaxSpinnerSize = 1024;
    /// <summary>Smallest logo maximum-edge length.</summary>
    public const int MinLogoMaxSize = 1;
    /// <summary>Largest logo maximum-edge length.</summary>
    public const int MaxLogoMaxSize = 4096;
    /// <summary>Smallest anchored-edge padding.</summary>
    public const int MinPadding = 0;
    /// <summary>Largest anchored-edge padding.</summary>
    public const int MaxPadding = 4096;
    /// <summary>Smallest absolute placement coordinate (an element placed off the
    /// top-left is unreachable, not a feature).</summary>
    public const int MinAbsoluteCoordinate = 0;
    /// <summary>Largest absolute placement coordinate in logical pixels.</summary>
    public const int MaxAbsoluteCoordinate = 16384;

    /// <summary>Splash title and caption are single unwrapped lines. This cap bounds
    /// both Settings and boot layout work while remaining longer than the panel can
    /// display usefully.</summary>
    public const int MaxSplashTextLength = 200;

    /// <summary>Covers hexadecimal and named Avalonia colours with room to spare,
    /// while bounding the text parsed on each live Appearance-page edit.</summary>
    public const int MaxColorLength = 32;

    /// <summary>Repairs explicit JSON nulls inside a splash section (see
    /// <see cref="Normalize"/>), bounds the display strings, and clamps every
    /// numeric field into the range the Appearance editor enforces. Shared with
    /// splash-theme import, which deserializes the same external contract from
    /// archives.</summary>
    internal static SplashConfig NormalizeSplash(SplashConfig splash)
    {
        splash.Text ??= Defaults.Splash.Text;
        splash.TextColor ??= Defaults.Splash.TextColor;
        splash.Caption ??= Defaults.Splash.Caption;
        splash.CaptionColor ??= Defaults.Splash.CaptionColor;
        splash.SpinnerColor ??= Defaults.Splash.SpinnerColor;
        splash.BackgroundColor ??= Defaults.Splash.BackgroundColor;
        // Truncate rather than reject: a theme whose title is too long is still a
        // usable theme, and dropping the whole import over one field would lose the
        // images and every other setting with it.
        splash.Text = Truncate(splash.Text, MaxSplashTextLength, "Splash title text");
        splash.Caption = Truncate(splash.Caption, MaxSplashTextLength, "Splash caption");
        splash.TextColor = Truncate(splash.TextColor, MaxColorLength, "Splash text color");
        splash.CaptionColor = Truncate(splash.CaptionColor, MaxColorLength, "Splash caption color");
        splash.SpinnerColor = Truncate(splash.SpinnerColor, MaxColorLength, "Splash spinner color");
        splash.BackgroundColor = Truncate(splash.BackgroundColor, MaxColorLength, "Splash background color");
        // "No image" has exactly one representation, "": every consumer tests these
        // with IsNullOrWhiteSpace, so a hand-edited config or an imported theme
        // carrying "   " means no image — and must not be persisted as whitespace by
        // the next save either (SplashAssets.PrepareSlot normalizes the same way).
        splash.BackgroundImagePath = Blank(splash.BackgroundImagePath);
        splash.LogoImagePath = Blank(splash.LogoImagePath);
        splash.TextPlacement ??= new SplashElementPlacement();
        splash.SpinnerPlacement ??= new SplashElementPlacement { Mode = SplashPlacementMode.WithText };
        splash.LogoPlacement ??= new SplashElementPlacement { Mode = SplashPlacementMode.WithText };

        splash.TitleFontSize = Math.Clamp(splash.TitleFontSize, MinFontSize, MaxTitleFontSize);
        splash.CaptionFontSize = Math.Clamp(splash.CaptionFontSize, MinFontSize, MaxCaptionFontSize);
        splash.SpinnerSize = Math.Clamp(splash.SpinnerSize, MinSpinnerSize, MaxSpinnerSize);
        splash.LogoMaxSize = Math.Clamp(splash.LogoMaxSize, MinLogoMaxSize, MaxLogoMaxSize);
        splash.SpinnerStyle = Definite(splash.SpinnerStyle, Defaults.Splash.SpinnerStyle);
        splash.SweepEdge = Definite(splash.SweepEdge, Defaults.Splash.SweepEdge);
        NormalizePlacement(splash.TextPlacement);
        NormalizePlacement(splash.SpinnerPlacement);
        NormalizePlacement(splash.LogoPlacement);
        return splash;
    }

    /// <summary>Cuts an over-long display string down to <paramref name="limit"/>
    /// characters, logging once with the original length so a truncated shared theme
    /// (or a hand-edited config) is diagnosable from the log. Values within the limit
    /// are returned untouched — no trimming, no other rewriting.</summary>
    /// <param name="value">The value to bound.</param>
    /// <param name="limit">Maximum number of characters to keep.</param>
    /// <param name="field">Human-readable field label for the warning, written as it
    /// should read at the start of the sentence ("Splash caption", "Accent color").</param>
    private static string Truncate(string value, int limit, string field)
    {
        if (value.Length <= limit)
        {
            return value;
        }
        // Never cut between the halves of a surrogate pair — a lone surrogate would
        // render as a replacement glyph at the end of an otherwise fine line.
        var keep = char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit;
        Log.Warn($"{field} is {value.Length} characters — truncated to {keep}.");
        return value[..keep];
    }

    /// <summary>Maps a null or whitespace-only image path to the single "no image"
    /// value, leaving every real path untouched (leading/trailing spaces are legal
    /// in Windows path components, so nothing else is trimmed).</summary>
    private static string Blank(string? path) => string.IsNullOrWhiteSpace(path) ? "" : path;

    /// <summary>Clamps one element placement into the editor's ranges and drops
    /// unknown enum members back to their defaults.</summary>
    private static void NormalizePlacement(SplashElementPlacement placement)
    {
        placement.Mode = Definite(placement.Mode, PlacementDefaults.Mode);
        placement.Anchor = Definite(placement.Anchor, PlacementDefaults.Anchor);
        placement.PaddingX = Math.Clamp(placement.PaddingX, MinPadding, MaxPadding);
        placement.PaddingY = Math.Clamp(placement.PaddingY, MinPadding, MaxPadding);
        placement.X = Math.Clamp(placement.X, MinAbsoluteCoordinate, MaxAbsoluteCoordinate);
        placement.Y = Math.Clamp(placement.Y, MinAbsoluteCoordinate, MaxAbsoluteCoordinate);
    }

    private static void PreserveCorruptFile()
    {
        try
        {
            // This runs in the elevated one-shots too (UacSettings/LockScreenSettings
            // call Load), and %LOCALAPPDATA%\WSGM is writable by the unelevated user:
            // a pre-planted reparse point at a PREDICTABLE destination would redirect
            // an overwriting elevated copy (CopyFileEx follows destination links). An
            // unpredictable name cannot be pre-planted, and CreateNew refuses to write
            // through anything that already occupies it — no overwrite, no follow.
            var bad = Path.Combine(Log.Directory, $"config.bad.{Guid.NewGuid():N}.json");
            using (var source = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dest = new FileStream(bad, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(dest);
            }
            Log.Error($"Corrupt config preserved at {bad} — registry snapshots may be recoverable from it.");
            PruneCorruptFiles();
        }
        catch
        {
            // Best effort — an unreadable file cannot be preserved either.
        }
    }

    /// <summary>Keeps only the newest few preserved copies. Every Load of a broken
    /// config writes another uniquely named one — several per boot across the shell,
    /// Settings and the elevated one-shots — and nothing else ever reclaims them.
    /// Deleting by enumerated exact name keeps the unpredictable-name property that
    /// makes the write itself reparse-point safe.</summary>
    private static void PruneCorruptFiles()
    {
        const int keep = 5;
        try
        {
            var stale = new DirectoryInfo(Log.Directory)
                .GetFiles("config.bad.*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep);
            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not prune {file.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not prune preserved configs: {ex.Message}");
        }
    }

    /// <summary>Atomically persists a complete configuration snapshot.</summary>
    /// <param name="config">The configuration state to serialize.</param>
    public static void Save(AppConfig config)
    {
        using var guard = ConfigMutex.Acquire(requireExclusive: true);
        Directory.CreateDirectory(Log.Directory);
        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        var temp = $"{ConfigPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
            }

            // Atomic replace (MoveFileEx REPLACE_EXISTING) — covers both the exists and
            // not-yet-exists cases without a TOCTOU window.
            File.Move(temp, ConfigPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Cleanup must not replace the actual write/move failure with a
                // secondary temp-file error. A unique orphan is harmless and can
                // be diagnosed from this bounded warning.
                Log.Warn($"Config temp cleanup failed for '{Path.GetFileName(temp)}': {ex.Message}");
            }
        }
    }

    /// <summary>The only supported read-modify-write path for config.json: takes the
    /// cross-process lock, loads through <see cref="LoadForMutation"/>, applies
    /// <paramref name="mutate"/>, and saves — all inside one scope, so no other WSGM
    /// process can persist between the read and the write and have its fields dropped
    /// by it. Callers must apply ONLY their own fields: everything else in the loaded
    /// instance is written straight back.
    /// <para>The strict load is the point. <see cref="Load"/> answers an unreadable
    /// file with defaults, which is right for a reader but catastrophic here — saving
    /// those defaults erases the previous-shell/UAC/lock-screen registry snapshots
    /// uninstall restores from. An unreadable existing file therefore throws out of
    /// this method and ABORTS the mutation; <see cref="Load"/> stays available for
    /// read-only callers.</para>
    /// <para>A caller that needs more work under the same lock (see
    /// SettingsViewModel's save transaction, which also promotes splash assets and writes the
    /// boot manifest) wraps this in its own <see cref="AcquireLock"/> scope — the
    /// nested acquisition is free.</para></summary>
    /// <param name="mutate">Applies the caller's fields to the freshly loaded configuration.</param>
    /// <returns>The configuration instance that was persisted.</returns>
    /// <exception cref="InvalidDataException">The existing file could not be parsed.</exception>
    internal static AppConfig Mutate(Action<AppConfig> mutate)
    {
        using var guard = ConfigMutex.Acquire(requireExclusive: true);
        var config = LoadForMutation();
        mutate(config);
        Save(config);
        return config;
    }

    /// <summary>Takes the cross-process config lock for a caller that must keep a
    /// whole read-modify-write sequence — plus the file work between its steps —
    /// atomic against other WSGM processes. SettingsViewModel.Save holds it
    /// across Load → Save → the splash-asset Commit → the boot-manifest write, so
    /// config.json and the live splash images can never be left describing different
    /// states. Only FAST operations belong in such a scope: the timeout below is
    /// sized for a small JSON write, so anything slow (the splash-asset staging
    /// copies, which can be tens of megabytes) must be done before the lock is taken.
    /// <para>The <see cref="Load"/> and <see cref="Save"/> calls made inside such a
    /// scope acquire the SAME lock again. Those nested acquisitions are FREE: a
    /// thread-local depth counter short-circuits them, so they neither touch the
    /// kernel object nor — and this is the point — pay the
    /// <see cref="MutexTimeoutMs"/> timeout a second, third and fourth time when
    /// another process holds the lock. Relying on the Win32 mutex's own per-thread
    /// recursion count instead made a contended save cost one timeout per nested call
    /// (Load + Save + repair Save + the outer scope ≈ 6-8 s of frozen UI and four
    /// "Config mutex timed out" lines). Only the OUTERMOST scope releases, so the hold
    /// survives until this scope is disposed. Write transactions never enter a
    /// degraded scope: timeout or mutex failure aborts them.</para></summary>
    /// <returns>A scope that releases the lock when disposed.</returns>
    internal static IDisposable AcquireLock() => ConfigMutex.Acquire(requireExclusive: true);

    /// <summary>Deep-copies one configuration document through the production JSON
    /// contract — the one clone mechanism for config shapes, so a copy can never
    /// diverge from what a save/load round trip would produce.</summary>
    /// <typeparam name="T">A type registered on <see cref="ConfigJsonContext"/>.</typeparam>
    /// <param name="value">The instance to copy.</param>
    /// <param name="typeInfo">The source-generated metadata for <typeparamref name="T"/>.</param>
    /// <returns>An isolated copy sharing no mutable state with <paramref name="value"/>.</returns>
    internal static T CloneJson<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class, new() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(value, typeInfo), typeInfo) ?? new T();

    /// <summary>Test seam: how deeply the CALLING thread currently holds the config
    /// lock (0 = not held). Exists so the acquire/release balance of the nested scopes
    /// can be asserted without going near the per-user config file.</summary>
    internal static int LockDepth => ConfigMutex.CurrentDepth;

    /// <summary>Whether the calling thread owns the named mutex.</summary>
    internal static bool HasExclusiveLock => ConfigMutex.HasExclusiveOwnership;

    /// <summary>Cross-process guard around Load/Save. Read-only loads may degrade
    /// with a warning; writes fail closed when the mutex cannot be acquired.
    /// Re-entrant per thread through a depth counter: only the outermost scope talks
    /// to the kernel object, so a nested acquisition costs nothing even while another
    /// process holds the lock.
    /// <para>Scopes are meant to be disposed in reverse acquisition order (they are
    /// all <c>using</c> blocks today). Out-of-order disposal is a caller error, and
    /// what is guaranteed for it is only that the state stays sound: the depth never
    /// goes negative, a late nested Dispose cannot pop a level it does not own, and
    /// the mutex is released exactly once — by the scope that acquired it, at the
    /// moment that scope is disposed. Cross-process exclusion consequently ENDS
    /// there: a nested scope that outlives its owner holds nothing, and the counter
    /// stops pretending otherwise rather than blocking a later real acquisition.</para></summary>
    private sealed class ConfigMutex : IDisposable
    {
        // Per-thread lock state. The mutex itself is thread-owned in Win32, so the
        // depth can only ever describe the thread that took it; a nested acquisition
        // from ANOTHER thread is a real, competing acquisition and is treated as one.
        [ThreadStatic]
        private static int _depth;
        [ThreadStatic]
        private static bool _hasExclusiveOwnership;

        private readonly Mutex? _mutex;
        private readonly bool _owned;
        private readonly bool _nested;

        // The depth this scope established (1 for the outermost). Dispose pops back
        // to _level - 1 instead of blindly decrementing, which is what keeps the
        // counter sane when scopes are disposed OUT OF ORDER (see Dispose).
        private readonly int _level;
        private bool _disposed;

        private ConfigMutex(Mutex? mutex, bool owned, bool nested, int level)
        {
            _mutex = mutex;
            _owned = owned;
            _nested = nested;
            _level = level;
        }

        /// <summary>How deeply the calling thread holds the lock (0 = not at all).</summary>
        internal static int CurrentDepth => _depth;
        internal static bool HasExclusiveOwnership => _hasExclusiveOwnership;

        public static ConfigMutex Acquire(bool requireExclusive)
        {
            if (_depth > 0)
            {
                if (requireExclusive && !_hasExclusiveOwnership)
                {
                    throw new InvalidOperationException(
                        "An exclusive config operation cannot be nested inside a degraded read.");
                }
                // Already held by this thread (Settings Save's scope around Load/Save):
                // no kernel call, and above all no second MutexTimeoutMs wait.
                _depth++;
                return new ConfigMutex(null, owned: false, nested: true, level: _depth);
            }

            Mutex? mutex = null;
            var owned = false;
            try
            {
                mutex = new Mutex(initiallyOwned: false, MutexName);
                try
                {
                    owned = mutex.WaitOne(MutexTimeoutMs);
                }
                catch (AbandonedMutexException)
                {
                    // Previous holder died mid-section; Save is atomic, the file is intact.
                    // The wait DID succeed, so this scope owns the mutex and must release it.
                    owned = true;
                }
                if (!owned)
                {
                    if (requireExclusive)
                    {
                        throw new TimeoutException(
                            "The shared WSGM configuration is busy; the save was not performed.");
                    }

                    Log.Warn("Config mutex timed out — continuing with a read-only snapshot.");
                }
            }
            catch (Exception ex)
            {
                if (requireExclusive)
                {
                    mutex?.Dispose();
                    throw;
                }

                Log.Warn($"Config mutex unavailable for read-only load: {ex.Message}");
            }
            // Counted even when the acquisition degraded, so the nested steps of one
            // sequence inherit that decision instead of each paying the timeout again.
            _depth = 1;
            _hasExclusiveOwnership = owned;
            return new ConfigMutex(mutex, owned, nested: false, level: 1);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                // Balance is per scope: a double Dispose (an explicit one plus the
                // `using`) must not pop a depth level its scope never pushed.
                return;
            }
            _disposed = true;

            // Each scope owns one recorded depth. A late out-of-order Dispose must not pop a newer
            // acquisition, so it changes depth only while its own level is still counted.
            if (_depth >= _level)
            {
                _depth = _level - 1;
                if (_depth == 0)
                {
                    _hasExclusiveOwnership = false;
                }
            }

            if (_nested)
            {
                // Nested scopes never touch the kernel object; only the scope that
                // acquired the mutex releases it, exactly once.
                return;
            }

            try
            {
                if (_owned)
                {
                    _mutex?.ReleaseMutex();
                }
            }
            catch (Exception ex)
            {
                // Cleanup failure does not replace the save/load outcome. The handle is still
                // disposed below so the next waiter observes abandonment instead of a stuck owner.
                Log.Warn($"Config mutex release failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    _mutex?.Dispose();
                }
                catch
                {
                    // Closing a handle: nothing left to fall back to.
                }
            }
        }
    }
}
