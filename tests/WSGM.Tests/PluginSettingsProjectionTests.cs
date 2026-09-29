using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class PluginSettingsProjectionTests
{
    [Fact]
    public void Project_SettingNamingAnUndeclaredSection_IsDrawnUnderTheFallbackRatherThanDropped()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("power")],
            Settings = [Toggle("a", "nonexistent")],
        };

        PluginSettingsView view = PluginSettingsCoordinator.Project(manifest, Resolve(manifest));

        Assert.Contains(
            view.Sections,
            section => section.SectionId == PluginSettingsCoordinator.FallbackSectionId);
        Assert.Single(view.Settings[PluginSettingsCoordinator.FallbackSectionId]);
    }

    [Fact]
    public void Project_SectionWithNothingUnderIt_IsNotDrawn()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("power"), Section("empty")],
            Settings = [Toggle("a", "power")],
        };

        PluginSettingsView view = PluginSettingsCoordinator.Project(manifest, Resolve(manifest));

        Assert.Equal(["power"], view.Sections.Select(section => section.SectionId));
    }

    [Fact]
    public void Project_SectionsFollowSortOrderThenDeclarationOrder()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections =
            [
                Section("third", sort: 5),
                Section("first", sort: 1),
                Section("second", sort: 1),
            ],
            Settings = [Toggle("a", "third"), Toggle("b", "first"), Toggle("c", "second")],
        };

        PluginSettingsView view = PluginSettingsCoordinator.Project(manifest, Resolve(manifest));

        Assert.Equal(
            ["first", "second", "third"],
            view.Sections.Select(section => section.SectionId));
    }

    [Fact]
    public void Project_SettingsWithinASectionFollowSortOrderThenDeclarationOrder()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("power")],
            Settings =
            [
                Toggle("c", "power", sort: 9),
                Toggle("a", "power"),
                Toggle("b", "power"),
            ],
        };

        PluginSettingsView view = PluginSettingsCoordinator.Project(manifest, Resolve(manifest));

        Assert.Equal(
            ["a", "b", "c"],
            view.Settings["power"].Select(setting => setting.Descriptor.SettingId));
    }

    [Fact]
    public void Project_FallbackSectionSortsLastSoDeclaredSectionsKeepTheirPlaces()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [Section("power", sort: 100)],
            Settings = [Toggle("a", "power"), Toggle("b", "nowhere")],
        };

        PluginSettingsView view = PluginSettingsCoordinator.Project(manifest, Resolve(manifest));

        Assert.Equal(
            ["power", PluginSettingsCoordinator.FallbackSectionId],
            view.Sections.Select(section => section.SectionId));
    }

    [Fact]
    public void Project_ManifestWithNoSettings_DrawsNothing()
    {
        PluginSettingsManifest manifest = new() { Sections = [Section("power")] };

        PluginSettingsView view = PluginSettingsCoordinator.Project(manifest, Resolve(manifest));

        Assert.Empty(view.Sections);
        Assert.Empty(view.Settings);
    }

    private static PluginSettingsResolution Resolve(PluginSettingsManifest manifest) =>
        PluginSettingsResolver.Resolve(manifest, []);

    private static PluginSettingSection Section(string id, int sort = 0) => new()
    {
        SectionId = id,
        Key = SettingSectionKey.General,
        SortOrder = sort,
    };

    private static PluginSettingDescriptor Toggle(string id, string? section, int sort = 0) => new()
    {
        SettingId = id,
        ValueKind = CapabilityValueKind.Boolean,
        Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "A setting" },
        Default = new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = false },
        SectionId = section,
        SortOrder = sort,
    };
}
