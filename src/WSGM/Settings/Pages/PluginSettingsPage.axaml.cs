using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Settings.Pages;

/// <summary>Settings the installed device plugin declares, rendered from its manifest.</summary>
/// <remarks>
/// The page owns no controls of its own. Its content comes from the one projection in
/// <c>PluginSettingsCoordinator.Project</c>, shared with the overlay so both surfaces order and
/// place a plugin's settings identically, and it changes with whichever plugin is installed — which
/// is why the sections and rows are bound rather than written here.
/// </remarks>
public partial class PluginSettingsPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public PluginSettingsPage()
    {
        InitializeComponent();
        // The editor reports the edited curve; the row holds it and the view model records that the
        // profile list is dirty. Without the last part a curve edit is discarded at save, because
        // profiles are only written when this window actually changed them.
        ProfileCurve.CurveChanged += OnCurveChanged;
    }

    private void OnCurveChanged(IReadOnlyList<CurvePoint> curve)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        if (viewModel.SelectedDeviceProfile is { } profile)
        {
            profile.Curve = curve;
        }

        viewModel.NoteDeviceProfileEdited();
    }

    private void OnAddProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            // The fan curve is the only curve capability WSGM has a semantic role for, so it is the
            // one a new profile authors until a plugin publishes another.
            viewModel.AddDeviceProfile(FanCurveCapabilityId);
        }
    }

    private void OnAddColorProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.AddDeviceProfile(LightingCapabilityId, color: true);
        }
    }

    private void OnRemoveProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.RemoveSelectedDeviceProfile();
        }
    }

    private void OnEditProfileName(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { SelectedDeviceProfile: { } profile }
            && TopLevel.GetTopLevel(this) is SettingsWindow window)
        {
            window.ShowOnScreenKeyboard(
                profile.Name,
                DeviceAuthoredProfile.MaxNameLength,
                "Device profile name",
                value =>
                {
                    profile.Name = value;
                    return null;
                });
        }
    }

    private void OnEditProfileColor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { SelectedDeviceProfile: { } profile }
            && TopLevel.GetTopLevel(this) is SettingsWindow window)
        {
            ShowColorKeyboard(window, profile.ColorHex, "Device profile colour", value => profile.ColorHex = value);
        }
    }

    private void OnEditPluginText(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PluginSettingRowViewModel row
            && TopLevel.GetTopLevel(this) is SettingsWindow window)
        {
            window.ShowOnScreenKeyboard(
                row.TextValue,
                row.MaximumLength,
                row.Label,
                value =>
                {
                    row.TextValue = value;
                    return null;
                });
        }
    }

    private void OnEditPluginColor(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PluginSettingRowViewModel row
            && TopLevel.GetTopLevel(this) is SettingsWindow window)
        {
            ShowColorKeyboard(window, row.ColorHex, row.Label, value => row.ColorHex = value);
        }
    }

    private static void ShowColorKeyboard(
        SettingsWindow window,
        string initial,
        string title,
        System.Action<string> apply) =>
        window.ShowOnScreenKeyboard(initial, 9, title, value =>
        {
            if (!Color.TryParse(value, out _))
            {
                return "Enter a color such as #FF9D3D.";
            }
            apply(value);
            return null;
        });

    /// <summary>The capability a newly authored curve profile targets.</summary>
    private const string FanCurveCapabilityId = "fan.curve";

    /// <summary>The capability a newly authored colour profile targets.</summary>
    private const string LightingCapabilityId = "lighting.zone-color";
}
