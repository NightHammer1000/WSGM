using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>
/// The window mechanics the three status panels that hang from the quick access sheet's header
/// share: radio, audio and safe eject. Each owns its own content and commands; only the geometry is
/// common.
/// </summary>
internal static class StatusPanel
{
    /// <summary>Wires the behaviour every docked panel shares: Escape closes it, a focused row is
    /// scrolled into view, and Windows' touch-synthesized mouse messages are swallowed.</summary>
    /// <param name="window">The panel window.</param>
    /// <param name="scroller">The panel's scrolling row list, or null for a panel whose controls
    /// all fit (the audio panel is a slider and two pickers).</param>
    /// <remarks>
    /// The scroll-into-view is explicit because directional focus navigation does not raise the
    /// request itself, so a controller could otherwise focus a row off-screen. The touch filter and
    /// the controller's 150 ms deferred close are one mechanism — see invariant 3 in
    /// <c>docs\overlay-and-input.md</c>; removing either brings back ghost clicks on whatever sits
    /// under the panel.
    /// </remarks>
    internal static void WirePanelBehaviour(Window window, Control? scroller = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        scroller?.AddHandler(InputElement.GotFocusEvent, OnRowGotFocus, RoutingStrategies.Bubble);
        Win32Properties.AddWndProcHookCallback(window, NativeMethods.SwallowTouchSynthesizedMouse);
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                window.Close();
            }
        };
    }

    private static void OnRowGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control control and not ScrollViewer)
        {
            control.BringIntoView();
        }
    }

    /// <summary>Renders the panel at the user's desktop DPI, clamps it to the space below the
    /// sheet header, and parks it under the header's right end where the status pills are. Game
    /// mode forces every display to 100% scaling, which would otherwise shrink a DIP-sized panel —
    /// and any on-screen keyboard inside it — to millimetres on a dense handheld display.</summary>
    /// <param name="window">The panel window being positioned.</param>
    /// <param name="root">The panel's layout-transform root, which carries the touch scale.</param>
    /// <param name="uiScale">The configured overlay UI scale.</param>
    /// <param name="baseWidth">The panel's design width in DIPs.</param>
    /// <param name="baseHeight">The panel's design height in DIPs.</param>
    /// <param name="anchorBottom">The sheet header's physical bottom edge, or 0 to hang from the
    /// top of the display.</param>
    /// <param name="anchorRight">The sheet's physical right edge, or 0 when it is unavailable.</param>
    /// <param name="name">Panel name for the scale log line.</param>
    /// <remarks>
    /// Positioned from the header's ACTUAL bottom edge rather than derived from the working area:
    /// the sheet is a topmost window, not a registered appbar, so the working area does not account
    /// for it. The window is moved onto the target display before its effective DPI is queried.
    /// The scale never comes from <c>screen.Scaling</c> — the screens cache still reports the
    /// pre-game-mode factor at exactly the moment this runs, and using it parked the panel far
    /// from its anchor (device-reported).
    /// </remarks>
    internal static void DockBelowHeader(
        Window window,
        LayoutTransformControl root,
        double uiScale,
        double baseWidth,
        double baseHeight,
        int anchorBottom,
        int anchorRight,
        string name)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(root);

        Screen? screen = anchorRight != 0 && anchorBottom != 0
            ? window.Screens.ScreenFromPoint(new PixelPoint(anchorRight - 1, anchorBottom - 1))
            : window.Screens.ScreenFromWindow(window);
        screen ??= window.Screens.Primary
            ?? (window.Screens.ScreenCount > 0 ? window.Screens.All[0] : null);
        if (screen is null)
        {
            return;
        }

        PixelRect area = screen.Bounds;
        int top = Math.Max(anchorBottom, area.Y);

        // A top-level is initially created on Windows' default monitor. Move it first, then ask
        // the HWND for its effective DPI; reading DesktopScaling before this move sizes a panel
        // with the primary display's DPI when the sheet was summoned on another monitor.
        window.Position = new PixelPoint(area.X, area.Y);
        double scale = CurrentWindowScale(window);
        double factor = Math.Clamp(uiScale / scale, 1.0, 3.0);
        if (Math.Abs(factor - 1.0) >= 0.01)
        {
            Log.Info($"{name} panel UI scale {factor:0.##}x (desktop DPI over current {scale:0.##}).");
            root.LayoutTransform = new ScaleTransform(factor, factor);
        }

        // Clamp against the space below the header, in DIPs. The panel's own scroll viewer absorbs
        // a shortened panel, and the sizes must be final before the position is computed from them.
        window.Width = Math.Min(baseWidth * factor, (area.Width / scale) - 12);
        window.Height = Math.Min(baseHeight * factor, ((area.Y + area.Height - top) / scale) - 8);
        window.UpdateLayout();

        int width = (int)Math.Round(window.Width * scale);
        int height = (int)Math.Round(window.Height * scale);
        // Small and deliberate: the panel should look attached to the header, not floating below it.
        int gap = (int)Math.Round(2 * scale);
        int margin = (int)Math.Round(6 * scale);
        // Right-aligned, under the pills that open it and where Windows puts its own quick
        // settings; never allowed to run off the bottom of a short display.
        int x = area.X + area.Width - width - margin;
        int y = Math.Min(top + gap, Math.Max(area.Y, area.Y + area.Height - height));
        window.Position = new PixelPoint(x, y);
    }

    /// <summary>Gets the HWND's current effective scale, falling back to Avalonia only when the
    /// native handle is unavailable. The sheet and its peer panels deliberately share this rule.</summary>
    internal static double CurrentWindowScale(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        nint hwnd = window.TryGetPlatformHandle()?.Handle ?? 0;
        uint dpi = hwnd == 0 ? 0 : NativeMethods.GetDpiForWindow(hwnd);
        double scale = dpi == 0 ? window.DesktopScaling : dpi / 96.0;
        return double.IsFinite(scale) && scale > 0 ? scale : 1.0;
    }
}
