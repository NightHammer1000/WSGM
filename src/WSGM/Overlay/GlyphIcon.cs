using System;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>Renders a controller button glyph from the bundled Kenney CC0 SVGs.
/// The SVGs are simple single/multi &lt;path fill d&gt; files, so they are parsed
/// directly into Avalonia geometry, avoiding a second SVG rendering dependency.
/// Button names are by LABEL ("a" shows the style's A/Cross art); the confirm
/// action always displays "a" — for Nintendo the INPUT mapping swaps instead
/// (see GamepadNavigation), so the labeled-A button confirms in every style.</summary>
public sealed partial class GlyphIcon : ContentControl
{
    /// <summary>Defines the Avalonia property that selects the controller-glyph family.</summary>
    public static readonly StyledProperty<GlyphStyle> GlyphStyleProperty =
        AvaloniaProperty.Register<GlyphIcon, GlyphStyle>(nameof(GlyphStyle));

    /// <summary>Defines the Avalonia property that selects the labeled button glyph.</summary>
    public static readonly StyledProperty<string> ButtonProperty =
        AvaloniaProperty.Register<GlyphIcon, string>(nameof(Button), "a");

    // Attributes are matched per <path> tag, order-independent: a re-exported or
    // optimized SVG may put d before fill.
    [GeneratedRegex("<path\\b[^>]*>", RegexOptions.Singleline)]
    private static partial Regex PathRegex();

    [GeneratedRegex("\\bfill=\"(?<fill>[^\"]+)\"")]
    private static partial Regex FillRegex();

    [GeneratedRegex("\\bd=\"(?<data>[^\"]+)\"", RegexOptions.Singleline)]
    private static partial Regex DataRegex();

    /// <summary>Gets or sets the controller-glyph family to render.</summary>
    public GlyphStyle GlyphStyle
    {
        get => GetValue(GlyphStyleProperty);
        set => SetValue(GlyphStyleProperty, value);
    }

    /// <summary>Gets or sets the labeled controller button to render.</summary>
    public string Button
    {
        get => GetValue(ButtonProperty);
        set => SetValue(ButtonProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GlyphStyleProperty || change.Property == ButtonProperty)
        {
            Rebuild();
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Content is null)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var styleName = GlyphStyle switch
        {
            GlyphStyle.PlayStation => "playstation",
            GlyphStyle.Nintendo => "nintendo",
            _ => "xbox",
        };

        try
        {
            var uri = new Uri($"avares://WSGM/Assets/Glyphs/{styleName}/{Button}.svg");
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            var svg = reader.ReadToEnd();

            var canvas = new Canvas { Width = 64, Height = 64 };
            foreach (Match tag in PathRegex().Matches(svg))
            {
                var data = DataRegex().Match(tag.Value);
                if (!data.Success)
                {
                    continue;
                }
                var fill = FillRegex().Match(tag.Value);
                canvas.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    // Default fill rule (EvenOdd) turns inner subpaths (letters,
                    // symbols) into holes — matching how these SVGs are drawn.
                    Data = Geometry.Parse(data.Groups["data"].Value),
                    Fill = new SolidColorBrush(fill.Success ? Color.Parse(fill.Groups["fill"].Value) : Colors.White),
                });
            }

            if (canvas.Children.Count == 0)
            {
                // Would otherwise render silently empty (no exception, so the
                // catch-block fallback never runs).
                Log.Warn($"Glyph {styleName}/{Button}: no <path> data parsed — using text fallback.");
                Content = new TextBlock { Text = Button.ToUpperInvariant() };
                return;
            }

            Content = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load glyph {styleName}/{Button}", ex);
            Content = new TextBlock { Text = Button.ToUpperInvariant() };
        }
    }
}
