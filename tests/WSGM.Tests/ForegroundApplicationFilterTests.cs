using WSGM.Core;

namespace WSGM.Tests;

public sealed class ForegroundApplicationFilterTests
{
    [Theory]
    [InlineData("forza.exe")]
    [InlineData("Cyberpunk2077.exe")]
    public void Classify_AnOrdinaryApplication_DrivesPerApplicationPolicy(string executable)
    {
        Assert.Equal(
            ForegroundApplicationKind.Application,
            ForegroundApplicationFilter.Classify(executable));
    }

    [Theory]
    [InlineData("wsgm.exe")]
    [InlineData("WSGM.exe")]
    [InlineData("steam.exe")]
    // Big Picture's renderer, and the foreground process at the exact moment Steam reports a
    // launch: treating it as an application paired it as the running game's executable.
    [InlineData("steamwebhelper.exe")]
    [InlineData("EpicGamesLauncher.exe")]
    [InlineData("explorer.exe")]
    [InlineData("taskmgr.exe")]
    [InlineData("StartMenuExperienceHost.exe")]
    [InlineData("consent.exe")]
    public void Classify_FocusStealingFurniture_LeavesTheProfileAlone(string executable)
    {
        // Restricted is not "no application": the running game's profile must stay in force, or
        // opening the overlay would change the power limit underneath the game being played.
        Assert.Equal(
            ForegroundApplicationKind.Restricted,
            ForegroundApplicationFilter.Classify(executable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_AnUnresolvableWindow_IsRestrictedRatherThanGuessed(string? executable)
    {
        // A window whose process could not be read is exactly where guessing attaches the wrong
        // profile to a running game.
        Assert.Equal(
            ForegroundApplicationKind.Restricted,
            ForegroundApplicationFilter.Classify(executable));
    }

    [Fact]
    public void Classify_IsCaseInsensitive_BecauseWindowsPathsAre()
    {
        Assert.Equal(
            ForegroundApplicationFilter.Classify("EXPLORER.EXE"),
            ForegroundApplicationFilter.Classify("explorer.exe"));
    }

    [Fact]
    public void Classify_IgnoresSurroundingWhitespace()
    {
        Assert.Equal(
            ForegroundApplicationKind.Restricted,
            ForegroundApplicationFilter.Classify(" taskmgr.exe "));
    }
}
