using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Write access to one RTSS on-screen-display slot, for the writer and tests.</summary>
/// <remarks>Offsets are absolute within the <c>RTSSSharedMemoryV2</c> mapping. The busy flag is
/// separate from the plain writes because the real region takes it with an interlocked exchange
/// on the mapped page, which an accessor cannot express.</remarks>
internal interface IRtssOsdRegion
{
    /// <summary>Mapped length in bytes.</summary>
    long Capacity { get; }

    /// <summary>Reads one little-endian DWORD.</summary>
    /// <param name="offset">Byte offset.</param>
    /// <returns>The value.</returns>
    uint ReadUInt32(long offset);

    /// <summary>Writes one little-endian DWORD.</summary>
    /// <param name="offset">Byte offset.</param>
    /// <param name="value">The value.</param>
    void WriteUInt32(long offset, uint value);

    /// <summary>Reads <paramref name="count"/> bytes into <paramref name="buffer"/>.</summary>
    /// <param name="offset">Byte offset.</param>
    /// <param name="buffer">Destination.</param>
    /// <param name="count">Bytes to read.</param>
    void ReadBytes(long offset, byte[] buffer, int count);

    /// <summary>Writes <paramref name="count"/> bytes from <paramref name="buffer"/>.</summary>
    /// <param name="offset">Byte offset.</param>
    /// <param name="buffer">Source.</param>
    /// <param name="count">Bytes to write.</param>
    void WriteBytes(long offset, byte[] buffer, int count);

    /// <summary>Attempts to take the v2.14+ OSD busy flag. False means skip this text write.</summary>
    /// <param name="offset">The busy DWORD's byte offset.</param>
    /// <returns>Whether the flag was taken.</returns>
    bool TryAcquireBusy(long offset);

    /// <summary>Releases the busy flag taken by <see cref="TryAcquireBusy"/>.</summary>
    /// <param name="offset">The busy DWORD's byte offset.</param>
    void ReleaseBusy(long offset);
}

/// <summary>
/// The RTSS OSD slot protocol: claim by owner name, update text, release by zeroing. A C# port of
/// the slot semantics of RTSSSharedMemoryNET (the library HandheldCompanion ships), decided over
/// vendoring its C++/CLI project — the decompiled claim/update/release paths and the header layout
/// were live-verified against RTSS 2.21 on the reference Claw (2026-09-01).
/// </summary>
internal static class RtssOsdSlots
{
    // The server writes dwSignature = 'RTSS' as a C multichar constant, so the DWORD VALUE is
    // 0x52545353 and the bytes in memory read "SSTR". Live-verified on RTSS 2.21 (2026-09-01);
    // the byte-order-mirrored 0x53535452 is the value that never matches a real server. Same
    // header the frametime reader walks: signature, version, app fields, then the OSD fields.
    private const uint Signature = 0x52545353;
    private const int HeaderVersionOffset = 4;
    private const int HeaderOsdEntrySizeOffset = 20;
    private const int HeaderOsdArrOffsetOffset = 24;
    private const int HeaderOsdArrSizeOffset = 28;
    private const int HeaderOsdFrameOffset = 32;
    private const int HeaderOsdBusyOffset = 36;

    // OSD entry: szOSD[256] (pre-2.7 text), szOSDOwner[256], szOSDEx[4096] (2.7+ text).
    private const int OwnerOffset = 256;
    private const int OwnerLength = 256;
    private const int TextOffset = 0;
    private const int TextLength = 256;
    private const int TextExOffset = 512;
    private const int TextExLength = 4096;

    // 2.7 moved text to szOSDEx; 2.14 added the busy flag around text writes.
    private const uint TextExMinimumVersion = 0x0002_0007;
    private const uint BusyMinimumVersion = 0x0002_000E;

    /// <summary>Claims a slot for <paramref name="owner"/> when needed and writes the OSD text.</summary>
    /// <param name="region">The mapped RTSS shared memory.</param>
    /// <param name="owner">Slot owner identity, ANSI, at most 255 bytes.</param>
    /// <param name="text">OSD text; empty clears the display while keeping the slot.</param>
    /// <returns>False when the mapping is not an RTSS region this build understands or every
    /// slot is owned by someone else.</returns>
    /// <remarks>Slot 0 belongs to RTSS itself and is never touched. An existing slot carrying
    /// this owner is reused, so the claim survives a writer restart.</remarks>
    internal static bool TryWrite(IRtssOsdRegion region, string owner, string text)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!TryReadHeader(region, out uint version, out long entrySize, out long arrayOffset,
                out uint arraySize))
        {
            return false;
        }

        byte[] ownerBytes = Encoding.ASCII.GetBytes(owner);
        long slot = FindSlot(region, ownerBytes, entrySize, arrayOffset, arraySize, claim: true);
        if (slot < 0)
        {
            return false;
        }

        long entry = arrayOffset + (slot * entrySize);
        byte[] textBytes = Encoding.ASCII.GetBytes(text);
        bool useExtended = version >= TextExMinimumVersion
            && entrySize >= TextExOffset + TextExLength;
        long textAt = entry + (useExtended ? TextExOffset : TextOffset);
        int capacity = useExtended ? TextExLength : TextLength;
        byte[] payload = new byte[Math.Min(textBytes.Length, capacity - 1) + 1];
        Array.Copy(textBytes, payload, payload.Length - 1);

        if (version >= BusyMinimumVersion)
        {
            if (region.TryAcquireBusy(HeaderOsdBusyOffset))
            {
                region.WriteBytes(textAt, payload, payload.Length);
                region.ReleaseBusy(HeaderOsdBusyOffset);
            }
            // A held busy flag means another writer is mid-update; the library skips the write
            // too and the next 100 ms tick repeats it.
        }
        else
        {
            region.WriteBytes(textAt, payload, payload.Length);
        }

        region.WriteUInt32(HeaderOsdFrameOffset, region.ReadUInt32(HeaderOsdFrameOffset) + 1);
        return true;
    }

    /// <summary>Releases every slot owned by <paramref name="owner"/> by zeroing it whole.</summary>
    /// <param name="region">The mapped RTSS shared memory.</param>
    /// <param name="owner">Slot owner identity.</param>
    internal static void Release(IRtssOsdRegion region, string owner)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!TryReadHeader(region, out _, out long entrySize, out long arrayOffset,
                out uint arraySize))
        {
            return;
        }

        byte[] ownerBytes = Encoding.ASCII.GetBytes(owner);
        long slot = FindSlot(region, ownerBytes, entrySize, arrayOffset, arraySize, claim: false);
        if (slot < 0)
        {
            return;
        }

        long entry = arrayOffset + (slot * entrySize);
        byte[] zero = new byte[Math.Min(entrySize, 65536)];
        long remaining = entrySize;
        long at = entry;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, zero.Length);
            region.WriteBytes(at, zero, chunk);
            at += chunk;
            remaining -= chunk;
        }

        region.WriteUInt32(HeaderOsdFrameOffset, region.ReadUInt32(HeaderOsdFrameOffset) + 1);
    }

    private static bool TryReadHeader(
        IRtssOsdRegion region,
        out uint version,
        out long entrySize,
        out long arrayOffset,
        out uint arraySize)
    {
        version = 0;
        entrySize = 0;
        arrayOffset = 0;
        arraySize = 0;
        if (region.Capacity < HeaderOsdBusyOffset + 4 || region.ReadUInt32(0) != Signature)
        {
            return false;
        }

        version = region.ReadUInt32(HeaderVersionOffset);
        entrySize = region.ReadUInt32(HeaderOsdEntrySizeOffset);
        arrayOffset = region.ReadUInt32(HeaderOsdArrOffsetOffset);
        arraySize = region.ReadUInt32(HeaderOsdArrSizeOffset);
        return entrySize >= OwnerOffset + OwnerLength && arraySize > 1;
    }

    private static long FindSlot(
        IRtssOsdRegion region,
        byte[] owner,
        long entrySize,
        long arrayOffset,
        uint arraySize,
        bool claim)
    {
        byte[] current = new byte[OwnerLength];
        long firstEmpty = -1;
        for (uint index = 1; index < arraySize; index++)
        {
            long entry = arrayOffset + (index * entrySize);
            if (entry < 0 || entry + entrySize > region.Capacity)
            {
                break;
            }

            region.ReadBytes(entry + OwnerOffset, current, OwnerLength);
            if (OwnerMatches(current, owner))
            {
                return index;
            }

            if (firstEmpty < 0 && current[0] == 0)
            {
                firstEmpty = index;
            }
        }

        if (!claim || firstEmpty < 0)
        {
            return -1;
        }

        byte[] claimBytes = new byte[OwnerLength];
        Array.Copy(owner, claimBytes, Math.Min(owner.Length, OwnerLength - 1));
        region.WriteBytes(arrayOffset + (firstEmpty * entrySize) + OwnerOffset,
            claimBytes, OwnerLength);
        return firstEmpty;
    }

    private static bool OwnerMatches(byte[] current, byte[] owner)
    {
        if (owner.Length >= OwnerLength || current.Length < owner.Length + 1)
        {
            return false;
        }

        for (int i = 0; i < owner.Length; i++)
        {
            if (current[i] != owner[i])
            {
                return false;
            }
        }

        return current[owner.Length] == 0;
    }
}

/// <summary>Owns WSGM's RTSS OSD slot: read-write mapping, reopen after an RTSS restart, and a
/// zeroed release on dispose so the slot returns to the pool.</summary>
internal sealed class RtssOsdWriter : IDisposable
{
    private const string MapName = "RTSSSharedMemoryV2";
    private const string Owner = "WSGM";

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private bool _disposed;

    /// <summary>Writes the OSD text, claiming a slot when needed.</summary>
    /// <param name="text">OSD text; empty clears the display while keeping the slot.</param>
    /// <returns>False while RTSS is not running or offers no free slot.</returns>
    internal bool TryUpdate(string text)
    {
        if (_disposed || !TryOpen())
        {
            return false;
        }

        try
        {
            bool written = RtssOsdSlots.TryWrite(new AccessorRegion(_view!), Owner, text);
            Log.Change(
                "rtss-osd-slot",
                written
                    ? "RTSS OSD slot claimed and updating."
                    : "RTSS OSD slot refused: foreign mapping layout or no free slot.");
            return written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or ObjectDisposedException)
        {
            // RTSS exited or replaced its mapping mid-write; reopen on the next tick.
            Log.Warn($"RTSS OSD write failed; reopening next tick: {ex.Message}");
            Close();
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_view is not null)
            {
                RtssOsdSlots.Release(new AccessorRegion(_view), Owner);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or ObjectDisposedException)
        {
            // RTSS already gone; there is no slot left to hand back.
        }

        Close();
    }

    private bool TryOpen()
    {
        if (_view is not null)
        {
            return true;
        }

        try
        {
            _map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
            _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            Log.Change("rtss-osd-map", "RTSS OSD mapping open for writing.");
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
            or IOException)
        {
            // Not running, or running elevated while WSGM is not — the OSD is simply
            // unavailable until RTSS is reachable, exactly like the frametime source.
            Log.Change(
                "rtss-osd-map",
                $"RTSS OSD mapping unavailable: {ex.GetType().Name}: {ex.Message}");
            Close();
            return false;
        }
    }

    private void Close()
    {
        _view?.Dispose();
        _view = null;
        _map?.Dispose();
        _map = null;
    }

    private sealed class AccessorRegion(MemoryMappedViewAccessor view) : IRtssOsdRegion
    {
        public long Capacity => view.Capacity;

        public uint ReadUInt32(long offset) => view.ReadUInt32(offset);

        public void WriteUInt32(long offset, uint value) => view.Write(offset, value);

        public void ReadBytes(long offset, byte[] buffer, int count) =>
            view.ReadArray(offset, buffer, 0, count);

        public void WriteBytes(long offset, byte[] buffer, int count) =>
            view.WriteArray(offset, buffer, 0, count);

        public unsafe bool TryAcquireBusy(long offset)
        {
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            try
            {
                return Interlocked.CompareExchange(ref *(int*)(pointer + offset), 1, 0) == 0;
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        public unsafe void ReleaseBusy(long offset)
        {
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            try
            {
                Volatile.Write(ref *(int*)(pointer + offset), 0);
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }
}

/// <summary>One sample of everything the OSD can currently source. Null omits the element, which
/// is HandheldCompanion's own degrade rule — an entry with no elements never renders.</summary>
internal sealed record RtssOsdMetrics(
    double? CpuLoadPercent,
    double? CpuPowerWatts,
    double? CpuTemperatureC,
    double? GpuLoadPercent,
    double? GpuPowerWatts,
    double? GpuTemperatureC,
    double? GpuMemoryUsedGb,
    double? GpuMemoryTotalGb,
    double? MemoryUsedGb,
    double? MemoryTotalGb,
    double? BatteryPercent,
    double? BatteryWatts,
    int? BatteryMinutesRemaining,
    bool OnAcPower)
{
    internal static readonly RtssOsdMetrics Empty = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, false);
}

/// <summary>The custom overlay's configuration — selector level 4, HandheldCompanion's Custom
/// level: which widgets render, in which order, at which detail. Configured in WSGM's Settings
/// rather than through any RTSS-side mechanism.</summary>
/// <param name="Order">Canonical widget names in render order, one row per name.</param>
/// <param name="Time">Clock detail: 0 hidden, 1 short time, 2 full timestamp.</param>
/// <param name="Fps">Framerate detail: 0 hidden, 1 FPS, 2 FPS and frametime.</param>
/// <param name="Cpu">CPU detail: 0 hidden, 1 load and power, 2 adds temperature.</param>
/// <param name="Ram">Memory detail: 0 hidden, 1 used, 2 used of total.</param>
/// <param name="Gpu">GPU detail: 0 hidden, 1 load and power, 2 adds temperature.</param>
/// <param name="Vram">Video-memory detail: 0 hidden, 1 used, 2 used of total.</param>
/// <param name="Battery">Battery detail: 0 hidden, 1 percent and time, 2 adds charge rate.</param>
internal sealed record RtssOsdCustomSettings(
    IReadOnlyList<string> Order,
    int Time,
    int Fps,
    int Cpu,
    int Ram,
    int Gpu,
    int Vram,
    int Battery)
{
    private static readonly string[] KnownWidgets =
        ["TIME", "GPU", "CPU", "VRAM", "RAM", "BATT", "FPS"];

    /// <summary>HandheldCompanion's defaults: its shipped order, every widget at full detail.</summary>
    internal static readonly RtssOsdCustomSettings Default =
        new(KnownWidgets: "Time,GPU,CPU,VRAM,RAM,BATT,FPS", 2, 2, 2, 2, 2, 2, 2);

    private RtssOsdCustomSettings(
        string KnownWidgets, int Time, int Fps, int Cpu, int Ram, int Gpu, int Vram, int Battery)
        : this(ParseOrder(KnownWidgets), Time, Fps, Cpu, Ram, Gpu, Vram, Battery)
    {
    }

    /// <summary>Builds the settings from the persisted configuration, dropping unknown widget
    /// names and clamping every detail level into range.</summary>
    /// <param name="configuration">The persisted performance configuration.</param>
    /// <returns>The renderer-ready settings.</returns>
    internal static RtssOsdCustomSettings FromConfig(PerformanceConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new(
            ParseOrder(configuration.OsdCustomOrder),
            Math.Clamp(configuration.OsdCustomTime, 0, 2),
            Math.Clamp(configuration.OsdCustomFps, 0, 2),
            Math.Clamp(configuration.OsdCustomCpu, 0, 2),
            Math.Clamp(configuration.OsdCustomRam, 0, 2),
            Math.Clamp(configuration.OsdCustomGpu, 0, 2),
            Math.Clamp(configuration.OsdCustomVram, 0, 2),
            Math.Clamp(configuration.OsdCustomBattery, 0, 2));
    }

    private static IReadOnlyList<string> ParseOrder(string? order)
    {
        List<string> names = [];
        foreach (string part in (order ?? string.Empty).Split(
            ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string canonical = part.ToUpperInvariant();
            if (Array.IndexOf(KnownWidgets, canonical) >= 0 && !names.Contains(canonical))
            {
                names.Add(canonical);
            }
        }

        return names;
    }
}

/// <summary>Reads the sensor XML RTSS's own LibreHardwareMonitor provider publishes.</summary>
/// <remarks>
/// <c>LHMDataProvider.exe</c> is the GUI-less LibreHardwareMonitor the Overlay Editor ships; it
/// exports the whole sensor tree as XML into the <c>LHMDPSharedMemory</c> mapping under the
/// <c>Global\Access_LHMDPSharedMemory</c> mutex (live-verified on the Claw, 2026-09-01). Reading
/// it gives WSGM the same values the user's overlay layouts see, with no sensor stack of its own.
/// The provider self-deduplicates, so asking it to start when the mapping is absent is safe.
/// </remarks>
internal sealed class LhmSensorReader : IDisposable
{
    private const string MapName = "LHMDPSharedMemory";
    private const string MutexName = "Global\\Access_LHMDPSharedMemory";

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private bool _disposed;

    /// <summary>Reads the current sensor XML, or null while the provider is not publishing.</summary>
    /// <returns>The XML fragment stream (multiple root elements), or null.</returns>
    internal string? TryReadXml()
    {
        if (_disposed || !TryOpen())
        {
            return null;
        }

        try
        {
            Mutex? gate = null;
            bool held = false;
            try
            {
                try
                {
                    gate = Mutex.OpenExisting(MutexName);
                    held = gate.WaitOne(TimeSpan.FromMilliseconds(200));
                }
                catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException
                    or UnauthorizedAccessException or AbandonedMutexException)
                {
                    // No lock is still a readable snapshot: the provider replaces the buffer
                    // in one write and the XML parse rejects a torn one harmlessly.
                    held = ex is AbandonedMutexException;
                }

                long capacity = _view!.Capacity;
                byte[] buffer = new byte[Math.Min(capacity, 4 * 1024 * 1024)];
                _view.ReadArray(0, buffer, 0, buffer.Length);
                int length = Array.IndexOf(buffer, (byte)0);
                if (length <= 0)
                {
                    return null;
                }

                return Encoding.UTF8.GetString(buffer, 0, length);
            }
            finally
            {
                if (held)
                {
                    gate?.ReleaseMutex();
                }

                gate?.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or ObjectDisposedException)
        {
            Log.Warn($"LHM sensor read failed; reopening next sample: {ex.Message}");
            Close();
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        Close();
    }

    private bool TryOpen()
    {
        if (_view is not null)
        {
            return true;
        }

        try
        {
            _map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            Log.Change("rtss-lhm-map", "LHM sensor shared memory open.");
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
            or IOException)
        {
            Log.Change(
                "rtss-lhm-map",
                $"LHM sensor shared memory unavailable: {ex.GetType().Name}.");
            Close();
            return false;
        }
    }

    private void Close()
    {
        _view?.Dispose();
        _view = null;
        _map?.Dispose();
        _map = null;
    }
}

/// <summary>Selects the OSD's values out of the provider's sensor XML — HandheldCompanion's
/// sensor-name rules (<c>LibreHardwarePlatform</c>) ported onto the exported tree.</summary>
internal static class RtssLhmSensors
{
    /// <summary>Parses the provider XML into the metrics it can supply. Fields with no matching
    /// sensor stay null; a torn or foreign buffer yields <see cref="RtssOsdMetrics.Empty"/>.</summary>
    /// <param name="xml">The exported sensor XML (a fragment stream, one element per hardware).</param>
    /// <returns>The partial metrics.</returns>
    internal static RtssOsdMetrics Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        double? cpuLoad = null, cpuPower = null, cpuTemp = null;
        double? gpuLoad = null, gpuPower = null, gpuTemp = null;
        int gpuMemoryUsedRank = int.MaxValue, gpuMemoryTotalRank = int.MaxValue;
        double? gpuMemoryUsedMb = null, gpuMemoryTotalMb = null;
        double? memoryUsedGb = null, memoryAvailableGb = null;
        try
        {
            using System.Xml.XmlReader reader = System.Xml.XmlReader.Create(
                new StringReader(xml),
                new System.Xml.XmlReaderSettings
                {
                    ConformanceLevel = System.Xml.ConformanceLevel.Fragment,
                });
            string hardwareType = string.Empty;
            string element = string.Empty;
            string sensorName = string.Empty, sensorType = string.Empty, sensorValue = string.Empty;
            bool inSensor = false;
            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element)
                {
                    element = reader.Name;
                    if (element == "sensor")
                    {
                        inSensor = true;
                        sensorName = sensorType = sensorValue = string.Empty;
                    }
                }
                else if (reader.NodeType == System.Xml.XmlNodeType.Text)
                {
                    if (!inSensor && element == "type")
                    {
                        hardwareType = reader.Value;
                    }
                    else if (inSensor)
                    {
                        switch (element)
                        {
                            case "name": sensorName = reader.Value; break;
                            case "type": sensorType = reader.Value; break;
                            case "value": sensorValue = reader.Value; break;
                        }
                    }
                }
                else if (reader.NodeType == System.Xml.XmlNodeType.EndElement
                    && reader.Name == "sensor")
                {
                    inSensor = false;
                    if (!TryParseValue(sensorValue, out double value))
                    {
                        continue;
                    }

                    if (hardwareType.StartsWith("Cpu", StringComparison.Ordinal))
                    {
                        // HC: total load, package power, package temperature.
                        if (sensorType == "Load" && sensorName == "CPU Total")
                        {
                            cpuLoad = value;
                        }
                        else if (sensorType == "Power"
                            && sensorName is "CPU Package" or "Package")
                        {
                            cpuPower = value;
                        }
                        else if (sensorType == "Temperature"
                            && sensorName is "CPU Package" or "Core (Tctl/Tdie)")
                        {
                            cpuTemp = value;
                        }
                    }
                    else if (hardwareType.StartsWith("Gpu", StringComparison.Ordinal))
                    {
                        if (sensorType == "Load"
                            && (sensorName == "D3D 3D" || (sensorName == "GPU Core" && gpuLoad is null)))
                        {
                            gpuLoad = sensorName == "D3D 3D" ? value : gpuLoad ?? value;
                        }
                        else if (sensorType == "Power" && gpuPower is null
                            && sensorName is "GPU Power" or "GPU Package" or "GPU Core" or "GPU SoC")
                        {
                            gpuPower = value;
                        }
                        else if (sensorType == "Temperature" && sensorName == "GPU Core")
                        {
                            gpuTemp = value;
                        }
                        else if (sensorType is "Data" or "SmallData")
                        {
                            // HC's preference order: dedicated GPU memory beats D3D dedicated
                            // beats D3D shared; the exporter reports megabytes.
                            int usedRank = sensorName switch
                            {
                                "GPU Memory Used" => 0,
                                "D3D Dedicated Memory Used" => 1,
                                "D3D Shared Memory Used" => 2,
                                _ => -1,
                            };
                            if (usedRank >= 0 && usedRank < gpuMemoryUsedRank)
                            {
                                gpuMemoryUsedRank = usedRank;
                                gpuMemoryUsedMb = value;
                            }

                            int totalRank = sensorName switch
                            {
                                "GPU Memory Total" => 0,
                                "D3D Dedicated Memory Total" => 1,
                                "D3D Shared Memory Total" => 2,
                                _ => -1,
                            };
                            if (totalRank >= 0 && totalRank < gpuMemoryTotalRank)
                            {
                                gpuMemoryTotalRank = totalRank;
                                gpuMemoryTotalMb = value;
                            }
                        }
                    }
                    else if (hardwareType == "Memory")
                    {
                        if (sensorType == "Data" && sensorName == "Memory Used")
                        {
                            memoryUsedGb = value;
                        }
                        else if (sensorType == "Data" && sensorName == "Memory Available")
                        {
                            memoryAvailableGb = value;
                        }
                    }
                }
            }
        }
        catch (System.Xml.XmlException)
        {
            // A torn snapshot read without the mutex; the next sample gets a whole one.
            return RtssOsdMetrics.Empty;
        }

        return RtssOsdMetrics.Empty with
        {
            CpuLoadPercent = cpuLoad,
            CpuPowerWatts = cpuPower,
            CpuTemperatureC = cpuTemp,
            GpuLoadPercent = gpuLoad,
            GpuPowerWatts = gpuPower,
            GpuTemperatureC = gpuTemp,
            GpuMemoryUsedGb = gpuMemoryUsedMb / 1024.0,
            GpuMemoryTotalGb = gpuMemoryTotalMb / 1024.0,
            MemoryUsedGb = memoryUsedGb,
            MemoryTotalGb = memoryUsedGb + memoryAvailableGb,
        };
    }

    private static bool TryParseValue(string text, out double value)
    {
        // The exporter formats with the provider machine's culture: "37,5" on this device.
        return double.TryParse(
            text.Replace(',', '.'),
            System.Globalization.NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }
}

/// <summary>Sources the OSD metrics: RTSS's LibreHardwareMonitor provider first, with the kernel
/// counters (CPU times, memory status, power status) filling anything the provider does not
/// publish. Samples are cached for one second — HandheldCompanion's sensor cadence — so the OSD's
/// 100 ms redraw does not turn scheduler noise into a flickering number.</summary>
internal sealed class RtssOsdMetricsSource : IDisposable
{
    private static readonly TimeSpan SampleLifetime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProviderStartCooldown = TimeSpan.FromSeconds(30);

    private readonly LhmSensorReader _lhm = new();
    private readonly Func<string?>? _rtssExecutablePath;
    // "Never" is one interval in the past, not long.MinValue: TickCount64 minus MinValue
    // overflows negative, which read as "cache still fresh" forever and shipped an OSD with no
    // sensor values at all. One interval back keeps the first call eligible even right at boot.
    private RtssOsdMetrics _cached = RtssOsdMetrics.Empty;
    private long _cachedAtTicks = -(long)SampleLifetime.TotalMilliseconds;
    private long _providerAttemptTicks = -(long)ProviderStartCooldown.TotalMilliseconds;
    private long _lastIdle;
    private long _lastBusyBase;
    private bool _hasCpuSample;
    private double? _batteryWatts;
    private long _batteryWattsAtTicks = -1000;

    internal RtssOsdMetricsSource(Func<string?>? rtssExecutablePath = null)
    {
        _rtssExecutablePath = rtssExecutablePath;
    }

    /// <summary>Takes one sample, at most once per second. CPU load needs two samples before the
    /// kernel fallback reports.</summary>
    /// <returns>The sample.</returns>
    internal RtssOsdMetrics Sample()
    {
        long now = Environment.TickCount64;
        if (now - _cachedAtTicks < SampleLifetime.TotalMilliseconds)
        {
            return _cached;
        }

        _cachedAtTicks = now;
        string? xml = _lhm.TryReadXml();
        RtssOsdMetrics metrics = xml is null ? RtssOsdMetrics.Empty : RtssLhmSensors.Parse(xml);
        if (xml is null)
        {
            TryStartProvider(now);
        }

        metrics = metrics with { CpuLoadPercent = metrics.CpuLoadPercent ?? SampleCpu() };
        if (metrics.MemoryUsedGb is null || metrics.MemoryTotalGb is null)
        {
            (double? usedGb, double? totalGb) = SampleMemory();
            metrics = metrics with
            {
                MemoryUsedGb = metrics.MemoryUsedGb ?? usedGb,
                MemoryTotalGb = metrics.MemoryTotalGb ?? totalGb,
            };
        }

        // The provider ships with its battery section disabled, so the battery stays kernel-fed.
        (double? percent, int? minutes, bool onAc) = SampleBattery();
        _cached = metrics with
        {
            BatteryPercent = percent,
            BatteryWatts = percent is null ? null : SampleBatteryWatts(now),
            BatteryMinutesRemaining = minutes,
            OnAcPower = onAc,
        };
        return _cached;
    }

    /// <inheritdoc/>
    public void Dispose() => _lhm.Dispose();

    /// <summary>Starts RTSS's LHM provider when its mapping is absent. It deduplicates itself,
    /// and the Overlay Editor starts the same process the same way.</summary>
    /// <param name="nowTicks">Current tick count, for the retry cooldown.</param>
    private void TryStartProvider(long nowTicks)
    {
        if (nowTicks - _providerAttemptTicks < ProviderStartCooldown.TotalMilliseconds)
        {
            return;
        }

        _providerAttemptTicks = nowTicks;
        string? executable = _rtssExecutablePath?.Invoke();
        string? directory = executable is null ? null : Path.GetDirectoryName(executable);
        if (directory is null)
        {
            Log.Change("rtss-lhm-provider", "LHM sensor provider not started: RTSS location unknown.");
            return;
        }

        string provider = Path.Combine(
            directory, "Plugins", "Client", "LHMDataProvider", "LHMDataProvider.exe");
        if (!File.Exists(provider))
        {
            Log.Change("rtss-lhm-provider", "LHM sensor provider not installed beside RTSS.");
            return;
        }

        try
        {
            using System.Diagnostics.Process? started = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(provider, "-i")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(provider)!,
                });
            Log.Change("rtss-lhm-provider", "LHM sensor provider start requested.");
        }
        catch (Exception ex)
        {
            Log.Change("rtss-lhm-provider", $"LHM sensor provider start failed: {ex.Message}");
        }
    }

    private double? SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            return null;
        }

        // Kernel time includes idle, so busy = (kernel + user) - idle.
        long busyBase = kernel + user;
        double? load = null;
        if (_hasCpuSample)
        {
            long totalDelta = busyBase - _lastBusyBase;
            long idleDelta = idle - _lastIdle;
            if (totalDelta > 0)
            {
                load = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
            }
        }

        _lastIdle = idle;
        _lastBusyBase = busyBase;
        _hasCpuSample = true;
        return load;
    }

    private static (double? UsedGb, double? TotalGb) SampleMemory()
    {
        NativeMethods.MemoryStatusEx status = default;
        status.Length = (uint)System.Runtime.InteropServices.Marshal
            .SizeOf<NativeMethods.MemoryStatusEx>();
        if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
        {
            return (null, null);
        }

        const double Gib = 1024.0 * 1024 * 1024;
        return ((status.TotalPhys - status.AvailPhys) / Gib, status.TotalPhys / Gib);
    }

    private static (double? Percent, int? MinutesRemaining, bool OnAcPower) SampleBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out NativeMethods.SystemPowerStatus power)
            || (power.BatteryFlag & 0x80) != 0)
        {
            return (null, null, false);
        }

        double? percent = power.BatteryLifePercent == 255 ? null : power.BatteryLifePercent;
        int? minutes = power.BatteryLifeTime == uint.MaxValue
            ? null
            : (int)(power.BatteryLifeTime / 60);
        return (percent, minutes, power.ACLineStatus == 1);
    }

    private double? SampleBatteryWatts(long nowTicks)
    {
        // The WinRT battery report allocates; the whole sample is already cached for a second,
        // so this only avoids a second report inside the same burst.
        if (nowTicks - _batteryWattsAtTicks < 1000)
        {
            return _batteryWatts;
        }

        _batteryWattsAtTicks = nowTicks;
        try
        {
            int? milliwatts = Windows.Devices.Power.Battery.AggregateBattery
                .GetReport().ChargeRateInMilliwatts;
            _batteryWatts = milliwatts is null ? null : milliwatts.Value / 1000.0;
        }
        catch (Exception)
        {
            // No aggregate battery, or WinRT unavailable in this session.
            _batteryWatts = null;
        }

        return _batteryWatts;
    }
}

/// <summary>
/// Builds the OSD text for the WSGM-rendered overlay levels. The structure, tags, colors and
/// per-level layout are HandheldCompanion's (<c>OSDManager</c> / <c>Overlay/Strategy</c>): level 1
/// is Minimal (FPS), level 2 Extended (one combined row) and level 3 Full (one row per subject).
/// <c>&lt;FR&gt;</c>/<c>&lt;FT&gt;</c> are RTSS's own framerate/frametime tags, filled per hooked
/// application, so FPS needs no sensor. Pure, for the tests.
/// </summary>
internal static class RtssOsdContent
{
    // HC's header: C0 default text, C1 separator, alignment and the two script sizes.
    private const string Header = "<C0=FFFFFF><C1=8000FF><A0=-4><S0=-50><S1=50>";
    private const string FpsColor = "FF0000";
    private const string GpuColor = "8040";
    private const string VramColor = "8000FF";
    private const string CpuColor = "80FF";
    private const string RamColor = "FF80C0";
    private const string BattColor = "FF8000";

    /// <summary>Builds the OSD text for one rendered level.</summary>
    /// <param name="level">1 to 3; anything else yields an empty display.</param>
    /// <param name="metrics">The current sample.</param>
    /// <returns>The RTSS-tagged text.</returns>
    internal static string Build(int level, RtssOsdMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return level switch
        {
            1 => Compose(MinimalFpsRow()),
            2 => Compose(ExtendedRow(metrics)),
            3 => Compose(
                Row(Entry("GPU", GpuColor, true, GpuElements(metrics, full: true))),
                Row(Entry("CPU", CpuColor, true, CpuElements(metrics, full: true))),
                Row(Entry("RAM", RamColor, true, RamElements(metrics, full: true))),
                Row(Entry("VRAM", VramColor, true, VramElements(metrics, full: true))),
                Row(Entry("BATT", BattColor, true, BatteryElements(metrics, full: true))),
                Row(Entry("<APP>", FpsColor, true, FpsElements(full: true)))),
            _ => string.Empty,
        };
    }

    private static string MinimalFpsRow() =>
        Row(Entry("<APP>", FpsColor, false, FpsElements(full: false)));

    // HC's Extended order: FPS, GPU, VRAM, CPU, RAM, BATT — every entry at its minimal detail.
    private static string ExtendedRow(RtssOsdMetrics metrics) => Row(
        Entry("<APP>", FpsColor, false, FpsElements(full: true)),
        Entry("GPU", GpuColor, false, GpuElements(metrics, full: false)),
        Entry("VRAM", VramColor, false, VramElements(metrics, full: false)),
        Entry("CPU", CpuColor, false, CpuElements(metrics, full: false)),
        Entry("RAM", RamColor, false, RamElements(metrics, full: false)),
        Entry("BATT", BattColor, false, BatteryElements(metrics, full: false)));

    /// <summary>Builds the user-configured Custom overlay — HandheldCompanion's Custom level:
    /// one row per configured widget name, each at its own detail.</summary>
    /// <param name="custom">The order and per-widget detail from WSGM's Settings.</param>
    /// <param name="metrics">The current sample.</param>
    /// <returns>The RTSS-tagged text.</returns>
    internal static string BuildCustom(RtssOsdCustomSettings custom, RtssOsdMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(custom);
        ArgumentNullException.ThrowIfNull(metrics);
        List<string> rows = [];
        foreach (string name in custom.Order)
        {
            // HC's CustomStrategy shows the widget's literal name, FPS included.
            string? entry = name switch
            {
                "TIME" when custom.Time > 0 =>
                    Entry("TIME", "FFFFFF", true, TimeElements(custom.Time == 2)),
                "GPU" when custom.Gpu > 0 =>
                    Entry("GPU", GpuColor, true, GpuElements(metrics, custom.Gpu == 2)),
                "CPU" when custom.Cpu > 0 =>
                    Entry("CPU", CpuColor, true, CpuElements(metrics, custom.Cpu == 2)),
                "VRAM" when custom.Vram > 0 =>
                    Entry("VRAM", VramColor, true, VramElements(metrics, custom.Vram == 2)),
                "RAM" when custom.Ram > 0 =>
                    Entry("RAM", RamColor, true, RamElements(metrics, custom.Ram == 2)),
                "BATT" when custom.Battery > 0 =>
                    Entry("BATT", BattColor, true, BatteryElements(metrics, custom.Battery == 2)),
                "FPS" when custom.Fps > 0 =>
                    Entry("FPS", FpsColor, true, FpsElements(custom.Fps == 2)),
                _ => null,
            };
            string row = Row(entry);
            if (row.Length > 0)
            {
                rows.Add(row);
            }
        }

        return Compose([.. rows]);
    }

    private static List<string> TimeElements(bool full) =>
        [Element(DateTime.Now.ToString(full ? "G" : "t", CultureInfo.InvariantCulture), string.Empty)];

    private static List<string> FpsElements(bool full)
    {
        List<string> elements = [Element("<FR>", "FPS")];
        if (full)
        {
            elements.Add(Element("<FT>", "ms"));
        }

        return elements;
    }

    private static List<string> CpuElements(RtssOsdMetrics metrics, bool full)
    {
        List<string> elements = [];
        AddIfNotNull(elements, metrics.CpuLoadPercent, "%");
        AddIfNotNull(elements, metrics.CpuPowerWatts, "W");
        if (full)
        {
            AddIfNotNull(elements, metrics.CpuTemperatureC, "C");
        }

        return elements;
    }

    private static List<string> GpuElements(RtssOsdMetrics metrics, bool full)
    {
        List<string> elements = [];
        AddIfNotNull(elements, metrics.GpuLoadPercent, "%");
        AddIfNotNull(elements, metrics.GpuPowerWatts, "W");
        if (full)
        {
            AddIfNotNull(elements, metrics.GpuTemperatureC, "C");
        }

        return elements;
    }

    private static List<string> VramElements(RtssOsdMetrics metrics, bool full)
    {
        List<string> elements = [];
        if (full)
        {
            AddIfNotNull(elements, metrics.GpuMemoryUsedGb, metrics.GpuMemoryTotalGb, "GB");
        }
        else
        {
            AddIfNotNull(elements, metrics.GpuMemoryUsedGb, "GB");
        }

        return elements;
    }

    private static List<string> RamElements(RtssOsdMetrics metrics, bool full)
    {
        List<string> elements = [];
        if (full)
        {
            AddIfNotNull(elements, metrics.MemoryUsedGb, metrics.MemoryTotalGb, "GB");
        }
        else
        {
            AddIfNotNull(elements, metrics.MemoryUsedGb, "GB");
        }

        return elements;
    }

    private static List<string> BatteryElements(RtssOsdMetrics metrics, bool full)
    {
        List<string> elements = [];
        AddIfNotNull(elements, metrics.BatteryPercent, "%");
        if (full)
        {
            AddIfNotNull(elements, metrics.BatteryWatts, "W");
        }

        // Remaining time only while discharging, like HC: a charger makes the estimate noise.
        if (elements.Count > 0 && !metrics.OnAcPower
            && metrics.BatteryMinutesRemaining is int minutes)
        {
            elements.Add(Element(Format(minutes / 60, "h"), "h"));
            elements.Add(Element(Format(minutes % 60, "min"), "min"));
        }

        return elements;
    }

    private static void AddIfNotNull(List<string> elements, double? value, string unit)
    {
        if (value is double present)
        {
            elements.Add(Element(Format(present, unit), unit));
        }
    }

    private static void AddIfNotNull(
        List<string> elements, double? value, double? available, string unit)
    {
        if (value is double present && available is double total)
        {
            elements.Add(Element($"{Format(present, unit)}/{Format(total, unit)}", unit));
        }
    }

    private static string Compose(params string[] rows)
    {
        List<string> populated = [];
        foreach (string row in rows)
        {
            if (row.Length > 0)
            {
                populated.Add(populated.Count == 0 ? Header + row : row);
            }
        }

        return string.Join("\n", populated);
    }

    private static string Row(params string?[] entries)
    {
        List<string> populated = [];
        foreach (string? entry in entries)
        {
            if (!string.IsNullOrEmpty(entry))
            {
                populated.Add(entry);
            }
        }

        return string.Join("<C1> | <C>", populated);
    }

    private static string? Entry(string name, string color, bool indent, List<string> elements)
    {
        if (elements.Count == 0)
        {
            return null;
        }

        string label = $"<C={color}>{name}{(indent ? "\t" : string.Empty)}<C>";
        return $"{label} {string.Join(" ", elements)}";
    }

    private static string Element(string value, string unit) => $"<C0>{value}<S1>{unit}<S><C>";

    private static string Format(double value, string unit)
    {
        string format = unit switch
        {
            "GB" => "0.0",
            "W" or "%" or "C" or "h" or "min" => "00",
            "MB" => "0",
            _ => "0.##",
        };
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}

/// <summary>Renders the WSGM-owned overlay levels into RTSS's OSD at HC's 100 ms cadence and
/// carries the whole selector state: 0 clears and parks the loop, 1 to 3 render the fixed
/// presets, and 4 renders the user-configured Custom layout.</summary>
internal sealed class RtssOsdRenderer : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private readonly RtssOsdWriter _writer = new();
    private readonly RtssOsdMetricsSource _metrics;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Task _loop;
    private volatile RtssOsdCustomSettings _custom = RtssOsdCustomSettings.Default;
    private volatile int _level;
    private bool _disposed;

    internal RtssOsdRenderer(Func<string?>? rtssExecutablePath = null)
    {
        _metrics = new RtssOsdMetricsSource(rtssExecutablePath);
        _loop = Task.Run(RenderLoopAsync);
    }

    /// <summary>Applies the Custom overlay's configuration; live on the next tick when level 4
    /// is showing.</summary>
    /// <param name="settings">The widget order and per-widget detail.</param>
    internal void ApplyCustom(RtssOsdCustomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _custom = settings;
    }

    /// <summary>Gets the level currently rendered — the adapter's overlay readback.</summary>
    internal int Level => _level;

    /// <summary>Switches the selector level.</summary>
    /// <param name="level">0 clears; 1 to 3 render the presets, 4 the Custom layout. Values
    /// outside are clamped into range.</param>
    internal void SetLevel(int level)
    {
        int bounded = Math.Clamp(level, 0, 4);
        if (_level == bounded)
        {
            return;
        }

        _level = bounded;
        Log.Change("rtss-osd-level", $"RTSS OSD level {bounded}.");
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake already covers this change.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation is the normal exit.
        }

        _writer.Dispose();
        _metrics.Dispose();
        _shutdown.Dispose();
    }

    private async Task RenderLoopAsync()
    {
        CancellationToken cancellationToken = _shutdown.Token;
        bool cleared = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int level = _level;
                if (level == 0)
                {
                    if (!cleared)
                    {
                        _writer.TryUpdate(string.Empty);
                        cleared = true;
                    }

                    await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                cleared = false;
                try
                {
                    RtssOsdMetrics sample = _metrics.Sample();
                    _writer.TryUpdate(level == 4
                        ? RtssOsdContent.BuildCustom(_custom, sample)
                        : RtssOsdContent.Build(level, sample));
                }
                catch (Exception ex)
                {
                    // One bad sample or write must not silently kill the renderer for the
                    // session; the next tick retries with fresh state.
                    Log.Change(
                        "rtss-osd-tick",
                        $"RTSS OSD render tick failed: {ex.GetType().Name}: {ex.Message}");
                }

                await Task.Delay(RefreshInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Log.Error("RTSS OSD renderer stopped unexpectedly", ex);
        }
    }
}
