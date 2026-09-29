using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class AutoTdpServiceTests
{
    private const string PowerCapability = "power.primary-limit";
    private const string GameExecutable = @"C:\Games\game.exe";

    [Fact]
    public async Task ADisabledServiceNeverWritesPower()
    {
        Harness harness = new();
        harness.Frametimes.Live = [Rendering(22.0)];

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Empty(harness.Writes);
        Assert.Equal(AutoTdpState.Off, harness.Service.Status.State);
    }

    [Fact]
    public async Task AMissingPowerCapabilityIsReportedRatherThanGuessed()
    {
        Harness harness = new(capabilities: []);
        harness.Service.Apply(enabled: true);

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(AutoTdpState.Unavailable, harness.Service.Status.State);
        Assert.Empty(harness.Writes);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task NoRenderingApplicationHoldsInsteadOfActing()
    {
        Harness harness = new();
        harness.Service.Apply(enabled: true);
        harness.Frametimes.Live = [];

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(AutoTdpState.Idle, harness.Service.Status.State);
        Assert.Empty(harness.Writes);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task SustainedMissesRaiseThePowerLimitThroughTheCapability()
    {
        Harness harness = new();
        harness.Service.Apply(enabled: true);
        harness.Frametimes.Live = [Rendering(22.0)];

        for (int tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Equal(PowerCapability, Assert.Single(harness.Writes).CapabilityId);
        Assert.Equal(17, harness.Writes[0].Value.IntegerValue);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task SeveralRenderersWithoutAnIdentityAreNotGuessedBetween()
    {
        Harness harness = new();
        harness.Service.Apply(enabled: true);
        harness.Frametimes.Live =
        [
            Rendering(22.0),
            Rendering(22.0, @"C:\Games\other.exe", processId: 2),
        ];

        for (int tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Empty(harness.Writes);
        Assert.Equal(AutoTdpState.Idle, harness.Service.Status.State);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task TheRunningApplicationPicksItsOwnRendererOutOfSeveral()
    {
        Harness harness = new();
        harness.Service.Apply(enabled: true);
        harness.Service.ApplyRunningApplication(Running(GameExecutable));
        harness.Frametimes.Live =
        [
            Rendering(9.0, @"C:\Games\other.exe", processId: 2),
            Rendering(22.0),
        ];

        for (int tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Equal(17, Assert.Single(harness.Writes).Value.IntegerValue);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task AManualChangePausesControlAndStopsFurtherWrites()
    {
        Harness harness = new();
        harness.Service.Apply(enabled: true);
        harness.Frametimes.Live = [Rendering(22.0)];
        await harness.Service.TickAsync(CancellationToken.None);

        harness.Service.NoteManualChange(24);
        for (int tick = 0; tick < 10; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Empty(harness.Writes);
        Assert.Equal(AutoTdpState.Paused, harness.Service.Status.State);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task DisposingRestoresTheLimitAutoTdpTookOverFrom()
    {
        Harness harness = new();
        harness.Service.Apply(enabled: true);
        harness.Frametimes.Live = [Rendering(22.0)];
        for (int tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        await harness.Service.DisposeAsync();

        Assert.Equal(15, harness.Writes[^1].Value.IntegerValue);
    }

    [Fact]
    public async Task TurningTheServiceOffRestoresTheLimitWithoutDisposal()
    {
        Harness harness = new();
        harness.Service.Apply(enabled: true);
        harness.Frametimes.Live = [Rendering(22.0)];
        for (int tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        harness.Service.Apply(enabled: false);
        await WaitForWriteCountAsync(harness, 2);

        Assert.Equal(15, harness.Writes[^1].Value.IntegerValue);
        Assert.Equal(AutoTdpState.Off, harness.Service.Status.State);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task AWriteTheDeviceRefusedIsNotTreatedAsAppliedControl()
    {
        // The controller has already moved its believed wattage by the time the write returns, so
        // an outcome that is not "written" leaves every later decision resting on a limit the
        // hardware may never have taken.
        Harness harness = new();
        harness.Service.Apply(enabled: true);
        harness.Frametimes.Live = [Rendering(22.0)];
        harness.Outcome = CommandOutcome.Rejected;

        for (int tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Single(harness.Writes);
        Assert.Equal(AutoTdpState.Unavailable, harness.Service.Status.State);
        Assert.Contains("did not accept", harness.Service.Status.Detail, StringComparison.Ordinal);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task ControlResumesFromTheObservedLimitAfterAnUnappliedWrite()
    {
        // Re-basing costs one window and is the only honest way back: continuing would judge frames
        // against a limit the device never took. Control is not abandoned either — once writes are
        // accepted again the service goes on controlling from what the hardware reports.
        Harness harness = new();
        harness.Service.Apply(enabled: true);
        harness.Frametimes.Live = [Rendering(22.0)];
        harness.Outcome = CommandOutcome.TimedOut;
        for (int tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Single(harness.Writes);
        Assert.Equal(AutoTdpState.Unavailable, harness.Service.Status.State);

        harness.Outcome = CommandOutcome.AppliedVerified;
        for (int tick = 0;
            tick < AutoTdpController.SettleWindows + AutoTdpController.SustainedMisses;
            tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Equal(2, harness.Writes.Count);
        Assert.Equal(AutoTdpState.Controlling, harness.Service.Status.State);
        await harness.Service.DisposeAsync();
    }

    [Fact]
    public async Task AnUnconfirmedRestoreIsNotReportedAsRestored()
    {
        // "The previous limit was restored" for a value the device refused is the one message that
        // makes the handheld's real power state undiagnosable from a log.
        Harness harness = new();
        harness.Service.Apply(enabled: true);
        harness.Frametimes.Live = [Rendering(22.0)];
        for (int tick = 0; tick < AutoTdpController.SustainedMisses; tick++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
        }

        harness.Outcome = CommandOutcome.Rejected;
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.DisposeAsync().AsTask());

        Assert.Equal(15, harness.Writes[^1].Value.IntegerValue);
        Assert.Equal(AutoTdpState.Off, harness.Service.Status.State);
        Assert.Contains("was not confirmed", harness.Service.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("could not verify restoration", failure.Message, StringComparison.Ordinal);
    }

    private static async Task WaitForWriteCountAsync(Harness harness, int count)
    {
        for (int attempt = 0; attempt < 100 && harness.Writes.Count < count; attempt++)
        {
            await Task.Delay(10);
        }
    }

    private static RtssFrametimeSample Rendering(
        double frametimeMs,
        string executable = GameExecutable,
        uint processId = 1) =>
        new(processId, executable, frametimeMs, 60, 100);

    private static RunningApplicationTargetSnapshot Running(string executable) => new(
        1,
        1,
        RunningApplicationTargetState.Active,
        "steam:70",
        70,
        executable,
        "game",
        DateTimeOffset.UtcNow,
        null);

    private sealed record Write(string CapabilityId, string? InstanceId, CapabilityValue Value);

    private sealed class FakeFrametimeSource : IFrametimeSource
    {
        internal IReadOnlyList<RtssFrametimeSample> Live { get; set; } = [];

        public IReadOnlyList<RtssFrametimeSample> ReadLive() => Live;
    }

    private sealed class Harness
    {
        internal Harness(IReadOnlyList<DeviceCapabilityView>? capabilities = null)
        {
            IReadOnlyList<DeviceCapabilityView> views = capabilities ?? [PowerView(15)];
            Service = new AutoTdpService(
                Frametimes,
                () => views,
                (capabilityId, instanceId, value, _) =>
                {
                    Writes.Add(new Write(capabilityId, instanceId, value));
                    return Task.FromResult(new CapabilityCommandResult
                    {
                        CommandId = Guid.NewGuid(),
                        Outcome = Outcome,
                        ReadbackValue = value,
                        CompletedAt = DateTimeOffset.UtcNow,
                    });
                },
                () => 16.6);
        }

        internal FakeFrametimeSource Frametimes { get; } = new();

        /// <summary>What the capability layer reports for the next write.</summary>
        internal CommandOutcome Outcome { get; set; } = CommandOutcome.AppliedVerified;

        internal List<Write> Writes { get; } = [];

        internal AutoTdpService Service { get; }

        private static DeviceCapabilityView PowerView(int watts) => new(
            new CapabilityDescriptor
            {
                CapabilityId = PowerCapability,
                Role = CapabilityRole.PowerSustainedLimit,
                ValueKind = CapabilityValueKind.Integer,
                Display = new CapabilityDisplay { Key = DisplayKey.SustainedPowerLimit },
                SupportsRead = true,
                SupportsWrite = true,
                Minimum = 8,
                Maximum = 30,
                Step = 2,
                Persistence = CapabilityPersistence.Volatile,
            },
            new CapabilityProjection
            {
                State = new CapabilityState
                {
                    CapabilityId = PowerCapability,
                    Available = true,
                    Quality = HardwareStateQuality.Verified,
                    ObservedValue = new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Integer,
                        IntegerValue = watts,
                    },
                    DescriptorGeneration = 1,
                    CycleGeneration = 1,
                },
            },
            null);
    }
}
