using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class PerformancePolicyPersistenceTests
{
    [Fact]
    public void RtssPolicyMergePreservesNonRtssGlobalAndApplicationFields()
    {
        PerformanceConfig config = new()
        {
            FrameLimitStrategy = FrameLimitStrategy.FrameDoubling,
            TdpWatts = 22,
            VariableRefreshRate = true,
            Applications =
            [
                new PerformanceApplicationConfig
                {
                    ApplicationId = "steam:42",
                    RtssProfileName = "old.exe",
                    FrameLimit = 40,
                    OverlayLevel = 1,
                    UsePerGameProfile = true,
                    TdpWatts = 18,
                    VariableRefreshRate = false,
                },
            ],
        };
        PerformancePolicy rtss = new(
            new PerformanceValues(60, 3),
            [new PerformanceApplicationPolicy(
                "steam:42",
                "game.exe",
                new PerformanceValues(45, 2))],
            Enabled: true);

        ShellSession.MergePerformancePolicy(config, rtss);

        Assert.True(config.Enabled);
        Assert.Equal(60, config.FrameLimit);
        Assert.Equal(3, config.OverlayLevel);
        Assert.Equal(FrameLimitStrategy.FrameDoubling, config.FrameLimitStrategy);
        Assert.Equal(22, config.TdpWatts);
        Assert.True(config.VariableRefreshRate);
        PerformanceApplicationConfig application = Assert.Single(config.Applications);
        Assert.Equal("game.exe", application.RtssProfileName);
        Assert.Equal(45, application.FrameLimit);
        Assert.Equal(2, application.OverlayLevel);
        Assert.True(application.UsePerGameProfile);
        Assert.Equal(18, application.TdpWatts);
        Assert.False(application.VariableRefreshRate);
    }

    [Fact]
    public void RemovingAnActiveRtssPolicyRetainsItsDisabledStoredProfile()
    {
        PerformanceConfig config = new()
        {
            Applications =
            [
                new PerformanceApplicationConfig
                {
                    ApplicationId = "steam:42",
                    RtssProfileName = "game.exe",
                    FrameLimit = 40,
                    OverlayLevel = 1,
                    UsePerGameProfile = true,
                    TdpWatts = 18,
                    VariableRefreshRate = false,
                },
            ],
        };

        ShellSession.MergePerformancePolicy(
            config,
            new PerformancePolicy(new PerformanceValues(60, 3), [], Enabled: true));

        PerformanceApplicationConfig application = Assert.Single(config.Applications);
        Assert.False(application.UsePerGameProfile);
        Assert.Equal(40, application.FrameLimit);
        Assert.Equal(1, application.OverlayLevel);
        Assert.Equal(18, application.TdpWatts);
        Assert.False(application.VariableRefreshRate);
    }
}
