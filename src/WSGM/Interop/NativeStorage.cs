using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSGM.Interop;

/// <summary>Flat Win32 storage interop shared by the eject, card and format flows:
/// volume-to-disk mapping (IOCTL_STORAGE_GET_DEVICE_NUMBER), the hotplug
/// classification that separates a USB device from a built-in card reader
/// (IOCTL_STORAGE_GET_HOTPLUG_INFO), the PnP device eject
/// (CM_Request_Device_EjectW) and the media-level dismount sequence
/// (FSCTL_LOCK_VOLUME → FSCTL_DISMOUNT_VOLUME → IOCTL_STORAGE_EJECT_MEDIA).
///
/// Everything here is cfgmgr32/kernel32 — no COM and no WMI. Devnode discovery
/// goes through the cfgmgr32 interface list rather than SetupAPI's devinfo sets:
/// same data, no variable-size detail-struct marshalling.
///
/// The two fixed-layout records are decoded from documented offsets by pure
/// span readers, so the layouts are unit-testable from a synthetic buffer without
/// a live device.</summary>
internal static unsafe partial class NativeStorage
{
    // ---- return codes / constants ----

    /// <summary>CONFIGRET success.</summary>
    internal const int CrSuccess = 0;

    /// <summary>CONFIGRET: the eject was vetoed; the veto type and name say why.</summary>
    internal const int CrRemoveVetoed = 0x17;

    /// <summary>CONFIGRET: the buffer sized by the preceding size query no longer
    /// fits, because the device set changed in between. Re-query and retry.</summary>
    private const int CrBufferSmall = 0x1A;

    /// <summary>How often the interface-list size query and list call are retried
    /// as a pair before the list is reported as empty.</summary>
    private const int InterfaceListAttempts = 3;

    /// <summary>CM_DEVCAP_REMOVABLE: the devnode itself can be ejected.</summary>
    private const uint DevCapRemovable = 0x4;

    // CM_DRP_* registry properties (SPDRP value + 1).
    private const uint DrpDeviceDesc = 0x01;
    private const uint DrpFriendlyName = 0x0D;
    private const uint DrpCapabilities = 0x10;

    /// <summary>STORAGE_DEVICE_NUMBER.DeviceType for a disk.</summary>
    internal const int FileDeviceDisk = 0x7;

    private const uint IoctlStorageGetDeviceNumber = 0x2D1080;
    private const uint IoctlStorageGetHotplugInfo = 0x2D0C14;
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint IoctlDiskGetLengthInfo = 0x7405C;
    private const uint IoctlDiskGetDriveLayoutEx = 0x70050;
    private const uint FsctlLockVolume = 0x090018;
    private const uint FsctlDismountVolume = 0x090020;
    private const uint IoctlStorageMediaRemoval = 0x2D4804;
    private const uint IoctlStorageEjectMedia = 0x2D4808;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    /// <summary>GUID_DEVINTERFACE_DISK: every present disk exposes one of these
    /// interfaces; enumerating them is how a volume's device number becomes a
    /// devnode.</summary>
    private static Guid DiskInterfaceGuid { get; } =
        new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    /// <summary>GUID_DEVINTERFACE_VOLUME: every volume the volume manager has
    /// surfaced exposes one of these — letter or no letter — which is what makes
    /// the list usable as a "has the new partition's volume arrived yet" probe.</summary>
    private static Guid VolumeInterfaceGuid { get; } =
        new("53f5630d-b6bf-11d0-94f2-00a0c91efb8b");

    /// <summary>How Windows says an eject was refused (cfg.h PNP_VETO_TYPE,
    /// zero-based).</summary>
    internal enum PnpVetoType
    {
        /// <summary>No reason was named.</summary>
        TypeUnknown = 0,

        /// <summary>A legacy device cannot be ejected.</summary>
        LegacyDevice = 1,

        /// <summary>A close is still pending on the device.</summary>
        PendingClose = 2,

        /// <summary>An application vetoed; the veto name is a module.</summary>
        WindowsApp = 3,

        /// <summary>A service vetoed; the veto name is a service name.</summary>
        WindowsService = 4,

        /// <summary>Open handles remain on the device.</summary>
        OutstandingOpen = 5,

        /// <summary>The device itself refused.</summary>
        Device = 6,

        /// <summary>The driver refused.</summary>
        Driver = 7,

        /// <summary>The request is illegal for this device.</summary>
        IllegalDeviceRequest = 8,

        /// <summary>Insufficient power to complete the operation.</summary>
        InsufficientPower = 9,

        /// <summary>The device cannot be disabled.</summary>
        NonDisableable = 10,

        /// <summary>A legacy driver vetoed.</summary>
        LegacyDriver = 11,

        /// <summary>The caller lacks the rights to eject.</summary>
        InsufficientRights = 12,
    }

    // ---- kernel32 ----

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, nint inBuffer, uint inBufferSize,
        nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    // ---- cfgmgr32 ----

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_List_SizeW(
        out uint length, in Guid interfaceClassGuid, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_ListW(
        in Guid interfaceClassGuid, string? deviceId, char* buffer, uint bufferLength,
        uint flags);

    /// <summary>DEVPROPKEY, blittable: a property category GUID plus an id.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    /// <summary>DEVPKEY_Device_InstanceId.</summary>
    private static readonly DevPropKey DevicePropertyInstanceId = new()
    {
        Fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"),
        Pid = 256,
    };

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_PropertyW(
        string deviceInterface, in DevPropKey propertyKey, out uint propertyType,
        char* propertyBuffer, ref uint propertyBufferSize, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(
        out uint devInst, string deviceInstanceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static partial int CM_Get_DevNode_Registry_PropertyW(
        uint devInst, uint property, out uint regDataType, byte* buffer, ref uint length,
        uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Request_Device_EjectW")]
    private static partial int CM_Request_Device_EjectW(
        uint devInst, out int vetoType, char* vetoName, uint nameLength, uint flags);

    // ---- fixed-layout record readers (unit-tested from synthetic buffers) ----

    /// <summary>Size of a STORAGE_DEVICE_NUMBER record.</summary>
    internal const int DeviceNumberRecordSize = 12;

    /// <summary>Size of a STORAGE_HOTPLUG_INFO record.</summary>
    internal const int HotplugRecordSize = 8;

    /// <summary>Decodes a STORAGE_DEVICE_NUMBER buffer.</summary>
    /// <param name="buffer">At least <see cref="DeviceNumberRecordSize"/> bytes.</param>
    internal static (int DeviceType, int DeviceNumber, int PartitionNumber) ReadDeviceNumber(
        ReadOnlySpan<byte> buffer) =>
        (BitConverter.ToInt32(buffer),
            BitConverter.ToInt32(buffer[4..]),
            BitConverter.ToInt32(buffer[8..]));

    /// <summary>Decodes a STORAGE_HOTPLUG_INFO buffer: whether the media is
    /// removable from the device, and whether the device itself is hot-pluggable.</summary>
    /// <param name="buffer">At least <see cref="HotplugRecordSize"/> bytes.</param>
    internal static (bool MediaRemovable, bool DeviceHotplug) ReadHotplugInfo(
        ReadOnlySpan<byte> buffer) => (buffer[4] != 0, buffer[6] != 0);

    // ---- queries ----

    /// <summary>Opens a volume for attribute queries only — zero access needs no
    /// privilege and touches no media.</summary>
    /// <param name="letter">The drive letter.</param>
    internal static SafeFileHandle OpenVolumeForQuery(char letter) =>
        CreateFileW($@"\\.\{letter}:", 0, FileShareReadWrite, 0, OpenExisting, 0, 0);

    /// <summary>Opens a volume for the lock/dismount/eject sequence.</summary>
    /// <param name="letter">The drive letter.</param>
    internal static SafeFileHandle OpenVolumeForEject(char letter) =>
        CreateFileW($@"\\.\{letter}:", GenericRead | GenericWrite, FileShareReadWrite, 0,
            OpenExisting, 0, 0);

    /// <summary>Opens a device-interface path for attribute queries only.</summary>
    /// <param name="path">A path from <see cref="ListDiskInterfaces"/>.</param>
    internal static SafeFileHandle OpenVolumeForQueryPath(string path) =>
        CreateFileW(path, 0, FileShareReadWrite, 0, OpenExisting, 0, 0);

    /// <summary>Opens a physical disk for attribute queries only.</summary>
    /// <param name="number">The disk number.</param>
    internal static SafeFileHandle OpenDiskForQuery(int number) =>
        CreateFileW($@"\\.\PhysicalDrive{number}", 0, FileShareReadWrite, 0, OpenExisting, 0, 0);

    /// <summary>Opens a physical disk for reading — some queries
    /// (IOCTL_DISK_GET_LENGTH_INFO) demand read access.</summary>
    /// <param name="number">The disk number.</param>
    internal static SafeFileHandle OpenDiskForRead(int number) =>
        CreateFileW($@"\\.\PhysicalDrive{number}", GenericRead, FileShareReadWrite, 0,
            OpenExisting, 0, 0);

    /// <summary>Reads which physical disk (and partition) a volume lives on.</summary>
    /// <param name="volume">An open volume handle.</param>
    /// <param name="deviceType">The FILE_DEVICE_* type of the underlying device.</param>
    /// <param name="deviceNumber">The physical disk number.</param>
    internal static bool TryGetDeviceNumber(
        SafeFileHandle volume, out int deviceType, out int deviceNumber)
    {
        var buffer = stackalloc byte[DeviceNumberRecordSize];
        if (!DeviceIoControl(volume, IoctlStorageGetDeviceNumber, 0, 0, (nint)buffer,
                DeviceNumberRecordSize, out var written, 0)
            || written < DeviceNumberRecordSize)
        {
            deviceType = 0;
            deviceNumber = -1;
            return false;
        }
        (deviceType, deviceNumber, _) =
            ReadDeviceNumber(new ReadOnlySpan<byte>(buffer, DeviceNumberRecordSize));
        return true;
    }

    /// <summary>Reads the disk's hotplug facts — the classification that decides
    /// between a device-level and a media-level eject.</summary>
    /// <param name="disk">An open physical-disk handle.</param>
    /// <param name="mediaRemovable">Whether the media can leave the device.</param>
    /// <param name="deviceHotplug">Whether the device itself is hot-pluggable.</param>
    internal static bool TryGetHotplugInfo(
        SafeFileHandle disk, out bool mediaRemovable, out bool deviceHotplug)
    {
        var buffer = stackalloc byte[HotplugRecordSize];
        if (!DeviceIoControl(disk, IoctlStorageGetHotplugInfo, 0, 0, (nint)buffer,
                HotplugRecordSize, out var written, 0)
            || written < HotplugRecordSize)
        {
            mediaRemovable = false;
            deviceHotplug = false;
            return false;
        }
        (mediaRemovable, deviceHotplug) =
            ReadHotplugInfo(new ReadOnlySpan<byte>(buffer, HotplugRecordSize));
        return true;
    }

    /// <summary>One mounted local volume, mapped back to its physical disk.</summary>
    /// <param name="Letter">The upper-case drive letter.</param>
    /// <param name="Disk">The physical disk number from the device-number query.</param>
    /// <param name="DeviceType">The FILE_DEVICE_* type of the underlying device.</param>
    /// <param name="Ready">Whether the media was ready when walked.</param>
    /// <param name="DriveType">The .NET drive type (Fixed or Removable).</param>
    /// <param name="SizeBytes">The volume size, 0 when the media is not ready.</param>
    internal readonly record struct MountedVolume(
        char Letter, int Disk, int DeviceType, bool Ready, DriveType DriveType, long SizeBytes);

    /// <summary>The one mounted-volume walk behind every letter-to-disk lookup:
    /// each local Fixed/Removable drive letter whose volume answered the
    /// device-number query. No readiness or device-type filtering here — callers
    /// keep their own (a letterless or not-ready card is meaningful to some of
    /// them). A drive vanishing mid-walk is skipped, matching the per-drive
    /// tolerance every previous copy of this loop had.</summary>
    internal static List<MountedVolume> MountedVolumes()
    {
        var result = new List<MountedVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }
                var letter = char.ToUpperInvariant(drive.Name[0]);
                using var volume = OpenVolumeForQuery(letter);
                if (volume.IsInvalid
                    || !TryGetDeviceNumber(volume, out var type, out var disk))
                {
                    continue;
                }
                var ready = drive.IsReady;
                result.Add(new MountedVolume(
                    letter, disk, type, ready, drive.DriveType, ready ? drive.TotalSize : 0));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
                // A volume we may not even query is not one any caller can act on.
            }
        }
        return result;
    }

    /// <summary>Lists the device-interface paths of every present disk. The size
    /// query and the list call are a documented race — a disk arriving or leaving
    /// between them makes the list call report CR_BUFFER_SMALL — so the pair is
    /// retried with a freshly queried size before giving up.</summary>
    internal static string[] ListDiskInterfaces() => ListInterfaces(DiskInterfaceGuid);

    /// <summary>Lists the device-interface paths of every volume the volume
    /// manager currently exposes, mounted or not. Each opens with
    /// <see cref="OpenVolumeForQueryPath"/> for a device-number query, which maps
    /// it back to its disk.</summary>
    internal static string[] ListVolumeInterfaces() => ListInterfaces(VolumeInterfaceGuid);

    private static string[] ListInterfaces(Guid guid)
    {
        char[]? buffer = null;
        for (var attempt = 0; attempt < InterfaceListAttempts; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out var length, in guid, null, 0) != CrSuccess
                || length < 2)
            {
                return [];
            }
            var candidate = new char[length];
            int code;
            fixed (char* p = candidate)
            {
                code = CM_Get_Device_Interface_ListW(in guid, null, p, length, 0);
            }
            if (code == CrSuccess)
            {
                buffer = candidate;
                break;
            }
            if (code != CrBufferSmall)
            {
                return [];
            }
        }
        if (buffer is null)
        {
            return [];
        }
        // Double-NUL-terminated multi-string.
        var result = new System.Collections.Generic.List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }
            if (i > start)
            {
                result.Add(new string(buffer, start, i - start));
            }
            start = i + 1;
        }
        return [.. result];
    }

    /// <summary>Resolves a device-interface path to its devnode.</summary>
    /// <param name="interfacePath">A path from <see cref="ListDiskInterfaces"/>.</param>
    /// <param name="devInst">The devnode handle.</param>
    internal static bool TryGetDevNode(string interfacePath, out uint devInst)
    {
        devInst = 0;
        var size = 1024u;
        var buffer = stackalloc char[512];
        if (CM_Get_Device_Interface_PropertyW(interfacePath, in DevicePropertyInstanceId,
                out _, buffer, ref size, 0) != CrSuccess)
        {
            return false;
        }
        // size is the byte count cfgmgr32 wrote back (buffer holds 512 chars).
        var instanceId = ReadBoundedString(buffer, (int)(Math.Min(size, 1024u) / 2));
        return instanceId.Length > 0
            && CM_Locate_DevNodeW(out devInst, instanceId, 0) == CrSuccess;
    }

    /// <summary>Reads the devnode's device instance path — the stable identity a
    /// list row keys on.</summary>
    /// <param name="devInst">The devnode.</param>
    internal static string GetDeviceInstanceId(uint devInst)
    {
        // CM_Get_Device_IDW; capped at MAX_DEVICE_ID_LEN (200). Decode bounded:
        // `new string(char*)` scans for a NUL with no end, so an id that exactly
        // fills the buffer would read past the stack allocation.
        var buffer = stackalloc char[200];
        return CM_Get_Device_IDW(devInst, buffer, 200, 0) == CrSuccess
            ? ReadBoundedString(buffer, 200)
            : "";
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static partial int CM_Get_Device_IDW(
        uint devInst, char* buffer, uint bufferLength, uint flags);

    /// <summary>Reads the devnode's display name: the friendly name when set,
    /// else the device description, else an empty string.</summary>
    /// <param name="devInst">The devnode.</param>
    internal static string GetDeviceDisplayName(uint devInst)
    {
        var name = ReadDevNodeString(devInst, DrpFriendlyName);
        return name.Length > 0 ? name : ReadDevNodeString(devInst, DrpDeviceDesc);
    }

    private static string ReadDevNodeString(uint devInst, uint property)
    {
        var length = 1024u;
        var buffer = stackalloc byte[1024];
        if (CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buffer, ref length, 0)
            != CrSuccess)
        {
            return "";
        }
        // REG_SZ data is not guaranteed NUL-terminated; decode at most the
        // returned byte count (buffer holds 1024 bytes = 512 chars).
        return ReadBoundedString((char*)buffer, (int)(Math.Min(length, 1024u) / 2));
    }

    /// <summary>Decodes a UTF-16 buffer up to its first NUL, never reading past
    /// <paramref name="capacity"/> chars — cfgmgr32/registry strings are not
    /// guaranteed NUL-terminated when they exactly fill the buffer.</summary>
    private static string ReadBoundedString(char* buffer, int capacity)
    {
        var span = new ReadOnlySpan<char>(buffer, capacity);
        var end = span.IndexOf('\0');
        return new string(end >= 0 ? span[..end] : span);
    }

    /// <summary>Reads the devnode's CM_DEVCAP_* capability bits, 0 on failure.</summary>
    /// <param name="devInst">The devnode.</param>
    private static uint GetCapabilities(uint devInst)
    {
        var length = 4u;
        uint capabilities = 0;
        return CM_Get_DevNode_Registry_PropertyW(devInst, DrpCapabilities, out _,
                (byte*)&capabilities, ref length, 0) == CrSuccess
            ? capabilities
            : 0;
    }

    /// <summary>Walks from a disk devnode to the node the PnP eject should
    /// target: the first ancestor (or the disk itself) whose capabilities say
    /// CM_DEVCAP_REMOVABLE. For USB storage that is the USB device above the
    /// USBSTOR disk — ejecting the disk node itself commonly fails. Falls back
    /// to the immediate parent when no ancestor claims removability.</summary>
    /// <param name="diskDevInst">The disk devnode.</param>
    internal static uint FindEjectTarget(uint diskDevInst)
    {
        var node = diskDevInst;
        for (var depth = 0; depth < 4; depth++)
        {
            if ((GetCapabilities(node) & DevCapRemovable) != 0)
            {
                return node;
            }
            if (CM_Get_Parent(out var parent, node, 0) != CrSuccess)
            {
                break;
            }
            node = parent;
        }
        // Nothing claimed removability: the classic fallback is the disk's parent.
        return CM_Get_Parent(out var fallback, diskDevInst, 0) == CrSuccess
            ? fallback
            : diskDevInst;
    }

    // ---- disk facts for the Format flow ----

    /// <summary>STORAGE_BUS_TYPE: the disk sits in a native SD host slot.</summary>
    internal const int BusTypeSd = 12;

    /// <summary>STORAGE_BUS_TYPE: eMMC/MMC bus.</summary>
    internal const int BusTypeMmc = 13;

    /// <summary>STORAGE_BUS_TYPE: USB-attached (sticks, external drives, and
    /// USB-bridged card readers alike).</summary>
    internal const int BusTypeUsb = 7;

    /// <summary>Reads the disk's total size in bytes, or 0 on failure.</summary>
    /// <param name="disk">A disk handle opened with read access.</param>
    internal static long GetDiskLength(SafeFileHandle disk)
    {
        long length = 0;
        return DeviceIoControl(disk, IoctlDiskGetLengthInfo, 0, 0, (nint)(&length), 8,
                out var written, 0) && written >= 8
            ? length
            : 0;
    }

    /// <summary>Reads the disk's bus type and vendor/product identity via
    /// IOCTL_STORAGE_QUERY_PROPERTY (StorageDeviceProperty).</summary>
    /// <param name="disk">An open disk handle (query access suffices).</param>
    /// <param name="busType">The STORAGE_BUS_TYPE value, -1 on failure.</param>
    /// <param name="product">Vendor + product strings, trimmed, possibly empty.</param>
    internal static bool TryGetDeviceDescriptor(
        SafeFileHandle disk, out int busType, out string product)
    {
        busType = -1;
        product = "";
        // STORAGE_PROPERTY_QUERY { StorageDeviceProperty=0, PropertyStandardQuery=0 }.
        var query = stackalloc byte[12];
        var buffer = stackalloc byte[1024];
        if (!DeviceIoControl(disk, IoctlStorageQueryProperty, (nint)query, 12, (nint)buffer,
                1024, out var written, 0)
            || written < DeviceDescriptorHeaderSize)
        {
            return false;
        }
        (busType, product) =
            ReadDeviceDescriptor(new ReadOnlySpan<byte>(buffer, (int)written));
        return true;
    }

    /// <summary>The fixed header size of STORAGE_DEVICE_DESCRIPTOR.</summary>
    internal const int DeviceDescriptorHeaderSize = 36;

    /// <summary>Decodes a STORAGE_DEVICE_DESCRIPTOR buffer: the bus type and the
    /// combined vendor+product identity string.</summary>
    /// <param name="buffer">The descriptor, header plus trailing string data.</param>
    internal static (int BusType, string Product) ReadDeviceDescriptor(
        ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < DeviceDescriptorHeaderSize)
        {
            return (-1, "");
        }
        var busType = BitConverter.ToInt32(buffer[28..]);
        var vendor = ReadAnsiAt(buffer, BitConverter.ToInt32(buffer[12..]));
        var product = ReadAnsiAt(buffer, BitConverter.ToInt32(buffer[16..]));
        var combined = $"{vendor} {product}".Trim();
        return (busType, combined);
    }

    /// <summary>Reads a NUL-terminated ANSI string at a descriptor-relative
    /// offset; empty for offset 0 or out-of-range offsets.</summary>
    private static string ReadAnsiAt(ReadOnlySpan<byte> buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length)
        {
            return "";
        }
        var slice = buffer[offset..];
        var end = slice.IndexOf((byte)0);
        if (end >= 0)
        {
            slice = slice[..end];
        }
        return System.Text.Encoding.ASCII.GetString(slice).Trim();
    }

    /// <summary>GPT partition-type GUID for Linux filesystem data — the ext4
    /// partitions a Steam Deck card carries.</summary>
    internal static Guid LinuxFilesystemGuid { get; } =
        new("0fc63daf-8483-4772-8e79-3d69d8477de4");

    /// <summary>One partition's identifying type facts.</summary>
    /// <param name="MbrType">The MBR partition-type byte (0x83 = Linux), 0 for GPT disks.</param>
    /// <param name="GptType">The GPT partition-type GUID, empty for MBR disks.</param>
    internal readonly record struct PartitionType(byte MbrType, Guid GptType)
    {
        /// <summary>Whether this partition looks like a Linux filesystem.</summary>
        internal bool IsLinux => MbrType == 0x83 || GptType == LinuxFilesystemGuid;
    }

    /// <summary>Reads the disk's partition style and per-partition types, for the
    /// "this looks like a Steam Deck card" hint. Returns false when the layout
    /// cannot be read (RAW/uninitialized disks commonly fail here — the caller
    /// treats that as "no recognizable partitions").</summary>
    /// <param name="disk">An open disk handle.</param>
    /// <param name="partitionStyle">0 MBR, 1 GPT, 2 RAW.</param>
    /// <param name="partitions">The partition types found.</param>
    internal static bool TryGetPartitionTypes(
        SafeFileHandle disk, out int partitionStyle,
        out System.Collections.Generic.List<PartitionType> partitions)
    {
        const int BufferSize = 8192;
        var buffer = stackalloc byte[BufferSize];
        if (!DeviceIoControl(disk, IoctlDiskGetDriveLayoutEx, 0, 0, (nint)buffer, BufferSize,
                out var written, 0))
        {
            partitionStyle = 2;
            partitions = [];
            return false;
        }
        (partitionStyle, partitions) =
            ReadDriveLayout(new ReadOnlySpan<byte>(buffer, (int)written));
        return true;
    }

    /// <summary>DRIVE_LAYOUT_INFORMATION_EX geometry: entries start after the
    /// 48-byte header, one PARTITION_INFORMATION_EX (144 bytes) each.</summary>
    internal const int DriveLayoutHeaderSize = 48;

    /// <summary>The size of one PARTITION_INFORMATION_EX record.</summary>
    internal const int PartitionRecordSize = 144;

    /// <summary>Decodes a DRIVE_LAYOUT_INFORMATION_EX buffer into the partition
    /// style and each partition's type. Zeroed MBR entries (empty table slots —
    /// MBR layouts always report 4-slot multiples) are skipped.</summary>
    /// <param name="buffer">The layout buffer as returned by the IOCTL.</param>
    internal static (int Style, System.Collections.Generic.List<PartitionType> Partitions)
        ReadDriveLayout(ReadOnlySpan<byte> buffer)
    {
        var partitions = new System.Collections.Generic.List<PartitionType>();
        if (buffer.Length < DriveLayoutHeaderSize)
        {
            return (2, partitions);
        }
        var style = BitConverter.ToInt32(buffer);
        var count = BitConverter.ToInt32(buffer[4..]);
        for (var i = 0; i < count; i++)
        {
            var at = DriveLayoutHeaderSize + (i * PartitionRecordSize);
            if (at + PartitionRecordSize > buffer.Length)
            {
                break;
            }
            var entry = buffer.Slice(at, PartitionRecordSize);
            // Union at offset 32: GPT PartitionType GUID / MBR PartitionType byte.
            if (style == 1)
            {
                partitions.Add(new PartitionType(0, new Guid(entry.Slice(32, 16))));
            }
            else if (style == 0)
            {
                var mbrType = entry[32];
                if (mbrType != 0)
                {
                    partitions.Add(new PartitionType(mbrType, Guid.Empty));
                }
            }
        }
        return (style, partitions);
    }

    // ---- volume-arrival broadcast ----

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static partial nint SendMessageTimeoutW(
        nint hWnd, uint msg, nuint wParam, nint lParam, uint flags, uint timeout,
        out nuint result);

    /// <summary>Broadcasts a synthetic volume-arrival notification
    /// (WM_DEVICECHANGE / DBT_DEVICEARRIVAL / DEV_BROADCAST_VOLUME) for a drive
    /// letter — the same message a real card insertion generates. Used after the
    /// Format flow writes the Steam library files: the REAL arrival fired when
    /// the empty volume mounted, before the library existed, so a running Steam
    /// has already scanned and found nothing; this re-poke makes drive watchers
    /// (Steam's library detection, Explorer) look again. Best effort.</summary>
    /// <param name="letter">The drive letter that should be re-scanned.</param>
    internal static void BroadcastVolumeArrival(char letter)
    {
        const uint WmDeviceChange = 0x0219;
        const nuint DbtDeviceArrival = 0x8000;
        const int DbtDevTypVolume = 2;
        const uint SmtoAbortIfHung = 0x0002;
        var index = char.ToUpperInvariant(letter) - 'A';
        if (index is < 0 or > 25)
        {
            return;
        }
        // DEV_BROADCAST_VOLUME: size, devicetype, reserved, unitmask, flags.
        var broadcast = stackalloc byte[20];
        BitConverter.TryWriteBytes(new Span<byte>(broadcast, 4), 20);
        BitConverter.TryWriteBytes(new Span<byte>(broadcast + 4, 4), DbtDevTypVolume);
        BitConverter.TryWriteBytes(new Span<byte>(broadcast + 12, 4), 1u << index);
        SendMessageTimeoutW(0xFFFF, WmDeviceChange, DbtDeviceArrival, (nint)broadcast,
            SmtoAbortIfHung, 1000, out _);
    }

    // ---- eject operations ----

    /// <summary>Asks PnP to eject a device — the same operation as Explorer's
    /// "Safely Remove Hardware". Dismounts and flushes every volume on the
    /// device.</summary>
    /// <param name="devInst">The devnode to eject (see <see cref="FindEjectTarget"/>).</param>
    /// <param name="vetoType">Why the eject was refused, when it was.</param>
    /// <param name="vetoName">The vetoing module/service/path, possibly empty.</param>
    /// <returns>The CONFIGRET code: <see cref="CrSuccess"/>, <see cref="CrRemoveVetoed"/>,
    /// or another CR_* failure.</returns>
    internal static int RequestDeviceEject(
        uint devInst, out PnpVetoType vetoType, out string vetoName)
    {
        const int MaxPath = 260;
        var buffer = stackalloc char[MaxPath];
        var result = CM_Request_Device_EjectW(devInst, out var rawVeto, buffer, MaxPath, 0);
        vetoType = (PnpVetoType)rawVeto;
        vetoName = ReadBoundedString(buffer, MaxPath);
        return result;
    }

    /// <summary>Takes the exclusive volume lock — the open-files check for the
    /// media-level eject. Fails while any other handle is open on the volume.</summary>
    /// <param name="volume">A volume opened via <see cref="OpenVolumeForEject"/>.</param>
    internal static bool LockVolume(SafeFileHandle volume) =>
        DeviceIoControl(volume, FsctlLockVolume, 0, 0, 0, 0, out _, 0);

    /// <summary>Dismounts the file system, flushing it first.</summary>
    /// <param name="volume">A locked volume handle.</param>
    internal static bool DismountVolume(SafeFileHandle volume) =>
        DeviceIoControl(volume, FsctlDismountVolume, 0, 0, 0, 0, out _, 0);

    /// <summary>Releases any software media lock (PREVENT_MEDIA_REMOVAL = FALSE),
    /// then asks the device to eject the media. Card readers without a motor may
    /// fail the final call — the caller treats lock+dismount as the real
    /// success.</summary>
    /// <param name="volume">A locked, dismounted volume handle.</param>
    internal static bool EjectMedia(SafeFileHandle volume)
    {
        byte allow = 0;
        DeviceIoControl(volume, IoctlStorageMediaRemoval, (nint)(&allow), 1, 0, 0, out _, 0);
        return DeviceIoControl(volume, IoctlStorageEjectMedia, 0, 0, 0, 0, out _, 0);
    }

    /// <summary>The calling thread's last Win32 error, for log lines.</summary>
    internal static int LastWin32Error() => Marshal.GetLastPInvokeError();

    // ---- DOS-to-NT device path translation ----

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint QueryDosDeviceW(string deviceName, char* targetPath, uint maxLength);

    /// <summary>Converts a local DOS path to the NT device notation kernel
    /// drivers (HidHide) consume. Returns the normalized input when Windows
    /// cannot translate it, logging why.</summary>
    /// <param name="path">The DOS path to translate.</param>
    internal static string FromDosPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        var root = Path.GetPathRoot(fullPath);
        if (root is null || root.Length < 2 || root[1] != ':')
        {
            Core.Log.Warn(
                $"NT device-path conversion skipped: application path is not on a local drive ({fullPath}).");
            return fullPath;
        }

        // QueryDosDevice returns a MULTI_SZ; the first mapping is the active
        // drive target, which is the one HidHide compares against.
        var buffer = stackalloc char[1024];
        var target = QueryDosDeviceW(root[..2], buffer, 1024) == 0
            ? ""
            : ReadBoundedString(buffer, 1024);
        if (target.Length == 0)
        {
            Core.Log.Warn(
                $"NT device-path conversion failed for {root[..2]} with Win32 error "
                + $"{Marshal.GetLastPInvokeError()}; HidHide readability may be unavailable.");
            return fullPath;
        }

        return target + fullPath[2..];
    }
}
