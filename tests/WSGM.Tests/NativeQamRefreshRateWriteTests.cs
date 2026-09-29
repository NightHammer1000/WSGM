using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class NativeQamRefreshRateWriteTests
{
    private static PerformanceServiceNativeQamAdapter Adapter(Func<int, bool>? applyRefresh)
    {
        PerformanceService service = new(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask,
            PerformancePolicy.Empty);
        return new PerformanceServiceNativeQamAdapter(service) { ApplyRefreshRate = applyRefresh };
    }

    [Fact]
    public async Task ARefreshRateChangeReachesTheDisplay()
    {
        List<int> applied = [];
        PerformanceServiceNativeQamAdapter adapter = Adapter(hz =>
        {
            applied.Add(hz);
            return true;
        });

        SteamUiCommandResult result = await adapter.ApplyPerfChangeAsync(
            new NativeQamPerfChange(NativeQamPerfSetting.RefreshRateHz, 60),
            "test",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(60, Assert.Single(applied));
    }

    [Fact]
    public async Task ADisplayThatRefusesTheRateIsReportedAsAFailure()
    {
        PerformanceServiceNativeQamAdapter adapter = Adapter(_ => false);

        SteamUiCommandResult result = await adapter.ApplyPerfChangeAsync(
            new NativeQamPerfChange(NativeQamPerfSetting.RefreshRateHz, 48),
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("48", result.Error);
    }

    [Fact]
    public async Task WithNoApplierTheChangeIsRefusedByNameRatherThanDropped()
    {
        // Under the pairing strategies the frame cap owns the refresh rate, so the session supplies
        // no applier and the write must say so rather than appear to succeed.
        PerformanceServiceNativeQamAdapter adapter = Adapter(null);

        SteamUiCommandResult result = await adapter.ApplyPerfChangeAsync(
            new NativeQamPerfChange(NativeQamPerfSetting.RefreshRateHz, 60),
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("RefreshRateHz", result.Error);
    }

    [Fact]
    public async Task AnUnbackedSettingIsStillRefusedByName()
    {
        // AdvancedSettingsEnabled is the last performance setting with no WSGM backend: it is
        // Steam's own Basic/Advanced view state, which the store holds and WSGM does not drive.
        // This test has already had to move twice as settings were implemented — if it moves again,
        // check whether anything is genuinely unbacked before repointing it rather than deleting
        // the coverage, because the refusal path is what keeps a dead control from looking alive.
        PerformanceServiceNativeQamAdapter adapter = Adapter(_ => true);

        SteamUiCommandResult result = await adapter.ApplyPerfChangeAsync(
            new NativeQamPerfChange(NativeQamPerfSetting.AdvancedSettingsEnabled, 1),
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("AdvancedSettingsEnabled", result.Error);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task AVrrToggleReachesTheDeviceWithTheRequestedState(int value, bool expected)
    {
        List<bool> applied = [];
        PerformanceServiceNativeQamAdapter adapter = Adapter(null);
        adapter.ApplyVariableRefreshRate = (enabled, _) =>
        {
            applied.Add(enabled);
            return Task.FromResult(true);
        };

        SteamUiCommandResult result = await adapter.ApplyPerfChangeAsync(
            new NativeQamPerfChange(NativeQamPerfSetting.VariableRefreshRate, value),
            "test",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, Assert.Single(applied));
    }

    [Fact]
    public async Task ADeviceThatRefusesVrrIsNotReportedAsApplied()
    {
        // Steam's toggle is controlled, so reporting success before the device answered would show
        // it moved and then snap it back on the next publish.
        PerformanceServiceNativeQamAdapter adapter = Adapter(null);
        adapter.ApplyVariableRefreshRate = (_, _) => Task.FromResult(false);

        SteamUiCommandResult result = await adapter.ApplyPerfChangeAsync(
            new NativeQamPerfChange(NativeQamPerfSetting.VariableRefreshRate, 1),
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("variable refresh rate", result.Error);
    }

    [Fact]
    public async Task WithNoDeviceVrrIsRefusedByNameRatherThanDropped()
    {
        PerformanceServiceNativeQamAdapter adapter = Adapter(null);

        SteamUiCommandResult result = await adapter.ApplyPerfChangeAsync(
            new NativeQamPerfChange(NativeQamPerfSetting.VariableRefreshRate, 1),
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("VariableRefreshRate", result.Error);
    }
}
