using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The built-in boot-splash presets offered by the Appearance page.</summary>
internal enum SplashPreset
{
    /// <summary>The classic default look (black, white "Please wait", ring spinner).</summary>
    Classic,

    /// <summary>Large centered "WSGM" wordmark with a small ring spinner.</summary>
    Wordmark,

    /// <summary>Small wordmark inside a large accent ring on a near-black background.</summary>
    MonogramRing,

    /// <summary>Minimal dim status line near the bottom of the screen.</summary>
    QuietConsole,

    /// <summary>Wordmark with an accent sweep-line along the bottom edge.</summary>
    SweepLine,
}

/// <summary>Factories for the built-in splash presets. A preset only fills a
/// <see cref="SplashConfig"/> with starting values — every field stays ordinary
/// user-editable configuration afterwards, and no preset fabricates image assets.</summary>
internal static class SplashPresets
{
    /// <summary>All presets in picker display order.</summary>
    internal static readonly IReadOnlyList<SplashPreset> All =
    [
        SplashPreset.Classic,
        SplashPreset.Wordmark,
        SplashPreset.MonogramRing,
        SplashPreset.QuietConsole,
        SplashPreset.SweepLine,
    ];

    /// <summary>Human-readable name for the preset combo box.</summary>
    internal static string DisplayName(SplashPreset preset) => preset switch
    {
        SplashPreset.Classic => "Classic",
        SplashPreset.Wordmark => "Wordmark",
        SplashPreset.MonogramRing => "Monogram ring",
        SplashPreset.QuietConsole => "Quiet console",
        SplashPreset.SweepLine => "Sweep line",
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
    };

    /// <summary>Creates a fresh <see cref="SplashConfig"/> filled with the preset's
    /// default values.</summary>
    internal static SplashConfig Create(SplashPreset preset) => preset switch
    {
        SplashPreset.Classic => Classic(),
        SplashPreset.Wordmark => Wordmark(),
        SplashPreset.MonogramRing => MonogramRing(),
        SplashPreset.QuietConsole => QuietConsole(),
        SplashPreset.SweepLine => SweepLine(),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
    };

    /// <summary>The classic default look — exactly the <see cref="SplashConfig"/> defaults.</summary>
    internal static SplashConfig Classic() => new();

    /// <summary>Black background, large centered "WSGM" title with a "STARTING STEAM"
    /// caption and a small white ring spinner riding the text stack.</summary>
    internal static SplashConfig Wordmark() => new()
    {
        Text = "WSGM",
        TitleFontSize = 44,
        TextColor = "#FFFFFF",
        Caption = "STARTING STEAM",
        CaptionColor = "#666666",
        CaptionFontSize = 12,
        SpinnerStyle = SplashSpinnerStyle.Ring,
        SpinnerColor = "#FFFFFF",
        SpinnerSize = 30,
        BackgroundColor = "#000000",
        TextPlacement = new SplashElementPlacement
        {
            Mode = SplashPlacementMode.Anchor,
            Anchor = SplashPlacementAnchor.Center,
        },
        SpinnerPlacement = new SplashElementPlacement { Mode = SplashPlacementMode.WithText },
    };

    /// <summary>Near-black vignetted background with a small "WSGM" mark and a large
    /// accent-orange ring spinner drawn around the centered text block.</summary>
    internal static SplashConfig MonogramRing() => new()
    {
        Text = "WSGM",
        TitleFontSize = 17,
        TextColor = "#FFFFFF",
        Caption = "STARTING STEAM",
        CaptionColor = "#5F5F5F",
        // 10 (not 12) so the caption's width clears the ring's inner circle at
        // the caption's off-center height — 12 px would clip the ring's edge.
        CaptionFontSize = 10,
        SpinnerStyle = SplashSpinnerStyle.Ring,
        SpinnerColor = "#FF9D3D",
        SpinnerSize = 112,
        BackgroundColor = "#0B0B0D",
        VignetteEnabled = true,
        TextPlacement = new SplashElementPlacement
        {
            Mode = SplashPlacementMode.Anchor,
            Anchor = SplashPlacementAnchor.Center,
        },
        // Both center-ANCHORED (not WithText, which would stack the ring below
        // the text): anchor-mode elements are independent layers, so the two
        // centered elements overlap and the ring draws around the wordmark.
        SpinnerPlacement = new SplashElementPlacement
        {
            Mode = SplashPlacementMode.Anchor,
            Anchor = SplashPlacementAnchor.Center,
        },
    };

    /// <summary>Minimal quiet look: a dim "Starting Steam" line with a tiny ring
    /// spinner, anchored toward the bottom of an almost-black screen. (The mockup's
    /// corner brand mark needs a user-supplied logo image; presets never fabricate one.)</summary>
    internal static SplashConfig QuietConsole() => new()
    {
        Text = "Starting Steam",
        TitleFontSize = 14,
        TextColor = "#CFCFCF",
        Caption = "",
        SpinnerStyle = SplashSpinnerStyle.Ring,
        SpinnerColor = "#CFCFCF",
        SpinnerSize = 20,
        BackgroundColor = "#050505",
        TextPlacement = new SplashElementPlacement
        {
            Mode = SplashPlacementMode.Anchor,
            Anchor = SplashPlacementAnchor.BottomCenter,
            PaddingY = 200,
        },
        SpinnerPlacement = new SplashElementPlacement { Mode = SplashPlacementMode.WithText },
    };

    /// <summary>Black background, centered "WSGM" wordmark, and an accent-orange
    /// sweep-line spinner traveling along the bottom edge.</summary>
    internal static SplashConfig SweepLine() => new()
    {
        Text = "WSGM",
        TitleFontSize = 40,
        TextColor = "#FFFFFF",
        Caption = "STARTING STEAM",
        CaptionColor = "#5F5F5F",
        CaptionFontSize = 12,
        SpinnerStyle = SplashSpinnerStyle.SweepLine,
        SpinnerColor = "#FF9D3D",
        SweepEdge = SweepEdge.Bottom,
        BackgroundColor = "#000000",
        TextPlacement = new SplashElementPlacement
        {
            Mode = SplashPlacementMode.Anchor,
            Anchor = SplashPlacementAnchor.Center,
        },
    };
}
