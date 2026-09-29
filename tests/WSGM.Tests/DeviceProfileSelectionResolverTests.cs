using WSGM.Core;

namespace WSGM.Tests;

public sealed class DeviceProfileSelectionResolverTests
{
    private const string Fan = "thermal.fan-curve";

    private static DeviceAuthoredProfile Profile(string id) => new()
    {
        ProfileId = id,
        Name = id,
        CapabilityId = Fan,
        Curve = [new AuthoredCurvePoint { Input = 0, Output = 10 }],
    };

    private static DeviceProfileSelection Selection(
        string? global,
        params (string Application, string Profile)[] overrides) => new()
        {
            CapabilityId = Fan,
            GlobalProfileId = global,
            ApplicationOverrides = [.. overrides.Select(entry =>
                new DeviceApplicationProfileSelection
                {
                    ApplicationId = entry.Application,
                    ProfileId = entry.Profile,
                })],
        };

    [Fact]
    public void TheGlobalChoiceAppliesWhenNoApplicationOverridesIt()
    {
        DeviceProfileResolution resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("quiet")],
            [Profile("quiet")],
            Fan,
            "steam:42");

        Assert.Equal("quiet", resolution.Profile?.ProfileId);
        Assert.False(resolution.ApplicationScoped);
    }

    [Fact]
    public void AnApplicationOverrideOutranksTheGlobalChoice()
    {
        DeviceProfileResolution resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("quiet", ("steam:42", "loud"))],
            [Profile("quiet"), Profile("loud")],
            Fan,
            "steam:42");

        Assert.Equal("loud", resolution.Profile?.ProfileId);
        Assert.True(resolution.ApplicationScoped);
    }

    [Fact]
    public void AnotherApplicationStillGetsTheGlobalChoice()
    {
        DeviceProfileResolution resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("quiet", ("steam:42", "loud"))],
            [Profile("quiet"), Profile("loud")],
            Fan,
            "process:game.exe");

        Assert.Equal("quiet", resolution.Profile?.ProfileId);
    }

    [Fact]
    public void NoRunningApplicationUsesTheGlobalChoice()
    {
        DeviceProfileResolution resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("quiet", ("steam:42", "loud"))],
            [Profile("quiet"), Profile("loud")],
            Fan,
            null);

        Assert.Equal("quiet", resolution.Profile?.ProfileId);
    }

    [Fact]
    public void NoSelectionAtAllLeavesTheCapabilityAlone()
    {
        // Inventing a choice would take the capability away from whatever else drives it.
        DeviceProfileResolution resolution = DeviceProfileSelectionStore.Resolve(
            [],
            [Profile("quiet")],
            Fan,
            "steam:42");

        Assert.Null(resolution.Profile);
        Assert.Null(resolution.Diagnostic);
    }

    [Fact]
    public void ASelectionForAnotherCapabilityIsNotUsed()
    {
        DeviceProfileResolution resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("quiet")],
            [Profile("quiet")],
            "lighting.color",
            null);

        Assert.Null(resolution.Profile);
    }

    [Fact]
    public void AnApplicationOverrideNamingADeletedProfileIsReportedNotDowngraded()
    {
        // Falling back to the global profile would hide that the user's intent for this application
        // is gone, and the fans would quietly run someone else's curve.
        DeviceProfileResolution resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("quiet", ("steam:42", "deleted"))],
            [Profile("quiet")],
            Fan,
            "steam:42");

        Assert.Null(resolution.Profile);
        Assert.True(resolution.ApplicationScoped);
        Assert.Contains("deleted", resolution.Diagnostic);
        Assert.Contains("steam:42", resolution.Diagnostic);
    }

    [Fact]
    public void AGlobalSelectionNamingADeletedProfileIsReported()
    {
        DeviceProfileResolution resolution = DeviceProfileSelectionStore.Resolve(
            [Selection("gone")],
            [Profile("quiet")],
            Fan,
            null);

        Assert.Null(resolution.Profile);
        Assert.Contains("gone", resolution.Diagnostic);
    }

    [Fact]
    public void ASelectionReferencesTheProfileSoEditsPropagate()
    {
        // By id, never by copy: editing a profile has to change every application already using it.
        DeviceAuthoredProfile profile = Profile("quiet");
        DeviceProfileSelection selection = Selection("quiet", ("steam:42", "quiet"));

        profile.Curve = [new AuthoredCurvePoint { Input = 40, Output = 80 }];
        DeviceProfileResolution resolution = DeviceProfileSelectionStore.Resolve(
            [selection],
            [profile],
            Fan,
            "steam:42");

        Assert.Equal(80, resolution.Profile?.Curve[0].Output);
    }
}
