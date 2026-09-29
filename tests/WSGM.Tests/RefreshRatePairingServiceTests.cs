using WSGM.Core;

namespace WSGM.Tests;

public sealed class RefreshRatePairingServiceTests
{
    private static readonly int[] Advertised = [60, 120];
    private static readonly int[] Accepted = [30, 48, 60, 75, 100, 120];

    [Fact]
    public void ApplyForCap_UnderCapOnly_TouchesTheDisplayAtAll()
    {
        Harness harness = new();

        Assert.Null(harness.Service.ApplyForCap(30));

        Assert.Empty(harness.Applied);
    }

    [Fact]
    public void ApplyForCap_UnderFrameDoubling_AppliesTheLowestExactMultiple()
    {
        Harness harness = new();
        harness.Service.SetStrategy(FrameLimitStrategy.FrameDoubling);

        Assert.Equal(48, harness.Service.ApplyForCap(24));

        Assert.Equal([48], harness.Applied);
    }

    [Fact]
    public void ApplyForCap_UnderNativeModes_IgnoresASynthesizedMode()
    {
        Harness harness = new();
        harness.Service.SetStrategy(FrameLimitStrategy.NativeModes);

        // 48 divides only the synthesized 48 Hz, which this strategy may not use. It still gets an
        // advertised mode that can present the cap — what it must not do is reach for the 48 Hz
        // the driver merely accepts.
        Assert.Equal(60, harness.Service.ApplyForCap(48));

        Assert.Equal([60], harness.Applied);
    }

    [Fact]
    public void ApplyForCap_DriverRefusingTheRate_ReportsNothingApplied()
    {
        Harness harness = new() { ApplySucceeds = false };
        harness.Service.SetStrategy(FrameLimitStrategy.FrameDoubling);

        Assert.Null(harness.Service.ApplyForCap(30));
    }

    [Fact]
    public void Restore_PutsBackTheRateFoundBeforeTheFirstChange()
    {
        Harness harness = new();
        harness.Service.SetStrategy(FrameLimitStrategy.FrameDoubling);
        harness.Service.ApplyForCap(30);
        harness.Service.ApplyForCap(24);

        Assert.True(harness.Service.Restore());

        // The original is captured once, so two cap changes still restore 120 rather than 60.
        Assert.Equal([60, 48, 120], harness.Applied);
    }

    [Fact]
    public void Restore_WithoutHavingChangedAnything_DoesNotTouchTheDisplay()
    {
        Harness harness = new();

        Assert.True(harness.Service.Restore());

        Assert.Empty(harness.Applied);
    }

    [Fact]
    public void FailedRestoreRetainsTheOriginalForARetry()
    {
        Harness harness = new();
        harness.Service.SetStrategy(FrameLimitStrategy.FrameDoubling);
        harness.Service.ApplyForCap(30);
        harness.ApplySucceeds = false;

        Assert.False(harness.Service.Restore());
        harness.ApplySucceeds = true;
        Assert.True(harness.Service.Restore());

        Assert.Equal([60, 120], harness.Applied);
    }

    [Fact]
    public void SetStrategy_BackToCapOnly_HandsTheDisplayBackImmediately()
    {
        Harness harness = new();
        harness.Service.SetStrategy(FrameLimitStrategy.FrameDoubling);
        harness.Service.ApplyForCap(30);

        harness.Service.SetStrategy(FrameLimitStrategy.FrameLimitOnly);

        Assert.Equal([60, 120], harness.Applied);
    }

    [Fact]
    public void Discovery_IsCachedAcrossCalls_BecauseEachRateCostsADriverRoundTrip()
    {
        Harness harness = new();
        harness.Service.SetStrategy(FrameLimitStrategy.FrameDoubling);

        harness.Service.ApplyForCap(30);
        harness.Service.ApplyForCap(60);
        _ = harness.Service.FrameLimitOptions();
        _ = harness.Service.AcceptedRates();
        harness.Service.TryApplyManual(60, capFps: 0);

        Assert.Equal(1, harness.AcceptedReads);
    }

    [Fact]
    public void TryApplyManual_UnderAPairingStrategyWithACap_IsRefused()
    {
        // The cap owns the refresh rate there; honouring the write would let the next cap change
        // silently revert it.
        Harness harness = new();
        harness.Service.SetStrategy(FrameLimitStrategy.FrameDoubling);

        Assert.False(harness.Service.TryApplyManual(60, capFps: 30));
        Assert.Empty(harness.Applied);
    }

    [Fact]
    public void TryApplyManual_WithNoCapInForce_AppliesEvenUnderAPairingStrategy()
    {
        // With the frame limit off there is no cadence to pair to, so the unified row's slider is
        // the rate itself.
        Harness harness = new();
        harness.Service.SetStrategy(FrameLimitStrategy.FrameDoubling);

        Assert.True(harness.Service.TryApplyManual(60, capFps: 0));
        Assert.Equal([60], harness.Applied);
    }

    [Fact]
    public void TryApplyManual_ARateDiscoveryDidNotAccept_NeverReachesTheDriver()
    {
        Harness harness = new();

        Assert.False(harness.Service.TryApplyManual(59, capFps: 0));
        Assert.Empty(harness.Applied);
    }

    [Fact]
    public void TryApplyManual_IsUserOwned_SoRestoreDoesNotUndoIt()
    {
        Harness harness = new();

        Assert.True(harness.Service.TryApplyManual(60, capFps: 0));
        Assert.True(harness.Service.Restore());

        Assert.Equal([60], harness.Applied);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Service = new RefreshRatePairingService(
                () =>
                {
                    AcceptedReads++;
                    return Accepted;
                },
                () => Advertised,
                rate =>
                {
                    if (!ApplySucceeds)
                    {
                        return false;
                    }

                    Applied.Add(rate);
                    Current = rate;
                    return true;
                },
                () => Current);
        }

        public RefreshRatePairingService Service { get; }

        public List<int> Applied { get; } = [];

        public int AcceptedReads { get; private set; }

        public bool ApplySucceeds { get; set; } = true;

        private int Current { get; set; } = 120;
    }
}
