using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Settings;

namespace WSGM.Tests;

public sealed class PluginSettingsRoundTripTests
{
    private const string Device = "msi.claw8";
    private const string Plugin = "wsgm.device.msi";

    private static PluginSettingsManifest Manifest(string label = "Flag") => new()
    {
        Sections = [new PluginSettingSection { SectionId = "one", Key = SettingSectionKey.General }],
        Settings =
        [
            new PluginSettingDescriptor
            {
                SettingId = "vendor.flag",
                ValueKind = CapabilityValueKind.Boolean,
                Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = label },
                Default = new CapabilityValue
                {
                    Kind = CapabilityValueKind.Boolean,
                    BooleanValue = false,
                },
                SectionId = "one",
            },
        ],
    };

    private static AppConfig Config(PluginSettingsManifest? declaration) => new()
    {
        DeviceIntegration = new DeviceIntegrationConfig
        {
            PluginSettings =
            [
                new PluginSettingsScope
                {
                    DeviceDefinitionId = Device,
                    PluginId = Plugin,
                    Declaration = declaration,
                },
            ],
        },
    };

    [Fact]
    public void ACachedDeclarationProducesAnEditablePage()
    {
        SettingsViewModel viewModel = new(Config(Manifest()));

        Assert.True(viewModel.PluginSettingsAvailable);
        Assert.Equal(
            "vendor.flag",
            viewModel.PluginSettingSections[0].Rows[0].SettingId);
    }

    [Fact]
    public void NoCachedDeclarationSaysSoRatherThanShowingABlankPage()
    {
        SettingsViewModel viewModel = new(Config(null));

        Assert.False(viewModel.PluginSettingsAvailable);
        Assert.Contains("plugin", viewModel.PluginSettingsEmptyReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEditReachesTheConfigurationTheSaveActuallyWrites()
    {
        // The save re-reads configuration from disk and applies the view model onto THAT object, so
        // an edit written to the loaded copy would be silently discarded.
        SettingsViewModel viewModel = new(Config(Manifest()));
        viewModel.PluginSettingSections[0].Rows[0].BooleanValue = true;

        AppConfig fresh = Config(Manifest());
        viewModel.ApplyPluginSettingsTo(fresh);

        PluginSettingValue stored = Assert.Single(fresh.DeviceIntegration.PluginSettings[0].Values);
        Assert.Equal("vendor.flag", stored.SettingId);
        Assert.True(stored.Boolean);
    }

    [Fact]
    public void AnUntouchedSettingIsLeftExactlyAsAnotherProcessWroteIt()
    {
        // The running shell owns the same store while Settings is open, so writing an unedited
        // snapshot over the fresh load would silently revert it.
        SettingsViewModel viewModel = new(Config(Manifest()));

        AppConfig fresh = Config(Manifest());
        fresh.DeviceIntegration.PluginSettings[0].Values.Add(new PluginSettingValue
        {
            SettingId = "vendor.flag",
            Boolean = true,
        });
        viewModel.ApplyPluginSettingsTo(fresh);

        Assert.True(fresh.DeviceIntegration.PluginSettings[0].Values[0].Boolean);
    }

    [Fact]
    public void AStoredValueTheDeclarationNoLongerAllowsFallsBackToItsDefault()
    {
        // A cache written by an older plugin build can describe bounds the stored values no longer
        // fit, and the page must not offer a value the plugin would refuse.
        AppConfig config = Config(Manifest());
        config.DeviceIntegration.PluginSettings[0].Values.Add(new PluginSettingValue
        {
            SettingId = "vendor.flag",
            // Wrong shape for a boolean setting: the integer field is set and the boolean is not.
            Integer = 7,
        });

        SettingsViewModel viewModel = new(config);

        Assert.False(viewModel.PluginSettingSections[0].Rows[0].BooleanValue);
    }

    [Fact]
    public void TheMostRecentlyPublishedDeclarationWinsOverAStaleScope()
    {
        AppConfig config = Config(Manifest("Stale"));
        PluginSettingsScope current = new()
        {
            DeviceDefinitionId = "msi.claw8-current",
            PluginId = Plugin,
            Declaration = Manifest("Current"),
        };
        config.DeviceIntegration.PluginSettings.Add(current);

        SettingsViewModel viewModel = new(config);

        Assert.Equal("Current", viewModel.PluginSettingSections[0].Rows[0].Label);
    }

    [Fact]
    public void SettingsSelectsOnlyTheCurrentlyInstalledPluginDeclaration()
    {
        AppConfig config = Config(Manifest("Replaced"));
        config.DeviceIntegration.PluginSettings.Add(new PluginSettingsScope
        {
            DeviceDefinitionId = "other.device",
            PluginId = "wsgm.device.current",
            Declaration = Manifest("Installed"),
        });

        SettingsViewModel viewModel = new(config, "wsgm.device.current");

        Assert.Equal("Installed", viewModel.PluginSettingSections[0].Rows[0].Label);
    }

    [Fact]
    public void EmptyPluginSlotDoesNotExposeAReplacedPluginDeclaration()
    {
        SettingsViewModel viewModel = new(Config(Manifest("Replaced")), installedPluginId: null);

        Assert.False(viewModel.PluginSettingsAvailable);
    }
}
