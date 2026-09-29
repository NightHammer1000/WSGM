using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>
/// AutoTDP as Steam's own quick-access menu shows it.
/// </summary>
/// <remarks>
/// The switch is deliberately more than a boolean. A user watching the power limit move on its own
/// has to be able to tell control from a fault, so these pin what the menu says in each state rather
/// than only whether the setting is on.
/// </remarks>
public sealed class NativeQamAutoTdpTests
{
    [Fact]
    public void WithNoPowerLimitTheSwitchIsNotOfferedAtAll()
    {
        // Better absent than present and silently ineffective: there is nothing for AutoTDP to
        // drive, so offering the switch would be a promise the device cannot keep.
        NativeQamAutoTdpState state = Project(enabled: true, status: null, powerLimitAvailable: false);

        Assert.False(state.Available);
        Assert.Contains("No primary power limit", state.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchedOnBeforeTheServiceReportsAnythingSaysItIsStarting()
    {
        NativeQamAutoTdpState state = Project(enabled: true, status: null);

        Assert.True(state.Available);
        Assert.True(state.Enabled);
        Assert.False(state.Controlling);
        Assert.Equal("applying", state.Progress);
        Assert.Equal("Starting.", state.StatusText);
    }

    [Fact]
    public void SwitchedOffIsQuietRatherThanReportingAnythingToExplain()
    {
        NativeQamAutoTdpState state = Project(enabled: false, status: null);

        Assert.True(state.Available);
        Assert.False(state.Enabled);
        Assert.Empty(state.Progress);
        Assert.Empty(state.StatusText);
    }

    [Fact]
    public void ControllingCarriesTheLimitItSettledOn()
    {
        NativeQamAutoTdpState state = Project(
            enabled: true,
            new AutoTdpStatus(AutoTdpState.Controlling, 17, 14.2, 16.6, "steam:70", "sustained-miss"));

        Assert.True(state.Controlling);
        Assert.Equal(17, state.Watts);
        Assert.Equal("completed", state.Progress);
    }

    [Fact]
    public void APausedSwitchStaysOperableSoTheUserCanTurnItOff()
    {
        // Paused is a state the user caused by moving the slider. Locking the switch would leave
        // them unable to act on what they are being told.
        NativeQamAutoTdpState state = Project(
            enabled: true,
            new AutoTdpStatus(AutoTdpState.Paused, 22, null, null, null, "Paused by a manual change."));

        Assert.True(state.Available);
        Assert.False(state.Controlling);
        Assert.Equal("Paused by a manual change.", state.StatusText);
    }

    [Fact]
    public void UnavailableIsTheOneStateThatLocksTheSwitch()
    {
        // It means AutoTDP cannot run on this device however the setting is left, so operating the
        // switch could not change anything.
        NativeQamAutoTdpState state = Project(
            enabled: true,
            new AutoTdpStatus(
                AutoTdpState.Unavailable,
                null,
                null,
                null,
                null,
                "No primary power limit is available."));

        Assert.False(state.Available);
        Assert.Equal("failed", state.Progress);
    }

    [Fact]
    public void WaitingForAGameIsNotReportedAsControlling()
    {
        NativeQamAutoTdpState state = Project(
            enabled: true,
            new AutoTdpStatus(AutoTdpState.Idle, 15, null, null, null, "No application is rendering."));

        Assert.True(state.Available);
        Assert.False(state.Controlling);
        Assert.Empty(state.Progress);
    }

    [Fact]
    public void TheStoredSettingIsReportedEvenWhileTheSwitchIsLocked()
    {
        // The switch shows the setting, not the outcome. A user who turned it on and hit an
        // unsupported device should still see their own choice reflected back.
        NativeQamAutoTdpState state = Project(enabled: true, status: null, powerLimitAvailable: false);

        Assert.True(state.Enabled);
    }

    [Fact]
    public async Task TheUnavailableServiceRefusesRatherThanSilentlyAccepting()
    {
        // No device platform in this session: the service is constructed without a coordinator,
        // which is how a session with device integration off projects this row.
        using DeviceCoordinatorNativeQamAutoTdpService service = new(null, null);

        Assert.False(service.Current.Available);
        SteamUiCommandResult result = await service.SetEnabledAsync(true, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(service.Current.StatusText, result.Error);
    }

    private static NativeQamAutoTdpState Project(
        bool enabled,
        AutoTdpStatus? status,
        bool powerLimitAvailable = true) =>
        DeviceCoordinatorNativeQamAutoTdpService.Project(enabled, status, powerLimitAvailable);
}
