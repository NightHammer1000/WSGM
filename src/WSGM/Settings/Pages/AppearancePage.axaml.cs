using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using WSGM.Core;
using WSGM.Themes;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace WSGM.Settings.Pages;

/// <summary>The Appearance settings page: the accent-color picker (preset swatches,
/// hex field, color-picker flyout — applied live to the running window as a
/// process-local preview; Save persists it) and the boot-splash editor (presets,
/// content, placements, colors, images, full-screen preview and .wsgmsplash
/// export/import). Inherits the window's <see cref="SettingsViewModel"/>
/// DataContext; pickers and the preview stay in this code-behind (StorageProvider
/// and the navigation swap need the visual tree's TopLevel).</summary>
public partial class AppearancePage : UserControl
{
    /// <summary>Preset accent swatches (D-pad friendly one-tap choices).</summary>
    private static readonly string[] AccentSwatches =
    [
        "#FFFF9D3D", // WSGM orange (default)
        "#FFE5484D", // red
        "#FFE93D82", // pink
        "#FF8E4EC6", // purple
        "#FF3B82F6", // blue
        "#FF00B7C3", // cyan
        "#FF30A46C", // green
        "#FFF5D90A", // yellow
        "#FFEEEEEE", // white
    ];

    private static readonly StreamGeometry CheckGeometry = StreamGeometry.Parse("M 2,7.5 L 6,11.5 L 12.5,3");

    private readonly List<(Button Button, Color Color, ShapePath Check)> _swatches = [];
    private SettingsViewModel? _viewModel;
    private Flyout? _splashColorFlyout;
    private Bitmap? _logoThumbBitmap;
    private Bitmap? _backgroundThumbBitmap;
    private bool _syncingAccent;

    /// <summary>Loads the compiled page XAML, builds the accent swatches and the
    /// splash preset list, and tracks the view model for live accent preview and
    /// image-thumbnail refreshes.</summary>
    public AppearancePage()
    {
        InitializeComponent();
        BuildSwatches();
        PresetCombo.ItemsSource = SplashPresets.All.Select(SplashPresets.DisplayName).ToList();
        PresetCombo.SelectedIndex = 0;
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) =>
        {
            LogoThumb.Source = null;
            BackgroundThumb.Source = null;
            _logoThumbBitmap?.Dispose();
            _logoThumbBitmap = null;
            _backgroundThumbBitmap?.Dispose();
            _backgroundThumbBitmap = null;
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        RefreshAccentVisuals();
        RefreshLogoThumbnail();
        RefreshBackgroundThumbnail();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.AccentColorHex):
                ApplyAccentPreview();
                break;
            case nameof(SettingsViewModel.SplashLogoPath):
                RefreshLogoThumbnail();
                break;
            case nameof(SettingsViewModel.SplashBackgroundImagePath):
                RefreshBackgroundThumbnail();
                break;
        }
    }

    // --- Accent ---
    private void BuildSwatches()
    {
        foreach (var hex in AccentSwatches)
        {
            var color = Color.Parse(hex);
            var check = new ShapePath
            {
                Data = CheckGeometry,
                Stroke = new ImmutableSolidColorBrush(
                    AccentPalette.UseBlackForeground(color) ? Colors.Black : Colors.White),
                StrokeThickness = 2.4,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Width = 15,
                Height = 15,
                Stretch = Stretch.Uniform,
                IsVisible = false,
            };
            var button = new Button
            {
                Width = 44,
                Height = 44,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                // Local value, so the shared :pointerover style can't wash it out.
                Background = new ImmutableSolidColorBrush(color),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = check,
                Tag = hex,
            };
            ToolTip.SetTip(button, hex);
            AutomationProperties.SetName(button, $"Use accent color {hex}");
            button.Click += OnSwatchClick;
            _swatches.Add((button, color, check));
            SwatchPanel.Children.Add(button);
        }
    }

    private void OnSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is Button { Tag: string hex })
        {
            _viewModel.AccentColorHex = hex;
        }
    }

    private void OnAccentPickerColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (_syncingAccent || _viewModel is null)
        {
            return;
        }
        var c = e.NewColor;
        _viewModel.AccentColorHex = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    /// <summary>Live process-local accent preview: a parsable hex re-colors the
    /// running UI immediately (Save persists it for every process). A half-typed
    /// value changes nothing rather than flashing the fallback accent.</summary>
    private void ApplyAccentPreview()
    {
        if (_viewModel is null || !Color.TryParse(_viewModel.AccentColorHex, out var color))
        {
            return;
        }
        if (Application.Current is { } app)
        {
            AccentPalette.Apply(app, color);
        }
        RefreshAccentVisuals();
    }

    private void RefreshAccentVisuals()
    {
        if (_viewModel is null || !Color.TryParse(_viewModel.AccentColorHex, out var color))
        {
            return;
        }
        foreach (var (_, swatchColor, check) in _swatches)
        {
            check.IsVisible = swatchColor == color;
        }
        _syncingAccent = true;
        try
        {
            AccentPicker.Color = color;
        }
        finally
        {
            _syncingAccent = false;
        }
    }

    // --- Splash colors ---
    /// <summary>One handler for the four splash color swatches, keyed by the
    /// button's Tag ("Background", "Text", "Caption", "Spinner").</summary>
    private void OnSplashColorSwatchClick(object? sender, RoutedEventArgs e)
    {
        switch ((sender as Button)?.Tag as string)
        {
            case "Background":
                ShowSplashColorFlyout(sender,
                    static vm => vm.SplashBackgroundColorHex, static (vm, hex) => vm.SplashBackgroundColorHex = hex);
                break;
            case "Text":
                ShowSplashColorFlyout(sender,
                    static vm => vm.SplashTextColorHex, static (vm, hex) => vm.SplashTextColorHex = hex);
                break;
            case "Caption":
                ShowSplashColorFlyout(sender,
                    static vm => vm.SplashCaptionColorHex, static (vm, hex) => vm.SplashCaptionColorHex = hex);
                break;
            case "Spinner":
                ShowSplashColorFlyout(sender,
                    static vm => vm.SplashSpinnerColorHex, static (vm, hex) => vm.SplashSpinnerColorHex = hex);
                break;
        }
    }

    /// <summary>Opens a color-picker flyout on a splash swatch button — the
    /// TOUCH/mouse path to these colors. It is deliberately NOT a controller
    /// path: flyout content lives in its own popup root and GamepadNavigation is
    /// scoped to the owning window, so a pad can neither reach the picker nor
    /// dismiss it (hence
    /// <see cref="TryCloseColorFlyout"/>, which the window's Back action calls
    /// first). Gamepad navigation also skips the paired hex TextBoxes, so on a
    /// pad-only device these four colors are currently editable by touch only.
    /// The picker starts on the row's current color and writes every change back
    /// through <paramref name="setHex"/>, so the swatch and TextBox update live;
    /// alpha is disabled to match the "#RRGGBB" splash color format. The flyout
    /// hosts the full picker panel (<see cref="ColorView"/>, the accent
    /// <see cref="ColorPicker"/>'s base class) directly — a nested ColorPicker
    /// would put a second drop-down button inside the flyout.</summary>
    private void ShowSplashColorFlyout(
        object? sender, Func<SettingsViewModel, string> getHex, Action<SettingsViewModel, string> setHex)
    {
        if (_viewModel is not { } viewModel || sender is not Button anchor)
        {
            return;
        }
        var picker = new ColorView { IsAlphaEnabled = false };
        if (Color.TryParse(getHex(viewModel), out var current))
        {
            picker.Color = current;
        }
        picker.ColorChanged += (_, args) =>
            setHex(viewModel, $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}");
        var flyout = new Flyout { Content = picker };
        _splashColorFlyout = flyout;
        flyout.ShowAt(anchor);
    }

    /// <summary>Closes an open splash color flyout. The settings window's
    /// controller Back action calls this before closing itself: B is the natural
    /// "dismiss this popup" press, but the flyout is outside gamepad navigation's
    /// window scope, so without this the press would close Settings and discard
    /// every unsaved edit. A light-dismissed flyout is already closed (IsOpen is
    /// false), which lets B fall through to closing the window.</summary>
    /// <returns><see langword="true"/> when a flyout was open and was closed.</returns>
    internal bool TryCloseColorFlyout()
    {
        if (_splashColorFlyout is not { IsOpen: true } flyout)
        {
            _splashColorFlyout = null;
            return false;
        }
        flyout.Hide();
        _splashColorFlyout = null;
        return true;
    }

    // --- Splash presets ---
    private void OnApplyPreset(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        var index = PresetCombo.SelectedIndex;
        if (index < 0 || index >= SplashPresets.All.Count)
        {
            return;
        }
        var preset = SplashPresets.All[index];
        _viewModel.LoadSplash(SplashPresets.Create(preset));
        _viewModel.StatusText = $"Preset '{SplashPresets.DisplayName(preset)}' applied — Save changes to keep it.";
    }

    // --- Splash images ---
    private void RefreshLogoThumbnail() =>
        _logoThumbBitmap = RefreshThumbnail(_viewModel?.SplashLogoPath, LogoThumb, LogoNone, _logoThumbBitmap);

    private void RefreshBackgroundThumbnail() =>
        _backgroundThumbBitmap = RefreshThumbnail(
            _viewModel?.SplashBackgroundImagePath, BackgroundThumb, BackgroundNone, _backgroundThumbBitmap);

    /// <summary>Longest edge decoded for an inline thumbnail. The preview panel is
    /// 44x28 device-independent pixels, so 128 px stays sharp at any display
    /// scale while keeping the pixel buffer at a few tens of kilobytes.</summary>
    private const int ThumbnailDecodePixels = 128;

    /// <summary>Loads one inline thumbnail; a missing or unreadable file shows the
    /// "NONE" placeholder instead. The previous bitmap is disposed only after the
    /// Image stopped referencing it.</summary>
    private static Bitmap? RefreshThumbnail(string? path, Image image, TextBlock placeholder, Bitmap? previous)
    {
        Bitmap? bitmap = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                bitmap = LoadThumbnail(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Appearance: couldn't load image thumbnail '{path}': {ex.Message}");
        }
        image.Source = bitmap;
        image.IsVisible = bitmap is not null;
        placeholder.IsVisible = bitmap is null;
        previous?.Dispose();
        return bitmap;
    }

    /// <summary>Decodes a thumbnail with the decoded pixel buffer bounded up front.
    /// Splash images can arrive from an imported (therefore untrusted)
    /// .wsgmsplash theme, whose byte caps bound only the ENCODED size — a few KB
    /// can declare tens of thousands of pixels per side, and a full-resolution
    /// decode then hangs or OOMs Settings. So the header is read first
    /// (<see cref="ImageHeader"/>): unreadable or absurd dimensions show the
    /// "NONE" placeholder, and anything larger than the preview is decoded scaled
    /// down its longer edge (which also keeps extreme aspect ratios bounded).
    /// The header is only what the file DECLARES; a lying one just fails the
    /// decode, which the caller already catches.</summary>
    private static Bitmap? LoadThumbnail(string path)
    {
        if (!ImageHeader.TryReadSize(path, out var width, out var height))
        {
            Log.Warn(
                "Appearance: unsupported image format or truncated header (supported: PNG, JPEG, BMP), "
                    + $"no thumbnail for '{path}'.");
            return null;
        }
        if (!ImageHeader.IsWithinLimits(width, height))
        {
            Log.Warn(
                $"Appearance: image declares {width}x{height} px (limit {ImageHeader.MaxDimension} px per side, "
                    + $"{ImageHeader.MaxPixels / 1_000_000} MP total), no thumbnail for '{path}'.");
            return null;
        }
        if (width <= ThumbnailDecodePixels && height <= ThumbnailDecodePixels)
        {
            return new Bitmap(path);
        }

        // DecodeToWidth/Height also UPSCALE, hence the size check above; scale the
        // longer edge so a tall, narrow source shrinks too.
        using var stream = File.OpenRead(path);
        return width >= height
            ? Bitmap.DecodeToWidth(stream, ThumbnailDecodePixels)
            : Bitmap.DecodeToHeight(stream, ThumbnailDecodePixels);
    }

    /// <summary>One picker for both image slots, keyed by the button's Tag
    /// ("Logo" or "Background").</summary>
    private void OnBrowseImage(object? sender, RoutedEventArgs e) =>
        ObservePageAction(() => BrowseImageAsync(sender), "Image picker");

    private async Task BrowseImageAsync(object? sender)
    {
        var logo = (sender as Button)?.Tag as string == "Logo";
        if (_viewModel is not null
            && await PickImageAsync(logo ? "Select logo image" : "Select background image") is { } path)
        {
            SetImagePath(logo, path);
        }
    }

    /// <summary>One clear action for both image slots, keyed by the button's Tag.</summary>
    private void OnClearImage(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            SetImagePath((sender as Button)?.Tag as string == "Logo", "");
        }
    }

    private void SetImagePath(bool logo, string path)
    {
        if (logo)
        {
            _viewModel!.SplashLogoPath = path;
        }
        else
        {
            _viewModel!.SplashBackgroundImagePath = path;
        }
    }

    private async Task<string?> PickImageAsync(string title)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return null;
        }
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] },
            ],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    // --- Actions ---
    private void OnPreviewSplash(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        try
        {
            if (TopLevel.GetTopLevel(this) is SettingsWindow window)
            {
                window.ShowSplashPreview(_viewModel.BuildSplashConfig());
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash preview failed: {ex.Message}");
            _viewModel.StatusText = $"Preview failed: {ex.Message}";
        }
    }

    private void OnExportSplash(object? sender, RoutedEventArgs e) =>
        ObservePageAction(() => ExportSplashAsync(sender), "Splash export");

    private async Task ExportSplashAsync(object? sender)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export splash theme",
            SuggestedFileName = "my-splash.wsgmsplash",
            DefaultExtension = "wsgmsplash",
            FileTypeChoices =
            [
                new FilePickerFileType("WSGM splash theme") { Patterns = ["*.wsgmsplash"] },
            ],
        });
        var path = file?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }
        // The archive write copies the splash images, which are megabytes on a
        // real theme: off the UI thread so Settings keeps repainting and keeps
        // answering touch and the pad while it runs.
        var splash = _viewModel.BuildSplashConfig();
        _viewModel.StatusText = "Exporting splash theme…";
        var exported = await RunArchiveWork(sender, () => SplashTheme.Export(splash, path));
        _viewModel.StatusText = exported
            ? $"Splash theme exported to {path}"
            : "Splash theme export failed — see wsgm.log for details.";
    }

    private void OnImportSplash(object? sender, RoutedEventArgs e) =>
        ObservePageAction(() => ImportSplashAsync(sender), "Splash import");

    private async Task ImportSplashAsync(object? sender)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import splash theme",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("WSGM splash theme") { Patterns = ["*.wsgmsplash"] },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }
        // Imported images land in a per-import staging directory; Save's staged
        // SplashAssets transaction commits them into the stable splash assets —
        // the live copies stay untouched until the user actually saves.
        // Off the UI thread for the same reason as the export: the decompression
        // caps allow 64 MB per image / 160 MB per theme, and a frozen window in
        // game mode is also a controller that looks dead (it holds the lease).
        _viewModel.StatusText = "Importing splash theme…";
        var imported = await RunArchiveWork(sender, () => SplashTheme.Import(path));
        if (imported is null)
        {
            _viewModel.StatusText = "Couldn't import: not a readable splash theme (see wsgm.log).";
            return;
        }
        _viewModel.LoadSplash(imported);
        _viewModel.StatusText = "Splash theme imported — Save changes to keep it.";
    }

    /// <summary>Opens the controller keyboard for one of the otherwise skipped text fields.</summary>
    private void OnEditTextWithController(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not SettingsWindow window)
        {
            return;
        }

        switch ((sender as Button)?.Tag as string)
        {
            case "Accent":
                window.ShowOnScreenKeyboard(AccentHexBox, "Accent color");
                break;
            case "Title":
                window.ShowOnScreenKeyboard(SplashTitleBox, "Splash title");
                break;
            case "Caption":
                window.ShowOnScreenKeyboard(SplashCaptionBox, "Splash caption");
                break;
            case "BackgroundColor":
                window.ShowOnScreenKeyboard(SplashBackgroundColorBox, "Splash background color");
                break;
            case "TextColor":
                window.ShowOnScreenKeyboard(SplashTextColorBox, "Splash text color");
                break;
            case "CaptionColor":
                window.ShowOnScreenKeyboard(SplashCaptionColorBox, "Splash caption color");
                break;
            case "SpinnerColor":
                window.ShowOnScreenKeyboard(SplashSpinnerColorBox, "Splash spinner color");
                break;
        }
    }

    /// <summary>Observes a page action across both its synchronous invocation and
    /// asynchronous continuation. File-picker and archive failures therefore stay
    /// visible in Settings instead of escaping an async-void event boundary.</summary>
    private void ObservePageAction(Func<Task> action, string operation) =>
        _ = ObservePageActionAsync(action, operation);

    private async Task ObservePageActionAsync(Func<Task> action, string operation)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"{operation} failed: {ex.Message}");
            if (_viewModel is not null)
            {
                _viewModel.StatusText = $"{operation} failed: {ex.Message}";
            }
        }
    }

    /// <summary>Runs one blocking splash-theme archive operation off the UI thread
    /// with the button that started it disabled, so the page stays responsive and
    /// a second run cannot be started on top of the first. The result is returned
    /// on the UI thread (the await captures the dispatcher context).</summary>
    /// <typeparam name="T">The operation's result type.</typeparam>
    /// <param name="sender">The clicked button, re-enabled when the work ends.</param>
    /// <param name="work">The blocking archive operation.</param>
    /// <returns>Whatever the operation returned.</returns>
    private static async Task<T> RunArchiveWork<T>(object? sender, Func<T> work)
    {
        var button = sender as Button;
        if (button is not null)
        {
            button.IsEnabled = false;
        }
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }
}

/// <summary>Display names for the splash editor's enum selectors. One place names
/// every member the ComboBoxes offer, so a new enum member cannot silently render
/// as its raw identifier in one selector and a friendly name in another.</summary>
public sealed class SplashEnumName : IValueConverter
{
    /// <summary>Gets the shared stateless instance referenced from page XAML.</summary>
    public static readonly SplashEnumName Instance = new();

    /// <summary>Maps one splash enum value to its selector label.</summary>
    /// <param name="value">The enum value being rendered.</param>
    /// <param name="targetType">Ignored; the result is always a string.</param>
    /// <param name="parameter">Ignored.</param>
    /// <param name="culture">Ignored; the labels are not localized.</param>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            SplashSpinnerStyle style => style switch
            {
                SplashSpinnerStyle.Ring => "Ring (classic)",
                SplashSpinnerStyle.LiArc => "Arc",
                SplashSpinnerStyle.LiArcs => "Arcs",
                SplashSpinnerStyle.LiArcsRing => "Arcs ring",
                SplashSpinnerStyle.LiDoubleBounce => "Double bounce",
                SplashSpinnerStyle.LiFlipPlane => "Flip plane",
                SplashSpinnerStyle.LiPulse => "Pulse",
                SplashSpinnerStyle.LiRing => "Ring",
                SplashSpinnerStyle.LiThreeDots => "Three dots",
                SplashSpinnerStyle.LiWave => "Wave",
                SplashSpinnerStyle.SweepLine => "Sweep line",
                SplashSpinnerStyle.Off => "Off",
                _ => style.ToString(),
            },
            SweepEdge edge => edge.ToString(),
            SplashPlacementMode mode => mode switch
            {
                SplashPlacementMode.Anchor => "Anchored",
                SplashPlacementMode.Absolute => "Absolute",
                SplashPlacementMode.WithText => "With text",
                _ => mode.ToString(),
            },
            SplashPlacementAnchor anchor => anchor switch
            {
                SplashPlacementAnchor.TopLeft => "Top left",
                SplashPlacementAnchor.TopCenter => "Top center",
                SplashPlacementAnchor.TopRight => "Top right",
                SplashPlacementAnchor.CenterLeft => "Center left",
                SplashPlacementAnchor.Center => "Center",
                SplashPlacementAnchor.CenterRight => "Center right",
                SplashPlacementAnchor.BottomLeft => "Bottom left",
                SplashPlacementAnchor.BottomCenter => "Bottom center",
                SplashPlacementAnchor.BottomRight => "Bottom right",
                _ => anchor.ToString(),
            },
            _ => value?.ToString() ?? "",
        };

    /// <summary>The selectors bind SelectedItem, not the label; converting back does nothing.</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}
