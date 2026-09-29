using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class RunningApplicationCoordinatorTests
{
    [Fact]
    public void ProjectMapsOnlyActiveTruthfulExecutableTarget()
    {
        var snapshot = Snapshot(
            RunningApplicationTargetState.Active,
            "steam:42",
            "game.exe");

        PerformanceApplicationTarget? target = RunningApplicationCoordinator.Project(snapshot);

        Assert.Equal("steam:42", target?.ApplicationId);
        Assert.Equal((uint)42, target?.SteamAppId);
        Assert.Equal("game.exe", target?.RtssProfileName);
    }

    [Fact]
    public void ProjectRetainsIdentityOnlySteamTargetWithoutAnExecutable()
    {
        var snapshot = Snapshot(
            RunningApplicationTargetState.IdentityOnly,
            "steam:42",
            null);

        PerformanceApplicationTarget? target = RunningApplicationCoordinator.Project(snapshot);

        Assert.Equal("steam:42", target?.ApplicationId);
        Assert.Equal((uint)42, target?.SteamAppId);
        Assert.Null(target?.RtssProfileName);
    }

    [Fact]
    public void ProjectClearsTargetWhenApplicationIdentityIsNotAuthoritative()
    {
        RunningApplicationTargetState[] states =
        [
            RunningApplicationTargetState.Global,
            RunningApplicationTargetState.Ambiguous,
            RunningApplicationTargetState.Unavailable,
        ];
        foreach (RunningApplicationTargetState state in states)
        {
            var snapshot = Snapshot(state, "steam:42", "stale.exe");
            Assert.Null(RunningApplicationCoordinator.Project(snapshot));
        }
    }

    [Theory]
    [InlineData(null, "game.exe")]
    [InlineData("", "game.exe")]
    public void ProjectRejectsIncompleteActiveTarget(string? applicationId, string? profileName)
    {
        var snapshot = Snapshot(
            RunningApplicationTargetState.Active,
            applicationId,
            profileName);

        Assert.Null(RunningApplicationCoordinator.Project(snapshot));
    }

    private static RunningApplicationTargetSnapshot Snapshot(
        RunningApplicationTargetState state,
        string? applicationId,
        string? profileName) => new(
            1,
            1,
            state,
            applicationId,
            42,
            profileName is null ? null : $"C:\\Games\\{profileName}",
            profileName,
            DateTimeOffset.UnixEpoch,
            null);
}
