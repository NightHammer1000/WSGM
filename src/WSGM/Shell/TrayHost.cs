using System;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Hosts the system tray in game mode by owning a top-level window whose
/// class is literally named "Shell_TrayWnd" — Shell_NotifyIcon locates the tray
/// via FindWindow on that class and delivers requests as WM_COPYDATA (the
/// mechanism every replacement shell uses; see TrayProtocol for the wire format).
/// Without this window there is NO tray in game mode: explorer isn't running, so
/// apps that close to the tray silently lose their icon.
///
/// Lifecycle contract (device-verified coexistence risk): explorer's taskbar
/// creates its own Shell_TrayWnd, and shell32 routes ALL tray traffic to
/// whichever one FindWindow sees first — two live hosts fight over Z-order
/// (ManagedShell needs a 100 ms polling war). WSGM therefore NEVER coexists:
/// this host is destroyed BEFORE explorer starts (SessionModes.DesktopModeStarting)
/// and recreated after game mode kills explorer (SessionModes.GameModeEntered).
/// Created once per game-mode span and kept alive throughout — apps whose tray
/// window is message-only never receive the TaskbarCreated broadcast, so a host
/// restart would lose their icons permanently.
///
/// Elevation gate: WSGM usually runs elevated (High IL) while most tray apps are
/// Medium IL, and UIPI silently drops WM_COPYDATA sent upward — without an
/// explicit ChangeWindowMessageFilterEx(MSGFLT_ALLOW) no ordinary app could ever
/// register. No shipped replacement shell runs elevated, so this exact gate is
/// WSGM-specific and device-verification-critical (hence the logging).</summary>
public sealed unsafe class TrayHost : IDisposable
{
    private const string TrayClassName = "Shell_TrayWnd";
    private const string NotifyClassName = "TrayNotifyWnd";

    private static TrayHost? _instance;

    private readonly TrayIconTable _table = new();
    private nint _trayHwnd;
    private nint _notifyHwnd;
    private bool _loggedAppBar;
    private bool _loggedIconRect;
    private bool _loggedLoadInProc;
    private bool _loggedBlockedCallback;
    private bool _disposed;

    /// <summary>Raised (on the window's thread — the Avalonia UI thread) whenever
    /// the set of visible icons changed.</summary>
    public event Action? IconsChanged;

    /// <summary>Gets the registered icons (hidden ones included; presentation filters).</summary>
    public TrayIconTable Table => _table;

    private TrayHost()
    {
    }

    /// <summary>Creates the tray host window pair and broadcasts TaskbarCreated so
    /// running apps re-register their icons. Must be called on the Avalonia UI
    /// thread (its message pump services the WndProc) and only while explorer is
    /// NOT running. Returns null when a host already exists or creation fails.</summary>
    public static TrayHost? Create()
    {
        if (_instance is not null)
        {
            Log.Warn("Tray host already exists — ignoring duplicate create.");
            return null;
        }
        if (ExplorerControl.IsRunningInSession())
        {
            // Explorer's own Shell_TrayWnd is (or will be) live; competing means a
            // Z-order war (see class doc). Refuse loudly instead.
            Log.Warn("Tray host not created: explorer is running in this session.");
            return null;
        }

        var host = new TrayHost();
        if (!host.CreateWindows())
        {
            host.Dispose();
            return null;
        }
        _instance = host;
        host.BroadcastTaskbarCreated();
        return host;
    }

    /// <summary>Destroys the active host if one exists (recovery paths call this
    /// unconditionally before handing the session back to explorer).</summary>
    public static void DestroyActive()
    {
        _instance?.Dispose();
    }

    private bool CreateWindows()
    {
        var hInstance = NativeMethods.GetModuleHandleW(0);
        // RegisterClass logs specifics; ERROR_CLASS_ALREADY_EXISTS is benign
        // (recreate after a previous destroy) and reported as success. Without the
        // protocol class there is no tray at all, so give up on the real error
        // instead of letting CreateWindowExW report a misleading 1407 later.
        if (!RegisterClass(TrayClassName, hInstance))
        {
            return false;
        }
        // The legacy TrayNotifyWnd child is optional (its creation failure below is
        // only warned about), so a failed registration must not sink the host.
        _ = RegisterClass(NotifyClassName, hInstance);

        // ManagedShell's shape: an invisible full-width popup pinned to the top of
        // the Z-order region shell32 scans. Never shown — WSGM's own taskbar UI
        // renders the icons; this window only speaks the protocol.
        var width = NativeMethods.GetSystemMetrics(0);
        _trayHwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WsExTopmost | (uint)NativeMethods.WsExToolWindow,
            TrayClassName, null,
            NativeMethods.WsPopup | NativeMethods.WsClipChildren | NativeMethods.WsClipSiblings,
            0, 0, width, 30,
            0, 0, hInstance, 0);
        if (_trayHwnd == 0)
        {
            Log.Warn($"Shell_TrayWnd creation failed (error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        // Legacy probers expect the TrayNotifyWnd child; DefWindowProc-only.
        _notifyHwnd = NativeMethods.CreateWindowExW(
            0, NotifyClassName, null,
            NativeMethods.WsChild | NativeMethods.WsClipChildren | NativeMethods.WsClipSiblings,
            0, 0, width, 30,
            _trayHwnd, 0, hInstance, 0);
        if (_notifyHwnd == 0)
        {
            Log.Warn($"TrayNotifyWnd creation failed (error {Marshal.GetLastWin32Error()}).");
        }

        // THE elevation gate (see class doc): allow Medium-IL apps' WM_COPYDATA
        // into this High-IL window. Logged because no shipped shell has run this
        // path elevated — device logs must show whether it held.
        var allowed = NativeMethods.ChangeWindowMessageFilterEx(
            _trayHwnd, NativeMethods.WmCopyData, NativeMethods.MsgfltAllow, 0);
        Log.Info($"Tray host created (hwnd 0x{_trayHwnd:X}, elevated={ElevationCheck.IsCurrentProcessElevated()}, " +
                 $"WM_COPYDATA filter {(allowed ? "allowed" : $"FAILED error {Marshal.GetLastWin32Error()}")}).");
        return true;
    }

    private static bool RegisterClass(string className, nint hInstance)
    {
        var terminated = className + "\0";
        fixed (char* pClassName = terminated)
        {
            var wc = new NativeMethods.WndClassW
            {
                lpfnWndProc = &WndProc,
                hInstance = hInstance,
                lpszClassName = (nint)pClassName,
            };
            if (NativeMethods.RegisterClassW(&wc) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1410)
                {
                    Log.Warn($"RegisterClassW({className}) failed (error {error}).");
                    return false;
                }
            }
        }
        return true;
    }

    private void BroadcastTaskbarCreated()
    {
        var message = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        // SendNotifyMessage, never a blocking broadcast — one wedged top-level
        // window would hang the shell. High→Medium delivery is UIPI-unrestricted,
        // so our elevation doesn't stop normal apps from hearing this. Exactly
        // once per host: repeated broadcasts duplicate NIM_ADDs in apps that
        // don't dedupe.
        if (NativeMethods.SendNotifyMessageW(NativeMethods.HwndBroadcast, message, 0, 0))
        {
            Log.Info("TaskbarCreated broadcast sent — apps should re-register tray icons.");
        }
        else
        {
            Log.Warn($"TaskbarCreated broadcast failed (error {Marshal.GetLastWin32Error()}).");
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        var host = _instance;
        if (host is null || host._disposed || hWnd != host._trayHwnd)
        {
            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
        try
        {
            if (msg == NativeMethods.WmCopyData)
            {
                return host.OnCopyData(lParam);
            }
            if (msg == NativeMethods.WmWindowPosChanged && NativeMethods.IsWindowVisible(hWnd))
            {
                // Something in the system showed the protocol window (ManagedShell
                // observes the same); it must stay invisible under WSGM's UI.
                NativeMethods.ShowWindow(hWnd, NativeMethods.SwHide);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Tray host message processing failed", ex);
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private nint OnCopyData(nint lParam)
    {
        var copyData = *(NativeMethods.CopyDataStruct*)lParam;
        switch ((int)copyData.dwData)
        {
            case TrayProtocol.CopyDataTray:
                if (copyData.lpData == 0 || copyData.cbData == 0)
                {
                    return 0;
                }
                return OnTrayData(new ReadOnlySpan<byte>((void*)copyData.lpData, (int)copyData.cbData));

            case TrayProtocol.CopyDataAppBar:
                // Full appbar support (work-area arithmetic, autohide) is out of
                // scope; a 0 reply reads as failure and callers degrade gracefully.
                if (!_loggedAppBar)
                {
                    _loggedAppBar = true;
                    Log.Info("Tray host: SHAppBarMessage traffic received — stubbed (unsupported).");
                }
                return 0;

            case TrayProtocol.CopyDataLoadInProc:
                // COM shell service objects (system volume/network/clock icons).
                // WSGM owns those surfaces itself and does not host arbitrary
                // in-process Explorer extensions.
                if (!_loggedLoadInProc)
                {
                    _loggedLoadInProc = true;
                    Log.Info("Tray host: SHLoadInProc request rejected (in-process Explorer extensions are unsupported).");
                }
                return 0;

            case TrayProtocol.CopyDataIconRect:
                if (!_loggedIconRect)
                {
                    _loggedIconRect = true;
                    Log.Info("Tray host: Shell_NotifyIconGetRect not supported yet.");
                }
                return 0;

            default:
                return 0;
        }
    }

    private nint OnTrayData(ReadOnlySpan<byte> payload)
    {
        if (!TrayProtocol.TryParse(payload, out var parsed) || parsed is null)
        {
            Log.Warn($"Tray request with unknown layout rejected ({payload.Length} bytes).");
            return 0;
        }

        var change = _table.Apply(parsed, out var icon);
        if (change == TrayChange.Rejected)
        {
            // Applications retry a rejected NIM_ADD on their own timer and never stop, so this was
            // ~6,000 lines across a handful of windows in one session. Keyed per window and uid so
            // a NEW application being rejected is still a new line.
            Log.Change(
                $"tray.rejected.{parsed.Hwnd:X}.{parsed.Uid}",
                $"Tray {Describe(parsed.Message)} rejected (hwnd 0x{parsed.Hwnd:X}, uid {parsed.Uid}).");
            return 0;
        }

        // Snapshot the icon pixels NOW: the HICON is a foreign USER handle that
        // stays valid only while the sender keeps it alive — after this handler
        // returns there is no guarantee. CopyIcon → rasterize → destroy the copy;
        // never destroy the sender's original.
        // NIS_SHAREDICON only says the HICON is also used by another icon; it changes
        // nothing about who owns the BITMAP. Every TrayIcon rasterizes its own, because
        // IconImage ownership is per icon (Removed disposes it, and so does teardown) —
        // aliasing one instance into two icons made the first Removed free an image the
        // surviving icon was still rendering.
        if (icon is not null && (parsed.Flags & TrayProtocol.NifIcon) != 0 && parsed.IconHandle != 0)
        {
            var copy = NativeMethods.CopyIcon(parsed.IconHandle);
            if (copy != 0)
            {
                try
                {
                    var bitmap = IconRasterizer.Rasterize(copy, 48);
                    if (bitmap is not null)
                    {
                        (icon.IconImage as Bitmap)?.Dispose();
                        icon.IconImage = bitmap;
                    }
                }
                finally
                {
                    NativeMethods.DestroyIcon(copy);
                }
            }
        }
        if (change == TrayChange.Removed)
        {
            (icon?.IconImage as Bitmap)?.Dispose();
            if (icon is not null)
            {
                icon.IconImage = null;
            }
        }

        // Added/Removed only: the device-verification contract needs the
        // registration lifecycle, while a tray app that animates its icon or
        // refreshes its tooltip on a timer would otherwise write a synchronous log
        // line per tick from the UI thread and push the boot/takeover/lease lines
        // out of the capped log.
        if (change is TrayChange.Added or TrayChange.Removed)
        {
            Log.Info($"Tray icon {change}: '{icon?.Tip}' (hwnd 0x{parsed.Hwnd:X}, uid {parsed.Uid}, " +
                     $"version {icon?.Version ?? 0}, guid {(parsed.Flags & TrayProtocol.NifGuid) != 0}).");
        }
        IconsChanged?.Invoke();
        return 1;
    }

    private static string Describe(uint nim) => nim switch
    {
        TrayProtocol.NimAdd => "NIM_ADD",
        TrayProtocol.NimModify => "NIM_MODIFY",
        TrayProtocol.NimDelete => "NIM_DELETE",
        TrayProtocol.NimSetFocus => "NIM_SETFOCUS",
        TrayProtocol.NimSetVersion => "NIM_SETVERSION",
        _ => $"NIM_{nim}",
    };

    // Double-click state: the host owns double-click detection (see SendClick).
    private nint _lastPrimaryHwnd;
    private uint _lastPrimaryUid;
    private ulong _lastPrimaryAtMs;

    /// <summary>Forwards a click to the icon's owner using the negotiated protocol
    /// version. Outbound High→Medium messages are UIPI-unrestricted, so WSGM's
    /// elevation helps on this path.</summary>
    /// <param name="icon">The icon that was activated.</param>
    /// <param name="contextMenu">True for a context-menu (right-click) activation.</param>
    /// <param name="screenX">Screen X of the activation, for cursor parking and the v4 coordinate protocol.</param>
    /// <param name="screenY">Screen Y of the activation, for cursor parking and the v4 coordinate protocol.</param>
    public void SendClick(TrayIconTable.TrayIcon icon, bool contextMenu, int screenX, int screenY)
    {
        if (_disposed)
        {
            return;
        }
        if (icon.CallbackMessage == 0)
        {
            // NIF_MESSAGE never arrived — the app cannot receive interactions.
            Log.Warn($"Tray click dropped: '{icon.Tip}' registered no callback message.");
            return;
        }
        if (!TrayProtocol.IsRelayableCallback(icon.CallbackMessage))
        {
            // Relay only application-defined messages. Registration still succeeds so shell32
            // does not retry NIM_ADD, and one-shot logging preserves the bounded diagnostic log.
            if (!_loggedBlockedCallback)
            {
                _loggedBlockedCallback = true;
                Log.Warn($"Tray click dropped: '{icon.Tip}' (hwnd 0x{icon.Hwnd:X}) registered callback " +
                         $"0x{icon.CallbackMessage:X}, outside the application-defined range " +
                         "0x400..0xFFFF; the icon stays registered. Logged once per tray host.");
            }
            return;
        }
        if (!NativeMethods.IsWindow(icon.Hwnd))
        {
            Log.Info($"Tray click dropped: owner window 0x{icon.Hwnd:X} is gone.");
            return;
        }

        // Without this, a menu the app pops can't take foreground and won't
        // dismiss on outside taps (ManagedShell does the same before button-downs).
        NativeMethods.GetWindowThreadProcessId(icon.Hwnd, out var pid);
        NativeMethods.AllowSetForegroundWindow(pid);

        // WinForms-hosted tray menus (Handheld Companion et al.) place themselves
        // at GetCursorPos and IGNORE the message coordinates entirely — with a
        // gamepad/synthetic activation the physical cursor is somewhere stale, so
        // the menu would pop at a random spot. Park the cursor on the anchor
        // first. A one-shot cursor MOVE, not input interception (invariant 2).
        NativeMethods.SetCursorPos(screenX, screenY);

        // Double-click detection is the HOST's job — Explorer itself watches
        // GetDoubleClickTime and sends WM_LBUTTONDBLCLK as the callback; apps
        // (WinForms NotifyIcon.DoubleClick — Handheld Companion's open-window
        // action) cannot reconstruct it from two single clicks.
        var now = (ulong)Environment.TickCount64;
        var isDouble = !contextMenu
            && icon.Hwnd == _lastPrimaryHwnd && icon.Uid == _lastPrimaryUid
            && now - _lastPrimaryAtMs <= NativeMethods.GetDoubleClickTime();
        string kind;
        if (contextMenu)
        {
            kind = "context";
            Notify(icon, NativeMethods.WmRButtonDown, screenX, screenY);
            Notify(icon, NativeMethods.WmRButtonUp, screenX, screenY);
            // Explorer sends the select/context notifications from version 3 on,
            // not only v4 as documented — many apps rely on exactly that
            // (ManagedShell: "documented as version 4, but Explorer does this
            // for version 3 as well").
            if (icon.Version >= 3)
            {
                Notify(icon, NativeMethods.WmContextMenu, screenX, screenY);
            }
        }
        else if (isDouble)
        {
            kind = "double";
            _lastPrimaryAtMs = 0;
            Notify(icon, NativeMethods.WmLButtonDblClk, screenX, screenY);
            Notify(icon, NativeMethods.WmLButtonUp, screenX, screenY);
        }
        else
        {
            kind = "primary";
            _lastPrimaryHwnd = icon.Hwnd;
            _lastPrimaryUid = icon.Uid;
            _lastPrimaryAtMs = now;
            Notify(icon, NativeMethods.WmLButtonDown, screenX, screenY);
            Notify(icon, NativeMethods.WmLButtonUp, screenX, screenY);
            if (icon.Version >= 3)
            {
                Notify(icon, NativeMethods.NinSelect, screenX, screenY);
            }
        }
        Log.Info($"Tray click forwarded to '{icon.Tip}' ({kind}, v{icon.Version}, cb 0x{icon.CallbackMessage:X}, hwnd 0x{icon.Hwnd:X}).");
    }

    private void Notify(TrayIconTable.TrayIcon icon, uint notification, int x, int y)
    {
        nint wParam;
        nint lParam;
        if (icon.Version >= 4)
        {
            // v4: wParam = packed screen coords, lParam = LOWORD(event)|HIWORD(uid).
            wParam = (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
            lParam = (nint)(((icon.Uid & 0xFFFF) << 16) | (notification & 0xFFFF));
        }
        else
        {
            // Legacy: wParam = uid, lParam = the mouse message.
            wParam = (nint)icon.Uid;
            lParam = (nint)notification;
        }
        NativeMethods.SendNotifyMessageW(icon.Hwnd, icon.CallbackMessage, wParam, lParam);
    }

    /// <summary>Destroys the protocol windows and drops all icons. When explorer
    /// starts next (desktop mode), its own taskbar broadcasts TaskbarCreated and
    /// the apps re-home their icons to it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_instance == this)
        {
            _instance = null;
        }
        foreach (var icon in _table.Icons)
        {
            (icon.IconImage as Bitmap)?.Dispose();
            icon.IconImage = null;
        }
        _table.Clear();
        if (_notifyHwnd != 0)
        {
            NativeMethods.DestroyWindow(_notifyHwnd);
            _notifyHwnd = 0;
        }
        if (_trayHwnd != 0)
        {
            if (!NativeMethods.DestroyWindow(_trayHwnd))
            {
                Log.Warn($"DestroyWindow(Shell_TrayWnd) failed (error {Marshal.GetLastWin32Error()}).");
            }
            _trayHwnd = 0;
        }
        Log.Info("Tray host destroyed.");
    }
}
