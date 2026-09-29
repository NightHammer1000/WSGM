using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Settings.Pages;

/// <summary>Ownership-only Device Integration settings and read-only coordinator status.</summary>
public partial class DeviceOwnershipPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public DeviceOwnershipPage() => InitializeComponent();

    private void OnRefreshStatus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            Core.Log.Observe(viewModel.RefreshDeviceOwnerStatusAsync(), "device owner status refresh");
        }
    }
}
