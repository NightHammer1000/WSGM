using WSGM.Core;

namespace WSGM.Tests;

public sealed class FrameLimitPairingTests
{
    // What the reference Claw actually reports: the panel advertises 60 and 120, while the driver
    // accepts four more it synthesizes inside the 30-120 adaptive-sync range.
    private static readonly int[] ClawNative = [60, 120];
    private static readonly int[] ClawAccepted = [30, 48, 60, 75, 100, 120];

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void SelectRefreshHz_FrameLimitOnly_NeverTouchesTheRefreshRate(int cap)
    {
        Assert.Null(FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameLimitOnly, cap, ClawNative, ClawAccepted));
    }

    [Theory]
    [InlineData(24, 48)]
    [InlineData(25, 75)]
    [InlineData(30, 60)]
    [InlineData(40, 120)]
    [InlineData(50, 100)]
    [InlineData(60, 120)]
    public void SelectRefreshHz_FrameDoubling_TakesTheLowestDoubledMultiple(int cap, int expected)
    {
        // At least twice the cap, so adaptive sync's low-framerate compensation has a cadence to
        // work with: 30 FPS pairs with 60 Hz rather than a flickery 1:1 30 Hz, and 60 with 120.
        Assert.Equal(expected, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, cap, ClawNative, ClawAccepted));
    }

    [Fact]
    public void SelectRefreshHz_FrameDoubling_FallsBackToTheExactMultipleWhenNothingDoubles()
    {
        // Multiples of 100 beyond 100 itself exceed what the driver accepts, so the exact
        // single-cadence mode is still better than a non-multiple.
        Assert.Equal(100, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 100, ClawNative, ClawAccepted));
        Assert.Equal(120, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 120, ClawNative, ClawAccepted));
    }

    [Theory]
    [InlineData(30, 60)]
    [InlineData(60, 60)]
    [InlineData(40, 120)]
    public void SelectRefreshHz_NativeModes_UsesOnlyWhatThePanelAdvertises(int cap, int expected)
    {
        Assert.Equal(expected, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.NativeModes, cap, ClawNative, ClawAccepted));
    }

    [Fact]
    public void SelectRefreshHz_NativeModes_FallsBackToAnAdvertisedModeWhenNoneIsAnExactMultiple()
    {
        // 48 divides only the synthesized 48 Hz; neither advertised rate is a multiple of it. The
        // panel is still set to the lowest advertised mode that can present the cap, because the
        // slider names a rate for every cap and leaving 120 Hz up costs power for nothing.
        Assert.Equal(60, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.NativeModes, 48, ClawNative, ClawAccepted));

        // Frame doubling has the synthesized mode and takes the exact cadence instead.
        Assert.Equal(48, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 48, ClawNative, ClawAccepted));
    }

    [Fact]
    public void SelectRefreshHz_CapWithNoExactMultiple_TakesTheLowestModeThatCanPresentIt()
    {
        // 45 divides none of 30/48/60/75/100/120. The cap is a free number now, so every cap gets a
        // rate: the lowest one that can present it without dropping frames.
        Assert.Equal(48, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 45, ClawNative, ClawAccepted));
    }

    [Fact]
    public void SelectRefreshHz_UncappedOrAbsurdlyLowCap_LeavesRefreshAlone()
    {
        Assert.Null(FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 0, ClawNative, ClawAccepted));
        Assert.Null(FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 5, ClawNative, ClawAccepted));
    }

    [Fact]
    public void SelectRefreshHz_PanelWithTwoModesOnly_StillPairsWhatItCan()
    {
        // The Legion Go case that motivated the strategy split.
        int[] twoModes = [30, 60];

        Assert.Equal(60, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 30, twoModes, twoModes));
        Assert.Equal(60, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 20, twoModes, twoModes));

        // 40 divides neither, so it falls back to the lowest mode that can present it.
        Assert.Equal(60, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 40, twoModes, twoModes));

        // Nothing can present a cap above every mode, and that is still the one honest null.
        Assert.Null(FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 90, twoModes, twoModes));
    }

    [Fact]
    public void FrameLimitOptions_AlwaysOffersOffFirst()
    {
        foreach (FrameLimitStrategy strategy in System.Enum.GetValues<FrameLimitStrategy>())
        {
            IReadOnlyList<int> options =
                FrameLimitPairing.FrameLimitOptions(strategy, ClawNative, ClawAccepted);
            Assert.Equal(0, options[0]);
        }
    }

    [Fact]
    public void FrameLimitOptions_CoupledStrategy_PairsEveryCapItOffers()
    {
        IReadOnlyList<int> options = FrameLimitPairing.FrameLimitOptions(
            FrameLimitStrategy.FrameDoubling, ClawNative, ClawAccepted);

        // The row names a refresh rate beside every cap, so an offered cap with no rate behind it
        // would leave the label half-written.
        foreach (int cap in options.Where(cap => cap != 0))
        {
            Assert.NotNull(FrameLimitPairing.SelectRefreshHz(
                FrameLimitStrategy.FrameDoubling, cap, ClawNative, ClawAccepted));
        }
    }

    [Fact]
    public void FrameLimitOptions_CoupledStrategy_IsTheSameFreeRangeAsTheUncoupledOne()
    {
        // The strategies differ in what the cap DOES to the display, not in which caps exist. A
        // notch set derived from exact cadences was the old split-slider model.
        Assert.Equal(
            FrameLimitPairing.FrameLimitOptions(
                FrameLimitStrategy.FrameLimitOnly, ClawNative, ClawAccepted),
            FrameLimitPairing.FrameLimitOptions(
                FrameLimitStrategy.FrameDoubling, ClawNative, ClawAccepted));
    }

    [Fact]
    public void FrameLimitRange_NativeModes_IsBoundedByWhatThePanelAdvertises()
    {
        // The accepted list reaches 120, but native modes may only use what the panel advertises.
        Assert.Equal(
            (30, 90),
            FrameLimitPairing.FrameLimitRange(
                FrameLimitStrategy.NativeModes, [60, 90], ClawAccepted));
    }

    [Fact]
    public void FrameLimitRange_PanelBelowTheFloor_OffersNothing()
    {
        Assert.Null(FrameLimitPairing.FrameLimitRange(
            FrameLimitStrategy.FrameDoubling, [24], [24]));
    }

    [Fact]
    public void FrameLimitOptions_NeverExceedsTheFastestAvailableMode()
    {
        IReadOnlyList<int> options = FrameLimitPairing.FrameLimitOptions(
            FrameLimitStrategy.FrameLimitOnly, ClawNative, ClawAccepted);

        Assert.All(options, cap => Assert.True(cap <= 120));
    }

    [Fact]
    public void FrameLimitOptions_OffersEveryIntegerFromThirtyToTheCeiling()
    {
        IReadOnlyList<int> options = FrameLimitPairing.FrameLimitOptions(
            FrameLimitStrategy.FrameLimitOnly, ClawNative, ClawAccepted);

        int[] expected = [0, .. Enumerable.Range(30, 91)];
        Assert.Equal(expected, options);
    }

    [Fact]
    public void FrameLimitOptions_UncoupledStrategy_CeilingBelowThirty_OffersOnlyOff()
    {
        Assert.Equal([0], FrameLimitPairing.FrameLimitOptions(
            FrameLimitStrategy.FrameLimitOnly, [24], [24]));
    }

    [Fact]
    public void FrameLimitOptions_NoUsableModes_OffersOnlyOff()
    {
        Assert.Equal([0], FrameLimitPairing.FrameLimitOptions(
            FrameLimitStrategy.FrameDoubling, [], []));
    }

    [Fact]
    public void RefreshRateIsUserOwned_OnlyWhenNothingElseIsMovingIt()
    {
        Assert.True(FrameLimitPairing.RefreshRateIsUserOwned(FrameLimitStrategy.FrameLimitOnly));
        Assert.False(FrameLimitPairing.RefreshRateIsUserOwned(FrameLimitStrategy.NativeModes));
        Assert.False(FrameLimitPairing.RefreshRateIsUserOwned(FrameLimitStrategy.FrameDoubling));
    }
}
