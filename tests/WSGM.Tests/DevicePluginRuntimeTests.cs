using WSGM.Core;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Device.Sdk.Packaging;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Settings;
using WSGM.Shell;

namespace WSGM.Device.Tests;

public sealed class DevicePluginRuntimeTests
{
    private const long InitialGeneration = 41;

    [Fact]
    public async Task DirectLoadRunsTheLifecycleInsideTheExplicitTemporaryStateRoot()
    {
        using TemporaryDirectory temporary = new();
        DevicePluginRuntime runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        List<CanonicalControllerSample> samples = [];
        runtime.ControllerSampleReceived += samples.Add;

        DevicePluginState started = await runtime.StartAsync(
            new DeviceIdentitySnapshot(),
            InitialGeneration,
            controllerManagementEnabled: true,
            CancellationToken.None);

        Assert.Equal(DeviceCycleState.Active, started.State);
        Assert.Equal(RuntimeFixturePlugin.DeviceDefinitionIdValue, started.DeviceDefinitionId);
        Assert.Equal(InitialGeneration, Assert.Single(samples).CycleGeneration);
        string stateDirectory = temporary.GetPath(
            "state",
            RuntimeFixturePlugin.PackageIdValue);
        Assert.True(File.Exists(Path.Combine(stateDirectory, "started.txt")));

        DevicePluginState suspended = await runtime.SuspendAsync(
            DateTimeOffset.UtcNow.AddSeconds(1),
            CancellationToken.None);
        Assert.Equal(DeviceCycleState.Suspended, suspended.State);

        long resumedGeneration = InitialGeneration + 1;
        DevicePluginState resumed = await runtime.ResumeAsync(
            resumedGeneration,
            DateTimeOffset.UtcNow.AddSeconds(1),
            CancellationToken.None);
        Assert.Equal(DeviceCycleState.Active, resumed.State);
        Assert.Equal(resumedGeneration, samples[^1].CycleGeneration);

        DevicePluginState stopped = await runtime.StopAsync(
            PluginStopReason.IntegrationDisabled,
            DateTimeOffset.UtcNow.AddSeconds(1),
            CancellationToken.None);
        Assert.Equal(DeviceCycleState.Disabled, stopped.State);
        Assert.Contains(
            nameof(PluginStopReason.IntegrationDisabled),
            await File.ReadAllTextAsync(Path.Combine(stateDirectory, "stopped.txt")),
            StringComparison.Ordinal);

        DeviceRuntimeExit exit = await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(DeviceRuntimeExitReason.Intentional, exit.Reason);

        await runtime.DisposeAsync();
        Assert.True(File.Exists(Path.Combine(stateDirectory, "disposed.txt")));
    }

    [Fact]
    public async Task BackgroundReportFaultCompletesTheRuntimeAndClosesCommandAdmission()
    {
        using TemporaryDirectory temporary = new();
        DevicePluginRuntime runtime = await StartRuntimeAsync(temporary, InitialGeneration);
        try
        {
            DeviceCommandDispatch dispatched = await runtime.ExecuteCommandAsync(
                Command("fault", InitialGeneration),
                CancellationToken.None);
            Assert.Equal(CommandOutcome.AppliedVerified, dispatched.Immediate.Outcome);

            DeviceRuntimeExit exit = await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(DeviceRuntimeExitReason.BackgroundFault, exit.Reason);
            Assert.Contains("background reader failed", exit.Detail, StringComparison.Ordinal);

            DeviceCommandDispatch refused = await runtime.ExecuteCommandAsync(
                Command("current-sample", InitialGeneration),
                CancellationToken.None);
            Assert.Equal(CommandOutcome.Rejected, refused.Immediate.Outcome);

            await runtime.StopAsync(
                PluginStopReason.RuntimeFault,
                DateTimeOffset.UtcNow.AddSeconds(1),
                CancellationToken.None);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task CanceledCommandReturnsImmediatelyAndKeepsItsLateCompletion()
    {
        using TemporaryDirectory temporary = new();
        DevicePluginRuntime runtime = await StartRuntimeAsync(temporary, InitialGeneration);
        try
        {
            CapabilityCommand command = Command(
                "late",
                InitialGeneration,
                DateTimeOffset.UtcNow.AddMilliseconds(30));

            DeviceCommandDispatch dispatched = await runtime.ExecuteCommandAsync(
                command,
                CancellationToken.None);

            Assert.Equal(CommandOutcome.TimedOut, dispatched.Immediate.Outcome);
            Task<CapabilityCommandResult>? late = dispatched.LateCompletion;
            Assert.NotNull(late);
            CapabilityCommandResult completed = await late.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(command.CommandId, completed.CommandId);
            Assert.Equal(CommandOutcome.AppliedVerified, completed.Outcome);
        }
        finally
        {
            await runtime.StopAsync(
                PluginStopReason.IntegrationDisabled,
                DateTimeOffset.UtcNow.AddSeconds(1),
                CancellationToken.None);
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task FreshGenerationAcceptsCurrentSamplesAndRejectsStaleSamples()
    {
        using TemporaryDirectory temporary = new();
        DevicePluginRuntime runtime = await LoadRuntimeAsync(temporary, InitialGeneration);
        List<CanonicalControllerSample> samples = [];
        runtime.ControllerSampleReceived += samples.Add;
        await runtime.StartAsync(
            new DeviceIdentitySnapshot(),
            InitialGeneration,
            controllerManagementEnabled: true,
            CancellationToken.None);
        await runtime.SuspendAsync(
            DateTimeOffset.UtcNow.AddSeconds(1),
            CancellationToken.None);
        long resumedGeneration = InitialGeneration + 1;
        await runtime.ResumeAsync(
            resumedGeneration,
            DateTimeOffset.UtcNow.AddSeconds(1),
            CancellationToken.None);

        try
        {
            int acceptedBeforeStale = samples.Count;
            DeviceCommandDispatch stale = await runtime.ExecuteCommandAsync(
                Command("stale-sample", resumedGeneration),
                CancellationToken.None);
            Assert.Equal(CommandOutcome.Indeterminate, stale.Immediate.Outcome);
            Assert.Equal(acceptedBeforeStale, samples.Count);

            DeviceCommandDispatch current = await runtime.ExecuteCommandAsync(
                Command("current-sample", resumedGeneration),
                CancellationToken.None);
            Assert.Equal(CommandOutcome.AppliedVerified, current.Immediate.Outcome);
            Assert.Equal(acceptedBeforeStale + 1, samples.Count);
            Assert.Equal(resumedGeneration, samples[^1].CycleGeneration);
        }
        finally
        {
            await runtime.StopAsync(
                PluginStopReason.IntegrationDisabled,
                DateTimeOffset.UtcNow.AddSeconds(1),
                CancellationToken.None);
            await runtime.DisposeAsync();
        }
    }

    private static async Task<DevicePluginRuntime> StartRuntimeAsync(
        TemporaryDirectory temporary,
        long cycleGeneration)
    {
        DevicePluginRuntime runtime = await LoadRuntimeAsync(temporary, cycleGeneration);
        await runtime.StartAsync(
            new DeviceIdentitySnapshot(),
            cycleGeneration,
            controllerManagementEnabled: true,
            CancellationToken.None);
        return runtime;
    }

    private static Task<DevicePluginRuntime> LoadRuntimeAsync(
        TemporaryDirectory temporary,
        long cycleGeneration)
    {
        string packageDirectory = temporary.GetPath("package");
        Directory.CreateDirectory(packageDirectory);
        string sourceAssembly = typeof(RuntimeFixturePlugin).Assembly.Location;
        string entryAssembly = Path.GetFileName(sourceAssembly);
        File.Copy(sourceAssembly, Path.Combine(packageDirectory, entryAssembly));
        InstalledDevicePackage package = new()
        {
            PackagePath = packageDirectory,
            Valid = true,
            Manifest = new PluginManifest
            {
                Id = RuntimeFixturePlugin.PackageIdValue,
                Name = "Runtime fixture",
                Version = "1.0.0",
                ApiVersion = DeviceApi.Version,
                EntryAssembly = entryAssembly,
                EntryType = typeof(RuntimeFixturePlugin).FullName!,
            },
        };
        return DevicePluginRuntime.StartAsync(
            package,
            cycleGeneration,
            CancellationToken.None,
            temporary.GetPath("state"));
    }

    private static CapabilityCommand Command(
        string capabilityId,
        long cycleGeneration,
        DateTimeOffset? deadline = null) => new()
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = capabilityId,
            ExpectedDescriptorGeneration = 1,
            ExpectedCycleGeneration = cycleGeneration,
            Deadline = deadline ?? DateTimeOffset.UtcNow.AddSeconds(1),
        };
}

/// <summary>Collectible package fixture used to exercise the production direct-plugin boundary.</summary>
public sealed class RuntimeFixturePlugin : IDevicePlugin
{
    public const string PackageIdValue = "wsgm.tests.runtime-fixture";
    public const string DeviceDefinitionIdValue = "runtime-fixture";
    private IPluginHostAdapter? _host;
    private string? _stateDirectory;
    private long _cycleGeneration;
    private long _sequence;

    public string PackageId => PackageIdValue;

    public ValueTask<PluginDetectionResult> DetectAsync(
        PluginDetectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginDetectionResult
        {
            Matched = true,
            DeviceDefinitionId = DeviceDefinitionIdValue,
        });
    }

    public async ValueTask<PluginStartResult> StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _host = context.Host;
        _cycleGeneration = context.CycleGeneration;
        _stateDirectory = context.StateDirectory;
        await File.WriteAllTextAsync(
            Path.Combine(_stateDirectory, "started.txt"),
            _cycleGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        await PublishSampleAsync(_cycleGeneration, cancellationToken);
        return Active();
    }

    public async ValueTask<CapabilityCommandResult> ExecuteCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.CapabilityId)
        {
            case "late":
                await Task.Delay(120, CancellationToken.None);
                break;
            case "fault":
                _ = ReportBackgroundFaultAsync();
                break;
            case "stale-sample":
                await PublishSampleAsync(_cycleGeneration - 1, cancellationToken);
                break;
            case "current-sample":
                await PublishSampleAsync(_cycleGeneration, cancellationToken);
                break;
        }

        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.AppliedVerified,
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    public ValueTask ApplySettingsAsync(
        IReadOnlyList<DeviceSettingValue> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask SuspendAsync(
        PluginQuiesceContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<PluginStartResult> ResumeAsync(
        PluginResumeContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _cycleGeneration = context.CycleGeneration;
        await PublishSampleAsync(_cycleGeneration, cancellationToken);
        return Active();
    }

    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginDiagnostics
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["state-directory"] = _stateDirectory ?? string.Empty,
            },
        });
    }

    public ValueTask ApplyHapticOutputAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginControllerRelease> ReleaseControllerAsync(
        PluginControllerReleaseContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginControllerRelease
        {
            Step = ControllerHandoffStep.TopologyVerified,
            Result = ControllerHandoffResult.ReleasedVerified,
        });
    }

    public ValueTask SetControllerManagementAsync(
        PluginControllerManagementContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Enabled)
        {
            _cycleGeneration = context.CycleGeneration;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<PluginStopResult> StopAsync(
        PluginStopContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(
            Path.Combine(StateDirectory, "stopped.txt"),
            context.Reason.ToString(),
            cancellationToken);
        return new PluginStopResult { Status = PluginStopStatus.Clean };
    }

    public async ValueTask DisposeAsync()
    {
        if (_stateDirectory is not null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_stateDirectory, "disposed.txt"),
                "disposed");
        }
    }

    private IPluginHostAdapter Host => _host
        ?? throw new InvalidOperationException("The fixture plugin has not started.");

    private string StateDirectory => _stateDirectory
        ?? throw new InvalidOperationException("The fixture plugin has not started.");

    private async ValueTask PublishSampleAsync(
        long cycleGeneration,
        CancellationToken cancellationToken)
    {
        await Host.PublishControllerSampleAsync(new CanonicalControllerSample
        {
            Sequence = Interlocked.Increment(ref _sequence),
            CycleGeneration = cycleGeneration,
            Timestamp = DateTimeOffset.UtcNow,
        }, cancellationToken);
    }

    private async Task ReportBackgroundFaultAsync()
    {
        await Task.Delay(20);
        Host.ReportFault("fixture", "background reader failed");
    }

    private static PluginStartResult Active() => new()
    {
        State = PluginOperationalState.Active,
    };
}
