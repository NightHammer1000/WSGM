using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WSGM.Controls;

/// <summary>
/// The shared card-row button used by WSGM's quick access panel and other
/// list-style surfaces: a vector icon, a title with an optional description
/// caption, and optional trailing text (for example a controller glyph hint).
/// It derives from <see cref="Button"/> so Click handlers, x:Name references,
/// tab order, and gamepad navigation keep working unchanged; the visuals live
/// in the CardButton ControlTheme (Themes\CardButtonTheme.axaml) and carry the
/// established quick-row look, including the border-based focus visual and the
/// <c>primary</c>/<c>danger</c> style classes.
/// </summary>
public class CardButton : Button
{
    /// <summary>
    /// Resolves the <c>ControlTheme</c> for this type and every type derived from it.
    /// </summary>
    /// <remarks>
    /// Avalonia looks a <c>ControlTheme</c> up by the control's actual runtime type, and the theme
    /// is keyed <c>{x:Type c:CardButton}</c>. Without this override a subclass finds no theme, gets
    /// no template, and lays out at zero size — present in the tree, counted by the parent, drawing
    /// nothing.
    /// <para>
    /// That is not hypothetical: every row on the overlay's Device page is a
    /// <c>DescriptorStatusRow</c>, so the page rendered its cards and showed an empty panel under
    /// the heading while the log correctly reported six rows and sixteen live capabilities. It
    /// presents as missing data, which is why it survived several passes looking at the data.
    /// </para>
    /// </remarks>
    protected override Type StyleKeyOverride => typeof(CardButton);

    /// <summary>
    /// Defines the <see cref="IconGeometry"/> property.
    /// </summary>
    public static readonly StyledProperty<Geometry?> IconGeometryProperty =
        AvaloniaProperty.Register<CardButton, Geometry?>(nameof(IconGeometry));

    /// <summary>
    /// Defines the <see cref="Title"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<CardButton, string?>(nameof(Title));

    /// <summary>
    /// Defines the <see cref="Description"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<CardButton, string?>(nameof(Description));

    /// <summary>
    /// Defines the <see cref="TrailingText"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> TrailingTextProperty =
        AvaloniaProperty.Register<CardButton, string?>(nameof(TrailingText));

    /// <summary>
    /// Defines the <see cref="TrailingGlyph"/> property.
    /// </summary>
    /// <remarks>
    /// Internal, unlike the rest of this control's properties, because the render plan it carries is
    /// internal: the glyph pipeline is not a public API and nothing outside this assembly should be
    /// handing a card one. The XAML template resolves it because it compiles into this assembly.
    /// </remarks>
    internal static readonly StyledProperty<PhysicalGlyphRenderPlan?> TrailingGlyphProperty =
        AvaloniaProperty.Register<CardButton, PhysicalGlyphRenderPlan?>(nameof(TrailingGlyph));

    /// <summary>
    /// Defines the <see cref="StatusBrush"/> property.
    /// </summary>
    /// <summary>Defines the <see cref="SwatchBrush"/> property.</summary>
    public static readonly StyledProperty<IBrush?> SwatchBrushProperty =
        AvaloniaProperty.Register<CardButton, IBrush?>(nameof(SwatchBrush));

    /// <summary>
    /// A live color swatch drawn in the icon slot — an RGB zone's current color — or null to show
    /// <see cref="IconGeometry"/>. Callers set exactly one of the two.
    /// </summary>
    public IBrush? SwatchBrush
    {
        get => GetValue(SwatchBrushProperty);
        set => SetValue(SwatchBrushProperty, value);
    }

    public static readonly StyledProperty<IBrush?> StatusBrushProperty =
        AvaloniaProperty.Register<CardButton, IBrush?>(nameof(StatusBrush));

    /// <summary>
    /// Defines the <see cref="IsPinned"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsPinnedProperty =
        AvaloniaProperty.Register<CardButton, bool>(nameof(IsPinned));

    /// <summary>
    /// Gets or sets the vector icon rendered at the left edge of the card,
    /// stroked with the button's foreground brush. The icon slot collapses
    /// when this is null.
    /// </summary>
    public Geometry? IconGeometry
    {
        get => GetValue(IconGeometryProperty);
        set => SetValue(IconGeometryProperty, value);
    }

    /// <summary>
    /// Gets or sets the primary label of the card (the setting-title line).
    /// Two-step confirmation flows may rewrite this at runtime, exactly like
    /// the previous per-row title TextBlocks.
    /// </summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Gets or sets the caption line rendered under the title. The caption
    /// collapses when this is null or empty.
    /// </summary>
    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>
    /// Gets or sets the semibold trailing text at the right edge of the card
    /// (for example the "A" activation hint). The trailing slot collapses when
    /// this is null or empty.
    /// </summary>
    public string? TrailingText
    {
        get => GetValue(TrailingTextProperty);
        set => SetValue(TrailingTextProperty, value);
    }

    /// <summary>
    /// Gets or sets the device's own glyph for the activation hint, drawn instead of
    /// <see cref="TrailingText"/> when one resolved.
    /// </summary>
    /// <remarks>
    /// The text is the fallback rather than the default: a machine with no glyph profile, or one
    /// whose active input is not the managed handheld, still shows the letter. Setting this never
    /// removes the text — the two share the trailing slot and exactly one is visible — so clearing
    /// it restores the letter without the caller having to remember what it was.
    /// </remarks>
    internal PhysicalGlyphRenderPlan? TrailingGlyph
    {
        get => GetValue(TrailingGlyphProperty);
        set => SetValue(TrailingGlyphProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="ShowTrailingText"/> property.
    /// </summary>
    public static readonly DirectProperty<CardButton, bool> ShowTrailingTextProperty =
        AvaloniaProperty.RegisterDirect<CardButton, bool>(
            nameof(ShowTrailingText),
            button => button.ShowTrailingText);

    /// <summary>
    /// Gets whether the trailing letter is the thing being shown in the trailing slot.
    /// </summary>
    /// <remarks>
    /// Computed rather than bound with a converter because it depends on two properties at once,
    /// which is the case a <c>TemplateBinding</c> converter cannot express. The glyph wins when it
    /// resolved, so the two never draw over each other.
    /// </remarks>
    public bool ShowTrailingText =>
        TrailingGlyph is null && !string.IsNullOrEmpty(TrailingText);

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TrailingGlyphProperty || change.Property == TrailingTextProperty)
        {
            RaisePropertyChanged(ShowTrailingTextProperty, !ShowTrailingText, ShowTrailingText);
        }
    }

    /// <summary>
    /// Gets or sets the fill of the small status dot rendered before the trailing
    /// text (a state indicator, for example the wake-lock colors). The dot slot
    /// collapses when this is null.
    /// </summary>
    public IBrush? StatusBrush
    {
        get => GetValue(StatusBrushProperty);
        set => SetValue(StatusBrushProperty, value);
    }

    /// <summary>
    /// Gets or sets whether this card's original location shows the Quick access pin marker.
    /// </summary>
    /// <remarks>
    /// The marker is presentation state only. The overlay owns the persisted pin list and updates
    /// this value immediately when the user toggles a row.
    /// </remarks>
    public bool IsPinned
    {
        get => GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }
}
