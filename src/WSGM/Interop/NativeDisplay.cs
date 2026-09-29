using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>
/// The display P/Invoke surface: DisplayConfig packets (per-monitor DPI scaling, advanced color,
/// GDI source names) plus the EnumDisplay*/ChangeDisplaySettingsEx mode API. The DPI packets
/// (types -3/-4) are undocumented but ABI-stable — the same mechanism the Settings app uses. Every
/// packet is blittable; keep layouts exactly as verified.
/// </summary>
internal static unsafe partial class NativeDisplay
{
    internal const int GetDpiScaleType = -3;
    internal const int SetDpiScaleType = -4;
    internal const int GetSourceNameType = 1;   // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
    internal const int GetAdvancedColorInfoType = 9;
    internal const int SetAdvancedColorStateType = 10;
    internal const uint QdcOnlyActivePaths = 0x00000002;
    internal const int ErrorInsufficientBuffer = 122;

    internal const uint EnumCurrentSettings = 0xFFFFFFFF;
    internal const uint DmPelsWidth = 0x00080000;
    internal const uint DmPelsHeight = 0x00100000;
    internal const uint DmDisplayFrequency = 0x00400000;
    internal const uint CdsUpdateRegistry = 0x00000001;
    internal const uint CdsTest = 0x00000002;
    internal const uint CdsNoReset = 0x10000000;
    internal const uint DisplayDeviceActive = 0x00000001;
    internal const uint DisplayDevicePrimary = 0x00000004;
    internal const uint GetDeviceInterfaceName = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;             // SOURCE id for DPI/name packets, TARGET id for advanced color
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DpiScaleGet     // 0x20 bytes; field order min,cur,max (verified)
    {
        public DeviceInfoHeader Header;
        public int MinScaleRel;
        public int CurScaleRel;
        public int MaxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DpiScaleSet     // 0x18 bytes
    {
        public DeviceInfoHeader Header;
        public int ScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx;
        public uint OutputTechnology; public uint Rotation; public uint Scaling;
        public uint RefreshRateNumerator; public uint RefreshRateDenominator;
        public uint ScanLineOrdering; public int TargetAvailable; public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct ModeInfo { public uint InfoType; public uint Id; public Luid AdapterId; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceDeviceName   // DISPLAYCONFIG_SOURCE_DEVICE_NAME, 0x54 bytes
    {
        public DeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[32];   // UTF-16 GDI name, e.g. \\.\DISPLAY1
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdvancedColorInfo
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdvancedColorState { public DeviceInfoHeader Header; public uint EnableAdvancedColor; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        public uint Size;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];
        public uint StateFlags;
        public fixed char DeviceId[128];
        public fixed char DeviceKey[128];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DevMode
    {
        public fixed char DeviceName[32];
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        public fixed char FormName[32];
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2;
        public uint PanningWidth, PanningHeight;
    }

    [LibraryImport("user32.dll")]
    internal static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [LibraryImport("user32.dll")]
    internal static partial int QueryDisplayConfig(uint flags, ref uint numPaths, [In, Out] PathInfo[] paths,
        ref uint numModes, [In, Out] ModeInfo[] modes, nint currentTopologyId);

    [LibraryImport("user32.dll")]
    internal static partial int DisplayConfigGetDeviceInfo(ref DpiScaleGet packet);

    [LibraryImport("user32.dll")]
    internal static partial int DisplayConfigGetDeviceInfo(ref SourceDeviceName packet);

    [LibraryImport("user32.dll")]
    internal static partial int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo packet);

    [LibraryImport("user32.dll")]
    internal static partial int DisplayConfigSetDeviceInfo(ref DpiScaleSet packet);

    [LibraryImport("user32.dll")]
    internal static partial int DisplayConfigSetDeviceInfo(ref AdvancedColorState packet);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayDevices(char* device, uint index, ref DisplayDevice displayDevice, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplaySettingsEx(char* deviceName, uint modeNum, ref DevMode devMode, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW")]
    internal static partial int ChangeDisplaySettingsEx(char* deviceName, DevMode* devMode, nint hwnd, uint flags, nint param);
}
