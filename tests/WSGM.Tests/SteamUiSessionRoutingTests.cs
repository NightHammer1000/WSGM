using System.Text.Json;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class SteamUiSessionRoutingTests
{
    [Fact]
    public async Task RouterReturnsExplicitSuccessAndMalformedPayloadRefusal()
    {
        await using var transport = new RoutingTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask);
        var toggles = 0;
        await using var host = new SteamUiSessionHost(
            transport,
            _ =>
            {
                toggles++;
                return Task.FromResult(true);
            },
            null,
            performance);
        host.Apply(true);
        await WaitForAsync(() => host.GetPatchSnapshots().Any(snapshot =>
            snapshot.Id == "wsgm.native-qam.bootstrap"
            && snapshot.State == SteamUiPatchState.Verified));

        transport.EmitRequest(
            "wsgm.native-qam.shell",
            "toggleQuickAccess",
            sequence: 1,
            actionGeneration: 1,
            payload: null);
        await WaitForAsync(() => transport.Responses.Count >= 1);
        transport.EmitRequest(
            "wsgm.native-qam.tdp",
            "setPrimaryLimit",
            sequence: 2,
            actionGeneration: 1,
            payload: new { watts = "not-a-number", enabled = true });
        await WaitForAsync(() => transport.Responses.Count >= 2);

        Assert.Equal(1, toggles);
        Assert.True(transport.Responses[0].GetProperty("ok").GetBoolean());
        Assert.False(transport.Responses[1].GetProperty("ok").GetBoolean());
        Assert.Equal(
            "The primary power-limit payload is invalid.",
            transport.Responses[1].GetProperty("error").GetString());
    }

    [Fact]
    public async Task CancelStopsInflightWorkAndTheNextRequestStillCompletes()
    {
        await using var transport = new RoutingTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask);
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        TimeSpan routeDeadline = TimeSpan.FromSeconds(2);
        await using var host = new SteamUiSessionHost(
            transport,
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref calls) > 1)
                {
                    return true;
                }

                using CancellationTokenRegistration registration = cancellationToken.Register(
                    () => firstCancelled.TrySetResult());
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return false;
            },
            null,
            performance);
        host.Apply(true);
        await WaitForAsync(() => host.GetPatchSnapshots().Any(snapshot =>
            snapshot.Id == "wsgm.native-qam.bootstrap"
            && snapshot.State == SteamUiPatchState.Verified));

        transport.EmitRequest(
            "wsgm.native-qam.shell",
            "toggleQuickAccess",
            sequence: 1,
            actionGeneration: 1,
            payload: null);
        await firstStarted.Task.WaitAsync(routeDeadline);
        transport.EmitRequest(
            "wsgm.native-qam.shell",
            "toggleQuickAccess",
            sequence: 1,
            actionGeneration: 1,
            payload: null,
            type: "cancel");
        await firstCancelled.Task.WaitAsync(routeDeadline);

        transport.EmitRequest(
            "wsgm.native-qam.shell",
            "toggleQuickAccess",
            sequence: 2,
            actionGeneration: 2,
            payload: null);
        await WaitForAsync(() => transport.Responses.Any(response =>
            response.GetProperty("sequence").GetInt64() == 2));

        Assert.Equal(2, calls);
        Assert.DoesNotContain(
            transport.Responses,
            response => response.GetProperty("sequence").GetInt64() == 1);
    }

    [Fact]
    public async Task PerformanceObservationExistsOnlyWhileRowsAndBridgeAreCurrent()
    {
        await using var transport = new RoutingTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask);
        await using var host = new SteamUiSessionHost(
            transport,
            _ => Task.FromResult(true),
            null,
            performance);
        host.Apply(true);

        await WaitForAsync(() => host.GetPatchSnapshots().Any(snapshot =>
            snapshot.Id == "wsgm.native-qam.frame-limit"
            && snapshot.State == SteamUiPatchState.Verified));
        await WaitForAsync(() => performance.ObserverCount == 1);

        transport.BridgeHandshakeSucceeds = false;
        transport.AdvanceSharedGeneration();
        await WaitForAsync(() => performance.ObserverCount == 0);

        Assert.Equal(0, performance.ObserverCount);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private sealed class RoutingTransport : ISteamUiTransport
    {
        private readonly object _responseGate = new();
        private readonly Dictionary<SteamUiTargetRole, SteamUiGenerations> _generations = new()
        {
            [SteamUiTargetRole.SharedJsContext] = new(1, 1, 1, 1, 1, 1),
            [SteamUiTargetRole.MainWindow] = new(1, 1, 1, 1, 1, 1),
        };
        private readonly List<JsonElement> _responses = [];

        public event EventHandler<SteamUiNotification>? NotificationReceived;

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged;

        internal bool BridgeHandshakeSucceeds { get; set; } = true;

        internal IReadOnlyList<JsonElement> Responses
        {
            get
            {
                lock (_responseGate)
                {
                    return [.. _responses];
                }
            }
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            SteamUiTargetRole role,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IAsyncDisposable>(new Lease());
        }

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role,
            string expression,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureResponse(expression);
            string value;
            if (!BridgeHandshakeSucceeds
                && expression.Contains("maximumPending", StringComparison.Ordinal))
            {
                value = "{\"ok\":false}";
            }
            else if (expression.Contains("wsgm_qam_probe_", StringComparison.Ordinal))
            {
                value = "{\"tdpAvailability\":1,\"tdpComponent\":1,"
                    + "\"performanceActions\":1,\"profileProjection\":1}";
            }
            else if (expression.Contains("wsgm_native_", StringComparison.Ordinal)
                && expression.Contains("_probe_", StringComparison.Ordinal))
            {
                value = "{\"performanceActions\":1,\"controllerPresentation\":1,"
                    + "\"tdpPresentation\":1,\"performanceRoot\":1,\"nativeFields\":1,"
                    + "\"nativeLayout\":1,\"localization\":1,\"react\":1}";
            }
            else if (expression.Contains("version:b&&b.version", StringComparison.Ordinal))
            {
                value = "{\"ok\":true,\"version\":1}";
            }
            else if (expression.Contains("absent:!window.__wsgmSteamUi", StringComparison.Ordinal))
            {
                value = "{\"absent\":true}";
            }
            else if (expression.Contains("generation replaced", StringComparison.Ordinal)
                && expression.Contains("nativeComponents", StringComparison.Ordinal))
            {
                value = "{\"ok\":true}";
            }
            else if (expression.Contains(
                "runtime:!!window.webpackChunksteamui",
                StringComparison.Ordinal))
            {
                value = "{\"ok\":true,\"runtime\":true,\"owned\":false}";
            }
            else
            {
                value = "{\"ok\":true}";
            }

            return Task.FromResult(new SteamUiEvaluationResult(
                true,
                value,
                null,
                _generations[role]));
        }

        public Task SetRuntimeBindingAsync(
            SteamUiTargetRole role,
            string bindingName,
            bool installed,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
            _generations.Select(pair => new SteamUiTransportSnapshot(
                pair.Key,
                SteamUiTransportHealth.Ready,
                pair.Value,
                "fixture-" + pair.Key,
                null,
                0,
                1)).ToArray();

        internal void EmitRequest(
            string patchId,
            string command,
            long sequence,
            long actionGeneration,
            object? payload,
            string type = "request")
        {
            SteamUiGenerations generation = _generations[SteamUiTargetRole.SharedJsContext];
            string envelope = JsonSerializer.Serialize(new
            {
                version = SteamUiBridgeHost.SchemaVersion,
                type,
                patchId,
                command,
                sequence,
                actionGeneration,
                contextGeneration = generation.ExecutionContext,
                documentGeneration = generation.Document,
                payload,
            });
            string parameters = JsonSerializer.Serialize(new
            {
                name = "__wsgmNativeBridge_v1_7b24d11c",
                payload = envelope,
            });
            NotificationReceived?.Invoke(this, new SteamUiNotification(
                SteamUiTargetRole.SharedJsContext,
                "Runtime.bindingCalled",
                parameters,
                generation));
        }

        internal void AdvanceSharedGeneration()
        {
            SteamUiTargetRole role = SteamUiTargetRole.SharedJsContext;
            _generations[role] = _generations[role] with
            {
                ExecutionContext = _generations[role].ExecutionContext + 1,
                Document = _generations[role].Document + 1,
            };
            SteamUiGenerations generation = _generations[role];
            GenerationChanged?.Invoke(this, new SteamUiTransportSnapshot(
                role,
                SteamUiTransportHealth.Ready,
                generation,
                "fixture-" + role,
                null,
                0,
                1));
        }

        private void CaptureResponse(string expression)
        {
            const string marker = "JSON.parse(";
            int start = expression.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return;
            }

            start += marker.Length;
            if (start >= expression.Length || expression[start] != '"')
            {
                return;
            }

            var escaped = false;
            for (int index = start + 1; index < expression.Length; index++)
            {
                char character = expression[index];
                if (!escaped && character == '"')
                {
                    string? json = JsonSerializer.Deserialize<string>(expression[start..(index + 1)]);
                    if (json is null)
                    {
                        return;
                    }

                    using JsonDocument document = JsonDocument.Parse(json);
                    if (!document.RootElement.TryGetProperty("type", out JsonElement type)
                        || type.GetString() != "response")
                    {
                        return;
                    }

                    lock (_responseGate)
                    {
                        _responses.Add(document.RootElement.Clone());
                    }
                    return;
                }

                escaped = !escaped && character == '\\';
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
