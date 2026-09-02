using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Tests;

public sealed class DeviceCoordinatorDiagnosticsTests
{
    [Fact]
    public void Snapshot_RoundTripsOneOptionalInstalledPackageWithoutASecondSchema()
    {
        DeviceCoordinatorDiagnosticsSnapshot original = Snapshot() with
        {
            InstalledPackage = new DeviceInstalledPackageDiagnostic(
                "wsgm.device.synthetic.dock-x1",
                "1.0.0"),
        };

        string json = JsonSerializer.Serialize(
            original,
            ConfigJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot);
        DeviceCoordinatorDiagnosticsSnapshot? restored = JsonSerializer.Deserialize(
            json,
            ConfigJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot);

        Assert.DoesNotContain("schemaVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"packages\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void Snapshot_NoInstalledPackage_RoundTripsAsNull()
    {
        DeviceCoordinatorDiagnosticsSnapshot original = Snapshot();

        string json = JsonSerializer.Serialize(
            original,
            ConfigJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot);
        DeviceCoordinatorDiagnosticsSnapshot? restored = JsonSerializer.Deserialize(
            json,
            ConfigJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot);

        Assert.NotNull(restored);
        Assert.Null(restored.InstalledPackage);
    }

    private static DeviceCoordinatorDiagnosticsSnapshot Snapshot() => new()
    {
        State = DeviceCycleState.Active,
        CycleGeneration = 9,
        CapabilityCount = 3,
        HealthyCapabilityCount = 2,
        FaultedCapabilityCount = 1,
        CapturedAt = DateTimeOffset.UnixEpoch,
    };
}
