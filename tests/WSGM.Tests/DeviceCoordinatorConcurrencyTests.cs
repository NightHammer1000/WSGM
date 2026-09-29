using System.Reflection;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DeviceCoordinatorConcurrencyTests
{
    [Fact]
    public void ApplicationEntryPoint_IsTheSynchronousStaMainWrapper()
    {
        MethodInfo entryPoint = typeof(Program).Assembly.EntryPoint
            ?? throw new InvalidOperationException("WSGM has no assembly entry point.");

        Assert.Equal(typeof(Program), entryPoint.DeclaringType);
        Assert.Equal(nameof(Program.Main), entryPoint.Name);
        Assert.Equal(typeof(int), entryPoint.ReturnType);
        Assert.NotNull(entryPoint.GetCustomAttribute<STAThreadAttribute>());
    }

    [Fact]
    public async Task CanceledStart_CleansPartialOwnershipRestoresRetryStateAndRethrows()
    {
        using var cancellation = new CancellationTokenSource();
        DeviceCycleState state = DeviceCycleState.Faulted;
        bool cleaned = false;
        bool restartPending = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DeviceCoordinator.RunCancellationSafeStartAsync(
                token =>
                {
                    _ = token;
                    state = DeviceCycleState.Activating;
                    cancellation.Cancel();
                    return Task.CompletedTask;
                },
                () =>
                {
                    cleaned = true;
                    restartPending = false;
                    return ValueTask.CompletedTask;
                },
                () => state = DeviceCycleState.Faulted,
                cancellation.Token));

        Assert.True(cleaned);
        Assert.False(restartPending);
        Assert.Equal(DeviceCycleState.Faulted, state);
    }

    [Fact]
    public async Task CanceledStart_LifetimeCancellationPreservesClientForShutdown()
    {
        bool callerCleanupRan = false;

        await DeviceCoordinator.RunCanceledStartCleanupPolicyAsync(
            lifetimeCancellationRequested: true,
            () =>
            {
                callerCleanupRan = true;
                return Task.CompletedTask;
            });

        Assert.False(callerCleanupRan);
    }

    [Fact]
    public async Task CanceledStart_CallerCancellationUsesAFreshBoundedCleanupContext()
    {
        using var canceledCaller = new CancellationTokenSource();
        canceledCaller.Cancel();
        DateTimeOffset now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        TimeSpan budget = TimeSpan.FromSeconds(5);
        DateTimeOffset receivedDeadline = default;
        CancellationToken receivedToken = canceledCaller.Token;

        await DeviceCoordinator.RunCanceledStartCleanupPolicyAsync(
            lifetimeCancellationRequested: false,
            () => DeviceCoordinator.RunFreshBoundedCleanupAsync(
                budget,
                (deadline, token) =>
                {
                    receivedDeadline = deadline;
                    receivedToken = token;
                    return Task.CompletedTask;
                },
                () => now));

        Assert.Equal(now.Add(budget), receivedDeadline);
        Assert.True(receivedToken.CanBeCanceled);
        Assert.False(receivedToken.IsCancellationRequested);
        Assert.NotEqual(canceledCaller.Token, receivedToken);
    }

    [Fact]
    public async Task ClientTeardown_StopsBeforeDetachAndDispose()
    {
        List<string> order = [];

        DeviceClientTeardownResult teardown = await DeviceCoordinator.RunClientTeardownAsync(
            _ =>
            {
                order.Add("controller");
                return Task.FromResult(VerifiedHandoff());
            },
            _ =>
            {
                order.Add("stop");
                return Task.FromResult(VerifiedStop());
            },
            () =>
            {
                order.Add("detach");
                return ValueTask.CompletedTask;
            },
            () =>
            {
                order.Add("dispose");
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(teardown.Verified);
        Assert.Equal(["controller", "stop", "detach", "dispose"], order);
    }

    [Fact]
    public async Task ClientTeardown_ThrowingAdmissionAndStateSubscribersCannotSkipProtocolCleanupOrDisposal()
    {
        var admissionFailure = new InvalidOperationException("capability subscriber failed");
        var transitionFailure = new InvalidOperationException("state subscriber failed");
        List<string> order = [];

        DeviceClientTeardownResult teardown =
            await DeviceCoordinator.RunClientTeardownWithStateNotificationsAsync(
                () =>
                {
                    order.Add("close-admission");
                    throw admissionFailure;
                },
                () =>
                {
                    order.Add("deactivating");
                    throw transitionFailure;
                },
                () => DeviceCoordinator.RunClientTeardownAsync(
                    _ =>
                    {
                        order.Add("controller");
                        return Task.FromResult(VerifiedHandoff());
                    },
                    _ =>
                    {
                        order.Add("stop");
                        return Task.FromResult(VerifiedStop());
                    },
                    () =>
            {
                order.Add("detach");
                return ValueTask.CompletedTask;
            },
                    () =>
                    {
                        order.Add("dispose");
                        return ValueTask.CompletedTask;
                    },
                    CancellationToken.None),
                () => order.Add("disabled"));

        Assert.False(teardown.Verified);
        Assert.Contains(admissionFailure, teardown.Failures);
        Assert.Contains(transitionFailure, teardown.Failures);
        Assert.Equal(
            [
                "close-admission",
                "deactivating",
                "controller",
                "stop",
                "detach",
                "dispose",
                "disabled",
            ],
            order);
    }

    [Fact]
    public async Task ClientTeardown_UnverifiedResponsesAreRetainedThroughDisposal()
    {
        bool disposed = false;
        ControllerHandoff handoff = VerifiedHandoff() with
        {
            Step = ControllerHandoffStep.TopologyUnverified,
            Result = ControllerHandoffResult.ReleasedVerified,
        };
        DevicePluginState stopped = VerifiedStop() with
        {
            Reason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "restore readback failed"),
        };

        DeviceClientTeardownResult teardown = await DeviceCoordinator.RunClientTeardownAsync(
            _ => Task.FromResult(handoff),
            _ => Task.FromResult(stopped),
            static () => ValueTask.CompletedTask,
            () =>
            {
                disposed = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(teardown.Verified);
        Assert.Equal(2, teardown.Failures.Count);
        Assert.True(disposed);
        Assert.Throws<InvalidOperationException>(() =>
            DeviceCoordinator.ThrowIfDeviceTeardownIncomplete(
                teardown,
                CancellationToken.None));
    }

    [Fact]
    public async Task ClientTeardown_ProtocolExceptionsDoNotSkipStopOrDispose()
    {
        var controllerFailure = new IOException("controller pipe failed");
        var stopFailure = new TimeoutException("plugin stop timed out");
        List<string> order = [];

        DeviceClientTeardownResult teardown = await DeviceCoordinator.RunClientTeardownAsync(
            _ =>
            {
                order.Add("controller");
                return Task.FromException<ControllerHandoff>(controllerFailure);
            },
            _ =>
            {
                order.Add("stop");
                return Task.FromException<DevicePluginState>(stopFailure);
            },
            () =>
            {
                order.Add("detach");
                return ValueTask.CompletedTask;
            },
            () =>
            {
                order.Add("dispose");
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Contains(controllerFailure, teardown.Failures);
        Assert.Contains(stopFailure, teardown.Failures);
        Assert.Equal(["controller", "stop", "detach", "dispose"], order);
        InvalidOperationException reported = Assert.Throws<InvalidOperationException>(() =>
            DeviceCoordinator.ThrowIfDeviceTeardownIncomplete(
                teardown,
                CancellationToken.None));
        Assert.IsType<AggregateException>(reported.InnerException);
    }

    [Fact]
    public async Task ClientTeardown_CanceledHandoffStillAttemptsStopBeforeDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> order = [];

        DeviceClientTeardownResult teardown = await DeviceCoordinator.RunClientTeardownAsync(
            token =>
            {
                order.Add("controller");
                return Task.FromCanceled<ControllerHandoff>(token);
            },
            token =>
            {
                order.Add("stop");
                return Task.FromCanceled<DevicePluginState>(token);
            },
            () =>
            {
                order.Add("detach");
                return ValueTask.CompletedTask;
            },
            () =>
            {
                order.Add("dispose");
                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(["controller", "stop", "detach", "dispose"], order);
        Assert.Equal(2, teardown.Failures.Count);
        OperationCanceledException canceled = Assert.ThrowsAny<OperationCanceledException>(() =>
            DeviceCoordinator.ThrowIfDeviceTeardownIncomplete(
                teardown,
                cancellation.Token));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.IsType<AggregateException>(canceled.InnerException);
    }

    [Fact]
    public void ClientTeardown_VerifiedCleanupStillRethrowsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException canceled = Assert.ThrowsAny<OperationCanceledException>(() =>
            DeviceCoordinator.ThrowIfDeviceTeardownIncomplete(
                DeviceClientTeardownResult.Clean,
                cancellation.Token));

        Assert.Equal(cancellation.Token, canceled.CancellationToken);
    }

    [Fact]
    public void PendingTeardownFailure_IsRetainedForShutdownWhenNoClientRemains()
    {
        var tracker = new DeviceTeardownFailureTracker();
        var hostExitFailure = new InvalidOperationException(
            "fault while shutdown waited for the transition");

        tracker.Retain(hostExitFailure);
        IReadOnlyList<Exception> drained = tracker.Drain();

        Assert.Single(drained);
        Assert.Same(hostExitFailure, drained[0]);
        Assert.Empty(tracker.Drain());
        Assert.False(tracker.HasFailures);
    }

    [Fact]
    public void PendingTeardownFailure_IsClearedOnlyByALaterVerifiedOwnerTeardown()
    {
        var tracker = new DeviceTeardownFailureTracker();
        tracker.Retain(new InvalidOperationException("earlier cleanup unverified"));

        tracker.ResolveAfterVerifiedOwnerTeardown();

        Assert.Empty(tracker.Drain());
    }

    [Fact]
    public async Task Shutdown_CancelsLifetimeBeforeWaitingForAnInFlightTransition()
    {
        using var lifetime = new CancellationTokenSource();
        using var transitionGate = new SemaphoreSlim(0, 1);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = lifetime.Token.Register(
            () => canceled.TrySetResult());

        Task waiting = DeviceCoordinator.CancelLifetimeAndWaitForTransitionAsync(
            lifetime,
            transitionGate);
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(waiting.IsCompleted);

            transitionGate.Release();
            await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (!waiting.IsCompleted)
            {
                transitionGate.Release();
            }
            await waiting.WaitAsync(TimeSpan.FromSeconds(1));
            transitionGate.Release();
        }
    }

    [Fact]
    public void OwnerMarkerCreationFailure_FailsClosed()
    {
        string name = $@"Local\WSGM.Tests.DeviceOwner.Failure.{Guid.NewGuid():N}";

        Mutex? owner = DeviceCoordinator.TryCreateOwnerMutex(
            name,
            static _ => throw new IOException("simulated named-object failure"));
        Mutex? denied = DeviceCoordinator.TryCreateOwnerMutex(
            name,
            static _ => throw new UnauthorizedAccessException("simulated access denial"));
        Mutex? unavailable = DeviceCoordinator.TryCreateOwnerMutex(
            name,
            static _ => throw new WaitHandleCannotBeOpenedException("simulated object failure"));

        Assert.Null(owner);
        Assert.Null(denied);
        Assert.Null(unavailable);
    }

    [Fact]
    public void OwnerMarker_IsUnownedAndItsHandleMayBeDisposedOnAnotherThread()
    {
        string name = $@"Local\WSGM.Tests.DeviceOwner.Lifetime.{Guid.NewGuid():N}";
        Mutex marker = Assert.IsType<Mutex>(DeviceCoordinator.TryCreateOwnerMutex(name));
        Mutex? ownerForCleanup = marker;
        try
        {
            Mutex? duplicate = DeviceCoordinator.TryCreateOwnerMutex(name);
            try
            {
                Assert.Null(duplicate);
            }
            finally
            {
                duplicate?.Dispose();
            }

            bool acquiredOnWorker = false;
            Exception? acquireFailure = null;
            var acquireThread = new Thread(() =>
            {
                try
                {
                    acquiredOnWorker = marker.WaitOne(TimeSpan.Zero);
                    if (acquiredOnWorker)
                    {
                        marker.ReleaseMutex();
                    }
                }
                catch (Exception ex)
                {
                    acquireFailure = ex;
                }
            });
            acquireThread.Start();
            Assert.True(acquireThread.Join(TimeSpan.FromSeconds(10)));
            if (!acquiredOnWorker && acquireFailure is null)
            {
                // This is the old initially-owned policy. Release it on the creating thread so a
                // failing regression test cannot strand a thread-owned named mutex in the runner.
                marker.ReleaseMutex();
            }
            Assert.Null(acquireFailure);
            Assert.True(acquiredOnWorker);

            Exception? disposeFailure = null;
            var disposeThread = new Thread(() =>
            {
                try
                {
                    marker.Dispose();
                }
                catch (Exception ex)
                {
                    disposeFailure = ex;
                }
            });
            disposeThread.Start();
            Assert.True(disposeThread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(disposeFailure);
            ownerForCleanup = null;

            using Mutex reacquired = Assert.IsType<Mutex>(
                DeviceCoordinator.TryCreateOwnerMutex(name));
        }
        finally
        {
            ownerForCleanup?.Dispose();
        }
    }

    [Fact]
    public void ProductionOwnerMarker_IsTheExactMachineWideHardwareReservation()
    {
        Assert.Equal(@"Global\WSGM.DeviceOwner", DeviceCoordinator.ProductionOwnerName);
    }

    [Fact]
    public void OwnerReservation_IsExclusiveWhileHeldAndReacquirableAfterRelease()
    {
        // Plugin maintenance holds this exact reservation across the whole slot operation
        // (a using scope around the maintenance body in Program), so exclusivity while held
        // and reacquirability after release are the load-bearing marker semantics.
        string name = $@"Local\WSGM.Tests.DeviceOwner.Maintenance.{Guid.NewGuid():N}";
        using (Mutex reservation = Assert.IsType<Mutex>(
            DeviceCoordinator.TryCreateOwnerMutex(name)))
        {
            Assert.Null(DeviceCoordinator.TryCreateOwnerMutex(name));
        }

        using Mutex reacquired = Assert.IsType<Mutex>(
            DeviceCoordinator.TryCreateOwnerMutex(name));
    }

    [Fact]
    public async Task DevicePluginMaintenance_HoldsOwnerReservationThroughTheWholeOperation()
    {
        string name = $@"Local\WSGM.Tests.DeviceOwner.Maintenance.{Guid.NewGuid():N}";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> maintenance = Program.RunDevicePluginMaintenanceWithOwnerReservationAsync(
            name,
            "test maintenance",
            async () =>
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return 23;
            });
        int outcome = 0;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Null(DeviceCoordinator.TryCreateOwnerMutex(name));
        }
        finally
        {
            release.TrySetResult();
            outcome = await maintenance.WaitAsync(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(23, outcome);
        using Mutex reacquired = Assert.IsType<Mutex>(
            DeviceCoordinator.TryCreateOwnerMutex(name));
    }

    private static ControllerHandoff VerifiedHandoff() => new()
    {
        Step = ControllerHandoffStep.TopologyVerified,
        Result = ControllerHandoffResult.ReleasedVerified,
    };

    private static DevicePluginState VerifiedStop() => new()
    {
        State = DeviceCycleState.Disabled,
        CycleGeneration = 1,
    };

}
