using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using FluentAvalonia.Styling;

namespace WSGM.Themes;

/// <summary>Runtime accent-color pipeline. Parses the configured accent string and
/// applies it to the running application: FluentAvalonia regenerates its accent
/// shades via <c>CustomAccentColor</c>, and the <c>Hc*</c> accent resource family in
/// <c>Palette.axaml</c> is shadowed in <c>Application.Resources</c> so every
/// DynamicResource consumer re-resolves live.</summary>
public static class AccentPalette
{
    /// <summary>The default WSGM accent (Handheld Companion orange), used when the
    /// configured value is missing or unparsable. The single source for the accent
    /// digits — the RGB-only forms below are derived from it.</summary>
    public const string DefaultAccent = "#FFFF9D3D";

    /// <summary>The default accent as a <c>#RRGGBB</c> string, for the splash
    /// contract whose colors carry no alpha channel.</summary>
    public static readonly string DefaultAccentRgbHex = "#" + DefaultAccent[3..];

    /// <summary>The default accent's packed RGB value, for the numeric color the
    /// device-profile contract stores.</summary>
    public static readonly int DefaultAccentRgb = Convert.ToInt32(DefaultAccent[3..], 16);

    /// <summary>Parses a configured accent color string. The result is always
    /// fully opaque (see <see cref="ForceOpaque"/>): an #AARRGGBB value keeps its
    /// RGB but drops its alpha.</summary>
    /// <param name="value">The configured color text (e.g. "#FF9D3D"), or null.</param>
    /// <returns>The parsed color forced opaque, or the default accent when the value is missing or invalid.</returns>
    public static Color Parse(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Color.TryParse(value, out var color))
        {
            return ForceOpaque(color);
        }
        return Color.Parse(DefaultAccent);
    }

    /// <summary>Applies the accent color to the application's theme and accent
    /// resources. The accent is normalized to full opacity first (see
    /// <see cref="ForceOpaque"/>).</summary>
    /// <param name="app">The running Avalonia application.</param>
    /// <param name="accent">The accent color to apply.</param>
    public static void Apply(Application app, Color accent)
    {
        accent = ForceOpaque(accent);
        var theme = app.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();
        if (theme is not null)
        {
            theme.CustomAccentColor = accent;
        }

        var onAccent = UseBlackForeground(accent) ? Colors.Black : Colors.White;
        var onAccentCaption = new Color(0xCC, onAccent.R, onAccent.G, onAccent.B);

        app.Resources["HcAccentBrush"] = new ImmutableSolidColorBrush(accent);
        app.Resources["HcOnAccentBrush"] = new ImmutableSolidColorBrush(onAccent);
        app.Resources["HcOnAccentCaptionBrush"] = new ImmutableSolidColorBrush(onAccentCaption);
    }

    /// <summary>Normalizes an accent to full opacity (A = 255), keeping its RGB.
    /// A translucent global accent is never rendered as-composited anyway, and a
    /// low-alpha value would let <see cref="UseBlackForeground"/> (which reads raw
    /// RGB) pick an unreadable on-accent foreground — so the applied accent is
    /// always opaque.</summary>
    internal static Color ForceOpaque(Color accent) => new(0xFF, accent.R, accent.G, accent.B);

    /// <summary>Decides whether black text is more legible than white on the given
    /// accent. Black wins when its WCAG contrast ratio against the accent exceeds
    /// white's, which reduces to relative luminance &gt; 0.1791.</summary>
    internal static bool UseBlackForeground(Color accent) => RelativeLuminance(accent) > 0.1791;

    /// <summary>WCAG relative luminance of an sRGB color (0 = black, 1 = white).</summary>
    internal static double RelativeLuminance(Color color)
    {
        return (0.2126 * Linearize(color.R)) + (0.7152 * Linearize(color.G)) + (0.0722 * Linearize(color.B));
    }

    private static double Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
