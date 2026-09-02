using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Interop;

/// <summary>Which registered power setting reported a display on/off transition.
///
/// <para>Windows offers no way to <i>query</i> the current display power state from
/// user mode, so a notification is the only mechanism there is. WSGM therefore listens on
/// all three that exist and records which one spoke — a wake that one setting misses can
/// still arrive on another, and the source name in the log is what makes a missing
/// notification diagnosable from a pasted device log instead of guesswork.</para></summary>
public enum DisplayStateSource
{
    /// <summary>GUID_SESSION_DISPLAY_STATUS — the display of this session. The primary
    /// source, the one Microsoft documents for interactive applications, and the only one
    /// that may be trusted to say the screen went dark.</summary>
    Session,

    /// <summary>GUID_CONSOLE_DISPLAY_STATE — the console session's display. Redundant
    /// wake source only: it describes whichever session owns the console, so acting on
    /// its "off" would mute the wrong session after a fast user switch.</summary>
    Console,

    /// <summary>GUID_MONITOR_POWER_ON — the superseded pre-Windows-8 setting. Modern
    /// Windows may never send it; treated as a best-effort wake source only.</summary>
    LegacyMonitor,
}

/// <summary>A raw message-only (HWND_MESSAGE) window whose queue is pumped by the
/// Avalonia UI thread. Hosts RegisterHotKey registrations.</summary>
public sealed unsafe class MessageWindow : IDisposable
{
    private static MessageWindow? _instance;
    private nint _hwnd;
    private uint _shellHookMessage;
    private bool _shellHookRegistered;
    private nint _displayNotify;
    private nint _consoleDisplayNotify;
    private nint _legacyDisplayNotify;
    private nint _volumeNotify;
    private bool _sessionNotify;

    /// <summary>Create() is the only entry point: a directly constructed instance
    /// would carry Handle == 0, and RegisterHotKey on hwnd 0 registers a thread
    /// hotkey the WndProc never sees.</summary>
    private MessageWindow()
    {
    }

    /// <summary>Gets the native handle of the message-only window.</summary>
    public nint Handle => _hwnd;

    /// <summary>Raised on the Avalonia UI thread with the hotkey id.</summary>
    public event Action<int>? HotkeyPressed;

    /// <summary>Raised on the Avalonia UI thread when a display turns on or off, with the
    /// MONITOR_DISPLAY_STATE value (0 = off, 1 = on, 2 = dimmed) and which of the three
    /// registered power settings reported it. Subscribers must weigh the source: only
    /// <see cref="DisplayStateSource.Session"/> describes this session's own display.
    /// </summary>
    public event Action<int, DisplayStateSource>? DisplayStateChanged;

    /// <summary>Raised on the Avalonia UI thread when this session's desktop is unlocked —
    /// an independent "the user is back at a lit screen" signal for wakes where no display
    /// notification is delivered.</summary>
    public event Action? SessionUnlocked;

    /// <summary>Raised on the Avalonia UI thread when this session's desktop is locked.</summary>
    /// <remarks>
    /// The counterpart to <see cref="SessionUnlocked"/>, and the point at which anything holding
    /// hardware the user is no longer in front of should let go of it.
    /// </remarks>
    public event Action? SessionLocked;

    /// <summary>Raised on the Avalonia UI thread when this interactive session logs off.</summary>
    public event Action? SessionEnding;

    /// <summary>Raised on the Avalonia UI thread when the system is about to suspend.</summary>
    /// <remarks>
    /// Delivered before the machine goes down and on a deadline Windows does not extend, so
    /// subscribers must start their work and return rather than block this notification.
    /// </remarks>
    public event Action? SystemSuspending;

    /// <summary>Raised on the Avalonia UI thread when the system resumed from suspend.</summary>
    /// <remarks>
    /// Raised for PBT_APMRESUMEAUTOMATIC and PBT_APMRESUMESUSPEND alike. Windows sends the first
    /// on every resume and adds the second only when the user caused it, so a subscriber that
    /// listened for one of them would miss half the wakes; it can fire twice for one resume and
    /// subscribers must be idempotent.
    /// </remarks>
    public event Action? SystemResumed;

    /// <summary>Raised on the Avalonia UI thread for a shell-hook notification.
    /// Its delegate receives the HSHELL_* event code followed by the event-specific
    /// lParam supplied by the shell.</summary>
    public event Action<nint, nint>? ShellHookReceived;

    /// <summary>Raised on the Avalonia UI thread when any volume appeared or
    /// disappeared. The argument is true for arrival, false for removal.</summary>
    /// <remarks>
    /// Deliberately carries no device identity. The payload's device path would have
    /// to be mapped back to a mount point, which is fragile and reader-specific;
    /// rescanning drive letters answers the same question and works for every reader.
    /// Subscribers must debounce and settle: the notification fires before Windows has
    /// finished mounting the volume and assigning its letter.
    /// </remarks>
    public event Action<bool>? VolumeChanged;

    /// <summary>Gets or creates the process-wide message-only window.</summary>
    /// <returns>The singleton message window.</returns>
    public static MessageWindow Create()
    {
        if (_instance is not null)
        {
            return _instance;
        }

        var hwnd = CreateMessageOnlyWindow(
            "WSGM.MessageWindow", &WndProc, "Failed to create message window");
        _instance = new MessageWindow { _hwnd = hwnd };
        _instance.RegisterSessionNotifications();
        return _instance;
    }

    /// <summary>Registers this window to receive shell-hook notifications.
    /// The caller must later call <see cref="DeregisterShellHook"/> before a
    /// different shell takes ownership of the desktop.</summary>
    /// <returns>True when the registration is active.</returns>
    public bool RegisterShellHook()
    {
        if (_shellHookRegistered)
        {
            return true;
        }

        _shellHookMessage = NativeMethods.RegisterWindowMessageW("SHELLHOOK");
        if (_shellHookMessage == 0)
        {
            Log.Warn($"RegisterWindowMessage(SHELLHOOK) failed (error {Marshal.GetLastWin32Error()}).");
            return false;
        }
        if (!NativeMethods.RegisterShellHookWindow(_hwnd))
        {
            Log.Warn($"RegisterShellHookWindow failed (error {Marshal.GetLastWin32Error()}).");
            _shellHookMessage = 0;
            return false;
        }

        _shellHookRegistered = true;
        Log.Info("Shell-hook window registered.");
        return true;
    }

    /// <summary>Stops this window receiving shell-hook notifications.</summary>
    public void DeregisterShellHook()
    {
        if (!_shellHookRegistered)
        {
            return;
        }

        if (!NativeMethods.DeregisterShellHookWindow(_hwnd))
        {
            Log.Warn($"DeregisterShellHookWindow failed (error {Marshal.GetLastWin32Error()}).");
        }
        _shellHookRegistered = false;
        _shellHookMessage = 0;
        Log.Info("Shell-hook window deregistered.");
    }

    /// <summary>Subscribes this window to display on/off notifications. Idempotent;
    /// safe to call when the feature toggle turns on at runtime.
    ///
    /// <para>THREE power settings are registered, not one.
    /// <c>GUID_SESSION_DISPLAY_STATUS</c> is the primary and the only one that describes
    /// this session's own display — it stays the sole source allowed to report the screen
    /// going dark. <c>GUID_CONSOLE_DISPLAY_STATE</c> and the superseded
    /// <c>GUID_MONITOR_POWER_ON</c> are redundant wake sources: a subscriber may act on
    /// them only to undo something, never to start it. Registering the extras costs one
    /// call each and a setting Windows never sends simply stays silent.</para></summary>
    /// <returns>True when the primary registration is active.</returns>
    public bool RegisterDisplayStateNotifications()
    {
        if (_displayNotify != 0)
        {
            return true;
        }
        _displayNotify = NativeMethods.RegisterPowerSettingNotification(
            _hwnd, NativeMethods.GuidSessionDisplayStatus, NativeMethods.DeviceNotifyWindowHandle);
        if (_displayNotify == 0)
        {
            Log.Warn("RegisterPowerSettingNotification(session display status) failed "
                + $"(error {Marshal.GetLastWin32Error()}).");
        }
        _consoleDisplayNotify = NativeMethods.RegisterPowerSettingNotification(
            _hwnd, NativeMethods.GuidConsoleDisplayState, NativeMethods.DeviceNotifyWindowHandle);
        _legacyDisplayNotify = NativeMethods.RegisterPowerSettingNotification(
            _hwnd, NativeMethods.GuidMonitorPowerOn, NativeMethods.DeviceNotifyWindowHandle);
        Log.Info($"Display-state notifications registered (session={_displayNotify != 0}, "
            + $"console={_consoleDisplayNotify != 0}, legacy={_legacyDisplayNotify != 0}).");
        return _displayNotify != 0;
    }

    /// <summary>Stops this window receiving display on/off notifications.</summary>
    public void DeregisterDisplayStateNotifications()
    {
        var any = _displayNotify != 0 || _consoleDisplayNotify != 0
            || _legacyDisplayNotify != 0;
        if (!any)
        {
            return;
        }
        UnregisterPowerSetting(ref _displayNotify, "session display status");
        UnregisterPowerSetting(ref _consoleDisplayNotify, "console display state");
        UnregisterPowerSetting(ref _legacyDisplayNotify, "monitor power on");
        Log.Info("Display-state notifications deregistered.");
    }

    private void RegisterSessionNotifications()
    {
        _sessionNotify = NativeMethods.WTSRegisterSessionNotification(
            _hwnd,
            NativeMethods.NotifyForThisSession);
        if (!_sessionNotify)
        {
            Log.Warn("WTSRegisterSessionNotification failed "
                + $"(error {Marshal.GetLastWin32Error()}).");
        }
    }

    private void UnregisterSessionNotifications()
    {
        if (!_sessionNotify)
        {
            return;
        }
        if (!NativeMethods.WTSUnRegisterSessionNotification(_hwnd))
        {
            Log.Warn("WTSUnRegisterSessionNotification failed "
                + $"(error {Marshal.GetLastWin32Error()}).");
        }
        _sessionNotify = false;
    }

    /// <summary>Subscribes this window to volume arrival and removal. Idempotent.
    /// </summary>
    /// <remarks>
    /// This replaces guessing at a card reader's identity. The Playnite-era approach
    /// watched WMI for a <c>Win32_DiskDrive</c> whose model matched a hard-coded
    /// string, which only ever worked for the reader it was written against. A device-interface
    /// registration for <c>GUID_DEVINTERFACE_VOLUME</c> is reader-agnostic, bus
    /// agnostic and pure Win32.
    /// </remarks>
    /// <returns>True when the registration is active.</returns>
    public bool RegisterVolumeNotifications()
    {
        if (_volumeNotify != 0)
        {
            return true;
        }
        var filter = new NativeMethods.DevBroadcastDeviceInterface
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.DevBroadcastDeviceInterface>(),
            DeviceType = NativeMethods.DbtDevTypDeviceInterface,
            ClassGuid = NativeMethods.GuidDevInterfaceVolume,
        };
        _volumeNotify = NativeMethods.RegisterDeviceNotification(
            _hwnd, filter, NativeMethods.DeviceNotifyWindowHandle);
        if (_volumeNotify == 0)
        {
            Log.Warn("RegisterDeviceNotification(volume interface) failed "
                + $"(error {Marshal.GetLastWin32Error()}) — card changes fall back to polling.");
            return false;
        }
        Log.Info("Volume arrival/removal notifications registered.");
        return true;
    }

    /// <summary>Stops this window receiving volume arrival and removal notifications.
    /// </summary>
    public void DeregisterVolumeNotifications()
    {
        if (_volumeNotify == 0)
        {
            return;
        }
        if (!NativeMethods.UnregisterDeviceNotification(_volumeNotify))
        {
            Log.Warn("UnregisterDeviceNotification(volume interface) failed "
                + $"(error {Marshal.GetLastWin32Error()}).");
        }
        _volumeNotify = 0;
        Log.Info("Volume arrival/removal notifications deregistered.");
    }

    private static void UnregisterPowerSetting(ref nint handle, string name)
    {
        if (handle == 0)
        {
            return;
        }
        if (!NativeMethods.UnregisterPowerSettingNotification(handle))
        {
            Log.Warn($"UnregisterPowerSettingNotification({name}) failed "
                + $"(error {Marshal.GetLastWin32Error()}).");
        }
        handle = 0;
    }

    /// <summary>Shared class-registration + window-creation path for the process's
    /// message-only (HWND_MESSAGE) windows. Class registration is idempotent:
    /// ERROR_CLASS_ALREADY_EXISTS (1410) is benign — a re-create after a destroy
    /// reuses the still-registered class. Any other registration failure is only
    /// logged, because CreateWindowExW then fails on the unknown class and throws
    /// <paramref name="failureMessage"/> anyway.</summary>
    internal static nint CreateMessageOnlyWindow(
        string className,
        delegate* unmanaged<nint, uint, nint, nint, nint> wndProc,
        string failureMessage)
    {
        var hInstance = NativeMethods.GetModuleHandleW(0);
        var terminatedClassName = className + "\0";
        fixed (char* pClassName = terminatedClassName)
        {
            var wc = new NativeMethods.WndClassW
            {
                lpfnWndProc = wndProc,
                hInstance = hInstance,
                lpszClassName = (nint)pClassName,
            };
            if (NativeMethods.RegisterClassW(&wc) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1410)
                {
                    Log.Warn($"RegisterClassW({className}) failed (error {error}).");
                }
            }
        }

        var hwnd = NativeMethods.CreateWindowExW(
            0, className, null, 0,
            0, 0, 0, 0,
            NativeMethods.HwndMessage, 0, hInstance, 0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException(failureMessage);
        }
        return hwnd;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        var instance = _instance;
        if (instance is null)
        {
            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
        if (msg == NativeMethods.WmHotkey)
        {
            var id = (int)wParam;
            Dispatcher.UIThread.Post(() => instance.HotkeyPressed?.Invoke(id));
            return 0;
        }
        if (msg == NativeMethods.WmPowerBroadcast
            && wParam == NativeMethods.PbtPowerSettingChange
            && lParam != 0)
        {
            var setting = Marshal.PtrToStructure<NativeMethods.PowerBroadcastSetting>(lParam);
            // The same window could later carry other power settings; only the three
            // display settings are ours, and only a 4-byte DWORD payload is the
            // documented shape.
            DisplayStateSource? source = null;
            if (setting.PowerSetting == NativeMethods.GuidSessionDisplayStatus)
            {
                source = DisplayStateSource.Session;
            }
            else if (setting.PowerSetting == NativeMethods.GuidConsoleDisplayState)
            {
                source = DisplayStateSource.Console;
            }
            else if (setting.PowerSetting == NativeMethods.GuidMonitorPowerOn)
            {
                source = DisplayStateSource.LegacyMonitor;
            }
            if (source is { } reported && setting.DataLength >= 4)
            {
                var state = Marshal.ReadInt32(
                    lParam + (int)Marshal.OffsetOf<NativeMethods.PowerBroadcastSetting>(
                        nameof(NativeMethods.PowerBroadcastSetting.Data)));
                Dispatcher.UIThread.Post(
                    () => instance.DisplayStateChanged?.Invoke(state, reported));
            }
            return 1;
        }
        if (msg == NativeMethods.WmPowerBroadcast
            && wParam == NativeMethods.PbtApmSuspend)
        {
            Dispatcher.UIThread.Post(() => instance.SystemSuspending?.Invoke());
            return 1;
        }
        if (msg == NativeMethods.WmPowerBroadcast
            && (wParam == NativeMethods.PbtApmResumeAutomatic
                || wParam == NativeMethods.PbtApmResumeSuspend))
        {
            Dispatcher.UIThread.Post(() => instance.SystemResumed?.Invoke());
            return 1;
        }
        if (msg == NativeMethods.WmWtsSessionChange && instance._sessionNotify)
        {
            if (wParam == NativeMethods.WtsSessionLock)
            {
                Dispatcher.UIThread.Post(() => instance.SessionLocked?.Invoke());
                return 0;
            }
            if (wParam == NativeMethods.WtsSessionUnlock)
            {
                Dispatcher.UIThread.Post(() => instance.SessionUnlocked?.Invoke());
                return 0;
            }
            if (wParam == NativeMethods.WtsSessionLogoff)
            {
                Dispatcher.UIThread.Post(() => instance.SessionEnding?.Invoke());
                return 0;
            }
        }
        if (msg == NativeMethods.WmDeviceChange && instance._volumeNotify != 0
            && (wParam == NativeMethods.DbtDeviceArrival
                || wParam == NativeMethods.DbtDeviceRemoveComplete))
        {
            // The payload is not read: see the VolumeChanged remarks. Returning
            // TRUE is the documented answer for a device event that is not a
            // removal QUERY, which this window never registers for.
            var arrived = wParam == NativeMethods.DbtDeviceArrival;
            Dispatcher.UIThread.Post(() => instance.VolumeChanged?.Invoke(arrived));
            return 1;
        }
        if (msg == instance._shellHookMessage && instance._shellHookRegistered)
        {
            Dispatcher.UIThread.Post(() => instance.ShellHookReceived?.Invoke(wParam, lParam));
            return 0;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>Destroys the native window and clears the process singleton.</summary>
    public void Dispose()
    {
        DeregisterShellHook();
        DeregisterDisplayStateNotifications();
        UnregisterSessionNotifications();
        DeregisterVolumeNotifications();
        if (_hwnd != 0)
        {
            if (!NativeMethods.DestroyWindow(_hwnd))
            {
                // Fails from the wrong thread; the handle then leaks until exit.
                Log.Warn($"DestroyWindow(message window) failed (error {Marshal.GetLastWin32Error()}).");
            }
            _hwnd = 0;
        }
        _instance = null;
    }
}
