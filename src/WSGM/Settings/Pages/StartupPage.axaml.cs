using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WSGM.Core;

namespace WSGM.Settings.Pages;

/// <summary>The Startup settings page: the ordered startup-app editor (the only
/// internally scrolling Settings surface), launch delays and the boot-splash
/// toggle. Inherits the window's <see cref="SettingsViewModel"/> DataContext;
/// the async file pickers stay in this code-behind (StorageProvider needs the
/// visual tree's TopLevel).</summary>
public partial class StartupPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public StartupPage() => InitializeComponent();

    private void OnAddApp(object? sender, RoutedEventArgs e) =>
        ObservePickerAction(AddAppAsync, "Startup application picker");

    private async Task AddAppAsync()
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }
        // A detected suggestion adds itself; "Choose a program…" opens the picker.
        if (viewModel.AddSelectedStartupApp())
        {
            return;
        }
        var path = await PickExeAsync();
        if (path is not null)
        {
            viewModel.StartupApps.Add(new StartupAppRow { Path = path, Enabled = true });
        }
    }

    private void OnBrowseStartupApp(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StartupAppRow row)
        {
            ObservePickerAction(() => BrowseStartupAppAsync(row), "Startup application picker");
        }
    }

    private async Task BrowseStartupAppAsync(StartupAppRow row)
    {
        var path = await PickExeAsync();
        if (path is not null)
        {
            row.Path = path;
        }
    }

    private void OnEditStartupText(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not StartupAppRow row
            || TopLevel.GetTopLevel(this) is not SettingsWindow window)
        {
            return;
        }

        if ((sender as Button)?.Tag as string == "Args")
        {
            window.ShowOnScreenKeyboard(row.Args, 32767, "Startup arguments", value =>
            {
                row.Args = value;
                return null;
            });
        }
        else
        {
            window.ShowOnScreenKeyboard(row.Path, 32767, "Startup application path", value =>
            {
                row.Path = value;
                return null;
            });
        }
    }

    private async Task<string?> PickExeAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return null;
        }
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select application",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Applications") { Patterns = ["*.exe"] }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private void ObservePickerAction(Func<Task> action, string operation) =>
        _ = ObservePickerActionAsync(action, operation);

    private async Task ObservePickerActionAsync(Func<Task> action, string operation)
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
            if (DataContext is SettingsViewModel viewModel)
            {
                viewModel.StatusText = $"{operation} failed: {ex.Message}";
            }
        }
    }
}
