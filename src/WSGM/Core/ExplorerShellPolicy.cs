using System;
using System.IO;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Pure acceptance policy for Explorer and its fixed-purpose launch anchor.</summary>
internal static class ExplorerShellPolicy
{
    /// <summary>Gets whether both initialized shell surfaces exist and belong to one process.</summary>
    internal static bool IsInitializedShellOwner(
        bool taskbarPresent,
        bool shellWindowPresent,
        uint taskbarOwnerProcessId,
        uint shellOwnerProcessId) =>
        taskbarPresent
        && shellWindowPresent
        && taskbarOwnerProcessId != 0
        && taskbarOwnerProcessId == shellOwnerProcessId;

    /// <summary>Evaluates whether a process has the exact image, session, integrity, job, and
    /// optional taskbar-readiness properties required of a launch owner or restored shell.</summary>
    internal static ExplorerShellAcceptance Evaluate(
        NativeShellProcessInfo process,
        string expectedImagePath,
        int expectedSessionId,
        bool ownsReadyTaskbar,
        bool requireReadyTaskbar)
    {
        if (process.Errors.Open != 0)
        {
            return new(false, ExplorerShellRejection.ProcessUnavailable);
        }
        if (requireReadyTaskbar && process.ProcessId == 0 && !ownsReadyTaskbar)
        {
            return new(false, ExplorerShellRejection.NotReady);
        }
        if (string.IsNullOrWhiteSpace(process.ImagePath))
        {
            return new(false, ExplorerShellRejection.ImageUnknown);
        }
        if (!Path.GetFullPath(process.ImagePath).Equals(
                Path.GetFullPath(expectedImagePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return new(false, ExplorerShellRejection.WrongImage);
        }
        if (process.SessionId is null)
        {
            return new(false, ExplorerShellRejection.SessionUnknown);
        }
        if (process.SessionId != expectedSessionId)
        {
            return new(false, ExplorerShellRejection.WrongSession);
        }
        if (process.Integrity != NativeIntegrityLevel.Medium)
        {
            return new(false, process.Integrity == NativeIntegrityLevel.Unknown
                ? ExplorerShellRejection.IntegrityUnknown
                : ExplorerShellRejection.WrongIntegrity);
        }
        if (requireReadyTaskbar && !ownsReadyTaskbar)
        {
            return new(false, ExplorerShellRejection.NotReady);
        }
        if (process.JobMembership != NativeJobMembership.NotInJob)
        {
            return new(false, process.JobMembership == NativeJobMembership.Unknown
                ? ExplorerShellRejection.JobMembershipUnknown
                : ExplorerShellRejection.JobBound);
        }
        return new(true, ExplorerShellRejection.None);
    }

    /// <summary>Classifies an observed taskbar owner. Only a canonical current-session medium
    /// Explorer can be usable in degraded mode, and a scheduler route is always recovery-only.</summary>
    internal static ExplorerDesktopOutcome ClassifyDesktop(
        ExplorerShellAcceptance acceptance,
        ExplorerDesktopRoute route)
    {
        if (acceptance.Accepted)
        {
            return route is ExplorerDesktopRoute.ScheduledTaskRecovery
                ? ExplorerDesktopOutcome.Degraded
                : ExplorerDesktopOutcome.Normal;
        }

        return acceptance.Rejection is ExplorerShellRejection.JobBound
            or ExplorerShellRejection.JobMembershipUnknown
            ? ExplorerDesktopOutcome.Degraded
            : ExplorerDesktopOutcome.Failed;
    }

    /// <summary>Decides whether an orphaned anchor may restore Explorer. An explicit stop or an
    /// ending/inactive session always wins, and any existing shell surface is preserved.</summary>
    internal static ExplorerAnchorOwnerLossAction DecideOwnerLoss(
        bool explicitStop,
        bool sessionActive,
        bool shellSurfacePresent)
    {
        if (explicitStop || !sessionActive || shellSurfacePresent)
        {
            return ExplorerAnchorOwnerLossAction.Exit;
        }

        return ExplorerAnchorOwnerLossAction.RestoreExplorer;
    }

    /// <summary>Combines the primary process-wait result with a separate owner-liveness
    /// observation. A faulted wait is never owner loss by itself; the explicit stop signal wins a
    /// simultaneous verified exit so planned stale-anchor cleanup never restores Explorer.</summary>
    internal static ExplorerAnchorDisconnectAction DecideAnchorOwnerWait(
        bool processWaitCompletedSuccessfully,
        bool ownerExitVerifiedSeparately,
        bool explicitStop)
    {
        if (explicitStop)
        {
            return ExplorerAnchorDisconnectAction.Exit;
        }

        return processWaitCompletedSuccessfully || ownerExitVerifiedSeparately
            ? ExplorerAnchorDisconnectAction.Recover
            : ExplorerAnchorDisconnectAction.Wait;
    }

    /// <summary>Gets whether the scheduler can be dispatched without racing an anchor request or
    /// a shell surface that appeared after the last observation.</summary>
    internal static bool CanDispatchScheduler(
        ExplorerAnchorLaunchDisposition anchorDisposition,
        bool shellSurfacePresent) =>
        anchorDisposition is ExplorerAnchorLaunchDisposition.NotDispatched && !shellSurfacePresent;

    /// <summary>Gets whether a scheduler request may still produce Explorer. Unknown is deliberately
    /// treated as dispatched so game-mode surfaces cannot race a late Task Scheduler launch.</summary>
    internal static bool SchedulerMayHaveDispatched(
        ScheduledTaskLaunchDisposition disposition) =>
        disposition is ScheduledTaskLaunchDisposition.Dispatched
            or ScheduledTaskLaunchDisposition.Unknown;
}

/// <summary>Result of applying the normal-shell acceptance policy.</summary>
internal readonly record struct ExplorerShellAcceptance(bool Accepted, ExplorerShellRejection Rejection);

/// <summary>Concrete reason a process cannot serve as the normal shell or its creation owner.</summary>
internal enum ExplorerShellRejection
{
    /// <summary>No rejection.</summary>
    None,
    /// <summary>The process could not be opened.</summary>
    ProcessUnavailable,
    /// <summary>The process image is not the required fixed image.</summary>
    WrongImage,
    /// <summary>Windows did not expose the process image.</summary>
    ImageUnknown,
    /// <summary>The process belongs to another interactive session.</summary>
    WrongSession,
    /// <summary>Windows did not expose the process session.</summary>
    SessionUnknown,
    /// <summary>Windows did not expose token integrity.</summary>
    IntegrityUnknown,
    /// <summary>The process is not medium integrity.</summary>
    WrongIntegrity,
    /// <summary>Windows did not answer the job-membership query.</summary>
    JobMembershipUnknown,
    /// <summary>The process belongs to a job.</summary>
    JobBound,
    /// <summary>The process does not own the initialized taskbar.</summary>
    NotReady,
}

/// <summary>Action taken by an anchor after its owning WSGM process disappears.</summary>
internal enum ExplorerAnchorOwnerLossAction
{
    /// <summary>Exit without starting anything.</summary>
    Exit,
    /// <summary>Restore the fixed canonical Explorer path.</summary>
    RestoreExplorer,
}

/// <summary>Action taken after the authenticated anchor command pipe disconnects.</summary>
internal enum ExplorerAnchorDisconnectAction
{
    /// <summary>Keep the recovery owner alive until its owner exits or it is explicitly stopped.</summary>
    Wait,
    /// <summary>Exit without restoring Explorer.</summary>
    Exit,
    /// <summary>Run abnormal owner-loss recovery.</summary>
    Recover,
}
