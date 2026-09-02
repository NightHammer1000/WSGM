using WSGM.Core;

namespace WSGM.Tests;

public sealed class DeviceProfileSelectionStoreTests
{
    private const string Fan = "thermal.fan-curve";

    private static PluginSettingsScope Scope() => new()
    {
        DeviceDefinitionId = "msi.claw8",
        PluginId = "wsgm.device.msi",
    };

    [Fact]
    public void ChoosingAGlobalProfileCreatesTheSelection()
    {
        PluginSettingsScope scope = Scope();

        Assert.True(DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "quiet",
            DeviceProfileScope.Global));

        Assert.Equal("quiet", scope.ProfileSelections[0].GlobalProfileId);
    }

    [Fact]
    public void ChoosingTheSameProfileAgainReportsNoChange()
    {
        PluginSettingsScope scope = Scope();
        DeviceProfileSelectionStore.SetSelection(scope, Fan, "quiet", DeviceProfileScope.Global);

        Assert.False(DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "quiet",
            DeviceProfileScope.Global));
    }

    [Fact]
    public void AnApplicationOverrideIsReadBackAsApplicationScoped()
    {
        PluginSettingsScope scope = Scope();
        DeviceProfileSelectionStore.SetSelection(scope, Fan, "quiet", DeviceProfileScope.Global);
        DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "loud",
            DeviceProfileScope.Application,
            "steam:42");

        string? read = DeviceProfileSelectionStore.ReadSelection(
            scope,
            Fan,
            "steam:42",
            out bool applicationScoped);

        Assert.Equal("loud", read);
        Assert.True(applicationScoped);
    }

    [Fact]
    public void AnotherApplicationReadsTheGlobalChoice()
    {
        PluginSettingsScope scope = Scope();
        DeviceProfileSelectionStore.SetSelection(scope, Fan, "quiet", DeviceProfileScope.Global);
        DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "loud",
            DeviceProfileScope.Application,
            "steam:42");

        string? read = DeviceProfileSelectionStore.ReadSelection(
            scope,
            Fan,
            "process:other.exe",
            out bool applicationScoped);

        Assert.Equal("quiet", read);
        Assert.False(applicationScoped);
    }

    [Fact]
    public void ClearingAnOverrideFallsBackToTheGlobalChoice()
    {
        // "This game uses the default" is what clearing an override means; there is deliberately no
        // way to express "this game uses nothing".
        PluginSettingsScope scope = Scope();
        DeviceProfileSelectionStore.SetSelection(scope, Fan, "quiet", DeviceProfileScope.Global);
        DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "loud",
            DeviceProfileScope.Application,
            "steam:42");

        Assert.True(DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            null,
            DeviceProfileScope.Application,
            "steam:42"));

        Assert.Equal(
            "quiet",
            DeviceProfileSelectionStore.ReadSelection(scope, Fan, "steam:42", out _));
    }

    [Fact]
    public void AnApplicationScopedChoiceWithNoRunningApplicationIsRefused()
    {
        // Silently widening a per-game change to every game is the worst possible reading of what
        // the user meant.
        PluginSettingsScope scope = Scope();

        Assert.False(DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "quiet",
            DeviceProfileScope.Application));

        Assert.Empty(scope.ProfileSelections);
    }

    [Fact]
    public void ClearingAChoiceThatWasNeverMadeCreatesNothing()
    {
        PluginSettingsScope scope = Scope();

        Assert.False(DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            null,
            DeviceProfileScope.Global));

        Assert.Empty(scope.ProfileSelections);
    }

    [Fact]
    public void ChangingAnExistingOverrideReplacesItRatherThanAddingASecond()
    {
        PluginSettingsScope scope = Scope();
        DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "loud",
            DeviceProfileScope.Application,
            "steam:42");
        DeviceProfileSelectionStore.SetSelection(
            scope,
            Fan,
            "quiet",
            DeviceProfileScope.Application,
            "steam:42");

        Assert.Single(scope.ProfileSelections[0].ApplicationOverrides);
        Assert.Equal("quiet", scope.ProfileSelections[0].ApplicationOverrides[0].ProfileId);
    }

    [Fact]
    public void NothingChosenReadsAsNull()
    {
        Assert.Null(DeviceProfileSelectionStore.ReadSelection(Scope(), Fan, "steam:42", out _));
    }
}
