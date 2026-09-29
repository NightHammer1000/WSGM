using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The sheet's Safe Eject panel: the removable devices, each with an
/// Eject action revealed on selection.
///
/// A real window rather than a taskbar flyout for the same reasons as the radio
/// panel: a flyout cannot hold a device list, and
/// <see cref="Input.GamepadNavigation"/> has no popup awareness, so a list
/// inside one would not be reachable with a controller at all.</summary>
public partial class EjectWindow : Window
{
    private const double BaseWidth = 500;
    private const double BaseHeight = 420;
    private readonly RemovableDriveManager _drives;
    private readonly double _uiScale;

    /// <summary>Creates the panel.</summary>
    /// <param name="drives">The manager backing the list. Not owned: the
    /// sheet's status object outlives this window.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI.</param>
    public EjectWindow(RemovableDriveManager drives, double uiScale = 1.0)
    {
        _drives = drives;
        _uiScale = uiScale;
        InitializeComponent();
        DataContext = drives;
        Opened += (_, _) => _drives.Refresh();
        StatusPanel.WirePanelBehaviour(this, ListScroller);
    }

    /// <summary>Places the panel just above the right-hand status section of the
    /// taskbar and scales it back to the user's normal desktop DPI (same
    /// mechanism as the audio panel).</summary>
    /// <param name="anchorBottom">The bar's top edge in physical screen pixels, or
    /// 0 when it is not on screen.</param>
    /// <param name="anchorRight">The bar's right edge in physical screen pixels, or 0.</param>
    internal void DockBelowHeader(int anchorBottom, int anchorRight) => StatusPanel.DockBelowHeader(
        this, RootScale, _uiScale, BaseWidth, BaseHeight, anchorBottom, anchorRight, "Eject");

    /// <summary>Selecting a row reveals its Eject action. It never ejects on its
    /// own: a stray tap must not pull a mounted game library.</summary>
    private void OnDriveClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not RemovableDriveEntry entry)
        {
            return;
        }
        foreach (var other in _drives.Drives)
        {
            other.Expanded = ReferenceEquals(other, entry) && !entry.Expanded;
        }
    }

    private void OnDriveEject(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RemovableDriveEntry entry)
        {
            // EjectAsync contains the user-visible error boundary. Detach only
            // after that boundary so no event-handler exception can reach Avalonia.
            Core.Log.Observe(_drives.EjectAsync(entry), $"eject {entry.Name}");
        }
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e) => _drives.Refresh();

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
