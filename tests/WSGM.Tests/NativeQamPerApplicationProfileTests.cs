using System.Text.Json;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class NativeQamPerApplicationProfileTests
{
    [Fact]
    public async Task SteamHeaderKeepsTheAppIdBeforeTheExecutableIsKnown()
    {
        await using PerformanceService service = Service();
        await service.SetTargetAsync(
            new PerformanceApplicationTarget("steam:42", 42, null));
        PerformanceServiceNativeQamAdapter adapter = new(service);

        NativeQamPerfState global = adapter.PerfState;

        Assert.Equal("42", global.CurrentGameId);
        Assert.Equal("769", global.ActiveProfileGameId);
        Assert.False(global.PerApp?.IsGamePerfProfileEnabled);

        Assert.True(await service.SetApplicationProfileEnabledAsync(true));
        NativeQamPerfState perApplication = adapter.PerfState;
        Assert.Equal("42", perApplication.CurrentGameId);
        Assert.Equal("42", perApplication.ActiveProfileGameId);
        Assert.True(perApplication.PerApp?.IsGamePerfProfileEnabled);
    }

    [Fact]
    public async Task DeltaForAnApplicationThatIsNoLongerCurrentIsRefused()
    {
        await using PerformanceService service = Service();
        await service.SetTargetAsync(
            new PerformanceApplicationTarget("steam:42", 42, "current.exe"));
        PerformanceServiceNativeQamAdapter adapter = new(service);
        using JsonDocument payload = JsonDocument.Parse(
            """{"delta":{"gameid":41,"settings_delta":{"per_app":{"is_game_perf_profile_enabled":true}}}}""");
        SteamUiBridgeRequest request = new(
            SteamUiBridgeHost.SchemaVersion,
            "request",
            "wsgm.native-qam.performance",
            "updateSettings",
            1,
            1,
            1,
            1,
            payload.RootElement.Clone());

        SteamUiCommandResult result = await adapter.HandlePerformanceDeltaAsync(
            request,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("stale AppID 41", result.Error);
        Assert.False(service.Current.ApplicationProfileEnabled);
    }

    private static PerformanceService Service() => new(
        new SimulatedRtssAdapter(),
        static (_, _) => Task.CompletedTask,
        new PerformancePolicy(new PerformanceValues(60, 1), []));
}
