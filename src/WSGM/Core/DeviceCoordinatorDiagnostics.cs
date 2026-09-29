using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Core;

/// <summary>Bounded read-only view exposed by the resident device coordinator.</summary>
internal sealed record DeviceCoordinatorDiagnosticsSnapshot
{
    public required DeviceCycleState State { get; init; }

    public DeviceInstalledPackageDiagnostic? InstalledPackage { get; init; }

    public required long CycleGeneration { get; init; }

    public required int CapabilityCount { get; init; }

    public required int HealthyCapabilityCount { get; init; }

    public required int FaultedCapabilityCount { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
}

/// <summary>Sanitized sole installed-package information for standalone Settings.</summary>
internal sealed record DeviceInstalledPackageDiagnostic(
    string PackageId,
    string Version);

/// <summary>Current-user-only one-shot diagnostics server owned by the shell process.</summary>
internal sealed class DeviceCoordinatorDiagnosticsServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<DeviceCoordinatorDiagnosticsSnapshot> _snapshot;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;

    internal DeviceCoordinatorDiagnosticsServer(
        uint sessionId,
        Func<DeviceCoordinatorDiagnosticsSnapshot> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _pipeName = $"WSGM.DeviceCoordinator.{sessionId}";
        _snapshot = snapshot;
        _worker = RunAsync(_lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream pipe = new(
                    _pipeName,
                    PipeDirection.Out,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: 4096,
                    outBufferSize: 64 * 1024);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await JsonSerializer.SerializeAsync(
                    pipe,
                    _snapshot(),
                    ConfigJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot,
                    cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or JsonException)
            {
                Log.Warn($"Device diagnostics pipe recovered after failure: {ex.Message}");
            }
        }
    }
}

/// <summary>Read-only client used by standalone Settings; it cannot own or command hardware.</summary>
internal static class DeviceCoordinatorDiagnosticsClient
{
    internal static async Task<DeviceCoordinatorDiagnosticsSnapshot?> TryReadAsync(
        uint sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        bounded.CancelAfter(timeout);
        await using NamedPipeClientStream pipe = new(
            ".",
            $"WSGM.DeviceCoordinator.{sessionId}",
            PipeDirection.In,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(bounded.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(
                pipe,
                ConfigJsonContext.Default.DeviceCoordinatorDiagnosticsSnapshot,
                bounded.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
            or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }
}
