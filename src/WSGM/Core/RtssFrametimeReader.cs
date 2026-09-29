using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace WSGM.Core;

/// <summary>One rendering application as RTSS currently reports it.</summary>
/// <param name="ProcessId">The rendering process.</param>
/// <param name="ExecutablePath">Path RTSS recorded for that process.</param>
/// <param name="MeanFrametimeMs">Mean frametime across RTSS's own averaging window.</param>
/// <param name="Frames">Frames in that window.</param>
/// <param name="AgeMs">How long ago the window ended.</param>
internal sealed record RtssFrametimeSample(
    uint ProcessId,
    string ExecutablePath,
    double MeanFrametimeMs,
    uint Frames,
    long AgeMs);

/// <summary>The frametime feed AutoTDP consumes.</summary>
internal interface IFrametimeSource
{
    /// <summary>Reads every application currently delivering frames.</summary>
    /// <returns>Live rendering applications, newest measurement each.</returns>
    IReadOnlyList<RtssFrametimeSample> ReadLive();
}

/// <summary>Random access to the mapped RTSS region.</summary>
/// <remarks>
/// A seam over the memory-mapped view, so the layout below can be exercised against a synthetic
/// region. Without it the parsing is only reachable when RTSS happens to be running with a hooked
/// application, which is exactly when a test cannot rely on it.
/// </remarks>
internal interface IRtssRegion
{
    /// <summary>Bytes available in the mapped region.</summary>
    long Capacity { get; }

    /// <summary>Reads one little-endian unsigned 32-bit value.</summary>
    /// <param name="offset">Byte offset into the region.</param>
    /// <returns>The value.</returns>
    uint ReadUInt32(long offset);

    /// <summary>Reads a run of bytes.</summary>
    /// <param name="offset">Byte offset into the region.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="count">Number of bytes to read.</param>
    void ReadBytes(long offset, byte[] buffer, int count);
}

/// <summary>
/// Reads frametimes from RTSS's own shared memory.
/// </summary>
/// <remarks>
/// Read-only, and the only thing WSGM takes from RTSS that its profile API cannot answer. The layout
/// below was confirmed against a live RTSS 2.21 (<c>dwVersion 0x00020015</c>) on the reference Claw
/// rather than copied from a header: an entry's <c>dwTime0</c>/<c>dwTime1</c> are
/// <c>GetTickCount</c> milliseconds and <c>dwFrames</c> is the frame count between them, which a
/// 1 fps application confirmed by reporting a 1000 ms mean over two frames.
/// <para>
/// Every field is read defensively. RTSS writes this region while WSGM reads it, the array is sized
/// by the header rather than by a constant, and a shared memory that is absent, truncated, or from
/// an unexpected version simply produces no samples — AutoTDP then holds rather than acting on
/// numbers it cannot trust.
/// </para>
/// </remarks>
internal sealed class RtssFrametimeReader : IFrametimeSource, IDisposable
{
    private const string MapName = "RTSSSharedMemoryV2";

    // The server writes dwSignature = 'RTSS' as a C multichar constant: the DWORD VALUE is
    // 0x52545353 and the bytes in memory read "SSTR". The byte-order-mirrored 0x53535452 shipped
    // here first and made Parse refuse every real mapping — the reader reported "no samples"
    // silently. Live-verified on RTSS 2.21 (2026-09-01, OSD work).
    private const uint Signature = 0x52545353;
    private const uint MinimumVersion = 0x0002_0000;

    // Header, all DWORD: signature, version, appEntrySize, appArrOffset, appArrSize.
    private const int HeaderVersionOffset = 4;
    private const int HeaderAppEntrySizeOffset = 8;
    private const int HeaderAppArrOffsetOffset = 12;
    private const int HeaderAppArrSizeOffset = 16;

    // App entry: dwProcessID, szName[260], dwFlags, dwTime0, dwTime1, dwFrames, dwFrameTime.
    private const int EntryNameOffset = 4;
    private const int EntryNameLength = 260;
    private const int EntryTime0Offset = 268;
    private const int EntryTime1Offset = 272;
    private const int EntryFramesOffset = 276;
    private const int MinimumEntrySize = 284;

    /// <summary>Entries whose last frame is older than this are treated as not rendering.</summary>
    /// <remarks>
    /// RTSS leaves an entry behind after an application stops drawing, so staleness is the only way
    /// to tell a finished game from one that is mid-frame. Two seconds is long enough to survive a
    /// shader-compilation hitch and short enough that AutoTDP stops acting on a dead entry quickly.
    /// </remarks>
    private const long MaximumAgeMs = 2000;

    /// <summary>Upper bound on entries walked, whatever the header claims.</summary>
    private const int MaximumEntries = 1024;

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private bool _disposed;

    /// <inheritdoc/>
    public IReadOnlyList<RtssFrametimeSample> ReadLive()
    {
        if (_disposed || !TryOpen())
        {
            return [];
        }

        try
        {
            return ReadLiveCore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or ObjectDisposedException)
        {
            // RTSS exited or replaced its mapping mid-read. Drop the handles so the next poll
            // reopens rather than reporting a permanently dead source.
            Log.Warn($"RTSS frametime read failed; reopening next poll: {ex.Message}");
            Close();
            return [];
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        Close();
    }

    private IReadOnlyList<RtssFrametimeSample> ReadLiveCore()
    {
        IReadOnlyList<RtssFrametimeSample> live = Parse(
            new AccessorRegion(_view!),
            Environment.TickCount64,
            out bool incompatible);
        if (incompatible)
        {
            Close();
        }

        return live;
    }

    /// <summary>
    /// Parses the RTSS application table out of a mapped region.
    /// </summary>
    /// <param name="region">The mapped region.</param>
    /// <param name="nowTicks">Current <see cref="Environment.TickCount64"/>.</param>
    /// <param name="incompatible">Set when the region is not an RTSS mapping this build understands.</param>
    /// <returns>Applications currently delivering frames.</returns>
    internal static IReadOnlyList<RtssFrametimeSample> Parse(
        IRtssRegion region,
        long nowTicks,
        out bool incompatible)
    {
        ArgumentNullException.ThrowIfNull(region);
        incompatible = false;
        if (region.ReadUInt32(0) != Signature
            || region.ReadUInt32(HeaderVersionOffset) < MinimumVersion)
        {
            incompatible = true;
            return [];
        }

        uint entrySize = region.ReadUInt32(HeaderAppEntrySizeOffset);
        uint arrayOffset = region.ReadUInt32(HeaderAppArrOffsetOffset);
        uint arraySize = region.ReadUInt32(HeaderAppArrSizeOffset);
        if (entrySize < MinimumEntrySize || arraySize == 0)
        {
            return [];
        }

        long capacity = region.Capacity;
        int count = (int)Math.Min(arraySize, MaximumEntries);
        long now = nowTicks;
        List<RtssFrametimeSample> live = [];
        byte[] name = new byte[EntryNameLength];
        for (int index = 0; index < count; index++)
        {
            long entry = arrayOffset + ((long)index * entrySize);
            if (entry < 0 || entry + entrySize > capacity)
            {
                break;
            }

            uint processId = region.ReadUInt32(entry);
            uint time0 = region.ReadUInt32(entry + EntryTime0Offset);
            uint time1 = region.ReadUInt32(entry + EntryTime1Offset);
            uint frames = region.ReadUInt32(entry + EntryFramesOffset);
            if (processId == 0 || frames == 0 || time1 == 0 || time1 <= time0)
            {
                continue;
            }

            // Both values are the low 32 bits of GetTickCount. Unsigned subtraction is the clock's
            // native wrap arithmetic; a genuinely future timestamp becomes a huge age and fails
            // the same freshness bound without a special rollover branch.
            long age = unchecked((uint)now - time1);
            if (age > MaximumAgeMs)
            {
                continue;
            }

            region.ReadBytes(entry + EntryNameOffset, name, EntryNameLength);
            live.Add(new RtssFrametimeSample(
                processId,
                DecodeName(name),
                (time1 - time0) / (double)frames,
                frames,
                age));
        }

        return live;
    }

    private static string DecodeName(byte[] name)
    {
        int length = Array.IndexOf(name, (byte)0);
        return Encoding.ASCII.GetString(name, 0, length < 0 ? name.Length : length);
    }

    private sealed class AccessorRegion(MemoryMappedViewAccessor view) : IRtssRegion
    {
        public long Capacity => view.Capacity;

        public uint ReadUInt32(long offset) => view.ReadUInt32(offset);

        public void ReadBytes(long offset, byte[] buffer, int count) =>
            view.ReadArray(offset, buffer, 0, count);
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
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException
            or IOException)
        {
            // Not running, or running elevated while WSGM is not. Neither is an error: AutoTDP is
            // simply unavailable until RTSS is reachable.
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
