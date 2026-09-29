using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Shell;

/// <summary>One plugin setting as a surface should draw it.</summary>
/// <param name="Descriptor">What the plugin declared.</param>
/// <param name="Value">The value in force.</param>
/// <param name="Origin">Whether it is the declared default, a stored value, or a rejected one.</param>
internal readonly record struct PluginSettingView(
    PluginSettingDescriptor Descriptor,
    CapabilityValue Value,
    PluginSettingOrigin Origin
);

/// <summary>The whole declared settings surface, ordered as it should be drawn.</summary>
/// <param name="Sections">Declared sections, by sort order then declaration order.</param>
/// <param name="Settings">Settings grouped under the section id they belong to.</param>
internal readonly record struct PluginSettingsView(
    IReadOnlyList<PluginSettingSection> Sections,
    IReadOnlyDictionary<string, IReadOnlyList<PluginSettingView>> Settings
);

/// <summary>
/// Holds the active plugin's settings declaration, reconciles it with what is stored, and keeps the
/// plugin supplied with the values in force.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="DeviceCapabilityRouter"/>. A capability writes hardware and
/// the device keeps the value; a setting configures the plugin and WSGM keeps it. Sharing one
/// projection would blur exactly the boundary that decides which surface a control belongs on.
/// </remarks>
internal sealed class PluginSettingsCoordinator : IDisposable
{
    /// <summary>Section id used for a setting that names one the manifest never declared.</summary>
    /// <remarks>
    /// The colon is load-bearing: <see cref="PlainText.IsIdentifier"/> does not accept one, so no
    /// plugin can declare this id and take the fallback group over. A dotted name would be a legal
    /// plugin section id and the collision would be silent — the plugin's own section and WSGM's
    /// leftovers would merge into one heading.
    /// </remarks>
    internal const string FallbackSectionId = "wsgm:other";

    private readonly object _gate = new();
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private DevicePluginRuntime? _client;
    private PluginSettingsManifest? _manifest;
    private string _deviceDefinitionId = string.Empty;
    private string _pluginId = string.Empty;
    private AppConfig? _config;
    private bool _disposed;

    /// <summary>
    /// Begins tracking a plugin's settings for one cycle.
    /// </summary>
    /// <param name="client">The active in-process plugin runtime.</param>
    /// <param name="deviceDefinitionId">Device definition the values are keyed under.</param>
    /// <param name="pluginId">Plugin the values are keyed under.</param>
    /// <param name="config">Current configuration, for stored values.</param>
    internal void Attach(
        DevicePluginRuntime client,
        string deviceDefinitionId,
        string pluginId,
        AppConfig config
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DetachUnderGate();
            _client = client;
            _deviceDefinitionId = deviceDefinitionId ?? string.Empty;
            _pluginId = pluginId ?? string.Empty;
            _config = config;
            _manifest = null;
            client.SettingsManifestReceived += OnManifest;
        }
    }

    /// <summary>Stops tracking and forgets the declaration.</summary>
    internal void Detach()
    {
        lock (_gate)
        {
            DetachUnderGate();
        }
    }

    /// <summary>Replaces the configuration used for stored values after a reload.</summary>
    /// <param name="config">The replacement configuration.</param>
    internal void ApplyConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            _config = config;
        }

        PublishAndPush();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DetachUnderGate();
        }
    }

    private void OnManifest(PluginSettingsManifest manifest)
    {
        string device;
        string plugin;
        lock (_gate)
        {
            _manifest = manifest;
            device = _deviceDefinitionId;
            plugin = _pluginId;
        }

        // Cached so Settings can draw the page without activating hardware. A declaration is
        // published by plugin code rather than stored in the package manifest, so this is the only
        // moment WSGM sees it. The config write stays off the runtime callback thread.
        if (device.Length > 0 && plugin.Length > 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    AppConfig persisted = ConfigStore.Mutate(
                        config => CacheDeclaration(config, device, plugin, manifest));
                    lock (_gate)
                    {
                        _config = persisted;
                    }
                }
                catch (Exception ex)
                {
                    // Not fatal: the running session already has the manifest in memory and only a
                    // later Settings process loses out, so this must never take the plugin down.
                    Log.Warn(
                        $"Plugin settings: caching the declaration for '{plugin}' failed: "
                        + ex.Message);
                }
            });
        }

        PublishAndPush();
    }

    internal static void CacheDeclaration(
        AppConfig config,
        string device,
        string plugin,
        PluginSettingsManifest manifest
    )
    {
        List<PluginSettingsScope> scopes = config.DeviceIntegration.PluginSettings;
        PluginSettingsScope? scope = scopes.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceDefinitionId, device, StringComparison.Ordinal)
            && string.Equals(candidate.PluginId, plugin, StringComparison.Ordinal));
        if (scope is null)
        {
            scope = new PluginSettingsScope { DeviceDefinitionId = device, PluginId = plugin };
            scopes.Add(scope);
        }

        // Only the active scope may describe the page. Keep older scopes' authored values and
        // profiles for a future device match, but clear their presentation cache so Settings never
        // renders a declaration from a device definition that is no longer active.
        foreach (PluginSettingsScope candidate in scopes)
        {
            if (!ReferenceEquals(candidate, scope))
            {
                candidate.Declaration = null;
            }
        }

        scope.Declaration = manifest;
    }

    private void PublishAndPush() => _ = PublishAndPushAsync(CancellationToken.None);

    private async Task PublishAndPushAsync(CancellationToken cancellationToken)
    {
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PluginSettingsManifest? manifest;
            DevicePluginRuntime? client;
            IReadOnlyList<PluginSettingValue> stored;
            lock (_gate)
            {
                manifest = _manifest;
                client = _client;
                stored = StoredUnderGate();
            }

            if (manifest is null)
            {
                return;
            }

            PluginSettingsResolution resolution = PluginSettingsResolver.Resolve(manifest, stored);
            foreach (EffectivePluginSetting rejected in resolution.Values.Where(
                value => value.Origin is PluginSettingOrigin.Rejected))
            {
                Log.Warn(
                    $"Plugin setting '{rejected.SettingId}' fell back to its default: {rejected.Reason}");
            }

            if (resolution.Orphans.Count > 0)
            {
                Log.Info(
                    "Plugin settings no longer declared: "
                    + string.Join(", ", resolution.Orphans));
            }

            if (client is null)
            {
                return;
            }

            IReadOnlyList<DeviceSettingValue> values =
                [.. resolution.Values.Select(value => new DeviceSettingValue(value.SettingId, value.Value))];
            try
            {
                await client.ApplySettingsValuesAsync(values, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The plugin keeps whatever it had. Reporting this matters because the surface will now
                // show values the plugin is not acting on, which is otherwise invisible.
                Log.Warn($"Plugin settings not delivered: {ex.Message}");
            }
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    /// <summary>
    /// Arranges a declaration and its resolved values into what a surface draws.
    /// </summary>
    /// <param name="manifest">The plugin's declaration.</param>
    /// <param name="resolution">The values in force.</param>
    /// <returns>Sections in draw order, with their settings grouped underneath.</returns>
    /// <remarks>Internal so the placement and ordering rules can be pinned without a device.</remarks>
    internal static PluginSettingsView Project(
        PluginSettingsManifest manifest,
        PluginSettingsResolution resolution
    )
    {
        Dictionary<string, CapabilityValue> byId = resolution.Values.ToDictionary(
            value => value.SettingId,
            value => value.Value,
            StringComparer.Ordinal);
        Dictionary<string, PluginSettingOrigin> originById = resolution.Values.ToDictionary(
            value => value.SettingId,
            value => value.Origin,
            StringComparer.Ordinal);
        HashSet<string> declaredSections = new(
            manifest.Sections.Select(section => section.SectionId),
            StringComparer.Ordinal);

        Dictionary<string, List<PluginSettingView>> grouped = new(StringComparer.Ordinal);
        // Declaration order is the tiebreak, so an ordering the plugin left unset still renders the
        // same way every time rather than following dictionary iteration.
        foreach (PluginSettingDescriptor setting in manifest.Settings
            .Select((setting, index) => (setting, index))
            .OrderBy(pair => pair.setting.SortOrder)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.setting))
        {
            string section = setting.SectionId is { Length: > 0 } named
                && declaredSections.Contains(named)
                ? named
                : FallbackSectionId;
            if (section == FallbackSectionId && setting.SectionId is { Length: > 0 } missing)
            {
                Log.Info(
                    $"Plugin setting '{setting.SettingId}' names undeclared section '{missing}'; "
                    + "drawn under the fallback section.");
            }

            if (!grouped.TryGetValue(section, out List<PluginSettingView>? list))
            {
                list = [];
                grouped[section] = list;
            }

            list.Add(new PluginSettingView(
                setting,
                byId.GetValueOrDefault(setting.SettingId, setting.Default),
                originById.GetValueOrDefault(setting.SettingId, PluginSettingOrigin.Default)));
        }

        IReadOnlyList<PluginSettingSection> sections =
        [
            .. manifest.Sections
                .Select((section, index) => (section, index))
                .OrderBy(pair => pair.section.SortOrder)
                .ThenBy(pair => pair.index)
                .Select(pair => pair.section)
                // An empty section is not drawn; a heading with nothing under it reads as a
                // feature that failed rather than one the device does not have.
                .Where(section => grouped.ContainsKey(section.SectionId)),
            .. grouped.ContainsKey(FallbackSectionId)
                ? new[]
                {
                    new PluginSettingSection
                    {
                        SectionId = FallbackSectionId,
                        Key = SettingSectionKey.General,
                        SortOrder = int.MaxValue,
                    },
                }
                : [],
        ];

        return new PluginSettingsView(
            sections,
            grouped.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<PluginSettingView>)pair.Value,
                StringComparer.Ordinal));
    }

    private IReadOnlyList<PluginSettingValue> StoredUnderGate()
    {
        if (_config is null)
        {
            return [];
        }

        return _config.DeviceIntegration.PluginSettings
            .FirstOrDefault(scope =>
                string.Equals(scope.DeviceDefinitionId, _deviceDefinitionId, StringComparison.Ordinal)
                && string.Equals(scope.PluginId, _pluginId, StringComparison.Ordinal))
            ?.Values ?? [];
    }

    private void DetachUnderGate()
    {
        if (_client is not null)
        {
            _client.SettingsManifestReceived -= OnManifest;
            _client = null;
        }

        _manifest = null;
    }
}
