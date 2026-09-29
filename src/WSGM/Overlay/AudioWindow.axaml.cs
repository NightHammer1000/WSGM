using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The sheet's controller-friendly master-volume and default-device
/// panel. It remains a separate top-level window so combo-box popups and focus
/// traversal work while the sheet is visible underneath.</summary>
public partial class AudioWindow : Window
{
    private const double BaseWidth = 500;
    private const double BaseHeight = 340;
    private readonly AudioManager _audio;
    private readonly double _uiScale;

    /// <summary>The slider receives controller focus when the panel opens.</summary>
    internal InputElement DefaultFocusTarget => VolumeSlider;

    /// <summary>Creates an audio panel over the supplied live manager.</summary>
    /// <param name="audio">The taskbar-owned audio manager.</param>
    /// <param name="uiScale">The desktop-DPI scale factor for WSGM UI.</param>
    public AudioWindow(AudioManager audio, double uiScale = 1.0)
    {
        _audio = audio;
        _uiScale = uiScale;
        InitializeComponent();
        DataContext = audio;
        Opened += (_, _) =>
        {
            _audio.Refresh();
            VolumeSlider.Focus(NavigationMethod.Directional);
        };
        StatusPanel.WirePanelBehaviour(this);
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e) => _audio.Refresh();

    /// <summary>Places the panel just above the right-hand status section of the
    /// taskbar and scales it back to the user's normal desktop DPI.</summary>
    /// <param name="anchorBottom">The sheet header's physical bottom edge.</param>
    /// <param name="anchorRight">The sheet's physical right edge.</param>
    internal void DockBelowHeader(int anchorBottom, int anchorRight) => StatusPanel.DockBelowHeader(
        this, RootScale, _uiScale, BaseWidth, BaseHeight, anchorBottom, anchorRight, "Audio");

}
