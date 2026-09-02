using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>
/// RTSS is WSGM's, not the device platform's.
/// </summary>
/// <remarks>
/// The frame limit and the performance overlay belong to WSGM and RTSS, so they have to keep working
/// on a machine with no plugin installed, with Device Integration switched off, or with a faulted
/// device cycle. These tests pin that by building the whole performance path — service, projection,
/// observation — with no device coordinator, plugin, or capability anywhere in it. If a device
/// dependency is ever introduced into that path, this file stops compiling.
/// </remarks>
public sealed class PerformanceIndependenceTests
{
    [Fact]
    public async Task TheServiceRunsWithNoDevicePlatformPresent()
    {
        await using PerformanceService service = NewService();

        Assert.True(service.Enabled);
        Assert.NotNull(service.Current);
    }

    [Fact]
    public async Task TheOverlayProjectionRendersItsRowsWithNoDevicePlatformPresent()
    {
        await using PerformanceService service = NewService();
        using PerformanceOverlayBridge bridge = new(service);

        PerformanceOverlaySnapshot snapshot = bridge.Snapshot();

        Assert.True(snapshot.Visible);
        Assert.Collection(
            snapshot.Rows,
            row => Assert.Equal("frame-limit", row.Id),
            row => Assert.Equal("overlay-level", row.Id));
    }

    [Fact]
    public async Task TurningTheServiceOffHidesTheRowsRatherThanRemovingTheProjection()
    {
        // Device Integration and this switch are unrelated: only this one governs the rows.
        await using PerformanceService service = new(
            new SimulatedRtssAdapter(),
            static (_, _) => Task.CompletedTask,
            PerformancePolicy.Empty with { Enabled = false });
        using PerformanceOverlayBridge bridge = new(service);

        PerformanceOverlaySnapshot snapshot = bridge.Snapshot();

        Assert.False(snapshot.Visible);
        Assert.Empty(snapshot.Rows);
    }

    [Fact]
    public async Task ObservationIsLeasedByTheOverlayRatherThanByTheDeviceCycle()
    {
        await using PerformanceService service = NewService();
        using PerformanceOverlayBridge bridge = new(service);

        Assert.Equal(0, service.ObserverCount);
        IDisposable lease = bridge.AcquireObservation();
        Assert.Equal(1, service.ObserverCount);

        // Polling exists for a rendered control, so it stops when the last UI client leaves — never
        // because a device cycle started, faulted, or ended.
        lease.Dispose();
        Assert.Equal(0, service.ObserverCount);
    }

    private static PerformanceService NewService() => new(
        new SimulatedRtssAdapter(),
        static (_, _) => Task.CompletedTask,
        PerformancePolicy.Empty);
}
