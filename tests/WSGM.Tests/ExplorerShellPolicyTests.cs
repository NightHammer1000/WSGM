using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Tests;

public sealed class ExplorerShellPolicyTests
{
    private const string ExplorerPath = @"C:\Windows\explorer.exe";

    [Fact]
    public void Evaluate_AcceptsCanonicalMediumJoblessReadyTaskbarOwner()
    {
        ExplorerShellAcceptance result = ExplorerShellPolicy.Evaluate(
            NormalProcess(),
            ExplorerPath,
            3,
            ownsReadyTaskbar: true,
            requireReadyTaskbar: true);

        Assert.True(result.Accepted);
        Assert.Equal(ExplorerShellRejection.None, result.Rejection);
    }

    [Theory]
    [InlineData((int)ExplorerShellRejection.ProcessUnavailable)]
    [InlineData((int)ExplorerShellRejection.ImageUnknown)]
    [InlineData((int)ExplorerShellRejection.WrongImage)]
    [InlineData((int)ExplorerShellRejection.SessionUnknown)]
    [InlineData((int)ExplorerShellRejection.WrongSession)]
    [InlineData((int)ExplorerShellRejection.IntegrityUnknown)]
    [InlineData((int)ExplorerShellRejection.WrongIntegrity)]
    [InlineData((int)ExplorerShellRejection.JobMembershipUnknown)]
    [InlineData((int)ExplorerShellRejection.JobBound)]
    [InlineData((int)ExplorerShellRejection.NotReady)]
    public void Evaluate_RejectsEachNonCanonicalShellState(int expectedValue)
    {
        ExplorerShellRejection expected = (ExplorerShellRejection)expectedValue;
        NativeShellProcessInfo process = expected switch
        {
            ExplorerShellRejection.ProcessUnavailable =>
                NativeShellProcessInfo.Unavailable(12, 5),
            ExplorerShellRejection.ImageUnknown =>
                NormalProcess() with
                {
                    ImagePath = null,
                    Errors = new NativeShellProcessErrors(0, 5, 0, 0, 0),
                },
            ExplorerShellRejection.WrongImage =>
                NormalProcess() with { ImagePath = @"C:\Windows\notepad.exe" },
            ExplorerShellRejection.SessionUnknown =>
                NormalProcess() with
                {
                    SessionId = null,
                    Errors = new NativeShellProcessErrors(0, 0, 5, 0, 0),
                },
            ExplorerShellRejection.WrongSession =>
                NormalProcess() with { SessionId = 4 },
            ExplorerShellRejection.IntegrityUnknown =>
                NormalProcess() with { Integrity = NativeIntegrityLevel.Unknown },
            ExplorerShellRejection.WrongIntegrity =>
                NormalProcess() with { Integrity = NativeIntegrityLevel.High },
            ExplorerShellRejection.JobMembershipUnknown =>
                NormalProcess() with { JobMembership = NativeJobMembership.Unknown },
            ExplorerShellRejection.JobBound =>
                NormalProcess() with { JobMembership = NativeJobMembership.InJob },
            _ => NormalProcess(),
        };

        ExplorerShellAcceptance result = ExplorerShellPolicy.Evaluate(
            process,
            ExplorerPath,
            3,
            ownsReadyTaskbar: expected is not ExplorerShellRejection.NotReady,
            requireReadyTaskbar: true);

        Assert.False(result.Accepted);
        Assert.Equal(expected, result.Rejection);
    }

    [Fact]
    public void Evaluate_DoesNotRequireTaskbarForFixedPurposeAnchor()
    {
        ExplorerShellAcceptance result = ExplorerShellPolicy.Evaluate(
            NormalProcess() with { ImagePath = @"C:\Program Files\WSGM\WSGM.exe" },
            @"C:\Program Files\WSGM\WSGM.exe",
            3,
            ownsReadyTaskbar: false,
            requireReadyTaskbar: false);

        Assert.True(result.Accepted);
    }

    [Fact]
    public void Evaluate_ReportsMissingShellSurfaceAsNotReady()
    {
        ExplorerShellAcceptance result = ExplorerShellPolicy.Evaluate(
            NativeShellProcessInfo.Unavailable(0, 0),
            ExplorerPath,
            3,
            ownsReadyTaskbar: false,
            requireReadyTaskbar: true);

        Assert.Equal(ExplorerShellRejection.NotReady, result.Rejection);
    }

    [Theory]
    [InlineData(true, true, 12u, 12u, true)]
    [InlineData(true, true, 12u, 99u, false)]
    [InlineData(true, false, 12u, 0u, false)]
    [InlineData(false, true, 0u, 12u, false)]
    [InlineData(true, true, 0u, 0u, false)]
    public void IsInitializedShellOwner_RequiresBothSurfacesFromSameNonzeroProcess(
        bool taskbarPresent,
        bool shellWindowPresent,
        uint taskbarOwner,
        uint shellOwner,
        bool expected)
    {
        Assert.Equal(
            expected,
            ExplorerShellPolicy.IsInitializedShellOwner(
                taskbarPresent,
                shellWindowPresent,
                taskbarOwner,
                shellOwner));
    }

    [Fact]
    public void DesktopResult_DistinguishesCreatedPidFromVerifiedTaskbarOwner()
    {
        ExplorerDesktopResult result = new(
            ExplorerDesktopOutcome.Normal,
            ExplorerDesktopRoute.ShellAnchor,
            processId: 42,
            createdProcessId: 17,
            detail: "normal-stable",
            launchDispatched: true,
            shellSurfacePresent: true,
            elapsed: TimeSpan.FromMilliseconds(500));

        Assert.Equal(17u, result.CreatedProcessId);
        Assert.Equal(42u, result.ProcessId);
    }

    [Fact]
    public void DesktopResult_SchedulerRouteCannotReportNormalOutcome()
    {
        ExplorerDesktopResult result = new(
            ExplorerDesktopOutcome.Normal,
            ExplorerDesktopRoute.ScheduledTaskRecovery,
            processId: 42,
            createdProcessId: 17,
            detail: "normal-stable",
            launchDispatched: true,
            shellSurfacePresent: true,
            elapsed: TimeSpan.FromMilliseconds(500));

        Assert.Equal(ExplorerDesktopOutcome.Degraded, result.Outcome);
    }

    [Fact]
    public void Evaluate_NotReadyWinsBeforeJobStateSoAnUninitializedShellIsNotDegraded()
    {
        ExplorerShellAcceptance result = ExplorerShellPolicy.Evaluate(
            NormalProcess() with { JobMembership = NativeJobMembership.InJob },
            ExplorerPath,
            3,
            ownsReadyTaskbar: false,
            requireReadyTaskbar: true);

        Assert.False(result.Accepted);
        Assert.Equal(ExplorerShellRejection.NotReady, result.Rejection);
        Assert.Equal(
            ExplorerDesktopOutcome.Failed,
            ExplorerShellPolicy.ClassifyDesktop(result, ExplorerDesktopRoute.ExistingShell));
    }

    [Theory]
    [InlineData((int)ExplorerDesktopRoute.ExistingShell, (int)ExplorerDesktopOutcome.Normal)]
    [InlineData((int)ExplorerDesktopRoute.ShellAnchor, (int)ExplorerDesktopOutcome.Normal)]
    [InlineData((int)ExplorerDesktopRoute.ScheduledTaskRecovery, (int)ExplorerDesktopOutcome.Degraded)]
    public void ClassifyDesktop_SchedulerRouteIsAlwaysRecoveryOnly(
        int routeValue,
        int expectedValue)
    {
        ExplorerDesktopRoute route = (ExplorerDesktopRoute)routeValue;
        ExplorerDesktopOutcome expected = (ExplorerDesktopOutcome)expectedValue;
        ExplorerShellAcceptance acceptance = ExplorerShellPolicy.Evaluate(
            NormalProcess(),
            ExplorerPath,
            3,
            ownsReadyTaskbar: true,
            requireReadyTaskbar: true);

        Assert.Equal(expected, ExplorerShellPolicy.ClassifyDesktop(acceptance, route));
    }

    [Theory]
    [InlineData((int)NativeJobMembership.InJob)]
    [InlineData((int)NativeJobMembership.Unknown)]
    public void ClassifyDesktop_OnlyCanonicalReadyMediumShellCanBeDegraded(
        int membershipValue)
    {
        NativeJobMembership membership = (NativeJobMembership)membershipValue;
        ExplorerShellAcceptance acceptance = ExplorerShellPolicy.Evaluate(
            NormalProcess() with { JobMembership = membership },
            ExplorerPath,
            3,
            ownsReadyTaskbar: true,
            requireReadyTaskbar: true);

        Assert.Equal(
            ExplorerDesktopOutcome.Degraded,
            ExplorerShellPolicy.ClassifyDesktop(acceptance, ExplorerDesktopRoute.ExistingShell));
    }

    [Theory]
    [InlineData((int)ExplorerShellRejection.ProcessUnavailable)]
    [InlineData((int)ExplorerShellRejection.WrongImage)]
    [InlineData((int)ExplorerShellRejection.ImageUnknown)]
    [InlineData((int)ExplorerShellRejection.WrongSession)]
    [InlineData((int)ExplorerShellRejection.SessionUnknown)]
    [InlineData((int)ExplorerShellRejection.IntegrityUnknown)]
    [InlineData((int)ExplorerShellRejection.WrongIntegrity)]
    [InlineData((int)ExplorerShellRejection.NotReady)]
    public void ClassifyDesktop_InvalidOrUnreadyTaskbarOwnerNeverBecomesDegraded(
        int rejectionValue)
    {
        ExplorerShellRejection rejection = (ExplorerShellRejection)rejectionValue;
        Assert.Equal(
            ExplorerDesktopOutcome.Failed,
            ExplorerShellPolicy.ClassifyDesktop(
                new ExplorerShellAcceptance(false, rejection),
                ExplorerDesktopRoute.ScheduledTaskRecovery));
    }

    [Theory]
    [InlineData(true, true, false, (int)ExplorerAnchorOwnerLossAction.Exit)]
    [InlineData(false, false, false, (int)ExplorerAnchorOwnerLossAction.Exit)]
    [InlineData(false, true, true, (int)ExplorerAnchorOwnerLossAction.Exit)]
    [InlineData(false, true, false, (int)ExplorerAnchorOwnerLossAction.RestoreExplorer)]
    public void DecideOwnerLoss_RestoresOnlyForUnexpectedLossInActiveShelllessSession(
        bool explicitStop,
        bool sessionActive,
        bool shellSurfacePresent,
        int expectedValue)
    {
        ExplorerAnchorOwnerLossAction expected = (ExplorerAnchorOwnerLossAction)expectedValue;
        Assert.Equal(
            expected,
            ExplorerShellPolicy.DecideOwnerLoss(explicitStop, sessionActive, shellSurfacePresent));
    }

    [Fact]
    public void ShellAnchorUsesASeparateInstallerSafeProcessIdentity()
    {
        Assert.Equal("WSGM.ShellAnchor.exe", ExplorerShellAnchor.ExecutableFileName);
        Assert.Equal(
            @"Local\WSGM.ShellAnchor.RecoverySettled",
            ExplorerShellAnchor.RecoverySettledEventName);
        Assert.EndsWith(
            ExplorerShellAnchor.ExecutableFileName,
            ExplorerShellAnchor.ExecutablePath!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false, false, (int)ExplorerAnchorDisconnectAction.Wait)]
    [InlineData(false, true, false, (int)ExplorerAnchorDisconnectAction.Recover)]
    [InlineData(false, false, true, (int)ExplorerAnchorDisconnectAction.Exit)]
    [InlineData(false, true, true, (int)ExplorerAnchorDisconnectAction.Exit)]
    [InlineData(true, false, false, (int)ExplorerAnchorDisconnectAction.Recover)]
    [InlineData(true, false, true, (int)ExplorerAnchorDisconnectAction.Exit)]
    public void DecideAnchorOwnerWait_FaultIsNotRecoveryWithoutSeparateExitEvidence(
        bool processWaitCompletedSuccessfully,
        bool ownerExitVerifiedSeparately,
        bool explicitStop,
        int expectedValue)
    {
        ExplorerAnchorDisconnectAction expected =
            (ExplorerAnchorDisconnectAction)expectedValue;

        Assert.Equal(
            expected,
            ExplorerShellPolicy.DecideAnchorOwnerWait(
                processWaitCompletedSuccessfully,
                ownerExitVerifiedSeparately,
                explicitStop));
    }

    [Theory]
    [InlineData((int)ExplorerAnchorDisconnectAction.Exit)]
    [InlineData((int)ExplorerAnchorDisconnectAction.Recover)]
    public async Task FaultedAnchorPipeRead_AwaitsVerifiedDisconnectActionBeforeCompleting(
        int actionValue)
    {
        ExplorerAnchorDisconnectAction expected =
            (ExplorerAnchorDisconnectAction)actionValue;
        var disconnectEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisconnect = new TaskCompletionSource<ExplorerAnchorDisconnectAction>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<ExplorerAnchorCommandReadResult> completion =
            ExplorerShellAnchor.CompleteCommandReadAsync(
                Task.FromException<string?>(new IOException("Injected broken pipe.")),
                async () =>
                {
                    disconnectEntered.TrySetResult();
                    return await releaseDisconnect.Task;
                });

        await disconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(completion.IsCompleted);
        releaseDisconnect.TrySetResult(expected);

        ExplorerAnchorCommandReadResult result =
            await completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Null(result.Command);
        Assert.Equal(expected, result.DisconnectAction);
    }

    [Theory]
    [InlineData((int)ExplorerAnchorLaunchDisposition.NotDispatched, false, true)]
    [InlineData((int)ExplorerAnchorLaunchDisposition.NotDispatched, true, false)]
    [InlineData((int)ExplorerAnchorLaunchDisposition.Dispatched, false, false)]
    [InlineData((int)ExplorerAnchorLaunchDisposition.Unknown, false, false)]
    public void CanDispatchScheduler_RefusesKnownAndUncertainLateLaunchRaces(
        int dispositionValue,
        bool shellSurfacePresent,
        bool expected)
    {
        ExplorerAnchorLaunchDisposition disposition =
            (ExplorerAnchorLaunchDisposition)dispositionValue;
        Assert.Equal(
            expected,
            ExplorerShellPolicy.CanDispatchScheduler(disposition, shellSurfacePresent));
    }

    [Theory]
    [InlineData((int)ScheduledTaskLaunchDisposition.NotDispatched, false)]
    [InlineData((int)ScheduledTaskLaunchDisposition.Dispatched, true)]
    [InlineData((int)ScheduledTaskLaunchDisposition.Unknown, true)]
    public void SchedulerMayHaveDispatched_PreservesUnknownLateLaunchRisk(
        int dispositionValue,
        bool expected)
    {
        ScheduledTaskLaunchDisposition disposition =
            (ScheduledTaskLaunchDisposition)dispositionValue;

        Assert.Equal(expected, ExplorerShellPolicy.SchedulerMayHaveDispatched(disposition));
    }

    [Fact]
    public void CanResumeGameModeSafely_RejectsLateLaunchAndVisibleShellRaces()
    {
        ExplorerDesktopResult safe = FailedResult(launchDispatched: false, shellSurfacePresent: false);
        ExplorerDesktopResult dispatched = FailedResult(launchDispatched: true, shellSurfacePresent: false);
        ExplorerDesktopResult visible = FailedResult(launchDispatched: false, shellSurfacePresent: true);

        Assert.True(safe.CanResumeGameModeSafely);
        Assert.False(dispatched.CanResumeGameModeSafely);
        Assert.False(visible.CanResumeGameModeSafely);
    }

    [Fact]
    public void InspectErrors_KeepIndependentWin32FailureCodes()
    {
        NativeShellProcessErrors errors = new(5, 6, 7, 8, 9);
        NativeShellProcessInfo process = NormalProcess() with { Errors = errors };

        Assert.Equal(5, process.Errors.Open);
        Assert.Equal(6, process.Errors.Image);
        Assert.Equal(7, process.Errors.Session);
        Assert.Equal(8, process.Errors.Integrity);
        Assert.Equal(9, process.Errors.Job);
    }

    [Fact]
    public async Task DisposedDesktopHost_RefusesNewShellOperations()
    {
        // Disposal closes admission: neither operation may reach the live shell afterwards.
        var host = new ExplorerDesktopHost();
        await host.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            host.PrepareForExplorerExitAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            host.RestoreDesktopAsync(TimeSpan.FromSeconds(1)));
    }

    private static ExplorerDesktopResult FailedResult(bool launchDispatched, bool shellSurfacePresent) =>
        new(
            ExplorerDesktopOutcome.Failed,
            ExplorerDesktopRoute.ShellAnchor,
            0,
            0,
            "test",
            launchDispatched,
            shellSurfacePresent,
            TimeSpan.Zero);

    private static NativeShellProcessInfo NormalProcess() => new(
        12,
        ExplorerPath,
        3,
        NativeIntegrityLevel.Medium,
        NativeJobMembership.NotInJob,
        default);
}
