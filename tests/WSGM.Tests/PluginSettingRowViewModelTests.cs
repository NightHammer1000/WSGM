using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Settings;

namespace WSGM.Tests;

public sealed class PluginSettingRowViewModelTests
{
    private static PluginSettingDescriptor Descriptor(
        CapabilityValueKind kind,
        int? minimum = null,
        int? maximum = null,
        int? maximumLength = null,
        params string[] choices) => new()
        {
            SettingId = "vendor.setting",
            ValueKind = kind,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Setting" },
            Default = new CapabilityValue { Kind = kind },
            Minimum = minimum,
            Maximum = maximum,
            Step = minimum is null ? null : 1,
            MaximumLength = maximumLength,
            Choices = [.. choices.Select(value => new CapabilityChoice(
                value,
                new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = value }))],
        };

    private static CapabilityValue Value(CapabilityValueKind kind) => new() { Kind = kind };

    [Fact]
    public void EachKindShowsExactlyOneControl()
    {
        // The page templates by visibility, so two flags true at once draws two controls in one row.
        (CapabilityValueKind Kind, Func<PluginSettingRowViewModel, bool> Flag)[] cases =
        [
            (CapabilityValueKind.Boolean, row => row.IsToggle),
            (CapabilityValueKind.Integer, row => row.IsRange),
            (CapabilityValueKind.Choice, row => row.IsChoice),
            (CapabilityValueKind.Color, row => row.IsColor),
            (CapabilityValueKind.Text, row => row.IsText),
        ];

        foreach ((CapabilityValueKind kind, Func<PluginSettingRowViewModel, bool> flag) in cases)
        {
            PluginSettingRowViewModel row = new(Descriptor(kind, 0, 10), Value(kind));
            bool[] flags = [row.IsToggle, row.IsRange, row.IsChoice, row.IsColor, row.IsText];

            Assert.True(flag(row));
            Assert.Single(flags, set => set);
        }
    }

    [Fact]
    public void AnEditIsReportedWithTheSettingIdAndTheNewValue()
    {
        PluginSettingRowViewModel row = new(
            Descriptor(CapabilityValueKind.Boolean),
            Value(CapabilityValueKind.Boolean));
        (string Id, CapabilityValue Value)? edit = null;
        row.Edited += (id, value) => edit = (id, value);

        row.BooleanValue = true;

        Assert.Equal("vendor.setting", edit?.Id);
        Assert.True(edit?.Value.BooleanValue);
    }

    [Fact]
    public void SettingTheSameValueAgainReportsNothing()
    {
        PluginSettingRowViewModel row = new(
            Descriptor(CapabilityValueKind.Boolean),
            new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = true });
        int edits = 0;
        row.Edited += (_, _) => edits++;

        row.BooleanValue = true;

        Assert.Equal(0, edits);
    }

    [Fact]
    public void AdoptingAValueDoesNotLookLikeAUserEdit()
    {
        // A refresh that reported edits would write back what it just read, and every reload would
        // appear in the log as the user changing the setting.
        PluginSettingRowViewModel row = new(
            Descriptor(CapabilityValueKind.Integer, 0, 100),
            Value(CapabilityValueKind.Integer));
        int edits = 0;
        row.Edited += (_, _) => edits++;

        row.Adopt(new CapabilityValue
        {
            Kind = CapabilityValueKind.Integer,
            IntegerValue = 42,
        });

        Assert.Equal(42, row.IntegerValue);
        Assert.Equal(0, edits);
    }

    [Theory]
    [InlineData(500, 100)]
    [InlineData(-20, 10)]
    public void ARangeValueIsHeldInsideItsDeclaredBounds(int requested, int expected)
    {
        // A slider bound to a stale range would otherwise report a value the plugin refuses, and the
        // control springs back with no explanation.
        PluginSettingRowViewModel row = new(
            Descriptor(CapabilityValueKind.Integer, 10, 100),
            Value(CapabilityValueKind.Integer))
        {
            IntegerValue = requested,
        };

        Assert.Equal(expected, row.IntegerValue);
    }

    [Fact]
    public void TextIsTruncatedToItsDeclaredMaximum()
    {
        PluginSettingRowViewModel row = new(
            Descriptor(CapabilityValueKind.Text, maximumLength: 4),
            Value(CapabilityValueKind.Text))
        {
            TextValue = "far too long",
        };

        Assert.Equal("far ", row.TextValue);
    }

    [Fact]
    public void AChoiceAdoptsTheMatchingDeclaredOption()
    {
        PluginSettingRowViewModel row = new(
            Descriptor(CapabilityValueKind.Choice, choices: ["quiet", "loud"]),
            new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = "loud" });

        Assert.Equal("loud", row.SelectedChoice?.Value);
    }

    [Fact]
    public void AStoredChoiceTheManifestNoLongerOffersSelectsNothing()
    {
        // Better than selecting the first option, which would silently rewrite the user's choice to
        // a different one on the next save.
        PluginSettingRowViewModel row = new(
            Descriptor(CapabilityValueKind.Choice, choices: ["quiet", "loud"]),
            new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = "gone" });

        Assert.Null(row.SelectedChoice);
    }

    [Fact]
    public void SelectingAChoicePublishesTheChoiceField()
    {
        PluginSettingRowViewModel row = new(
            Descriptor(CapabilityValueKind.Choice, choices: ["quiet", "loud"]),
            new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = "quiet" });
        CapabilityValue? edited = null;
        row.Edited += (_, value) => edited = value;

        row.SelectedChoice = row.Choices.Single(choice => choice.Value == "loud");

        Assert.NotNull(edited);
        Assert.Equal(CapabilityValueKind.Choice, edited.Kind);
        Assert.Equal("loud", edited.ChoiceValue);
        Assert.Null(edited.TextValue);
    }

    [Fact]
    public void AColorEditPublishesPackedRgbAndKeepsAControllerFriendlyHexValue()
    {
        PluginSettingRowViewModel row = new(
            Descriptor(CapabilityValueKind.Color),
            new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = 0x010203 });
        CapabilityValue? edited = null;
        row.Edited += (_, value) => edited = value;

        row.ColorHex = "#AABBCC";

        Assert.Equal("#AABBCC", row.ColorHex);
        Assert.Equal(CapabilityValueKind.Color, edited?.Kind);
        Assert.Equal(0xAABBCC, edited?.ColorValue);
    }

    [Fact]
    public void ChoiceOptionsExposeTheirValidatedDisplayLabelInsteadOfRecordToString()
    {
        PluginSettingDescriptor descriptor = Descriptor(CapabilityValueKind.Choice);
        descriptor = descriptor with
        {
            Choices =
            [
                new CapabilityChoice(
                    "machine-value",
                    new CapabilityDisplay { Key = DisplayKey.PerformanceProfile }),
            ],
        };

        PluginSettingRowViewModel row = new(
            descriptor,
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Choice,
                ChoiceValue = "machine-value",
            });

        Assert.Equal("machine-value", Assert.Single(row.Choices).Value);
        Assert.Equal("Performance profile", Assert.Single(row.Choices).Label);
    }

    [Fact]
    public void ACustomLabelIsUsedAndAKeyedOneIsNot()
    {
        PluginSettingRowViewModel custom = new(
            Descriptor(CapabilityValueKind.Boolean),
            Value(CapabilityValueKind.Boolean));

        Assert.Equal("Setting", custom.Label);
    }
}
