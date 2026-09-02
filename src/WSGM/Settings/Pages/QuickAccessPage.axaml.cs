using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Settings.Pages;

/// <summary>The Quick access settings page: the sheet test launcher, the
/// Steam Input lease toggle, glyph style, shortcut recorders and edge gestures.
/// Inherits the window's <see cref="SettingsViewModel"/> DataContext; the test
/// surfaces and recorders live on the owning <see cref="SettingsWindow"/> and
/// are reached through the visual tree's TopLevel.</summary>
public partial class QuickAccessPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public QuickAccessPage() => InitializeComponent();

    private SettingsWindow? Host => TopLevel.GetTopLevel(this) as SettingsWindow;

    private void OnTestOverlay(object? sender, RoutedEventArgs e) => Host?.ShowTestOverlay();

    private void OnRecordHotkey(object? sender, RoutedEventArgs e) => Host?.RecordHotkey();

    private void OnClearHotkey(object? sender, RoutedEventArgs e) => Host?.ClearHotkey();

    private void OnRecordChord(object? sender, RoutedEventArgs e) => Host?.RecordChord();

    private void OnClearChord(object? sender, RoutedEventArgs e) => Host?.ClearChord();
}
