namespace WSGM.Tests;

public sealed class ModeSelectionTests
{
    [Fact]
    public void ExplicitShellModeHasHighestPrecedence()
    {
        var mode = Program.DecideMode(["--settings", "--overlay-test", "--SHELL"]);

        Assert.Equal(RunMode.Shell, mode);
    }

    [Fact]
    public void ExplicitSettingsModeWinsOverOverlayTest()
    {
        var mode = Program.DecideMode(["--overlay-test", "--settings"]);

        Assert.Equal(RunMode.Settings, mode);
    }

    [Fact]
    public void OverlayTestFlagSelectsTheSafeOverlaySmokeTestMode()
    {
        // The only local surface that exercises the overlay without a takeover; every
        // other test in this file passes --overlay-test as a LOSER of the precedence
        // rules, so deleting its branch would go unnoticed without this one.
        var mode = Program.DecideMode(["--OVERLAY-TEST"]);

        Assert.Equal(RunMode.OverlayTest, mode);
    }

    [Fact]
    public void NoFlagSelectsTheSafeSettingsMode()
    {
        var mode = Program.DecideMode([]);

        Assert.Equal(RunMode.Settings, mode);
    }

    [Fact]
    public void ServiceBootSelectsShellMode()
    {
        var mode = Program.DecideMode(["--BOOT"]);

        Assert.Equal(RunMode.Shell, mode);
    }

    [Fact]
    public void ServiceBootOutranksSettingsAndOverlayTest()
    {
        var mode = Program.DecideMode(["--settings", "--overlay-test", "--boot"]);

        Assert.Equal(RunMode.Shell, mode);
    }

    [Theory]
    [InlineData(new[] { "--boot" }, true)]
    [InlineData(new[] { "--BOOT", "--elevated-relaunch" }, true)]
    [InlineData(new[] { "--shell" }, false)]
    [InlineData(new string[0], false)]
    public void IsServiceBootDetectsOnlyTheBootFlag(string[] args, bool expected)
    {
        Assert.Equal(expected, Program.IsServiceBoot(args));
    }

}
