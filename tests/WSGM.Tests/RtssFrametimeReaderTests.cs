using System.Text;
using WSGM.Core;

namespace WSGM.Tests;

/// <summary>
/// The RTSS shared-memory layout, as an executable specification.
/// </summary>
/// <remarks>
/// Every offset here was read off a live RTSS 2.21 (<c>dwVersion 0x00020015</c>) on the reference
/// Claw on 2026-08-29, not copied from a header: entry size 12416, a 256-entry application array,
/// and per entry <c>dwProcessID</c> at +0, <c>szName[260]</c> at +4, <c>dwFlags</c> at +264,
/// <c>dwTime0</c> at +268, <c>dwTime1</c> at +272, <c>dwFrames</c> at +276. A 1 fps application
/// reported <c>dwTime1 - dwTime0 = 2000</c> over <c>dwFrames = 2</c>, which is the 1000 ms mean
/// asserted below. These tests exist because the live path only produces data while RTSS happens to
/// have a rendering application hooked, which a test cannot depend on.
/// </remarks>
public sealed class RtssFrametimeReaderTests
{
    // 'RTSS' as the server's C multichar constant — bytes "SSTR" in memory.
    private const uint Signature = 0x52545353;
    private const uint Version = 0x0002_0015;
    private const int EntrySize = 12416;
    private const int ArrayOffset = 4096;
    private const int ArraySize = 8;

    [Fact]
    public void AOneFramePerSecondApplicationReportsAThousandMillisecondMean()
    {
        FakeRegion region = new();
        region.WriteEntry(0, processId: 5552, name: @"C:\Program Files\RustDesk\RustDesk.exe",
            time0: 41_237_921, time1: 41_239_921, frames: 2);

        RtssFrametimeSample sample = Assert.Single(
            RtssFrametimeReader.Parse(region, nowTicks: 41_240_156, out bool incompatible));

        Assert.False(incompatible);
        Assert.Equal(5552u, sample.ProcessId);
        Assert.Equal(@"C:\Program Files\RustDesk\RustDesk.exe", sample.ExecutablePath);
        Assert.Equal(1000d, sample.MeanFrametimeMs, 3);
        Assert.Equal(2u, sample.Frames);
        Assert.Equal(235, sample.AgeMs);
    }

    [Fact]
    public void ASixtyHertzApplicationReportsItsFrametime()
    {
        FakeRegion region = new();
        region.WriteEntry(0, 900, @"D:\Games\game.exe", time0: 1_000_000, time1: 1_001_000, frames: 60);

        RtssFrametimeSample sample = Assert.Single(
            RtssFrametimeReader.Parse(region, 1_001_100, out _));

        Assert.Equal(1000d / 60d, sample.MeanFrametimeMs, 3);
    }

    [Fact]
    public void AnEntryRtssHasNotUpdatedRecentlyIsNotRendering()
    {
        FakeRegion region = new();
        region.WriteEntry(0, 900, @"D:\Games\game.exe", time0: 1_000_000, time1: 1_001_000, frames: 60);

        // RTSS leaves an entry behind after an application stops drawing, so staleness is the only
        // thing separating a finished game from one that is mid-frame.
        Assert.Empty(RtssFrametimeReader.Parse(region, 1_001_000 + 2_001, out _));
    }

    [Fact]
    public void ProfileOnlyEntriesWithoutTimingAreIgnored()
    {
        FakeRegion region = new();
        region.WriteEntry(0, 1234, @"C:\Program Files\WinRAR\WinRAR.exe", 0, 0, 0);
        region.WriteEntry(1, 1235, @"C:\Tools\Other.exe", time0: 500, time1: 0, frames: 4);

        Assert.Empty(RtssFrametimeReader.Parse(region, 1_000, out _));
    }

    [Fact]
    public void OnlyRenderingEntriesAreReturnedFromAMixedTable()
    {
        FakeRegion region = new();
        region.WriteEntry(0, 1, @"C:\a.exe", 0, 0, 0);
        region.WriteEntry(1, 2, @"C:\live.exe", time0: 9_000, time1: 9_100, frames: 10);
        region.WriteEntry(2, 3, @"C:\stale.exe", time0: 1_000, time1: 1_100, frames: 10);

        RtssFrametimeSample sample = Assert.Single(
            RtssFrametimeReader.Parse(region, 9_200, out _));

        Assert.Equal(@"C:\live.exe", sample.ExecutablePath);
    }

    [Fact]
    public void ATickCounterWrapDoesNotProduceAHugeAge()
    {
        FakeRegion region = new();
        // dwTime1 just after a 32-bit wrap, with the 64-bit tick count far past it.
        region.WriteEntry(0, 7, @"C:\game.exe", time0: 40, time1: 140, frames: 10);

        RtssFrametimeSample sample = Assert.Single(
            RtssFrametimeReader.Parse(region, (1L << 32) + 300, out _));

        Assert.Equal(160, sample.AgeMs);
    }

    [Fact]
    public void ARecentSampleFromBeforeTheTickCounterWrapRemainsLive()
    {
        FakeRegion region = new();
        region.WriteEntry(
            0,
            7,
            @"C:\game.exe",
            time0: uint.MaxValue - 200,
            time1: uint.MaxValue - 50,
            frames: 10);

        RtssFrametimeSample sample = Assert.Single(
            RtssFrametimeReader.Parse(region, (1L << 32) + 100, out _));

        Assert.Equal(151, sample.AgeMs);
    }

    [Fact]
    public void AnEntryTimedInTheFutureIsDiscardedRatherThanTrusted()
    {
        FakeRegion region = new();
        region.WriteEntry(0, 7, @"C:\game.exe", time0: 5_000, time1: 6_000, frames: 10);

        Assert.Empty(RtssFrametimeReader.Parse(region, 5_500, out _));
    }

    [Fact]
    public void AMappingThatIsNotRtssIsReportedIncompatible()
    {
        FakeRegion region = new();
        region.WriteUInt32(0, 0xDEADBEEF);

        Assert.Empty(RtssFrametimeReader.Parse(region, 1_000, out bool incompatible));
        Assert.True(incompatible);
    }

    [Fact]
    public void AnOlderProtocolVersionIsReportedIncompatible()
    {
        FakeRegion region = new();
        region.WriteUInt32(4, 0x0001_0000);

        Assert.Empty(RtssFrametimeReader.Parse(region, 1_000, out bool incompatible));
        Assert.True(incompatible);
    }

    [Fact]
    public void EntriesRunningPastTheMappedRegionAreNotRead()
    {
        // A header claiming more entries than the region holds must stop at the boundary rather
        // than reading past it; RTSS writes this region while WSGM reads it.
        FakeRegion region = new(capacity: ArrayOffset + EntrySize + 16);
        region.WriteUInt32(16, 64);
        region.WriteEntry(0, 5, @"C:\one.exe", time0: 100, time1: 200, frames: 10);

        Assert.Single(RtssFrametimeReader.Parse(region, 300, out bool incompatible));
        Assert.False(incompatible);
    }

    [Fact]
    public void AnEntrySizeSmallerThanTheKnownLayoutIsRefused()
    {
        FakeRegion region = new();
        region.WriteUInt32(8, 64);

        Assert.Empty(RtssFrametimeReader.Parse(region, 1_000, out bool incompatible));
        Assert.False(incompatible);
    }

    private sealed class FakeRegion : IRtssRegion
    {
        private readonly byte[] _bytes;

        internal FakeRegion(int capacity = ArrayOffset + (EntrySize * ArraySize))
        {
            _bytes = new byte[capacity];
            WriteUInt32(0, Signature);
            WriteUInt32(4, Version);
            WriteUInt32(8, EntrySize);
            WriteUInt32(12, ArrayOffset);
            WriteUInt32(16, ArraySize);
        }

        public long Capacity => _bytes.Length;

        public uint ReadUInt32(long offset) =>
            BitConverter.ToUInt32(_bytes, checked((int)offset));

        public void ReadBytes(long offset, byte[] buffer, int count) =>
            Array.Copy(_bytes, checked((int)offset), buffer, 0, count);

        internal void WriteUInt32(long offset, uint value) =>
            BitConverter.GetBytes(value).CopyTo(_bytes, checked((int)offset));

        internal void WriteEntry(
            int index,
            uint processId,
            string name,
            uint time0,
            uint time1,
            uint frames)
        {
            int entry = ArrayOffset + (index * EntrySize);
            WriteUInt32(entry, processId);
            byte[] ascii = Encoding.ASCII.GetBytes(name);
            ascii.CopyTo(_bytes, entry + 4);
            _bytes[entry + 4 + ascii.Length] = 0;
            WriteUInt32(entry + 268, time0);
            WriteUInt32(entry + 272, time1);
            WriteUInt32(entry + 276, frames);
        }
    }
}
