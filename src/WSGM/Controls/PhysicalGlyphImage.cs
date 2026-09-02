using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace WSGM.Controls;

/// <summary>
/// Draws one resolved physical glyph: the plugin's own artwork, scaled to fit.
/// </summary>
/// <remarks>
/// A drawing rather than an image control, because there is nothing to load. The SDK's package
/// loader has already turned the plugin's SVG into a normalized path model, and
/// <see cref="PhysicalGlyphService"/> has already turned that into Avalonia geometry — so rendering
/// is a transform and a fill, with no parser, decoder, or external SVG library in the resident
/// application.
/// <para>
/// Nothing here reaches for a profile, a package or a file. The plan is supplied, and a plan that
/// carries no artwork draws nothing, which is how a device with no glyph profile renders as blank
/// space rather than as a broken image.
/// </para>
/// </remarks>
internal sealed class PhysicalGlyphImage : Control
{
    /// <summary>The resolved glyph to draw.</summary>
    public static readonly StyledProperty<PhysicalGlyphRenderPlan?> PlanProperty =
        AvaloniaProperty.Register<PhysicalGlyphImage, PhysicalGlyphRenderPlan?>(nameof(Plan));

    /// <summary>Colour used for paths whose fill or stroke is <c>currentColor</c>.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<PhysicalGlyphImage>();

    private Bitmap? _raster;
    private ReadOnlyMemory<byte> _rasterSource;

    static PhysicalGlyphImage()
    {
        AffectsRender<PhysicalGlyphImage>(PlanProperty, ForegroundProperty);
        AffectsMeasure<PhysicalGlyphImage>(PlanProperty);
    }

    /// <summary>Gets or sets the resolved glyph to draw.</summary>
    public PhysicalGlyphRenderPlan? Plan
    {
        get => GetValue(PlanProperty);
        set => SetValue(PlanProperty, value);
    }

    /// <summary>Gets or sets the colour used for <c>currentColor</c> paths.</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Raster plans own native bitmap memory. Glyph-preview rebuilds replace controls, so detach is
    /// the deterministic release boundary.
    /// </remarks>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ReleaseRaster();
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A plan that no longer draws a raster has no use for the decoded bitmap either.
        if (change.Property == PlanProperty
            && change.GetNewValue<PhysicalGlyphRenderPlan?>()?.RasterPng.IsEmpty is not false)
        {
            ReleaseRaster();
        }
    }

    private void ReleaseRaster()
    {
        _raster?.Dispose();
        _raster = null;
        _rasterSource = default;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        PhysicalGlyphRenderPlan? plan = Plan;
        if (plan is null || !plan.UsesDeviceArtwork)
        {
            return;
        }

        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        if (plan.Paths.Count == 0)
        {
            RenderRaster(context, plan, bounds);
            return;
        }

        if (plan.ViewBox is not { } viewBox || viewBox.Width <= 0 || viewBox.Height <= 0)
        {
            return;
        }

        // Uniform, centred: a glyph stretched to a non-square row would read as a different symbol.
        double scale = Math.Min(
            bounds.Width / (double)viewBox.Width,
            bounds.Height / (double)viewBox.Height);
        double offsetX = (bounds.Width - ((double)viewBox.Width * scale)) / 2;
        double offsetY = (bounds.Height - ((double)viewBox.Height * scale)) / 2;
        Matrix transform = Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation(
                offsetX - ((double)viewBox.X * scale),
                offsetY - ((double)viewBox.Y * scale));

        using DrawingContext.PushedState _ = context.PushTransform(transform);
        IBrush? foreground = Foreground;
        foreach (PhysicalGlyphPath path in plan.Paths)
        {
            IBrush? fill = Resolve(path.Fill, foreground);
            IBrush? stroke = Resolve(path.Stroke, foreground);
            if (fill is null && stroke is null)
            {
                continue;
            }

            // Stroke thickness is in the glyph's own coordinates, which the transform above is
            // already in — so it scales with the artwork rather than staying a constant hairline.
            Pen? pen = stroke is null || path.StrokeWidth <= 0
                ? null
                : new Pen(
                    stroke,
                    (double)path.StrokeWidth,
                    lineCap: LineCapFor(path.StrokeLineCap),
                    lineJoin: LineJoinFor(path.StrokeLineJoin));
            context.DrawGeometry(fill, pen, path.Geometry);
        }
    }

    private static PenLineCap LineCapFor(string token) => token switch
    {
        "round" => PenLineCap.Round,
        "square" => PenLineCap.Square,
        _ => PenLineCap.Flat,
    };

    private static PenLineJoin LineJoinFor(string token) => token switch
    {
        "round" => PenLineJoin.Round,
        "bevel" => PenLineJoin.Bevel,
        _ => PenLineJoin.Miter,
    };

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        PhysicalGlyphRenderPlan? plan = Plan;
        if (plan?.ViewBox is not { } viewBox || viewBox.Width <= 0 || viewBox.Height <= 0)
        {
            return default;
        }

        // The natural size is the glyph's own, so a row that does not constrain the control still
        // lays out sensibly; a constrained one keeps the aspect ratio through Render's uniform fit.
        double width = (double)viewBox.Width;
        double height = (double)viewBox.Height;
        if (double.IsInfinity(availableSize.Width) && double.IsInfinity(availableSize.Height))
        {
            return new Size(width, height);
        }

        double scale = Math.Min(
            double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width / width,
            double.IsInfinity(availableSize.Height) ? double.MaxValue : availableSize.Height / height);
        return new Size(width * scale, height * scale);
    }

    private void RenderRaster(DrawingContext context, PhysicalGlyphRenderPlan plan, Rect bounds)
    {
        Bitmap? bitmap = RasterFor(plan.RasterPng);
        if (bitmap is null)
        {
            return;
        }

        Size source = bitmap.Size;
        if (source.Width <= 0 || source.Height <= 0)
        {
            return;
        }

        double scale = Math.Min(bounds.Width / source.Width, bounds.Height / source.Height);
        double width = source.Width * scale;
        double height = source.Height * scale;
        context.DrawImage(
            bitmap,
            new Rect(source),
            new Rect((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height));
    }

    /// <summary>Decodes the PNG once and keeps it only while the same bytes are being drawn.</summary>
    /// <param name="png">The exact bytes from the plan.</param>
    /// <returns>The decoded bitmap, or null when the bytes are absent or undecodable.</returns>
    /// <remarks>
    /// Keyed on the memory itself rather than on a hash: a plan's bytes are a slice of the imported
    /// asset and never change in place, so equality of the slice is equality of the image. A failed
    /// decode is remembered as a null bitmap for those bytes so the failure costs one attempt rather
    /// than one per frame — the asset was validated at import, so a failure here means a corrupt
    /// package, not something a retry can fix.
    /// </remarks>
    private Bitmap? RasterFor(ReadOnlyMemory<byte> png)
    {
        if (png.IsEmpty)
        {
            return null;
        }

        if (_raster is not null && _rasterSource.Equals(png))
        {
            return _raster;
        }

        _raster?.Dispose();
        _raster = null;
        _rasterSource = png;
        try
        {
            using MemoryStream stream = new(png.ToArray(), writable: false);
            _raster = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Glyph raster could not be decoded: {ex.Message}");
        }

        return _raster;
    }

    /// <summary>Turns a canonical SVG paint token into a brush.</summary>
    /// <param name="token">The normalized token: <c>none</c>, <c>currentColor</c>, or a hex colour.</param>
    /// <param name="foreground">The brush <c>currentColor</c> resolves to.</param>
    /// <returns>The brush, or null for no paint.</returns>
    /// <remarks>
    /// The normalizer guarantees the token is one of those three, so an unparseable colour here is a
    /// contract break rather than untrusted input — it draws nothing instead of guessing a colour.
    /// </remarks>
    private static IBrush? Resolve(string token, IBrush? foreground)
    {
        if (string.IsNullOrEmpty(token) || string.Equals(token, "none", StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(token, "currentColor", StringComparison.Ordinal))
        {
            return foreground;
        }

        return Color.TryParse(token, out Color color) ? new SolidColorBrush(color) : null;
    }
}
