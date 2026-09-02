using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests;

public class SplashPresetsTests
{
    [Fact]
    public void ClassicPresetIsExactlyTheSplashConfigDefaults()
    {
        var classic = JsonSerializer.Serialize(
            SplashPresets.Create(SplashPreset.Classic), ConfigJsonContext.Default.SplashConfig);
        var defaults = JsonSerializer.Serialize(
            new SplashConfig(), ConfigJsonContext.Default.SplashConfig);

        Assert.Equal(defaults, classic);
    }

    [Fact]
    public void EveryPresetRoundTripsThroughSourceGeneratedJsonUnchanged()
    {
        foreach (var preset in SplashPresets.All)
        {
            var original = SplashPresets.Create(preset);
            var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.SplashConfig);
            var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.SplashConfig);

            Assert.NotNull(restored);
            Assert.Equal(
                json, JsonSerializer.Serialize(restored, ConfigJsonContext.Default.SplashConfig));
        }
    }

    [Fact]
    public void PresetListCoversEveryPresetOnceWithDistinctDisplayNames()
    {
        Assert.Equal(5, SplashPresets.All.Count);
        Assert.Equal(SplashPresets.All.Count, SplashPresets.All.Distinct().Count());

        var names = SplashPresets.All.Select(SplashPresets.DisplayName).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    [Fact]
    public void PresetsMatchTheApprovedMockupValues()
    {
        var wordmark = SplashPresets.Create(SplashPreset.Wordmark);
        Assert.Equal("WSGM", wordmark.Text);
        Assert.Equal(44, wordmark.TitleFontSize);
        Assert.Equal("STARTING STEAM", wordmark.Caption);
        Assert.Equal(SplashSpinnerStyle.Ring, wordmark.SpinnerStyle);
        Assert.Equal(30, wordmark.SpinnerSize);
        Assert.Equal(SplashPlacementMode.WithText, wordmark.SpinnerPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.Center, wordmark.TextPlacement.Anchor);

        var monogram = SplashPresets.Create(SplashPreset.MonogramRing);
        Assert.Equal("#0B0B0D", monogram.BackgroundColor);
        Assert.True(monogram.VignetteEnabled);
        Assert.Equal(17, monogram.TitleFontSize);
        Assert.Equal("#5F5F5F", monogram.CaptionColor);
        Assert.Equal(10, monogram.CaptionFontSize);
        Assert.Equal("#FF9D3D", monogram.SpinnerColor);
        Assert.Equal(112, monogram.SpinnerSize);
        // Ring and text are two independently center-anchored layers, so the
        // wordmark renders inside the ring (WithText would stack them).
        Assert.Equal(SplashPlacementMode.Anchor, monogram.SpinnerPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.Center, monogram.SpinnerPlacement.Anchor);
        Assert.Equal(SplashPlacementAnchor.Center, monogram.TextPlacement.Anchor);

        var quiet = SplashPresets.Create(SplashPreset.QuietConsole);
        Assert.Equal("Starting Steam", quiet.Text);
        Assert.Equal(14, quiet.TitleFontSize);
        Assert.Equal("#CFCFCF", quiet.TextColor);
        Assert.Equal("", quiet.Caption);
        Assert.Equal("", quiet.LogoImagePath);
        Assert.Equal(20, quiet.SpinnerSize);
        Assert.Equal("#050505", quiet.BackgroundColor);
        Assert.Equal(SplashPlacementAnchor.BottomCenter, quiet.TextPlacement.Anchor);
        Assert.Equal(200, quiet.TextPlacement.PaddingY);

        var sweep = SplashPresets.Create(SplashPreset.SweepLine);
        Assert.Equal("WSGM", sweep.Text);
        Assert.Equal(40, sweep.TitleFontSize);
        Assert.Equal(SplashSpinnerStyle.SweepLine, sweep.SpinnerStyle);
        Assert.Equal("#FF9D3D", sweep.SpinnerColor);
        Assert.Equal(SweepEdge.Bottom, sweep.SweepEdge);
        Assert.Equal(SplashPlacementAnchor.Center, sweep.TextPlacement.Anchor);
    }
}
