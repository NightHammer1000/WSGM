using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class KeepAwakeTests
{
    // ---- KeepAwakeService.NextDownloadHold: hold/release policy ----

    [Fact]
    public void FirstActiveSampleAcquiresImmediately()
    {
        var (hold, streak) = KeepAwakeService.NextDownloadHold(currentHold: false, inactiveStreak: 0, sampleActive: true);

        Assert.True(hold);
        Assert.Equal(0, streak);
    }

    [Fact]
    public void ActiveSampleResetsAnExistingInactiveStreak()
    {
        var (hold, streak) = KeepAwakeService.NextDownloadHold(currentHold: true, inactiveStreak: 1, sampleActive: true);

        Assert.True(hold);
        Assert.Equal(0, streak);
    }

    [Fact]
    public void SingleInactivePollKeepsTheHold()
    {
        var (hold, streak) = KeepAwakeService.NextDownloadHold(currentHold: true, inactiveStreak: 0, sampleActive: false);

        Assert.True(hold);
        Assert.Equal(1, streak);
    }

    [Fact]
    public void ConsecutiveInactivePollsReleaseTheHold()
    {
        var (hold1, streak1) = KeepAwakeService.NextDownloadHold(currentHold: true, inactiveStreak: 0, sampleActive: false);
        var (hold2, streak2) = KeepAwakeService.NextDownloadHold(hold1, streak1, sampleActive: false);

        Assert.False(hold2);
        Assert.Equal(KeepAwakeService.ReleaseAfterInactivePolls, streak2);
    }

    [Fact]
    public void InactiveSamplesWithoutAHoldNeverAcquire()
    {
        var state = (Hold: false, InactiveStreak: 0);
        for (var i = 0; i < 5; i++)
        {
            state = KeepAwakeService.NextDownloadHold(state.Hold, state.InactiveStreak, sampleActive: false);
            Assert.False(state.Hold);
        }

        // The streak is capped, so an arbitrarily long idle phase cannot overflow it.
        Assert.Equal(KeepAwakeService.ReleaseAfterInactivePolls, state.InactiveStreak);
    }

    [Fact]
    public void HoldReacquiresAfterAReleaseWhenActivityResumes()
    {
        var state = KeepAwakeService.NextDownloadHold(currentHold: true, inactiveStreak: 1, sampleActive: false);
        Assert.False(state.Hold);

        state = KeepAwakeService.NextDownloadHold(state.Hold, state.InactiveStreak, sampleActive: true);

        Assert.True(state.Hold);
        Assert.Equal(0, state.InactiveStreak);
    }

    // ---- SteamDownloads.Parse: CEF payloads ----

    [Fact]
    public void ParseReadsAnActiveDownload()
    {
        var overview = SteamDownloads.Parse(
            """{"state":"Downloading","paused":false,"appid":3280350,"bps":24162405}""");

        Assert.NotNull(overview);
        Assert.True(overview!.Value.Active);
        Assert.Equal("Downloading", overview.Value.State);
        Assert.Equal(3280350, overview.Value.AppId);
        Assert.Equal(24162405, overview.Value.NetworkBytesPerSecond);
    }

    [Fact]
    public void ParseTreatsStateNoneAsInactive()
    {
        var overview = SteamDownloads.Parse(
            """{"state":"None","paused":false,"appid":0,"bps":0}""");

        Assert.NotNull(overview);
        Assert.False(overview!.Value.Active);
    }

    [Fact]
    public void ParseTreatsAPausedQueueAsInactive()
    {
        var overview = SteamDownloads.Parse(
            """{"state":"Downloading","paused":true,"appid":42,"bps":0}""");

        Assert.NotNull(overview);
        Assert.False(overview!.Value.Active);
        Assert.True(overview.Value.Paused);
    }

    [Fact]
    public void ParseReturnsNullForErrorPayloads()
        => Assert.Null(SteamDownloads.Parse("""{"err":"timeout"}"""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void ParseReturnsNullForUnusablePayloads(string? json)
        => Assert.Null(SteamDownloads.Parse(json));

    [Fact]
    public void ParseDefaultsMissingFieldsToInactive()
    {
        var overview = SteamDownloads.Parse("{}");

        Assert.NotNull(overview);
        Assert.False(overview!.Value.Active);
        Assert.Equal("", overview.Value.State);
        Assert.Equal(0, overview.Value.AppId);
    }

    [Theory]
    [InlineData("Downloading", false, true)]
    [InlineData("Starting", false, true)]
    [InlineData("Stopping", false, true)]
    [InlineData("Downloading", true, false)]
    [InlineData("None", false, false)]
    [InlineData("", false, false)]
    public void IsActiveRequiresARealUnpausedState(string state, bool paused, bool expected)
        => Assert.Equal(expected, SteamDownloads.IsActive(state, paused));

    [Fact]
    public void ResolveActivity_ReachableIdleSnapshot_EndsKnownActivity()
    {
        var overview = new DownloadOverview(false, "None", false, 0, 0);

        Assert.False(SteamDownloads.ResolveActivity(currentActive: true, steamAlive: true, overview));
    }

    [Fact]
    public void ResolveActivity_UnreachableLiveClient_DoesNotInventDownloadCompletion()
    {
        Assert.True(SteamDownloads.ResolveActivity(
            currentActive: true,
            steamAlive: true,
            overview: null));
    }

    [Fact]
    public void ResolveActivity_DeadSteamClient_EndsKnownActivity()
    {
        Assert.False(SteamDownloads.ResolveActivity(
            currentActive: true,
            steamAlive: false,
            overview: null));
    }

    // ---- PowerTimeouts: preset cycling and labels ----

    [Theory]
    [InlineData(60, 180)]      // preset -> next preset
    [InlineData(3600, 0)]      // longest preset -> Never
    [InlineData(0, 60)]        // Never wraps to the shortest
    [InlineData(120, 180)]     // custom value snaps to the next longer preset
    [InlineData(7200, 0)]      // custom beyond the longest preset -> Never
    public void NextPresetCyclesLongerThenNeverThenWraps(int current, int expected)
        => Assert.Equal(expected, PowerTimeouts.NextPreset(current));

    [Fact]
    public void NextPresetVisitsEveryPresetExactlyOncePerLap()
    {
        var seen = new HashSet<int>();
        var value = 60;
        do
        {
            Assert.True(seen.Add(value));
            value = PowerTimeouts.NextPreset(value);
        }
        while (value != 60);

        Assert.Equal(PowerTimeouts.PresetsSeconds.Length, seen.Count);
    }

    [Theory]
    [InlineData(0, "Never")]
    [InlineData(60, "1 min")]
    [InlineData(300, "5 min")]
    [InlineData(1800, "30 min")]
    [InlineData(3600, "1 h")]
    [InlineData(5400, "1.5 h")]
    // A sub-minute timeout is reachable (powercfg takes seconds) and must never
    // round to "0 min", which would read as "off".
    [InlineData(1, "<1 min")]
    [InlineData(29, "<1 min")]
    [InlineData(59, "<1 min")]
    public void DescribeFormatsTimeoutsForTheBadge(int seconds, string expected)
        => Assert.Equal(expected, PowerTimeouts.Describe(seconds));
}
