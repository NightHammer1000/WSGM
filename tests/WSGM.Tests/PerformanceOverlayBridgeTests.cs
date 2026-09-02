using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>Shared overlay projection tests over the hardware-free RTSS simulation.</summary>
public sealed class PerformanceOverlayBridgeTests
{
    [Fact]
    public async Task OverlayProjectionObservesAndMutatesTheSinglePerformanceService()
    {
        await using PerformanceService service = new(
            new SimulatedRtssAdapter(),
            static (_, _) => Task.CompletedTask,
            new PerformancePolicy(
                new PerformanceValues(60, 0),
                Array.Empty<PerformanceApplicationPolicy>()));
        using PerformanceOverlayBridge bridge = new(service);
        using IDisposable observation = bridge.AcquireObservation();
        await service.RefreshAsync();

        PerformanceOverlaySnapshot before = bridge.Snapshot();
        DescriptorRow overlay = before.Rows.Single(row => row.Id == "overlay-level");
        await bridge.InvokeAsync(overlay, CancellationToken.None);

        PerformanceOverlaySnapshot after = bridge.Snapshot();
        Assert.True(after.Visible);
        Assert.Equal("3", after.Rows.Single(row => row.Id == "overlay-level").TrailingText);
        Assert.Equal(3, service.Current.Desired.OverlayLevel);
        Assert.Equal(1, service.ObserverCount);
    }

    [Fact]
    public async Task DisabledPerformancePolicyHidesTheProjectionWithoutPolling()
    {
        await using PerformanceService service = new(
            new SimulatedRtssAdapter(),
            static (_, _) => Task.CompletedTask,
            new PerformancePolicy(
                PerformanceValues.Empty,
                Array.Empty<PerformanceApplicationPolicy>(),
                Enabled: false));
        using PerformanceOverlayBridge bridge = new(service);

        PerformanceOverlaySnapshot snapshot = bridge.Snapshot();

        Assert.False(snapshot.Visible);
        Assert.Equal(0, service.ObserverCount);
    }

    [Fact]
    public async Task PerApplicationRowsLiveOnPowerAndThermalsExceptTheHeadlineToggle()
    {
        await using PerformanceService service = new(
            new SimulatedRtssAdapter(),
            static (_, _) => Task.CompletedTask,
            new PerformancePolicy(new PerformanceValues(60, 1), []));
        using PerformanceOverlayBridge bridge = new(service);
        await service.SetTargetAsync(
            new PerformanceApplicationTarget("steam:42", 42, "game.exe"));
        await service.RefreshAsync();

        PerformanceOverlaySnapshot before = bridge.Snapshot();

        Assert.Collection(
            before.ProfileRows,
            row => Assert.Equal("detected-application", row.Id),
            row => Assert.Equal("active-profile", row.Id),
            row => Assert.Equal("application-profile", row.Id),
            row => Assert.Equal("reset-profile", row.Id));
        Assert.Equal("Steam 42", before.ProfileRows[0].TrailingText);
        Assert.Equal("Global", before.ProfileRows[1].TrailingText);
        // The per-application detail rows and the shared frame-limit/overlay rows count into Power
        // and thermals, where they render; the enable toggle is the headline on the Device root, so
        // it is not counted into any section.
        DeviceOverlaySnapshot device = new(true, "Ready", string.Empty, null, []);
        DeviceOverlaySectionEntry power = Assert.Single(
            DeviceOverlaySectionPages.Build(device, before));
        Assert.Equal(DeviceOverlaySection.PowerAndThermals, power.Section);
        int toggleRows = before.ProfileRows.Count(row =>
            row.Id == DeviceOverlaySectionPages.ApplicationProfileRowId);
        Assert.Equal(1, toggleRows);
        Assert.Equal(before.ProfileRows.Count - toggleRows + before.Rows.Count, power.Count);

        await bridge.InvokeAsync(before.ProfileRows.Single(row =>
            row.Id == "application-profile"));

        PerformanceOverlaySnapshot after = bridge.Snapshot();
        Assert.True(service.Current.ApplicationProfileEnabled);
        Assert.Equal(
            "Application",
            after.ProfileRows.Single(row => row.Id == "active-profile").TrailingText);
    }
}
