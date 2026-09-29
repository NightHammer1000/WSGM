using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LoadingIndicators.Avalonia;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The borderless startup splash shown while Steam Big Picture launches.
/// Its whole look (background, vignette, text, spinner, logo, placements) comes
/// from a <see cref="SplashConfig"/>; elements that are disabled or fail to load
/// are omitted from the visual tree entirely.</summary>
public partial class BootSplashWindow : Window
{
    /// <summary>Raised when the user chooses the desktop fallback.</summary>
    public event Action? DesktopRequested;

    /// <summary>The control gamepad navigation should land on for the first D-pad
    /// press. Nothing is focused on open, so a stray A press activates nothing.</summary>
    internal InputElement DefaultFocusTarget => DesktopButton;

    private const double SweepPeriodMs = 1600;
    private const double SweepLineThickness = 3;

    /// <summary>Bottom margin of the bottom-edge sweep line, keeping it clear of
    /// the desktop button (which occupies roughly the bottom 68 px on the right).</summary>
    private const double SweepBottomClearance = 88;

    /// <summary>DPI headroom multiplied into the logo's decode cap.
    ///
    /// <c>LogoMaxSize</c> bounds the Image in DIPs, but the renderer draws it in
    /// PHYSICAL pixels — <c>DIP * DesktopScaling</c>. Decoding to the DIP number
    /// alone therefore starves every scaled display by exactly the scale factor
    /// and Avalonia upscales the shortfall, which is visibly soft. Two paths run
    /// above 1.0: the Settings preview on a normal desktop (commonly 150%), and
    /// the first boot-splash frames — the splash is raised BEFORE the 100%
    /// game-mode posture is applied, and re-covers itself on the display change.
    /// <see cref="BuildStyledContent"/> also runs from the constructor, before
    /// <c>Opened</c>/<see cref="CoverPrimaryScreen"/>, so NO DPI is known at
    /// decode time; a fixed headroom is what makes the decode DPI-independent
    /// (re-decoding after Opened would put a second image decode, and a second
    /// failure mode, on the boot path for no visual gain).
    ///
    /// 5 is the largest display scale WSGM supports: <see cref="DisplayScale"/>
    /// offers 100-500% and rejects anything outside it. (The 3.0 the overlay,
    /// taskbar and volume OSD clamp is a different number — the ratio between the
    /// desktop DPI and the CURRENT one, bounding how far their touch targets may be
    /// blown up — not a supported-DPI ceiling, and a headroom of 3 left every logo
    /// above 300% upscaled and soft.)
    ///
    /// The memory win that motivated the cap survives: the decode stays bounded by
    /// <c>LogoMaxSize * 5</c> per rendered edge, i.e. 25x the DIP-sized buffer, not
    /// the source's own resolution. Default 200 DIP -> 1000 px cap -> at most
    /// 1000*1000*4 B = 4 MB. Per-edge bounding alone stops binding at the extreme
    /// end (a 4096 DIP logo puts the cap past <see cref="ImageHeader.MaxDimension"/>),
    /// which is why the TOTAL output area is bounded as well — see
    /// <see cref="LogoDecodePixelBudget"/>.</summary>
    private const int LogoDecodeDpiHeadroom = 5;

    /// <summary>Widest the background is ever decoded to, in PHYSICAL pixels.
    ///
    /// The background is a fullscreen <see cref="Stretch.UniformToFill"/> cover, so
    /// only the panel it covers can be rendered: WSGM's supported range runs from a
    /// 1280x800 floor to handheld panels that top out well under 4K (2560x1600 on the
    /// largest of them). 2560 is that ceiling; beyond it the extra columns are
    /// resampled straight back down.</summary>
    private const int BackgroundDecodeMaxWidth = 2560;

    /// <summary>Total OUTPUT pixels the background decode may produce —
    /// 2560x1600 = 4.1 MP, the widest supported panel at its own aspect ratio,
    /// i.e. exactly the pixels a full cover of that display can show.
    ///
    /// Why a pixel budget and not just <see cref="BackgroundDecodeMaxWidth"/>: a
    /// width cap alone does not bind on a TALL source. A 2000x20000 background is
    /// inside every <see cref="ImageHeader"/> limit and already under 2560 wide, so a
    /// width-capped decode never scales it at all and allocates 40 MP (~160 MB at
    /// 4 B/px) synchronously on the boot path — for an image UniformToFill then crops
    /// nearly all the height off. Bounding the OUTPUT area instead makes the cost of
    /// a background independent of its aspect ratio: ~16 MB worst case, whatever the
    /// source declares.
    ///
    /// Deliberately not generous: this is a cover behind a splash, not detail work,
    /// and it is decoded before the game-mode posture is applied. Landscape sources —
    /// every realistic background — stay on the old path, because any aspect ratio at
    /// or wider than 16:10 hits <see cref="BackgroundDecodeMaxWidth"/> first.</summary>
    private const int BackgroundDecodePixelBudget = BackgroundDecodeMaxWidth * 1600;

    /// <summary>Absolute ceiling on ANY splash element's decoded output area, in
    /// pixels — the pixels a full cover of the widest supported panel can show
    /// (<see cref="BackgroundDecodePixelBudget"/>).
    ///
    /// The splash is ONE window covering ONE screen, so no element inside it can
    /// present more pixels than that cover: decoding past this ceiling buys detail
    /// the compositor resamples straight back down. It exists because the
    /// element-derived budget (<see cref="LogoDecodePixelBudget"/>) stops binding at
    /// the top of the configurable range — at the largest logo bound ConfigStore
    /// allows, 4096 DIP, the derived budget is 20480x20480 = 419 MP, far above what
    /// <see cref="ImageHeader"/> admits at all — so without it a theme-supplied
    /// <c>LogoMaxSize</c> would reinstate exactly the unbounded allocation the budget
    /// exists to prevent, on the boot path, at every sign-in.</summary>
    private const int SplashDecodePixelCeiling = BackgroundDecodePixelBudget;

    private readonly SplashConfig _splash;
    private readonly bool _preview;
    private readonly RotateTransform _spinnerRotate = new();
    private readonly TranslateTransform _sweepTransform = new();
    private readonly List<(Control Control, SplashElementPlacement Placement)> _absoluteElements = [];

    private Arc? _ringSpinner;
    private Border? _sweepLine;
    private Panel? _sweepHost;
    private Canvas? _absoluteCanvas;
    private Bitmap? _backgroundBitmap;
    private Bitmap? _logoBitmap;
    private DispatcherTimer? _spinnerTimer;
    private DispatcherTimer? _fadeTimer;
    private DateTime _spinnerStartedUtc;
    private DateTime _fadeStartedUtc;
    private TimeSpan _fadeDuration;
    private Action? _fadeDone;
    private nint _hwnd;

    /// <summary>XAML-designer/default constructor: classic look, boot behavior.</summary>
    public BootSplashWindow()
        : this(new SplashConfig(), preview: false)
    {
    }

    /// <summary>Creates the splash window styled by <paramref name="splash"/>.</summary>
    /// <param name="splash">The splash customization to render. Must not be null.</param>
    /// <param name="preview">True when shown as a Settings preview: Escape and any
    /// pointer press close the window; all rendering behavior stays identical.</param>
    public BootSplashWindow(SplashConfig splash, bool preview = false)
    {
        ArgumentNullException.ThrowIfNull(splash);
        _splash = splash;
        _preview = preview;
        InitializeComponent();
        Background = new SolidColorBrush(SplashStyle.ParseColor(splash.BackgroundColor, Colors.Black));
        BuildStyledContent();
        Opened += OnOpened;
        Closed += (_, _) =>
        {
            StopTimers();
            // Bitmaps are disposed strictly AFTER the timers stopped — a late tick
            // must never touch a disposed source.
            _backgroundBitmap?.Dispose();
            _backgroundBitmap = null;
            _logoBitmap?.Dispose();
            _logoBitmap = null;
        };

        // Touch pass-through defense, same as OverlayWindow: Avalonia never marks
        // touch raw events handled, so WM_POINTER falls to DefWindowProc, which
        // promotes a tap into a delayed synthesized mouse click. Eat those here so
        // a splash tap can never land on whatever the splash was covering.
        Win32Properties.AddWndProcHookCallback(
            this,
            Interop.NativeMethods.SwallowTouchSynthesizedMouse);

        if (_preview)
        {
            KeyDown += OnPreviewKeyDown;
            PointerPressed += OnPreviewPointerPressed;
        }
    }


    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e) => Close();

    /// <summary>Builds the configured visual tree. Every element is optional and
    /// only ever ADDED when enabled and loadable — disabled/broken elements are
    /// left out of the tree entirely (StackPanel Spacing would otherwise gap
    /// around invisible children). The desktop button stays the last child of
    /// <c>RootPanel</c> so it remains topmost.</summary>
    private void BuildStyledContent()
    {
        var textBrush = new SolidColorBrush(SplashStyle.ParseColor(_splash.TextColor, Colors.White));
        var captionBrush = new SolidColorBrush(SplashStyle.ParseColor(_splash.CaptionColor, Color.Parse("#666666")));
        var spinnerBrush = new SolidColorBrush(SplashStyle.ParseColor(_splash.SpinnerColor, Colors.White));

        // Background image (below everything else).
        var backgroundImageLoaded = false;
        if (!string.IsNullOrWhiteSpace(_splash.BackgroundImagePath))
        {
            _backgroundBitmap = TryLoadBitmap(_splash.BackgroundImagePath, BackgroundDecodeWidth);
            if (_backgroundBitmap is not null)
            {
                AddLayer(new Image { Source = _backgroundBitmap, Stretch = Stretch.UniformToFill });
                backgroundImageLoaded = true;
            }
        }

        // Vignette (darkens edges, above the background, below the elements).
        if (_splash.VignetteEnabled)
        {
            AddLayer(CreateVignette());
        }

        // Logo.
        Control? logo = null;
        if (!string.IsNullOrWhiteSpace(_splash.LogoImagePath))
        {
            var maxSize = Math.Max(1, _splash.LogoMaxSize);
            _logoBitmap = TryLoadBitmap(
                _splash.LogoImagePath,
                (sourceWidth, sourceHeight) => LogoDecodeWidth(maxSize, sourceWidth, sourceHeight));
            if (_logoBitmap is not null)
            {
                logo = new Image
                {
                    Source = _logoBitmap,
                    Stretch = Stretch.Uniform,
                    MaxWidth = maxSize,
                    MaxHeight = maxSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
            }
        }

        // Spinner. SweepLine is edge-hosted and ignores SpinnerPlacement; the
        // other styles produce a control placed like any element.
        Control? spinner = null;
        var spinnerSize = Math.Max(1, _splash.SpinnerSize);
        switch (_splash.SpinnerStyle)
        {
            case SplashSpinnerStyle.Off:
                break;
            case SplashSpinnerStyle.Ring:
                _ringSpinner = new Arc
                {
                    Width = spinnerSize,
                    Height = spinnerSize,
                    Stroke = spinnerBrush,
                    StrokeThickness = 3,
                    StartAngle = 0,
                    SweepAngle = 270,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    RenderTransform = _spinnerRotate,
                };
                spinner = _ringSpinner;
                break;
            case SplashSpinnerStyle.SweepLine:
                BuildSweepLine(spinnerBrush);
                break;
            default:
                spinner = new LoadingIndicator
                {
                    Mode = MapIndicatorMode(_splash.SpinnerStyle),
                    Foreground = spinnerBrush,
                    Width = spinnerSize,
                    Height = spinnerSize,
                    IsActive = true,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                break;
        }

        // Text stack: logo above title, caption under title, spinner below the
        // caption — WithText elements ride this stack (Spacing 26, classic look).
        var captionVisible = _splash.TextEnabled && !string.IsNullOrEmpty(_splash.Caption);
        var stack = new StackPanel { Spacing = 26 };
        if (logo is not null && _splash.LogoPlacement.Mode == SplashPlacementMode.WithText)
        {
            stack.Children.Add(logo);
        }
        if (_splash.TextEnabled && !string.IsNullOrEmpty(_splash.Text))
        {
            stack.Children.Add(new TextBlock
            {
                Text = _splash.Text,
                FontSize = Math.Max(1, _splash.TitleFontSize),
                FontWeight = FontWeight.Light,
                Foreground = textBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        if (captionVisible)
        {
            stack.Children.Add(new TextBlock
            {
                Text = _splash.Caption,
                FontSize = Math.Max(1, _splash.CaptionFontSize),
                Foreground = captionBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        if (spinner is not null && _splash.SpinnerPlacement.Mode == SplashPlacementMode.WithText)
        {
            stack.Children.Add(spinner);
        }
        if (stack.Children.Count > 0)
        {
            PlaceElement(stack, _splash.TextPlacement);
        }

        // Independently placed spinner/logo (Anchor or Absolute modes).
        if (spinner is not null && _splash.SpinnerPlacement.Mode != SplashPlacementMode.WithText)
        {
            PlaceElement(spinner, _splash.SpinnerPlacement);
        }
        if (logo is not null && _splash.LogoPlacement.Mode != SplashPlacementMode.WithText)
        {
            PlaceElement(logo, _splash.LogoPlacement);
        }

        Log.Info(
            $"Splash style: bg={_splash.BackgroundColor}, bgImage={(backgroundImageLoaded ? "yes" : "no")}, " +
            $"logo={(logo is not null ? "yes" : "no")}, text={(_splash.TextEnabled ? "on" : "off")}, " +
            $"caption={(captionVisible ? "on" : "off")}, spinner={_splash.SpinnerStyle} {spinnerSize}px, " +
            $"textPlacement={DescribePlacement(_splash.TextPlacement)}, preview={_preview}");
    }

    /// <summary>The logo's decode cap in PHYSICAL pixels (what gets rendered), from
    /// its bound in DIPs (what bounds the layout): <c>maxSize * LogoDecodeDpiHeadroom</c>.
    /// Computed in <see cref="long"/> and clamped to <see cref="ImageHeader.MaxDimension"/>
    /// so an unclamped preview config (SettingsViewModel builds a
    /// <see cref="SplashConfig"/> without ConfigStore's 1..4096 clamp) can neither
    /// overflow nor produce a cap larger than any source ImageHeader accepts — beyond
    /// that width the cap could never bind anyway. Downscale-only decides the rest: a
    /// source narrower than the cap is decoded at its own size, never stretched up to
    /// it.</summary>
    /// <param name="logoMaxSizeDips">The configured logo bound in DIPs.</param>
    /// <returns>The largest edge length, in pixels, the logo may be decoded to.</returns>
    internal static int LogoDecodeCap(int logoMaxSizeDips) =>
        (int)Math.Min((long)Math.Max(1, logoMaxSizeDips) * LogoDecodeDpiHeadroom, ImageHeader.MaxDimension);

    /// <summary>Total OUTPUT pixels the logo decode may produce, for the configured
    /// logo bound: the area of the <see cref="LogoDecodeCap"/> square the logo is
    /// fitted into — i.e. it follows from <c>LogoMaxSize</c> and the 100-500% DPI
    /// headroom, NOT from any display size — and never more than
    /// <see cref="SplashDecodePixelCeiling"/>.
    ///
    /// Why an area budget on top of the per-edge cap: the cap alone bounds only the
    /// decodes that actually go through <c>Bitmap.DecodeToWidth</c>. A source
    /// NARROWER than the cap is decoded whole, and from <c>LogoMaxSize</c> 2000 up the
    /// cap sits at or past <see cref="ImageHeader.MaxDimension"/>, so nearly every
    /// <see cref="ImageHeader"/>-admissible source slipped through unscaled: measured
    /// worst case 79,995,136 px (~305 MiB) at 2000 and 80,000,000 px at 4096. Since
    /// <c>LogoMaxSize</c> is carried by a shared <c>.wsgmsplash</c> theme, an untrusted
    /// file dictated that allocation on the BOOT path at every sign-in. Bounding the
    /// output AREA makes the cost of a logo independent of both its aspect ratio and
    /// its declared resolution: at most <see cref="SplashDecodePixelCeiling"/> px
    /// (~16 MB at 4 B/px) for any configurable bound, and the default 200 DIP logo
    /// keeps its own far smaller 1000x1000 = 1 MP (~4 MB) budget.</summary>
    /// <param name="logoMaxSizeDips">The configured logo bound in DIPs.</param>
    /// <returns>The largest number of pixels the logo decode may produce.</returns>
    internal static long LogoDecodePixelBudget(int logoMaxSizeDips)
    {
        var cap = (long)LogoDecodeCap(logoMaxSizeDips);
        return Math.Min(cap * cap, SplashDecodePixelCeiling);
    }

    /// <summary>The width the LOGO is decoded to, for a source of the given declared
    /// dimensions: the largest width whose output stays inside BOTH the per-edge
    /// <see cref="LogoDecodeCap"/> and <see cref="LogoDecodePixelBudget"/>.
    ///
    /// The edge bound: <see cref="Stretch.Uniform"/> with equal MaxWidth/MaxHeight
    /// fits the LONGER edge to the cap, so for a taller-than-wide source it is the
    /// HEIGHT that lands on the cap and the width stays smaller. Decoding such a
    /// source to width <c>cap</c> would still produce a bitmap <c>cap * height/width</c>
    /// tall — several times more pixels than are rendered — so the decode width is
    /// scaled by the header's aspect ratio. Wider-than-tall sources hit the cap on the
    /// width already and use it unchanged.
    ///
    /// The area bound is <c>sqrt(budget * width / height)</c>, the same construction
    /// the background uses (<see cref="BackgroundDecodeWidth"/>): the width at which an
    /// aspect-preserving decode produces exactly the budget's pixels.
    ///
    /// The result is only ever an UPPER bound: the caller decodes at the source's own
    /// size when that is smaller, so nothing is upscaled — and a source already inside
    /// the budget is by construction never scaled down, because <c>w*h &lt;= budget</c>
    /// is the same inequality as <c>w &lt;= sqrt(budget * w/h)</c>. That equivalence is
    /// what closes the hole a per-edge cap left open: whichever branch the caller takes,
    /// the decoded area is bounded by the budget.
    ///
    /// Computed in <see cref="double"/> throughout: <c>cap * sourceWidth</c> reaches
    /// 20000 * 20000 at the <see cref="ImageHeader"/> limits, which wraps a 32-bit
    /// multiply.</summary>
    /// <param name="logoMaxSizeDips">The configured logo bound in DIPs.</param>
    /// <param name="sourceWidth">Width the source's header declares, in pixels.</param>
    /// <param name="sourceHeight">Height the source's header declares, in pixels.</param>
    /// <returns>The decode width in pixels, at least 1.</returns>
    internal static int LogoDecodeWidth(int logoMaxSizeDips, int sourceWidth, int sourceHeight)
    {
        var cap = LogoDecodeCap(logoMaxSizeDips);
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return cap;
        }
        var byEdge = sourceHeight > sourceWidth
            ? Math.Ceiling((double)cap * sourceWidth / sourceHeight)
            : cap;
        var byArea = Math.Floor(
            Math.Sqrt((double)LogoDecodePixelBudget(logoMaxSizeDips) * sourceWidth / sourceHeight));
        return Math.Max(1, (int)Math.Min(byEdge, byArea));
    }

    /// <summary>The width the BACKGROUND is decoded to, for a source of the given
    /// declared dimensions: the largest width whose output stays inside BOTH
    /// <see cref="BackgroundDecodeMaxWidth"/> and
    /// <see cref="BackgroundDecodePixelBudget"/>.
    ///
    /// The pixel bound is <c>sqrt(budget * width / height)</c> — the width at which a
    /// decode preserving the source's aspect ratio produces exactly the budget's
    /// area. Landscape sources hit the width cap first and are therefore decoded
    /// exactly as before; tall ones, whose width cap never binds, are bounded by area
    /// instead of being decoded whole.
    ///
    /// The result is only ever an UPPER bound: the caller decodes at the source's own
    /// size when that is smaller, so nothing is upscaled. A source already inside the
    /// budget is by construction never scaled down — <c>w*h &lt;= budget</c> is the same
    /// inequality as <c>w &lt;= sqrt(budget * w/h)</c>.
    ///
    /// Computed in <see cref="double"/> throughout: <c>budget * width</c> is ~8.2e10 at
    /// the <see cref="ImageHeader"/> limits and would overflow both int and the
    /// intermediate of an int multiply.</summary>
    /// <param name="sourceWidth">Width the source's header declares, in pixels.</param>
    /// <param name="sourceHeight">Height the source's header declares, in pixels.</param>
    /// <returns>The decode width in pixels, at least 1.</returns>
    internal static int BackgroundDecodeWidth(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return BackgroundDecodeMaxWidth;
        }
        var byArea = Math.Sqrt((double)BackgroundDecodePixelBudget * sourceWidth / sourceHeight);
        var width = (int)Math.Min(BackgroundDecodeMaxWidth, Math.Floor(byArea));
        return Math.Max(1, width);
    }

    /// <summary>Adds a layer to the root panel just before the desktop button,
    /// which must always stay the last (topmost) child.</summary>
    private void AddLayer(Control control) =>
        RootPanel.Children.Insert(RootPanel.Children.Count - 1, control);

    /// <summary>Places an element per its configured placement: alignment + margin
    /// directly on the element for anchor mode, or onto the shared Canvas (position
    /// applied once the window covers the screen) for absolute mode.</summary>
    private void PlaceElement(Control control, SplashElementPlacement placement)
    {
        if (placement.Mode == SplashPlacementMode.Absolute)
        {
            if (_absoluteCanvas is null)
            {
                _absoluteCanvas = new Canvas();
                AddLayer(_absoluteCanvas);
            }
            _absoluteCanvas.Children.Add(control);
            _absoluteElements.Add((control, placement));
            return;
        }

        // Anchor mode ignores the screen/element sizes entirely.
        var layout = SplashStyle.MapPlacement(placement, default, default);
        control.HorizontalAlignment = layout.HorizontalAlignment;
        control.VerticalAlignment = layout.VerticalAlignment;
        control.Margin = layout.Margin;
        AddLayer(control);
    }

    /// <summary>Recomputes Canvas positions for absolute-mode elements against the
    /// current window size (called after the window covers the screen and again
    /// after display changes; clamping needs real dimensions).</summary>
    private void UpdateAbsolutePositions()
    {
        if (_absoluteElements.Count == 0)
        {
            return;
        }
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }
        foreach (var (control, placement) in _absoluteElements)
        {
            control.Measure(Size.Infinity);
            var layout = SplashStyle.MapPlacement(placement, size, control.DesiredSize);
            Canvas.SetLeft(control, layout.CanvasX);
            Canvas.SetTop(control, layout.CanvasY);
        }
    }

    private void BuildSweepLine(IBrush brush)
    {
        _sweepLine = new Border
        {
            Height = SweepLineThickness,
            Background = brush,
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = _sweepTransform,
        };
        var bottom = _splash.SweepEdge == SweepEdge.Bottom;
        _sweepHost = new Panel
        {
            Height = SweepLineThickness,
            VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top,
            // Bottom edge keeps clear of the desktop button; the top edge has
            // nothing to collide with.
            Margin = bottom ? new Thickness(0, 0, 0, SweepBottomClearance) : default,
        };
        _sweepHost.Children.Add(_sweepLine);
        AddLayer(_sweepHost);
    }

    private static LoadingIndicatorMode MapIndicatorMode(SplashSpinnerStyle style) => style switch
    {
        SplashSpinnerStyle.LiArc => LoadingIndicatorMode.Arc,
        SplashSpinnerStyle.LiArcs => LoadingIndicatorMode.Arcs,
        SplashSpinnerStyle.LiArcsRing => LoadingIndicatorMode.ArcsRing,
        SplashSpinnerStyle.LiDoubleBounce => LoadingIndicatorMode.DoubleBounce,
        SplashSpinnerStyle.LiFlipPlane => LoadingIndicatorMode.FlipPlane,
        SplashSpinnerStyle.LiPulse => LoadingIndicatorMode.Pulse,
        SplashSpinnerStyle.LiRing => LoadingIndicatorMode.Ring,
        SplashSpinnerStyle.LiThreeDots => LoadingIndicatorMode.ThreeDots,
        SplashSpinnerStyle.LiWave => LoadingIndicatorMode.Wave,
        _ => LoadingIndicatorMode.Ring,
    };

    private static string DescribePlacement(SplashElementPlacement placement) =>
        placement.Mode == SplashPlacementMode.Absolute
            ? $"Absolute({placement.X},{placement.Y})"
            : $"Anchor({placement.Anchor})";

    private static Border CreateVignette() => new()
    {
        IsHitTestVisible = false,
        Background = new RadialGradientBrush
        {
            Center = RelativePoint.Center,
            GradientOrigin = RelativePoint.Center,
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55),
                new GradientStop(Color.FromArgb(0xA0, 0, 0, 0), 1),
            },
        },
    };

    /// <param name="path">Full path to the image file.</param>
    /// <param name="decodeWidthFor">Given the source's DECLARED dimensions, the width
    /// in PHYSICAL pixels the decode may go up to; null decodes at the source's own
    /// size. Both elements bound their decode by total output area, from different
    /// budgets — the logo from its own DIP bound (<see cref="LogoDecodeWidth"/>), the
    /// background from the panel it covers (<see cref="BackgroundDecodeWidth"/>) — and
    /// only the caller knows which, so the
    /// rule comes in from outside while the header read, the downscale-only decision
    /// and the failure handling stay here. Callers whose element is sized in DIPs must
    /// fold the DPI headroom in themselves (see <see cref="LogoDecodeDpiHeadroom"/>) —
    /// no DPI is known here.</param>
    private static Bitmap? TryLoadBitmap(string path, Func<int, int, int>? decodeWidthFor)
    {
        try
        {
            if (!File.Exists(path))
            {
                Log.Warn($"Splash: image not found, skipping element: {path}");
                return null;
            }
            // Splash images can come from an imported (therefore untrusted)
            // .wsgmsplash theme, whose caps bound only the ENCODED bytes: a tiny
            // file may declare enormous dimensions. Read the declared size from
            // the header — no decode — and refuse the absurd ones outright, so the
            // boot path never commits an unbounded pixel buffer. Header values are
            // what the file CLAIMS; a lying one fails the decode below, which is
            // caught like any other load failure.
            if (!ImageHeader.TryReadSize(path, out var sourceWidth, out var sourceHeight))
            {
                Log.Warn(
                    "Splash: unsupported image format or truncated header (supported: PNG, JPEG, BMP), "
                        + $"skipping element: {path}");
                return null;
            }
            if (!ImageHeader.IsWithinLimits(sourceWidth, sourceHeight))
            {
                Log.Warn(
                    $"Splash: image declares {sourceWidth}x{sourceHeight} px (limit {ImageHeader.MaxDimension} px "
                        + $"per side, {ImageHeader.MaxPixels / 1_000_000} MP total), skipping element: {path}");
                return null;
            }
            if (decodeWidthFor is not null)
            {
                var width = Math.Max(1, decodeWidthFor(sourceWidth, sourceHeight));
                if (sourceWidth > width)
                {
                    // Downscale-only cap: DecodeToWidth would UPSCALE smaller sources,
                    // so the header's declared width decides. (The previous
                    // full-Bitmap size probe allocated exactly the buffer this cap
                    // exists to avoid.)
                    using var stream = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(stream, width);
                }
            }
            return new Bitmap(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash: failed to load image '{path}', skipping element: {ex.Message}");
            return null;
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        CoverPrimaryScreen();
        UpdateAbsolutePositions();
        // ClientSize lags the CoverPrimaryScreen resize until the platform delivers
        // it — absolute-mode clamping must recompute once the real size arrives.
        Resized += OnResized;
        Closed += (_, _) => Resized -= OnResized;
        // Service boots apply the 100% game-mode scale while the splash is already
        // up (the cover must precede the posture change) — re-cover so the DPI
        // change can't leave desktop pixels exposed around a stale-sized splash.
        if (Screens is not null)
        {
            Screens.Changed += OnScreensChanged;
            Closed += (_, _) => Screens.Changed -= OnScreensChanged;
        }

        // Layered style applied once, fully opaque — flipping it mid-fade risks a
        // first-frame flicker. The fade is cosmetic; without an HWND it is skipped.
        _hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (_hwnd != 0)
        {
            var ex = Interop.NativeMethods.GetWindowLong(_hwnd, Interop.NativeMethods.GwlExStyle);
            Interop.NativeMethods.SetWindowLong(_hwnd, Interop.NativeMethods.GwlExStyle,
                ex | Interop.NativeMethods.WsExLayered);
            Interop.NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, Interop.NativeMethods.LwaAlpha);
        }

        // The animation timer exists only for the in-repo spinners; the Li*
        // styles animate themselves and Off has nothing to animate.
        if (_ringSpinner is not null || _sweepLine is not null)
        {
            _spinnerStartedUtc = DateTime.UtcNow;
            _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _spinnerTimer.Tick += OnSpinnerTick;
            _spinnerTimer.Start();
        }
    }

    private void OnSpinnerTick(object? sender, EventArgs e)
    {
        // Time-based so a busy UI thread can't slow it (ring: one revolution per
        // second; sweep line: one edge-to-edge pass per 1.6 s).
        var elapsed = (DateTime.UtcNow - _spinnerStartedUtc).TotalMilliseconds;
        if (_ringSpinner is not null)
        {
            _spinnerRotate.Angle = elapsed * 0.36 % 360;
        }
        if (_sweepLine is not null && _sweepHost is not null)
        {
            var hostWidth = _sweepHost.Bounds.Width;
            if (hostWidth <= 0)
            {
                return;
            }
            var lineWidth = Math.Min(hostWidth, Math.Max(120, hostWidth * 0.2));
            _sweepLine.Width = lineWidth;
            var progress = elapsed % SweepPeriodMs / SweepPeriodMs;
            _sweepTransform.X = progress * (hostWidth - lineWidth);
        }
    }

    /// <summary>Fades the whole window (layered alpha) over what's underneath, then
    /// invokes <paramref name="onDone"/>. Degrades to an immediate callback when the
    /// platform handle is unavailable.</summary>
    public void BeginFadeOut(TimeSpan duration, Action onDone)
    {
        if (_hwnd == 0 || _fadeTimer is not null)
        {
            onDone();
            return;
        }
        _fadeDuration = duration;
        _fadeDone = onDone;
        _fadeStartedUtc = DateTime.UtcNow;
        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _fadeTimer.Tick += OnFadeTick;
        _fadeTimer.Start();
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        var progress = Math.Clamp(
            (DateTime.UtcNow - _fadeStartedUtc).TotalMilliseconds / _fadeDuration.TotalMilliseconds, 0, 1);
        Interop.NativeMethods.SetLayeredWindowAttributes(
            _hwnd, 0, (byte)Math.Round(255 * (1 - progress)), Interop.NativeMethods.LwaAlpha);
        if (progress >= 1)
        {
            _fadeTimer?.Stop();
            var done = _fadeDone;
            _fadeDone = null;
            done?.Invoke();
        }
    }

    private void OnResized(object? sender, WindowResizedEventArgs e) => UpdateAbsolutePositions();

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        CoverPrimaryScreen();
        UpdateAbsolutePositions();
        Core.Log.Info("Boot splash resized after display change.");
    }

    /// <summary>Primary display only — same assumption as the overlay; startup apps
    /// on a secondary screen may still flash (accepted on single-screen handhelds).</summary>
    private void CoverPrimaryScreen()
    {
        var screen = Screens?.Primary ?? (Screens?.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null)
        {
            return;
        }
        var bounds = screen.Bounds;
        // Window scaling, not screen.Scaling — the screens cache is stale after a
        // runtime display-scale flip (see OverlayWindow.DockToRightEdge).
        var scaling = DesktopScaling;
        Position = new PixelPoint(bounds.X, bounds.Y);
        Width = bounds.Width / scaling;
        Height = bounds.Height / scaling;
    }

    private void OnDesktop(object? sender, RoutedEventArgs e) => DesktopRequested?.Invoke();

    private void StopTimers()
    {
        _spinnerTimer?.Stop();
        _spinnerTimer = null;
        _fadeTimer?.Stop();
        _fadeTimer = null;
        _fadeDone = null;
    }
}
