using WSGM.Core;

namespace WSGM.Tests;

public sealed class PerApplicationPowerPolicyTests
{
    [Fact]
    public void AnEnabledProfilePrefersItsOwnLimit()
    {
        Assert.Equal(
            21,
            PerApplicationPowerPolicy.ResolveEffective(
                globalWatts: 37,
                applicationWatts: 21,
                perGameProfileActive: true));
    }

    [Fact]
    public void ADisabledProfileInheritsTheGlobalLimit()
    {
        // The per-game switch governs every performance value: a stored application limit is dormant
        // while its profile is off, exactly like the frame limit and overlay level.
        Assert.Equal(
            37,
            PerApplicationPowerPolicy.ResolveEffective(
                globalWatts: 37,
                applicationWatts: 21,
                perGameProfileActive: false));
    }

    [Fact]
    public void AnEnabledProfileWithNoLimitOfItsOwnInheritsTheGlobalLimit()
    {
        Assert.Equal(
            37,
            PerApplicationPowerPolicy.ResolveEffective(
                globalWatts: 37,
                applicationWatts: null,
                perGameProfileActive: true));
    }

    [Fact]
    public void NoLimitAnywhereResolvesToNone()
    {
        Assert.Null(PerApplicationPowerPolicy.ResolveEffective(null, null, true));
    }

    [Fact]
    public void AResolvedLimitIsAlwaysApplied()
    {
        PerAppPowerDecision decision = PerApplicationPowerPolicy.DecideOnTargetChange(
            effectiveWatts: 21,
            powerCurrentlyImposed: false,
            autoTdpEnabled: true,
            ceilingWatts: 37);

        Assert.Equal(PerAppPowerAction.Apply, decision.Action);
        Assert.Equal(21, decision.Watts);
    }

    [Fact]
    public void NoLimitResumesAutomaticControlWhenItIsOnAndAProfileHadImposedOne()
    {
        // The reported bug's AutoTDP case: a limit set in a game paused control; leaving the game
        // with no limit preferred must resume it rather than leave the game's limit latched.
        PerAppPowerDecision decision = PerApplicationPowerPolicy.DecideOnTargetChange(
            effectiveWatts: null,
            powerCurrentlyImposed: true,
            autoTdpEnabled: true,
            ceilingWatts: 37);

        Assert.Equal(PerAppPowerAction.ResumeAutomatic, decision.Action);
    }

    [Fact]
    public void NoLimitReleasesToTheCeilingWhenAutomaticControlIsOffAndAProfileHadImposedOne()
    {
        // The reported bug's non-AutoTDP case: without automatic control there is nothing to resume,
        // so the game's limit is released to the ceiling instead of leaking onto the desktop.
        PerAppPowerDecision decision = PerApplicationPowerPolicy.DecideOnTargetChange(
            effectiveWatts: null,
            powerCurrentlyImposed: true,
            autoTdpEnabled: false,
            ceilingWatts: 37);

        Assert.Equal(PerAppPowerAction.ReleaseToCeiling, decision.Action);
        Assert.Equal(37, decision.Watts);
    }

    [Fact]
    public void NoLimitAndNothingImposedLeavesTheDeviceUntouched()
    {
        // A session that never used the feature must never have its power limit written: WSGM has no
        // limit of its own to take back, and forcing the ceiling would raise power the user did not.
        PerAppPowerDecision autoOn = PerApplicationPowerPolicy.DecideOnTargetChange(
            effectiveWatts: null,
            powerCurrentlyImposed: false,
            autoTdpEnabled: true,
            ceilingWatts: 37);
        PerAppPowerDecision autoOff = PerApplicationPowerPolicy.DecideOnTargetChange(
            effectiveWatts: null,
            powerCurrentlyImposed: false,
            autoTdpEnabled: false,
            ceilingWatts: 37);

        Assert.Equal(PerAppPowerAction.Leave, autoOn.Action);
        Assert.Equal(PerAppPowerAction.Leave, autoOff.Action);
    }

    [Fact]
    public void VrrAnEnabledProfilePrefersItsOwnState()
    {
        Assert.True(PerApplicationVrrPolicy.ResolveEffective(
            globalState: false,
            applicationState: true,
            perGameProfileActive: true));
    }

    [Fact]
    public void VrrADisabledProfileInheritsTheGlobalState()
    {
        Assert.False(PerApplicationVrrPolicy.ResolveEffective(
            globalState: false,
            applicationState: true,
            perGameProfileActive: false));
    }

    [Fact]
    public void VrrNoStateAnywhereResolvesToNone()
    {
        Assert.Null(PerApplicationVrrPolicy.ResolveEffective(null, null, true));
    }

    [Fact]
    public void VrrAResolvedStateIsAlwaysApplied()
    {
        PerAppVrrDecision decision = PerApplicationVrrPolicy.DecideOnTargetChange(
            effectiveState: true,
            stateCurrentlyImposed: false);

        Assert.Equal(PerAppVrrAction.Apply, decision.Action);
        Assert.True(decision.Enabled);
    }

    [Fact]
    public void VrrNoStateReturnsToOffWhenAProfileHadImposedOne()
    {
        // The leak this prevents: a game turned VRR on; leaving it with no state preferred returns
        // to off — Steam's own default and a fixed-refresh desktop's expectation — not on.
        PerAppVrrDecision decision = PerApplicationVrrPolicy.DecideOnTargetChange(
            effectiveState: null,
            stateCurrentlyImposed: true);

        Assert.Equal(PerAppVrrAction.Apply, decision.Action);
        Assert.False(decision.Enabled);
    }

    [Fact]
    public void VrrNoStateAndNothingImposedLeavesTheDisplayUntouched()
    {
        PerAppVrrDecision decision = PerApplicationVrrPolicy.DecideOnTargetChange(
            effectiveState: null,
            stateCurrentlyImposed: false);

        Assert.Equal(PerAppVrrAction.Leave, decision.Action);
    }
}
