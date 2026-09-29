using System;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

internal static partial class NativeMethods
{
    internal const uint MbOk = 0x00000000;
    internal const uint MbIconError = 0x00000010;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);

    // ---- Shell / desktop detection ----
    [LibraryImport("user32.dll")]
    internal static partial nint GetShellWindow();

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowW(string lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>Returns the effective DPI for the monitor currently containing
    /// <paramref name="hWnd"/>. Unlike a cached screen descriptor, this reflects
    /// a window that has just crossed onto another monitor.</summary>
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hWnd);

    // ---- Input-desktop readiness (Core\InputDesktop) ----
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseDesktop(nint hDesktop);

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetUserObjectInformationW(nint hObj, int nIndex, [Out] char[] pvInfo, uint nLength, out uint lpnLengthNeeded);

    // ---- Hotkey ----
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;
    internal const uint ModNoRepeat = 0x4000;
    internal const uint WmHotkey = 0x0312;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hWnd, int id);

    // ---- Low-level keyboard hook (shortcut recording only — see KeyRecorder) ----
    internal const int WhKeyboardLl = 13;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static partial nint SetWindowsHookExW(int idHook, nint lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    internal static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int vKey);

    // ---- Synthetic keyboard input (Steam Big Picture's own Ctrl+1/Ctrl+2 shortcuts) ----
    internal const uint InputKeyboard = 1;
    internal const uint KeyEventKeyUp = 0x0002;
    internal const ushort VkControl = 0x11;
    internal const short KeyDownState = unchecked((short)0x8000);

    internal const uint KeyEventExtendedKey = 0x0001;
    internal const uint KeyEventScanCode = 0x0008;

    /// <summary>MAPVK_VK_TO_VSC: virtual key to scan code.</summary>
    internal const uint MapVkToVsc = 0;

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyExW")]
    internal static partial uint MapVirtualKeyExW(uint code, uint mapType, nint layout);

    [LibraryImport("user32.dll")]
    internal static partial nint GetKeyboardLayout(uint threadId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct InputRecord
    {
        public uint type;
        public InputUnion data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInputData keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInputData
    {
        public ushort virtualKey;
        public ushort scanCode;
        public uint flags;
        public uint time;
        public nuint extraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint SendInput(uint inputCount, [In] InputRecord[] inputs, int inputSize);

    // ---- Message-only window ----
    internal const nint HwndMessage = -3;

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct WndClassW
    {
        public uint style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    internal static unsafe partial ushort RegisterClassW(WndClassW* lpWndClass);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetModuleHandleW(nint lpModuleName);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    // ---- Raw touch input (edge swipes) ----
    internal const ushort HidUsagePageGenericDesktop = 0x01;
    internal const ushort HidUsagePageDigitizer = 0x0D;
    internal const ushort HidUsageTouchScreen = 0x04;
    internal const ushort HidUsageX = 0x30;
    internal const ushort HidUsageY = 0x31;
    internal const ushort HidUsageTipSwitch = 0x42;
    internal const uint RidevRemove = 0x00000001;
    internal const uint RidevInputSink = 0x00000100;
    internal const uint RidevDevNotify = 0x00002000;
    internal const uint WmInput = 0x00FF;
    internal const uint WmInputDeviceChange = 0x00FE;
    internal const nint GidcArrival = 1;
    internal const nint GidcRemoval = 2;
    internal const uint RidInput = 0x10000003;
    internal const uint RidiPreparsedData = 0x20000005;
    internal const uint RimTypeHid = 2;
    internal const int HidpStatusSuccess = 0x00110000;
    internal const int HidpInput = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDevice
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    /// <summary>The RAWHID payload follows this header in the WM_INPUT buffer:
    /// uint dwSizeHid; uint dwCount; byte bRawData[dwSizeHid * dwCount].</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputHeader
    {
        public uint dwType;
        public uint dwSize;
        public nint hDevice;
        public nint wParam;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterRawInputDevices(
        RawInputDevice[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [LibraryImport("user32.dll")]
    internal static partial uint GetRawInputData(
        nint hRawInput, uint uiCommand, nint pData, ref uint pcbSize, uint cbSizeHeader);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    internal static partial uint GetRawInputDeviceInfoW(
        nint hDevice, uint uiCommand, nint pData, ref uint pcbSize);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    /// <summary>HIDP_VALUE_CAPS (72 bytes). The trailing fields are the Range variant
    /// of the union; for NotRange caps, UsageMin holds the single usage.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HidpValueCaps
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort Reserved2a;
        public ushort Reserved2b;
        public ushort Reserved2c;
        public ushort Reserved2d;
        public ushort Reserved2e;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;
    }

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetCaps(nint preparsedData, out HidpCaps capabilities);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetValueCaps(
        int reportType, [Out] HidpValueCaps[] valueCaps, ref ushort valueCapsLength, nint preparsedData);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetUsageValue(
        int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, nint preparsedData, nint report, uint reportLength);

    [LibraryImport("hid.dll")]
    internal static partial int HidP_GetUsages(
        int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, nint preparsedData, nint report, uint reportLength);

    // ---- Window finding / focus ----
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(nint lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "RealGetWindowClassW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RealGetWindowClassW(nint hWnd, [Out] char[] pszType, uint cchType);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);

    internal const int SwRestore = 9;

    // ---- Touch-synthesized mouse message detection (overlay ghost-click eater) ----
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    /// <summary>Sent to an inactive window before a mouse button-down; the reply decides whether
    /// the click activates it. Touch activation uses WM_POINTERACTIVATE instead, so a reply here
    /// affects only mouse clicks — real ones and the ones Windows synthesizes from a tap.</summary>
    internal const uint WmMouseActivate = 0x0021;
    /// <summary>WM_MOUSEACTIVATE reply: deliver the click, do not activate.</summary>
    internal const nint MaNoActivate = 3;
    /// <summary>GetMessageExtraInfo() upper bits marking touch/pen-synthesized mouse messages.</summary>
    internal const uint MiWpSignatureMask = 0xFFFFFF00;
    internal const uint MiWpSignature = 0xFF515700;

    [LibraryImport("user32.dll")]
    internal static partial nint GetMessageExtraInfo();

    /// <summary>Swallows the mouse messages Windows synthesizes behind a touch, for a window that
    /// already handled the touch itself.</summary>
    /// <remarks>
    /// Every focus-taking WSGM window installs this. Without it a tap lands twice — once as touch
    /// on the surface the user aimed at, and again as a synthesized click on whatever moved into
    /// that position afterwards, which is the ghost-click the overlay's deferred close exists
    /// alongside. Written as a hook callback so <c>Win32Properties.AddWndProcHookCallback</c> can
    /// take it directly.
    /// </remarks>
    internal static nint SwallowTouchSynthesizedMouse(
        nint hWnd,
        uint msg,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (msg is WmMouseMove or WmLButtonDown or WmLButtonUp
            && ((uint)GetMessageExtraInfo() & MiWpSignatureMask) == MiWpSignature)
        {
            handled = true;
        }

        return nint.Zero;
    }

    // ---- Idle memory trim (Core\MemoryTrim) ----
    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyWorkingSet(nint hProcess);

    // ---- Boot-splash fade-out (layered-window alpha) ----
    internal const int WsExLayered = 0x00080000;
    internal const uint LwaAlpha = 0x00000002;

    // Ex-style is a 32-bit LONG even on x64 — SetWindowLongW, not the Ptr variant.
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    internal static partial int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    internal const uint WmNcHitTest = 0x0084;
    internal const nint HtTransparent = -1;
    internal const int WsExNoActivate = 0x08000000;
    internal const int WsExTransparent = 0x00000020;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    // ---- Notification suitability (volume OSD) ----
    internal const int QunsNotPresent = 1;
    internal const int QunsRunningD3dFullScreen = 3;

    [LibraryImport("shell32.dll")]
    internal static partial int SHQueryUserNotificationState(out int state);


    // ---- Switchable-window enumeration (alt-tab style) ----
    internal const int GwlExStyle = -20;
    internal const int WsExToolWindow = 0x0080;
    internal const uint DwmWaCloaked = 14;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static partial int GetWindowLong(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowTextW(nint hWnd, [Out] char[] text, int maxCount);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hWnd, uint attribute, out uint value, uint size);

    // ---- Update-exit event with explicit security (signalable from unelevated) ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out nint securityDescriptor, out uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateEventW(ref SecurityAttributes securityAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);

    internal const uint Synchronize = 0x00100000;
    internal const uint EventModifyState = 0x0002;

    [LibraryImport("kernel32.dll", EntryPoint = "OpenEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenEventW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ResetEvent(nint eventHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetEvent(nint eventHandle);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint mem);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    // ---- Elevation check of other processes ----
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint TokenQuery = 0x0008;
    internal const int TokenElevationClass = 20;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

    /// <summary>Buffer-based overload for variable-length token classes (integrity SID).</summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, nint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    // ---- Power ----
    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    // ---- Power requests (keep-awake wake lock) ----
    internal const uint PowerRequestContextVersion = 0;
    internal const uint PowerRequestContextSimpleString = 0x1;
    /// <summary>POWER_REQUEST_TYPE: PowerRequestDisplayRequired — pins the display
    /// on (which on a Modern Standby device also keeps the system awake).</summary>
    internal const int PowerRequestDisplayRequired = 0;
    /// <summary>POWER_REQUEST_TYPE: PowerRequestSystemRequired — blocks automatic
    /// sleep/standby entry while set; the display still turns off on its own timeout.</summary>
    internal const int PowerRequestSystemRequired = 1;

    /// <summary>REASON_CONTEXT with the simple-string variant of its union: the string
    /// pointer is a caller-owned UTF-16 buffer (this struct stores the pointer only, so
    /// the caller keeps the buffer alive for the life of the request object).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint PowerCreateRequest(in ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PowerSetRequest(nint powerRequest, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PowerClearRequest(nint powerRequest, int requestType);

    // ---- Power scheme values (display-off / sleep timeouts) ----
    // Flat powrprof.dll policy API instead of parsing `powercfg /q`, whose output is
    // localized (this codebase already learned that lesson with netstat). All return
    // ERROR_SUCCESS (0) on success.
    /// <summary>Returns the active scheme GUID as a LocalAlloc'd pointer the caller
    /// must free with <see cref="LocalFree"/>.</summary>
    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerSetActiveScheme(nint userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadACValueIndex(
        nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid powerSettingGuid,
        out uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadDCValueIndex(
        nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid powerSettingGuid,
        out uint dcValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteACValueIndex(
        nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid powerSettingGuid,
        uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteDCValueIndex(
        nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid powerSettingGuid,
        uint dcValueIndex);

    // ---- System-wide power request list (wake-lock indicator) ----
    // The undocumented GetPowerRequestList (45) information class — what
    // `powercfg /requests` uses internally. The documented CallNtPowerInformation
    // wrapper REJECTS this class with STATUS_INVALID_PARAMETER, so the call goes
    // against ntdll directly. Requires an elevated token (STATUS_ACCESS_DENIED
    // otherwise) — same restriction as powercfg itself.
    [LibraryImport("ntdll.dll")]
    internal static partial int NtPowerInformation(
        int informationLevel, nint inputBuffer, uint inputLength,
        nint outputBuffer, uint outputLength);

    [LibraryImport("ntdll.dll")]
    internal static partial void RtlGetNtVersionNumbers(out uint major, out uint minor, out uint build);

    // ---- System status (taskbar clock/battery cluster; Wi-Fi lives in WindowsRadio) ----
    /// <summary>SYSTEM_POWER_STATUS: BatteryFlag 128 = no system battery, 255 = unknown;
    /// BatteryLifePercent 255 = unknown.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    // ---- RTSS OSD metrics (Core\RtssOsd) ----
    // FILETIME pairs as raw 64-bit ticks; kernel time includes idle.
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(
        out long idleTime, out long kernelTime, out long userTime);

    /// <summary>MEMORYSTATUSEX; <see cref="Length"/> must be set before the call.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    // ---- Window icons (taskbar) ----
    internal const uint WmGetIcon = 0x007F;
    internal const uint WmQueryDragIcon = 0x0037;
    internal const nint IconSmall = 0;
    internal const nint IconBig = 1;
    internal const nint IconSmall2 = 2;
    internal const uint SmtoAbortIfHung = 0x0002;
    internal const int GclpHicon = -14;
    internal const int GclpHiconSm = -34;
    internal const uint DiMask = 0x0001;
    internal const uint DiNormal = 0x0003;
    internal const uint DibRgbColors = 0;
    internal const uint BiRgb = 0;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static partial nint SendMessageTimeoutW(
        nint hWnd, uint msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    // 64-bit-only entry point; the app ships win-x64 exclusively.
    [LibraryImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    internal static partial nint GetClassLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial nint CopyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DrawIconEx(
        nint hdc, int xLeft, int yTop, nint hIcon, int cxWidth, int cyWidth,
        uint istepIfAniCur, nint hbrFlickerFreeDraw, uint diFlags);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint ExtractIconExW(
        string lpszFile, int nIconIndex, out nint phiconLarge, out nint phiconSmall, uint nIcons);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageNameW(
        nint hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint ho);

    [LibraryImport("gdi32.dll")]
    internal static unsafe partial nint CreateDIBSection(
        nint hdc, BitmapInfoHeader* pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    // ---- Tray host (Shell_TrayWnd) ----
    internal const uint WmCopyData = 0x004A;
    internal const uint WmWindowPosChanged = 0x0047;
    internal const uint WmContextMenu = 0x007B;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint NinSelect = 0x0400;
    internal const uint MsgfltAllow = 1;
    internal const uint WsPopup = 0x80000000;
    internal const uint WsChild = 0x40000000;
    internal const uint WsClipChildren = 0x02000000;
    internal const uint WsClipSiblings = 0x04000000;
    internal const uint WsExTopmost = 0x00000008;
    internal const nint HwndBroadcast = 0xFFFF;
    internal const int SwHide = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyDataStruct
    {
        public nint dwData;
        public uint cbData;
        public nint lpData;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeWindowMessageFilterEx(
        nint hwnd, uint message, uint action, nint pChangeFilterStruct);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessageW(string lpString);

    // ---- Shell-hook notifications (replacement shell volume commands) ----
    internal const int HshellAppCommand = 12;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterShellHookWindow(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeregisterShellHookWindow(nint hWnd);

    /// <summary>WM_POWERBROADCAST.</summary>
    internal const uint WmPowerBroadcast = 0x0218;

    /// <summary>PBT_POWERSETTINGCHANGE — wParam of a power-setting notification.</summary>
    internal const nint PbtPowerSettingChange = 0x8013;

    /// <summary>PBT_APMSUSPEND — the system is about to enter a suspended state.</summary>
    internal const nint PbtApmSuspend = 0x4;

    /// <summary>PBT_APMRESUMESUSPEND — the system resumed from a normal suspend.</summary>
    internal const nint PbtApmResumeSuspend = 0x7;

    /// <summary>PBT_APMRESUMEAUTOMATIC — the system resumed, possibly with no user present.
    /// Windows always sends this one on resume and adds PBT_APMRESUMESUSPEND when the user
    /// caused it, so both must be treated as the same "hardware is back" signal.</summary>
    internal const nint PbtApmResumeAutomatic = 0x12;

    /// <summary>GUID_SESSION_DISPLAY_STATUS {2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5}: the
    /// display of the CALLING SESSION turned on or off. Microsoft documents this as the
    /// one interactive user-mode applications must use — GUID_CONSOLE_DISPLAY_STATE is
    /// for services and kernel-mode callers. Data is a DWORD MONITOR_DISPLAY_STATE:
    /// 0 = off, 1 = on, 2 = dimmed.</summary>
    internal static readonly Guid GuidSessionDisplayStatus =
        new(0x2B84C20E, 0xAD23, 0x4DDF, 0x93, 0xDB, 0x05, 0xFF, 0xBD, 0x7E, 0xFC, 0xA5);

    /// <summary>GUID_CONSOLE_DISPLAY_STATE {6FE69556-704A-47A0-8F24-C28D936FDA47}: the
    /// display attached to the CONSOLE session turned on or off, same DWORD
    /// MONITOR_DISPLAY_STATE payload. Microsoft points interactive apps at
    /// <see cref="GuidSessionDisplayStatus"/> instead, and this is NOT a replacement for
    /// it — it is registered alongside as a second, independent source so a wake that the
    /// session notification misses is still seen. It describes the console session rather
    /// than ours, so it may only ever drive a restore, never a mute.</summary>
    internal static readonly Guid GuidConsoleDisplayState =
        new(0x6FE69556, 0x704A, 0x47A0, 0x8F, 0x24, 0xC2, 0x8D, 0x93, 0x6F, 0xDA, 0x47);

    /// <summary>GUID_MONITOR_POWER_ON {02731015-4510-4526-99E6-E5A17EBD1AEA}: the
    /// superseded pre-Windows-8 display-power setting (DWORD 0 = off, 1 = on). Modern
    /// Windows may never send it; it is registered best-effort as a third restore-only
    /// source because the registration costs one call and a silent one costs nothing.
    /// </summary>
    internal static readonly Guid GuidMonitorPowerOn =
        new(0x02731015, 0x4510, 0x4526, 0x99, 0xE6, 0xE5, 0xA1, 0x7E, 0xBD, 0x1A, 0xEA);

    /// <summary>DEVICE_NOTIFY_WINDOW_HANDLE: deliver as WM_POWERBROADCAST messages.</summary>
    internal const uint DeviceNotifyWindowHandle = 0;

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint RegisterPowerSettingNotification(
        nint hRecipient, in Guid powerSettingGuid, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterPowerSettingNotification(nint handle);

    /// <summary>WM_DEVICECHANGE — a device or media was added or removed.</summary>
    internal const uint WmDeviceChange = 0x0219;

    /// <summary>DBT_DEVICEARRIVAL: the device named by lParam is now available.</summary>
    internal const nint DbtDeviceArrival = 0x8000;

    /// <summary>DBT_DEVICEREMOVECOMPLETE: the device named by lParam is gone.</summary>
    internal const nint DbtDeviceRemoveComplete = 0x8004;

    /// <summary>DBT_DEVTYP_DEVICEINTERFACE: the lParam payload describes a device
    /// interface class rather than a volume, port or handle.</summary>
    internal const uint DbtDevTypDeviceInterface = 0x0000_0005;

    /// <summary>GUID_DEVINTERFACE_VOLUME {53F5630D-B6BF-11D0-94F2-00A0C91EFB8B}.</summary>
    /// <remarks>
    /// The universal signal that a volume appeared or disappeared, whatever bus or
    /// reader it came from. It is registered EXPLICITLY rather than relying on the
    /// broadcast <c>DBT_DEVTYP_VOLUME</c> message, because Windows broadcasts that
    /// one only to top-level windows and WSGM's notification window is message-only
    /// (HWND_MESSAGE) — it would never see it. An explicit device-interface
    /// registration is delivered to a message-only window.
    /// </remarks>
    internal static readonly Guid GuidDevInterfaceVolume =
        new(0x53F5630D, 0xB6BF, 0x11D0, 0x94, 0xF2, 0x00, 0xA0, 0xC9, 0x1E, 0xFB, 0x8B);

    [LibraryImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW", SetLastError = true)]
    internal static partial nint RegisterDeviceNotification(
        nint hRecipient, in DevBroadcastDeviceInterface notificationFilter, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterDeviceNotification(nint handle);

    /// <summary>DEV_BROADCAST_DEVICEINTERFACE_W, as a REGISTRATION FILTER only.
    /// The variable-length device path that follows an incoming notification is
    /// deliberately not declared: WSGM reacts to "some volume changed" by
    /// rescanning drive letters, which is both simpler and more robust than
    /// mapping a device path back to a mount point.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DevBroadcastDeviceInterface
    {
        /// <summary>Size of this structure in bytes; must be set before the call.</summary>
        internal uint Size;

        /// <summary>DBT_DEVTYP_DEVICEINTERFACE.</summary>
        internal uint DeviceType;

        /// <summary>Reserved; must be zero.</summary>
        internal uint Reserved;

        /// <summary>The interface class to subscribe to.</summary>
        internal Guid ClassGuid;

        /// <summary>First UTF-16 unit of the device name; unused for a filter, and
        /// declared as <c>ushort</c> rather than <c>char</c> so the struct stays
        /// blittable for <c>LibraryImport</c> without runtime marshalling.</summary>
        internal ushort Name;
    }

    /// <summary>POWERBROADCAST_SETTING: the lParam payload of PBT_POWERSETTINGCHANGE.
    /// Only the fixed header is declared; <c>Data</c> is a variable-length array whose
    /// first four bytes carry the DWORD the display-status setting reports.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerBroadcastSetting
    {
        /// <summary>Which power setting changed.</summary>
        internal Guid PowerSetting;

        /// <summary>Size in bytes of the payload that follows.</summary>
        internal uint DataLength;

        /// <summary>First byte of the payload.</summary>
        internal byte Data;
    }

    /// <summary>LASTINPUTINFO: the tick count of the last keyboard/mouse/touch input in
    /// the session. It is the recovery signal for the display-off mute — a user who is
    /// typing or tapping is looking at a lit screen, so the mute can be undone even when
    /// the display-status notification for the screen coming back was never delivered.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LastInputInfo
    {
        /// <summary>Size of this structure in bytes; must be set before the call.</summary>
        internal uint CbSize;

        /// <summary>GetTickCount-based timestamp of the last input event.</summary>
        internal uint DwTime;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LastInputInfo plii);

    // ---- Session lock/unlock (an independent "the user is back" signal) ----

    /// <summary>WM_WTSSESSION_CHANGE.</summary>
    internal const uint WmWtsSessionChange = 0x02B1;

    /// <summary>WTS_SESSION_LOCK — the session's desktop was locked.</summary>
    internal const nint WtsSessionLock = 0x7;

    /// <summary>WTS_SESSION_UNLOCK — the session's desktop was unlocked.</summary>
    internal const nint WtsSessionUnlock = 0x8;

    /// <summary>WTS_SESSION_LOGOFF — this interactive session is ending.</summary>
    internal const nint WtsSessionLogoff = 0x6;

    /// <summary>NOTIFY_FOR_THIS_SESSION.</summary>
    internal const uint NotifyForThisSession = 0;

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSRegisterSessionNotification(nint hWnd, uint dwFlags);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSUnRegisterSessionNotification(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "SendNotifyMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SendNotifyMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(uint dwProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCursorPos(int x, int y);

    internal const uint WmLButtonDblClk = 0x0203;

    [LibraryImport("user32.dll")]
    internal static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hWnd);
}
