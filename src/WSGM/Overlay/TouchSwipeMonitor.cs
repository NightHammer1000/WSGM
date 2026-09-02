using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>A screen edge from which WSGM recognizes an inward swipe.</summary>
public enum ScreenEdge
{
    /// <summary>The bottom edge of the primary display.</summary>
    Bottom,

    /// <summary>The right edge of the primary display.</summary>
    Right,

    /// <summary>The left edge of the primary display.</summary>
    Left,

    /// <summary>The top edge of the primary display.</summary>
    Top,
}

/// <summary>
/// Turns inward swipes from enabled screen edges into <see cref="Triggered"/>
/// events by observing the touch digitizer through Raw Input (WM_INPUT on a
/// message-only window, RIDEV_INPUTSINK).
///
/// Purely observational: only the touch-screen HID device class is registered
/// (never mouse or keyboard), nothing is consumed, and no window takes part in
/// hit-testing — the foreground game keeps receiving every event untouched.
/// Contact coordinates are parsed straight from the raw HID reports and scaled
/// from the digitizer's logical range to primary-screen physical pixels (the
/// built-in panel is assumed to be the primary display, as before).
///
/// Fallback knowledge if raw HID parsing ever fails on a device: a hit-testable
/// strip window (layered alpha 1, NOT 0 — fully transparent layered windows are
/// click-through) whose WM_NCHITTEST returns HTCLIENT only when
/// GetMessageExtraInfo() carries MI_WP_SIGNATURE ((extra &amp; 0xFFFFFF00) ==
/// 0xFF515700, i.e. touch/pen-synthesized) and HTTRANSPARENT for real mouse.
/// </summary>
public sealed unsafe class TouchSwipeMonitor : IDisposable
{
    private const string WindowClassName = "WSGM.RawTouchWindow";
    private const int MinimumBandPx = 48;
    private const int TriggerDistancePx = 48;
    private const ulong TriggerTimeMs = 800;

    private static readonly object Gate = new();
    // Raw-input registration is per-process per HID usage: registering a second
    // window RETARGETS delivery, and one RIDEV_REMOVE kills it for everyone. So
    // ONE shared message-only window owns the registration, WM_INPUT is dispatched
    // to every live monitor in this registry, and the registration is dropped only
    // when the last monitor is disposed (the Settings test overlay's monitor must
    // never take the live shell's edge swipes down with it).
    private static readonly List<TouchSwipeMonitor> Instances = [];
    private static nint _sharedHwnd;

    private sealed class DeviceCaps
    {
        public nint PreparsedData;
        public ushort LinkCollection;
        public int XMin;
        public int XMax;
        public int YMin;
        public int YMax;
        /// <summary>Usage-list capacity for HidP_GetUsages, from HidP_GetCaps
        /// (NumberInputDataIndices bounds the usages one input report can carry).</summary>
        public int UsageListLength = 16;
        public bool Usable;
        public bool WarnedBadReport;
        public bool WarnedUsagesFailed;
    }

    private readonly Dictionary<nint, DeviceCaps> _devices = [];
    private ushort[] _usageBuffer = new ushort[16];
    private byte[] _inputBuffer = new byte[256];
    private bool _bottomEnabled;
    private bool _rightEnabled;
    private bool _leftEnabled;
    private bool _topEnabled;
    private int _bandPx = MinimumBandPx;
    private bool _armed = true;
    private bool _contactWasDown;
    private bool _tracking;
    private bool _bottomCandidate;
    private bool _rightCandidate;
    private bool _leftCandidate;
    private bool _topCandidate;
    private int _startX;
    private int _startY;
    private ulong _startedAt;
    private int _screenW;
    private int _screenH;
    private int _dispatchPending;
    private bool _loggedFirstReport;
    private bool _disposed;

    /// <summary>Raised on the Avalonia UI thread with the edge that was swiped.</summary>
    public event Action<ScreenEdge>? Triggered;

    /// <summary>Raised on the Avalonia UI thread with primary-screen pixel
    /// coordinates for every NEW touch contact while <see cref="WatchTaps"/> is on.
    /// Lets the overlay dismiss itself on taps outside its bounds.</summary>
    public event Action<int, int>? TappedAt;

    /// <summary>Enables <see cref="TappedAt"/> (overlay open).</summary>
    public bool WatchTaps { get; set; }

    /// <summary>Creates a monitor and joins the shared process-wide raw-input registration.</summary>
    public TouchSwipeMonitor()
    {
        lock (Gate)
        {
            if (Instances.Count == 0)
            {
                CreateSharedWindowAndRegister();
            }
            Instances.Add(this);
        }
    }

    private static void CreateSharedWindowAndRegister()
    {
        // Class registration + HWND_MESSAGE creation share MessageWindow's code
        // path; the raw-input registration below stays entirely local so its
        // semantics (dedicated INPUTSINK target, last-monitor teardown) are
        // unchanged.
        _sharedHwnd = MessageWindow.CreateMessageOnlyWindow(
            WindowClassName, &WndProc, "Failed to create raw touch input window.");

        var devices = new[]
        {
            new NativeMethods.RawInputDevice
            {
                usUsagePage = NativeMethods.HidUsagePageDigitizer,
                usUsage = NativeMethods.HidUsageTouchScreen,
                dwFlags = NativeMethods.RidevInputSink | NativeMethods.RidevDevNotify,
                hwndTarget = _sharedHwnd,
            },
        };
        if (!NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>()))
        {
            Log.Warn($"Raw touch input registration failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        else
        {
            Log.Info($"Raw touch input registered (HID digitizer sink, foreground {DescribeForeground()}).");
        }
    }

    /// <summary>Applies the enabled-edge and activation-band settings.</summary>
    /// <param name="gestures">The persisted gesture configuration to observe.</param>
    public void Configure(GestureConfig gestures)
    {
        _bottomEnabled = gestures.BottomEdge;
        _rightEnabled = gestures.RightEdgeSteamQuickAccess;
        _leftEnabled = gestures.LeftEdgeSteamMenu;
        _topEnabled = gestures.TopEdge;
        _bandPx = Math.Max(MinimumBandPx, gestures.StripThickness);
        _tracking = false;
        // Re-applied on every config reload, which the shell does often, so this restated an
        // unchanged gesture set 1,162 times in one session.
        Log.Change(
            "touch.edges",
            $"Touch edge swipes configured (bottom={_bottomEnabled}, top={_topEnabled}, " +
            $"left-steam={_leftEnabled}, right-qam={_rightEnabled}, band={_bandPx}px).");
    }

    /// <summary>Resume gesture detection (overlay closed).</summary>
    public void Arm()
    {
        if (!_disposed && !_armed)
        {
            _armed = true;
            // Reset the one-shot so every arm cycle proves whether raw reports
            // still flow — swipes reportedly die when specific apps take focus.
            _loggedFirstReport = false;
            Log.Info($"Touch edge swipes armed (foreground {DescribeForeground()}).");
        }
    }

    private static string DescribeForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0)
        {
            return "none";
        }
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            return $"0x{hwnd:X} ({System.Diagnostics.Process.GetProcessById((int)pid).ProcessName})";
        }
        catch
        {
            return $"0x{hwnd:X}";
        }
    }

    /// <summary>Suspend gesture detection (overlay open).</summary>
    public void Disarm()
    {
        if (_armed)
        {
            _armed = false;
            _tracking = false;
            Log.Info("Touch edge swipes disarmed.");
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (hwnd == _sharedHwnd)
        {
            TouchSwipeMonitor[] monitors;
            lock (Gate)
            {
                monitors = [.. Instances];
            }
            try
            {
                if (message == NativeMethods.WmInput)
                {
                    // hRawInput (lParam) is only valid during synchronous processing;
                    // read here, then still let DefWindowProc do the WM_INPUT cleanup.
                    foreach (var monitor in monitors)
                    {
                        if (!monitor._disposed)
                        {
                            monitor.ProcessRawInput(lParam);
                        }
                    }
                }
                else if (message == NativeMethods.WmInputDeviceChange)
                {
                    // With RIDEV_DEVNOTIFY, GIDC_ARRIVAL also fires at registration
                    // for devices already present — proves the WM_INPUT channel is
                    // alive before the first touch.
                    if (wParam == NativeMethods.GidcArrival)
                    {
                        Log.Info($"Touch digitizer 0x{lParam:X} present.");
                    }
                    else if (wParam == NativeMethods.GidcRemoval)
                    {
                        foreach (var monitor in monitors)
                        {
                            if (!monitor._disposed)
                            {
                                monitor.EvictDevice(lParam);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Raw touch input processing failed", ex);
            }
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void ProcessRawInput(nint hRawInput)
    {
        var headerSize = (uint)sizeof(NativeMethods.RawInputHeader);
        uint size = 0;
        if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RidInput, 0, ref size, headerSize) != 0 ||
            size == 0)
        {
            return;
        }

        if (_inputBuffer.Length < size)
        {
            _inputBuffer = new byte[size];
        }

        fixed (byte* buffer = _inputBuffer)
        {
            if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RidInput, (nint)buffer, ref size, headerSize) ==
                unchecked((uint)-1))
            {
                return;
            }

            var header = *(NativeMethods.RawInputHeader*)buffer;
            if (header.dwType != NativeMethods.RimTypeHid)
            {
                return;
            }

            // Before any parsing, so the log separates "WM_INPUT never arrives"
            // from "reports arrive but don't parse". Re-logged once per arm
            // cycle to show whether delivery survives foreground changes.
            if (!_loggedFirstReport)
            {
                _loggedFirstReport = true;
                Log.Info($"Raw touch reports arriving (foreground {DescribeForeground()}).");
            }

            var caps = GetDeviceCaps(header.hDevice);
            if (caps is null)
            {
                return;
            }

            // The RAWHID prefix (dwSizeHid, dwCount) must fit before it is read,
            // and the report area is bounds-checked in 64-bit so inconsistent
            // dwSizeHid*dwCount cannot wrap the 32-bit multiply past the buffer.
            if (size < headerSize + 8)
            {
                return;
            }
            var hid = buffer + sizeof(NativeMethods.RawInputHeader);
            var reportSize = *(uint*)hid;
            var reportCount = *(uint*)(hid + 4);
            var reports = hid + 8;
            if (reportSize == 0 || (ulong)reportSize * reportCount > size - headerSize - 8)
            {
                return;
            }

            for (uint i = 0; i < reportCount; i++)
            {
                ProcessReport(caps, (nint)(reports + i * reportSize), reportSize);
            }
        }
    }

    private DeviceCaps? GetDeviceCaps(nint hDevice)
    {
        if (_devices.TryGetValue(hDevice, out var cached))
        {
            return cached.Usable ? cached : null;
        }

        var caps = BuildDeviceCaps(hDevice);
        _devices[hDevice] = caps;
        return caps.Usable ? caps : null;
    }

    private DeviceCaps BuildDeviceCaps(nint hDevice)
    {
        var caps = new DeviceCaps();

        uint ppSize = 0;
        NativeMethods.GetRawInputDeviceInfoW(hDevice, NativeMethods.RidiPreparsedData, 0, ref ppSize);
        if (ppSize == 0)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: no preparsed HID data.");
            return caps;
        }

        var preparsed = Marshal.AllocHGlobal((int)ppSize);
        if (NativeMethods.GetRawInputDeviceInfoW(hDevice, NativeMethods.RidiPreparsedData, preparsed, ref ppSize) ==
            unchecked((uint)-1))
        {
            Marshal.FreeHGlobal(preparsed);
            Log.Warn($"Touch digitizer 0x{hDevice:X}: could not read preparsed HID data.");
            return caps;
        }
        caps.PreparsedData = preparsed;

        if (NativeMethods.HidP_GetCaps(preparsed, out var hidCaps) != NativeMethods.HidpStatusSuccess ||
            hidCaps.UsagePage != NativeMethods.HidUsagePageDigitizer ||
            hidCaps.Usage != NativeMethods.HidUsageTouchScreen)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: not a touch-screen collection, ignoring.");
            return caps;
        }

        // Every input usage/value owns one data index, so this bounds how many
        // button usages HidP_GetUsages can ever return for one report.
        caps.UsageListLength = Math.Max(16, (int)hidCaps.NumberInputDataIndices);

        var count = hidCaps.NumberInputValueCaps;
        if (count == 0)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: no input value caps.");
            return caps;
        }

        var valueCaps = new NativeMethods.HidpValueCaps[count];
        if (NativeMethods.HidP_GetValueCaps(NativeMethods.HidpInput, valueCaps, ref count, preparsed) !=
            NativeMethods.HidpStatusSuccess)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: HidP_GetValueCaps failed.");
            return caps;
        }

        // Per contact slot (link collection), the digitizer exposes X/Y on the
        // Generic Desktop page. The lowest collection with both is the primary
        // contact — all a one-finger edge swipe needs.
        var xByCollection = new Dictionary<ushort, (int Min, int Max)>();
        var yByCollection = new Dictionary<ushort, (int Min, int Max)>();
        for (var i = 0; i < count; i++)
        {
            var vc = valueCaps[i];
            if (vc.UsagePage != NativeMethods.HidUsagePageGenericDesktop)
            {
                continue;
            }
            var coversX = vc.IsRange != 0
                ? vc.UsageMin <= NativeMethods.HidUsageX && NativeMethods.HidUsageX <= vc.UsageMax
                : vc.UsageMin == NativeMethods.HidUsageX;
            var coversY = vc.IsRange != 0
                ? vc.UsageMin <= NativeMethods.HidUsageY && NativeMethods.HidUsageY <= vc.UsageMax
                : vc.UsageMin == NativeMethods.HidUsageY;
            if (coversX && !xByCollection.ContainsKey(vc.LinkCollection))
            {
                xByCollection[vc.LinkCollection] = (vc.LogicalMin, vc.LogicalMax);
            }
            if (coversY && !yByCollection.ContainsKey(vc.LinkCollection))
            {
                yByCollection[vc.LinkCollection] = (vc.LogicalMin, vc.LogicalMax);
            }
        }

        var found = false;
        ushort bestCollection = 0;
        foreach (var collection in xByCollection.Keys)
        {
            if (yByCollection.ContainsKey(collection) && (!found || collection < bestCollection))
            {
                bestCollection = collection;
                found = true;
            }
        }
        if (!found)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: no link collection with both X and Y.");
            return caps;
        }

        var x = xByCollection[bestCollection];
        var y = yByCollection[bestCollection];
        if (x.Max <= x.Min || y.Max <= y.Min)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: degenerate logical ranges X {x.Min}..{x.Max}, Y {y.Min}..{y.Max}.");
            return caps;
        }

        caps.LinkCollection = bestCollection;
        caps.XMin = x.Min;
        caps.XMax = x.Max;
        caps.YMin = y.Min;
        caps.YMax = y.Max;
        caps.Usable = true;
        Log.Info($"Touch digitizer 0x{hDevice:X}: link {bestCollection}, X {x.Min}..{x.Max}, Y {y.Min}..{y.Max}.");
        return caps;
    }

    private void EvictDevice(nint hDevice)
    {
        if (_devices.Remove(hDevice, out var caps) && caps.PreparsedData != 0)
        {
            Marshal.FreeHGlobal(caps.PreparsedData);
        }
    }

    private void ProcessReport(DeviceCaps caps, nint report, uint reportLength)
    {
        var tipDown = false;
        if (_usageBuffer.Length < caps.UsageListLength)
        {
            _usageBuffer = new ushort[caps.UsageListLength];
        }
        var usageCount = (uint)_usageBuffer.Length;
        var status = NativeMethods.HidP_GetUsages(
            NativeMethods.HidpInput, NativeMethods.HidUsagePageDigitizer, caps.LinkCollection,
            _usageBuffer, ref usageCount, caps.PreparsedData, report, reportLength);
        if (status == NativeMethods.HidpStatusSuccess)
        {
            for (var i = 0; i < usageCount; i++)
            {
                if (_usageBuffer[i] == NativeMethods.HidUsageTipSwitch)
                {
                    tipDown = true;
                    break;
                }
            }
        }
        else if (!caps.WarnedUsagesFailed)
        {
            // Once per device: a failure here silently reads as contact-up, which
            // would otherwise look like "touch dead" in a pasted log.
            caps.WarnedUsagesFailed = true;
            Log.Warn($"HidP_GetUsages failed (status 0x{status:X8}, buffer {_usageBuffer.Length}) — reports treated as contact-up.");
        }

        if (!tipDown)
        {
            _contactWasDown = false;
            _tracking = false;
            return;
        }

        if (NativeMethods.HidP_GetUsageValue(
                NativeMethods.HidpInput, NativeMethods.HidUsagePageGenericDesktop, caps.LinkCollection,
                NativeMethods.HidUsageX, out var rawX, caps.PreparsedData, report, reportLength) !=
            NativeMethods.HidpStatusSuccess ||
            NativeMethods.HidP_GetUsageValue(
                NativeMethods.HidpInput, NativeMethods.HidUsagePageGenericDesktop, caps.LinkCollection,
                NativeMethods.HidUsageY, out var rawY, caps.PreparsedData, report, reportLength) !=
            NativeMethods.HidpStatusSuccess)
        {
            if (!caps.WarnedBadReport)
            {
                caps.WarnedBadReport = true;
                Log.Warn("Touch digitizer report without X/Y values, ignoring.");
            }
            return;
        }

        if (!_contactWasDown)
        {
            _contactWasDown = true;
            OnContactDown(caps, rawX, rawY);
            return;
        }

        OnContactMove(caps, rawX, rawY);
    }

    private void OnContactDown(DeviceCaps caps, uint rawX, uint rawY)
    {
        _tracking = false;
        var watchTaps = WatchTaps;
        if (!_armed && !watchTaps)
        {
            return;
        }

        _screenW = NativeMethods.GetSystemMetrics(0);
        _screenH = NativeMethods.GetSystemMetrics(1);
        var (x, y) = ScaleToScreen(caps, rawX, rawY);

        if (watchTaps)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && WatchTaps)
                {
                    TappedAt?.Invoke(x, y);
                }
            });
        }

        if (!_armed)
        {
            return;
        }

        _bottomCandidate = _bottomEnabled && y >= _screenH - _bandPx;
        _rightCandidate = _rightEnabled && x >= _screenW - _bandPx;
        _leftCandidate = _leftEnabled && x < _bandPx;
        _topCandidate = _topEnabled && y < _bandPx;
        if (!_bottomCandidate && !_rightCandidate && !_leftCandidate && !_topCandidate)
        {
            return;
        }

        _tracking = true;
        _startX = x;
        _startY = y;
        _startedAt = (ulong)Environment.TickCount64;
        Log.Info(
            $"Touch edge swipe started at {x},{y} " +
            $"(bottom={_bottomCandidate}, right={_rightCandidate}, " +
            $"left={_leftCandidate}, top={_topCandidate}).");
    }

    private void OnContactMove(DeviceCaps caps, uint rawX, uint rawY)
    {
        if (!_tracking)
        {
            return;
        }
        if (!_armed)
        {
            _tracking = false;
            return;
        }
        if ((ulong)Environment.TickCount64 - _startedAt > TriggerTimeMs)
        {
            _tracking = false;
            return;
        }

        var (x, y) = ScaleToScreen(caps, rawX, rawY);
        var triggeredEdge = PickTriggeredEdge(
            _bottomCandidate, _rightCandidate, _leftCandidate, _topCandidate,
            _startX, _startY, x, y, TriggerDistancePx);
        if (triggeredEdge is null)
        {
            return;
        }

        _tracking = false;
        if (Interlocked.Exchange(ref _dispatchPending, 1) != 0)
        {
            return;
        }

        var edge = triggeredEdge.Value;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _dispatchPending, 0);
            if (_disposed)
            {
                return;
            }
            Log.Info($"{edge} touch edge swipe triggered.");
            Triggered?.Invoke(edge);
        });
    }

    /// <summary>Calculates how far a contact has moved inward from its tracked edge.</summary>
    /// <param name="edge">The edge that started the gesture.</param>
    /// <param name="startX">Starting horizontal screen coordinate.</param>
    /// <param name="startY">Starting vertical screen coordinate.</param>
    /// <param name="x">Current horizontal screen coordinate.</param>
    /// <param name="y">Current vertical screen coordinate.</param>
    /// <returns>The signed inward distance in physical pixels.</returns>
    internal static int InwardDistance(ScreenEdge edge, int startX, int startY, int x, int y) => edge switch
    {
        ScreenEdge.Bottom => startY - y,
        ScreenEdge.Right => startX - x,
        ScreenEdge.Left => x - startX,
        ScreenEdge.Top => y - startY,
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    /// <summary>Selects the candidate edge whose inward movement has crossed the
    /// trigger distance by the greatest amount. Tracking all candidates makes
    /// corner-origin gestures follow their movement instead of an arbitrary edge priority.</summary>
    /// <param name="bottomCandidate">Whether the contact began inside the bottom band.</param>
    /// <param name="rightCandidate">Whether the contact began inside the right band.</param>
    /// <param name="leftCandidate">Whether the contact began inside the left band.</param>
    /// <param name="topCandidate">Whether the contact began inside the top band.</param>
    /// <param name="startX">Starting horizontal screen coordinate.</param>
    /// <param name="startY">Starting vertical screen coordinate.</param>
    /// <param name="x">Current horizontal screen coordinate.</param>
    /// <param name="y">Current vertical screen coordinate.</param>
    /// <param name="triggerDistance">Required inward distance in physical pixels.</param>
    /// <returns>The movement-matching edge, or null while none has crossed the threshold.</returns>
    internal static ScreenEdge? PickTriggeredEdge(
        bool bottomCandidate, bool rightCandidate, bool leftCandidate, bool topCandidate,
        int startX, int startY, int x, int y, int triggerDistance)
    {
        ScreenEdge? bestEdge = null;
        var bestDistance = triggerDistance - 1;
        Consider(ScreenEdge.Bottom, bottomCandidate);
        Consider(ScreenEdge.Right, rightCandidate);
        Consider(ScreenEdge.Left, leftCandidate);
        Consider(ScreenEdge.Top, topCandidate);
        return bestEdge;

        void Consider(ScreenEdge edge, bool candidate)
        {
            if (!candidate)
            {
                return;
            }
            var distance = InwardDistance(edge, startX, startY, x, y);
            if (distance > bestDistance)
            {
                bestDistance = distance;
                bestEdge = edge;
            }
        }
    }

    private (int X, int Y) ScaleToScreen(DeviceCaps caps, uint rawX, uint rawY)
    {
        var x = (int)(((long)rawX - caps.XMin) * (_screenW - 1) / (caps.XMax - caps.XMin));
        var y = (int)(((long)rawY - caps.YMin) * (_screenH - 1) / (caps.YMax - caps.YMin));
        return (x, y);
    }

    /// <summary>Stops monitoring and removes shared raw-input registration when last disposed.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        lock (Gate)
        {
            Instances.Remove(this);
            // The registration is process-wide: it may only go away with the LAST
            // monitor, or disposing the Settings test monitor would kill the live
            // shell's edge swipes and tap-dismiss until the shell restarts.
            if (Instances.Count == 0)
            {
                var devices = new[]
                {
                    new NativeMethods.RawInputDevice
                    {
                        usUsagePage = NativeMethods.HidUsagePageDigitizer,
                        usUsage = NativeMethods.HidUsageTouchScreen,
                        dwFlags = NativeMethods.RidevRemove,
                        hwndTarget = 0,
                    },
                };
                if (!NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>()))
                {
                    Log.Warn($"Raw touch input de-registration failed (Win32 error {Marshal.GetLastWin32Error()}); last touch monitor disposed.");
                }
                else
                {
                    Log.Info("Raw touch input unregistered (last touch monitor disposed).");
                }

                if (_sharedHwnd != 0)
                {
                    // DestroyWindow fails from a thread other than the one that
                    // created the window; the window then still exists, so the
                    // handle must not be cleared as if it were gone.
                    if (NativeMethods.DestroyWindow(_sharedHwnd))
                    {
                        _sharedHwnd = 0;
                    }
                    else
                    {
                        Log.Warn($"Failed to destroy the raw touch input window (Win32 error {Marshal.GetLastWin32Error()}); the handle survives this teardown.");
                    }
                }
            }
        }

        foreach (var caps in _devices.Values)
        {
            if (caps.PreparsedData != 0)
            {
                Marshal.FreeHGlobal(caps.PreparsedData);
            }
        }
        _devices.Clear();
    }
}
