using System.Text;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class RtssOsdContentTests
{
    private static readonly RtssOsdMetrics FullMetrics = RtssOsdMetrics.Empty with
    {
        CpuLoadPercent = 42.5,
        CpuPowerWatts = 13.7,
        CpuTemperatureC = 61,
        GpuLoadPercent = 55.2,
        GpuPowerWatts = 9.8,
        GpuMemoryUsedGb = 1.2,
        GpuMemoryTotalGb = 18.0,
        MemoryUsedGb = 12.3,
        MemoryTotalGb = 31.6,
        BatteryPercent = 76,
        BatteryWatts = -14.2,
        BatteryMinutesRemaining = 95,
        OnAcPower = false,
    };

    private static readonly RtssOsdMetrics EmptyMetrics = RtssOsdMetrics.Empty with
    {
        OnAcPower = true,
    };

    [Fact]
    public void Minimal_IsOneRowOfRtssFramerate()
    {
        string text = RtssOsdContent.Build(1, EmptyMetrics);

        Assert.StartsWith("<C0=FFFFFF><C1=8000FF>", text);
        Assert.Contains("<FR>", text);
        Assert.Contains("<APP>", text);
        Assert.DoesNotContain("\n", text);
        Assert.DoesNotContain("<FT>", text);
    }

    [Fact]
    public void Extended_IsOneRowWithEverySourcedSubject()
    {
        string text = RtssOsdContent.Build(2, FullMetrics);

        Assert.DoesNotContain("\n", text);
        Assert.Contains("<FR>", text);
        Assert.Contains("<FT>", text);
        Assert.Contains("GPU", text);
        Assert.Contains("VRAM", text);
        Assert.Contains("CPU", text);
        Assert.Contains("RAM", text);
        Assert.Contains("BATT", text);
        // Entries are joined with HC's separator.
        Assert.Contains("<C1> | <C>", text);
    }

    [Fact]
    public void Full_IsOneRowPerSubject()
    {
        string text = RtssOsdContent.Build(3, FullMetrics);

        string[] rows = text.Split('\n');
        Assert.Equal(6, rows.Length);
        // Full shows used/total memory, temperatures and the discharge estimate.
        Assert.Contains("12.3/31.6", text);
        Assert.Contains("1.2/18.0", text);
        Assert.Contains("61<S1>C", text);
        Assert.Contains("01<S1>h", text);
        Assert.Contains("35<S1>min", text);
    }

    [Fact]
    public void SubjectsWithoutASourceDoNotRender()
    {
        string text = RtssOsdContent.Build(3, EmptyMetrics);

        // Only the FPS row survives: its tags are filled by RTSS, not by a sensor.
        Assert.DoesNotContain("GPU", text);
        Assert.DoesNotContain("CPU", text);
        Assert.DoesNotContain("RAM", text);
        Assert.DoesNotContain("BATT", text);
        Assert.Contains("<FR>", text);
        Assert.DoesNotContain("\n", text);
    }

    [Fact]
    public void BatteryTimeIsOmittedOnAcPower()
    {
        RtssOsdMetrics charging = FullMetrics with { OnAcPower = true };

        string text = RtssOsdContent.Build(3, charging);

        Assert.DoesNotContain("<S1>h", text);
        Assert.DoesNotContain("<S1>min", text);
        Assert.Contains("BATT", text);
    }

    [Fact]
    public void UnrenderedLevelsAreEmpty()
    {
        Assert.Equal(string.Empty, RtssOsdContent.Build(0, FullMetrics));
        Assert.Equal(string.Empty, RtssOsdContent.Build(4, FullMetrics));
    }
}

public sealed class RtssLhmSensorsTests
{
    // The provider's real shape from the Claw: a fragment stream of <hardware> elements,
    // culture-formatted decimal commas, GPU memory in megabytes.
    private const string ClawSample = """
        <hardware>
        <id>/motherboard</id><name>MSI MS-1T52</name><type>Motherboard</type>
        </hardware>
        <hardware>
        <id>/intelcpu/0</id><name>Intel Core Ultra 7 258V</name><type>Cpu</type>
        <sensor><id>/intelcpu/0/load/0</id><name>CPU Total</name><type>Load</type><value>17,4</value></sensor>
        <sensor><id>/intelcpu/0/load/2</id><name>CPU Core #1</name><type>Load</type><value>37,5</value></sensor>
        <sensor><id>/intelcpu/0/temperature/10</id><name>CPU Package</name><type>Temperature</type><value>58,0</value></sensor>
        <sensor><id>/intelcpu/0/power/0</id><name>CPU Package</name><type>Power</type><value>12,6</value></sensor>
        <sensor><id>/intelcpu/0/power/4</id><name>CPU Platform</name><type>Power</type><value>19,6</value></sensor>
        </hardware>
        <hardware>
        <id>/ram</id><name>Generic Memory</name><type>Memory</type>
        <sensor><id>/ram/data/0</id><name>Memory Used</name><type>Data</type><value>10,2</value></sensor>
        <sensor><id>/ram/data/1</id><name>Memory Available</name><type>Data</type><value>21,3</value></sensor>
        </hardware>
        <hardware>
        <id>/gpu-intel-integrated/x</id><name>Intel(R) Arc(TM) 140V GPU</name><type>GpuIntel</type>
        <sensor><id>/gpu-intel-integrated/x/power/0</id><name>GPU Power</name><type>Power</type><value>0,3</value></sensor>
        <sensor><id>/gpu-intel-integrated/x/load/0</id><name>D3D 3D</name><type>Load</type><value>8,3</value></sensor>
        <sensor><id>/gpu-intel-integrated/x/load/13</id><name>D3D Video Decode</name><type>Load</type><value>99,0</value></sensor>
        <sensor><id>/gpu-intel-integrated/x/smalldata/0</id><name>D3D Shared Memory Used</name><type>SmallData</type><value>1264,8</value></sensor>
        <sensor><id>/gpu-intel-integrated/x/smalldata/2</id><name>D3D Shared Memory Total</name><type>SmallData</type><value>18409,7</value></sensor>
        </hardware>
        """;

    [Fact]
    public void SelectsHandheldCompanionsSensors()
    {
        RtssOsdMetrics metrics = RtssLhmSensors.Parse(ClawSample);

        Assert.Equal(17.4, metrics.CpuLoadPercent!.Value, 3);
        Assert.Equal(12.6, metrics.CpuPowerWatts!.Value, 3);
        Assert.Equal(58.0, metrics.CpuTemperatureC!.Value, 3);
        Assert.Equal(8.3, metrics.GpuLoadPercent!.Value, 3);
        Assert.Equal(0.3, metrics.GpuPowerWatts!.Value, 3);
        Assert.Null(metrics.GpuTemperatureC);
        Assert.Equal(1264.8 / 1024.0, metrics.GpuMemoryUsedGb!.Value, 3);
        Assert.Equal(18409.7 / 1024.0, metrics.GpuMemoryTotalGb!.Value, 3);
        Assert.Equal(10.2, metrics.MemoryUsedGb!.Value, 3);
        Assert.Equal(31.5, metrics.MemoryTotalGb!.Value, 3);
        // Battery is kernel-fed; the provider ships with its battery section disabled.
        Assert.Null(metrics.BatteryPercent);
    }

    [Fact]
    public void PrefersDedicatedGpuMemoryOverShared()
    {
        const string sample = """
            <hardware>
            <id>/gpu/0</id><name>GPU</name><type>GpuAmd</type>
            <sensor><id>/g/1</id><name>D3D Shared Memory Used</name><type>SmallData</type><value>2048</value></sensor>
            <sensor><id>/g/2</id><name>GPU Memory Used</name><type>SmallData</type><value>1024</value></sensor>
            <sensor><id>/g/3</id><name>GPU Memory Total</name><type>SmallData</type><value>4096</value></sensor>
            </hardware>
            """;

        RtssOsdMetrics metrics = RtssLhmSensors.Parse(sample);

        Assert.Equal(1.0, metrics.GpuMemoryUsedGb!.Value, 3);
        Assert.Equal(4.0, metrics.GpuMemoryTotalGb!.Value, 3);
    }

    [Fact]
    public void ATornSnapshotYieldsNothing()
    {
        RtssOsdMetrics metrics = RtssLhmSensors.Parse("<hardware><sensor><name>CPU T");

        Assert.Equal(RtssOsdMetrics.Empty, metrics);
    }
}

public sealed class RtssOsdCustomTests
{
    private static readonly RtssOsdMetrics Metrics = RtssOsdMetrics.Empty with
    {
        CpuLoadPercent = 40,
        CpuPowerWatts = 12,
        GpuLoadPercent = 30,
        GpuPowerWatts = 8,
        GpuMemoryUsedGb = 1.5,
        GpuMemoryTotalGb = 18.0,
        MemoryUsedGb = 10.0,
        MemoryTotalGb = 32.0,
        BatteryPercent = 80,
        BatteryWatts = -12,
        BatteryMinutesRemaining = 60,
        OnAcPower = false,
    };

    [Fact]
    public void FromConfig_ParsesOrderAndDropsUnknownNames()
    {
        var config = new PerformanceConfig
        {
            OsdCustomOrder = "fps, cpu, Nonsense, CPU, batt",
            OsdCustomFps = 7,
            OsdCustomBattery = -3,
        };

        RtssOsdCustomSettings settings = RtssOsdCustomSettings.FromConfig(config);

        Assert.Equal(["FPS", "CPU", "BATT"], settings.Order);
        Assert.Equal(2, settings.Fps);
        Assert.Equal(0, settings.Battery);
    }

    [Fact]
    public void BuildCustom_RendersOneRowPerConfiguredWidgetInOrder()
    {
        RtssOsdCustomSettings settings = RtssOsdCustomSettings.Default;

        string text = RtssOsdContent.BuildCustom(settings, Metrics);

        string[] rows = text.Split('\n');
        // Default order Time,GPU,CPU,VRAM,RAM,BATT,FPS — every widget has a source here.
        Assert.Equal(7, rows.Length);
        Assert.Contains("TIME", rows[0]);
        Assert.Contains("GPU", rows[1]);
        Assert.Contains("CPU", rows[2]);
        Assert.Contains("VRAM", rows[3]);
        Assert.Contains("RAM", rows[4]);
        Assert.Contains("BATT", rows[5]);
        Assert.Contains("FPS", rows[6]);
        Assert.Contains("<FR>", rows[6]);
    }

    [Fact]
    public void BuildCustom_SkipsHiddenWidgetsAndSourcelessEntries()
    {
        RtssOsdCustomSettings settings = RtssOsdCustomSettings.Default with { Gpu = 0 };
        RtssOsdMetrics noBattery = Metrics with { BatteryPercent = null, BatteryWatts = null };

        string text = RtssOsdContent.BuildCustom(settings, noBattery);

        Assert.DoesNotContain("GPU", text.Replace("VRAM", string.Empty));
        Assert.DoesNotContain("BATT", text);
        Assert.Contains("CPU", text);
    }
}

public sealed class RtssOsdSlotsTests
{
    private const int EntrySize = 4608;
    private const int ArrayOffset = 96;
    private const int Slots = 4;
    private const int OwnerOffset = 256;
    private const int TextExOffset = 512;
    private const int FrameOffset = 32;

    [Fact]
    public void ClaimsTheFirstFreeSlotAndSkipsRtssOwnSlot()
    {
        FakeRegion region = NewRegion();

        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));

        Assert.Equal("WSGM", region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal("hello", region.ReadString(EntryOffset(1) + TextExOffset));
        // Slot 0 belongs to RTSS itself and stays untouched.
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(0) + OwnerOffset));
        Assert.Equal(1u, region.ReadUInt32(FrameOffset));
    }

    [Fact]
    public void ReusesItsOwnSlotAndRespectsOtherOwners()
    {
        FakeRegion region = NewRegion();
        region.WriteString(EntryOffset(1) + OwnerOffset, "RTSSSharedMemorySample");
        region.WriteString(EntryOffset(2) + OwnerOffset, "WSGM");

        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "updated"));

        Assert.Equal("updated", region.ReadString(EntryOffset(2) + TextExOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(1) + TextExOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(3) + TextExOffset));
    }

    [Fact]
    public void RefusesWhenEverySlotIsForeignOwned()
    {
        FakeRegion region = NewRegion();
        for (int slot = 1; slot < Slots; slot++)
        {
            region.WriteString(EntryOffset(slot) + OwnerOffset, $"owner-{slot}");
        }

        Assert.False(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));
        Assert.Equal(0u, region.ReadUInt32(FrameOffset));
    }

    [Fact]
    public void ReleaseZeroesOnlyTheOwnedEntry()
    {
        FakeRegion region = NewRegion();
        region.WriteString(EntryOffset(1) + OwnerOffset, "someone-else");
        region.WriteString(EntryOffset(1) + TextExOffset, "theirs");
        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "ours"));

        RtssOsdSlots.Release(region, "WSGM");

        Assert.Equal(string.Empty, region.ReadString(EntryOffset(2) + OwnerOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(2) + TextExOffset));
        Assert.Equal("someone-else", region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal("theirs", region.ReadString(EntryOffset(1) + TextExOffset));
    }

    [Fact]
    public void SkipsTheTextWriteWhenTheBusyFlagIsHeld()
    {
        FakeRegion region = NewRegion();
        region.BusyHeld = true;

        Assert.True(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));

        // The slot is claimed and the frame counter moves, but the text write is skipped —
        // exactly what the library does when another writer holds the flag.
        Assert.Equal("WSGM", region.ReadString(EntryOffset(1) + OwnerOffset));
        Assert.Equal(string.Empty, region.ReadString(EntryOffset(1) + TextExOffset));
        Assert.Equal(1u, region.ReadUInt32(FrameOffset));
    }

    [Fact]
    public void RefusesAForeignMapping()
    {
        FakeRegion region = NewRegion();
        region.WriteUInt32(0, 0x12345678);

        Assert.False(RtssOsdSlots.TryWrite(region, "WSGM", "hello"));
    }

    private static long EntryOffset(int slot) => ArrayOffset + ((long)slot * EntrySize);

    private static FakeRegion NewRegion()
    {
        var region = new FakeRegion(ArrayOffset + (Slots * EntrySize));
        // 'RTSS' as the server's C multichar constant — bytes "SSTR" in memory.
        region.WriteUInt32(0, 0x52545353);
        region.WriteUInt32(4, 0x0002_0015);
        region.WriteUInt32(20, EntrySize);
        region.WriteUInt32(24, ArrayOffset);
        region.WriteUInt32(28, Slots);
        return region;
    }

    private sealed class FakeRegion(long capacity) : IRtssOsdRegion
    {
        private readonly byte[] _memory = new byte[capacity];

        public bool BusyHeld { get; set; }

        public long Capacity => _memory.Length;

        public uint ReadUInt32(long offset) => BitConverter.ToUInt32(_memory, (int)offset);

        public void WriteUInt32(long offset, uint value) =>
            BitConverter.GetBytes(value).CopyTo(_memory, (int)offset);

        public void ReadBytes(long offset, byte[] buffer, int count) =>
            Array.Copy(_memory, offset, buffer, 0, count);

        public void WriteBytes(long offset, byte[] buffer, int count) =>
            Array.Copy(buffer, 0, _memory, offset, count);

        public bool TryAcquireBusy(long offset) => !BusyHeld;

        public void ReleaseBusy(long offset)
        {
        }

        public string ReadString(long offset)
        {
            int end = (int)offset;
            while (end < _memory.Length && _memory[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(_memory, (int)offset, end - (int)offset);
        }

        public void WriteString(long offset, string value) =>
            Encoding.ASCII.GetBytes(value).CopyTo(_memory, (int)offset);
    }
}
