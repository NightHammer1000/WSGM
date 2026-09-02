using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>One application chip on the quick access sheet's Open apps strip.
/// Mutable presentation state is INPC so the 1 s refresh can update chips IN PLACE —
/// replacing the collection wholesale would destroy the focused button under the
/// gamepad cursor on every tick.</summary>
public sealed class AppSwitcherEntry : INotifyPropertyChanged
{
    /// <summary>Raised when a mutable presentation property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates a switcher chip for an enumerated window.</summary>
    /// <param name="hwnd">The native window handle to activate.</param>
    /// <param name="title">The window title (tooltip text).</param>
    /// <param name="isSteam">Whether the window belongs to Steam (activated via protocol).</param>
    /// <param name="icon">The rasterized application icon, or null for the fallback glyph.</param>
    public AppSwitcherEntry(nint hwnd, string title, bool isSteam, Bitmap? icon)
    {
        Hwnd = hwnd;
        _title = title;
        IsSteam = isSteam;
        Icon = icon;
    }

    /// <summary>Gets the native window handle to activate.</summary>
    public nint Hwnd { get; }

    /// <summary>Gets whether the window belongs to Steam.</summary>
    public bool IsSteam { get; }

    private Bitmap? _icon;
    /// <summary>Gets or sets the rasterized application icon (null renders the fallback
    /// glyph). Settable because resolution runs off the UI thread: the tile is created
    /// with whatever is cached and the icon lands here IN PLACE when it arrives — a
    /// wholesale rebuild would destroy the button under the gamepad cursor.</summary>
    public Bitmap? Icon
    {
        get => _icon;
        set
        {
            if (!ReferenceEquals(_icon, value))
            {
                _icon = value;
                Raise(nameof(Icon));
                Raise(nameof(HasNoIcon));
            }
        }
    }

    /// <summary>Gets whether a fallback glyph should render instead of an icon.</summary>
    public bool HasNoIcon => Icon is null;

    private string _title;
    /// <summary>Gets or sets the window title shown on the chip.</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                Raise(nameof(Title));
            }
        }
    }

    private bool _isMinimized;
    /// <summary>Gets or sets whether the window is currently minimized.</summary>
    public bool IsMinimized
    {
        get => _isMinimized;
        set
        {
            if (_isMinimized != value)
            {
                _isMinimized = value;
                Raise(nameof(IsMinimized));
            }
        }
    }

    private bool _isActive;
    /// <summary>Gets or sets whether this window was foreground when the sheet opened
    /// (or last refreshed) — the highlighted chip.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                Raise(nameof(IsActive));
            }
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One tray icon tile. Wraps the host's live record; Refresh() re-raises
/// the bindable projections after a NIM_MODIFY.</summary>
public sealed class TrayIconEntry : INotifyPropertyChanged
{
    /// <summary>Raised when the projected icon state changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates a tile over a live tray-icon record.</summary>
    /// <param name="icon">The host's icon record.</param>
    public TrayIconEntry(TrayIconTable.TrayIcon icon)
    {
        Icon = icon;
    }

    /// <summary>Gets the underlying tray-icon record (click forwarding target).</summary>
    public TrayIconTable.TrayIcon Icon { get; }

    /// <summary>Gets the rasterized icon image.</summary>
    public Bitmap? Image => Icon.IconImage as Bitmap;

    /// <summary>Gets the tooltip text.</summary>
    public string Tip => Icon.Tip;

    /// <summary>Re-raises the projections after the underlying record changed.</summary>
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tip)));
    }
}

/// <summary>State for the quick access sheet's Open apps strip and tray area.</summary>
public sealed class AppSwitcherViewModel : INotifyPropertyChanged
{
    /// <summary>Raised after a switcher property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Application chips in first-seen order (stable across refreshes; new
    /// windows append, closed windows drop out).</summary>
    public ObservableCollection<AppSwitcherEntry> Entries { get; } = [];

    private bool _hasEntries;
    /// <summary>Gets or sets whether any application chip exists (drives the
    /// empty-state hint).</summary>
    public bool HasEntries
    {
        get => _hasEntries;
        set
        {
            if (_hasEntries != value)
            {
                _hasEntries = value;
                Raise(nameof(HasEntries));
            }
        }
    }

    /// <summary>Reconciles the chip collection against a fresh enumeration without
    /// disturbing surviving chips: updates title/minimized/active in place, removes
    /// chips whose window is gone, appends chips for new windows. Pure with respect
    /// to its inputs — the executable specification lives in the unit tests.</summary>
    /// <param name="fresh">The current switchable windows, enumeration order.</param>
    /// <param name="activeHwnd">The window considered foreground for highlighting.</param>
    /// <param name="create">Creates a chip for a newly appearing window.</param>
    public void Reconcile(
        IReadOnlyList<WindowFinder.AppWindow> fresh,
        nint activeHwnd,
        Func<WindowFinder.AppWindow, AppSwitcherEntry> create)
    {
        var byHwnd = new Dictionary<nint, WindowFinder.AppWindow>(fresh.Count);
        foreach (var window in fresh)
        {
            // Duplicate handles cannot occur in one EnumWindows pass; TryAdd keeps
            // the first (top-most) occurrence robustly anyway.
            byHwnd.TryAdd(window.Hwnd, window);
        }

        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (byHwnd.Remove(entry.Hwnd, out var window))
            {
                entry.Title = window.Title;
                entry.IsMinimized = window.IsMinimized;
                entry.IsActive = entry.Hwnd == activeHwnd;
            }
            else
            {
                Entries.RemoveAt(i);
            }
        }

        // Remaining map entries are new windows — append in enumeration order.
        foreach (var window in fresh)
        {
            if (byHwnd.Remove(window.Hwnd))
            {
                var entry = create(window);
                entry.IsMinimized = window.IsMinimized;
                entry.IsActive = window.Hwnd == activeHwnd;
                Entries.Add(entry);
            }
        }

        HasEntries = Entries.Count > 0;
    }

    /// <summary>Tray-icon tiles (registration order, hidden icons filtered out).</summary>
    public ObservableCollection<TrayIconEntry> TrayIcons { get; } = [];

    private bool _hasTrayIcons;
    /// <summary>Gets or sets whether the tray area (separator + icons) renders.</summary>
    public bool HasTrayIcons
    {
        get => _hasTrayIcons;
        set
        {
            if (_hasTrayIcons != value)
            {
                _hasTrayIcons = value;
                Raise(nameof(HasTrayIcons));
            }
        }
    }

    /// <summary>Reconciles the tray tiles against the host's live records — same
    /// in-place discipline as the app chips (identity = record reference), so a
    /// focused tray button survives unrelated changes.</summary>
    /// <param name="icons">The host's registered icons (hidden ones are filtered here).</param>
    public void ReconcileTray(IReadOnlyList<TrayIconTable.TrayIcon> icons)
    {
        var visible = new List<TrayIconTable.TrayIcon>();
        foreach (var icon in icons)
        {
            if (!icon.IsHidden)
            {
                visible.Add(icon);
            }
        }

        for (var i = TrayIcons.Count - 1; i >= 0; i--)
        {
            var entry = TrayIcons[i];
            if (visible.Remove(entry.Icon))
            {
                entry.Refresh();
            }
            else
            {
                TrayIcons.RemoveAt(i);
            }
        }
        foreach (var icon in visible)
        {
            TrayIcons.Add(new TrayIconEntry(icon));
        }
        HasTrayIcons = TrayIcons.Count > 0;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
