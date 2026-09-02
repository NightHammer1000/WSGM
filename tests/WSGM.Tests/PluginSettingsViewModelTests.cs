using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Settings;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class PluginSettingsViewModelTests
{
    private static SettingsViewModel ViewModel() => new(new AppConfig());

    private static PluginSettingDescriptor Setting(string id, string? sectionId) => new()
    {
        SettingId = id,
        ValueKind = CapabilityValueKind.Boolean,
        Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = id },
        Default = new CapabilityValue { Kind = CapabilityValueKind.Boolean },
        SectionId = sectionId,
    };

    /// <remarks>
    /// Through the one shared projection, not a Settings-specific copy: the overlay draws from the
    /// same call, so a second arrangement here would let the two surfaces disagree about where a
    /// plugin's settings live.
    /// </remarks>
    private static PluginSettingsView Page(PluginSettingsManifest manifest, params string[] ids) =>
        PluginSettingsCoordinator.Project(
            manifest,
            new PluginSettingsResolution(
                [.. ids.Select(id => new EffectivePluginSetting(
                    id,
                    new CapabilityValue { Kind = CapabilityValueKind.Boolean },
                    PluginSettingOrigin.Default,
                    null))],
                []));

    [Fact]
    public void APageWithNoSectionsReportsItselfUnavailableRatherThanDrawingNothing()
    {
        SettingsViewModel viewModel = ViewModel();

        viewModel.SetPluginSettings(Page(new PluginSettingsManifest()), (_, _) => { });

        Assert.False(viewModel.PluginSettingsAvailable);
        Assert.NotEmpty(viewModel.PluginSettingsEmptyReason);
    }

    [Fact]
    public void SectionsAndRowsArriveInRenderOrder()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections =
            [
                new PluginSettingSection { SectionId = "one", Key = SettingSectionKey.Power },
                new PluginSettingSection
                {
                    SectionId = "two",
                    Key = SettingSectionKey.Custom,
                    CustomTitle = "Vendor",
                    SortOrder = 1,
                },
            ],
            Settings = [Setting("a", "one"), Setting("b", "two")],
        };

        SettingsViewModel viewModel = ViewModel();
        viewModel.SetPluginSettings(Page(manifest, "a", "b"), (_, _) => { });

        Assert.True(viewModel.PluginSettingsAvailable);
        Assert.Equal(
            ["one", "two"],
            viewModel.PluginSettingSections.Select(section => section.SectionId));
        Assert.Equal("POWER", viewModel.PluginSettingSections[0].Title);
        Assert.Equal("VENDOR", viewModel.PluginSettingSections[1].Title);
    }

    [Fact]
    public void EditingARowReachesTheOwnerWithItsSettingId()
    {
        PluginSettingsManifest manifest = new()
        {
            Sections = [new PluginSettingSection { SectionId = "one", Key = SettingSectionKey.General }],
            Settings = [Setting("vendor.flag", "one")],
        };

        SettingsViewModel viewModel = ViewModel();
        List<string> edited = [];
        viewModel.SetPluginSettings(Page(manifest, "vendor.flag"), (id, _) => edited.Add(id));

        viewModel.PluginSettingSections[0].Rows[0].BooleanValue = true;

        Assert.Equal("vendor.flag", Assert.Single(edited));
    }

    [Fact]
    public void RebuildingReplacesTheRowsRatherThanAccumulatingThem()
    {
        // The manifest changes only when a plugin is installed or updated, so a wholesale rebuild is
        // the correct path -- but it must not leave the previous plugin's rows behind.
        PluginSettingsManifest manifest = new()
        {
            Sections = [new PluginSettingSection { SectionId = "one", Key = SettingSectionKey.General }],
            Settings = [Setting("a", "one")],
        };

        SettingsViewModel viewModel = ViewModel();
        viewModel.SetPluginSettings(Page(manifest, "a"), (_, _) => { });
        viewModel.SetPluginSettings(Page(manifest, "a"), (_, _) => { });

        Assert.Single(viewModel.PluginSettingSections);
        Assert.Single(viewModel.PluginSettingSections[0].Rows);
    }

    [Fact]
    public void AnOldRowStopsReachingTheOwnerAfterARebuild()
    {
        // Each rebuild subscribes fresh handlers. A retained row from the previous build would
        // otherwise keep writing settings for a manifest that is no longer installed.
        PluginSettingsManifest manifest = new()
        {
            Sections = [new PluginSettingSection { SectionId = "one", Key = SettingSectionKey.General }],
            Settings = [Setting("a", "one")],
        };

        SettingsViewModel viewModel = ViewModel();
        int firstOwnerEdits = 0;
        viewModel.SetPluginSettings(Page(manifest, "a"), (_, _) => firstOwnerEdits++);
        PluginSettingRowViewModel stale = viewModel.PluginSettingSections[0].Rows[0];

        viewModel.SetPluginSettings(Page(manifest, "a"), (_, _) => { });
        viewModel.PluginSettingSections[0].Rows[0].BooleanValue = true;

        Assert.Equal(0, firstOwnerEdits);
        Assert.NotSame(stale, viewModel.PluginSettingSections[0].Rows[0]);
    }
}
