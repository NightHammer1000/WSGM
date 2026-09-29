using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Starts a process with the interactive user's medium-IL token from an
/// elevated WSGM, by registering and running a one-shot scheduled task with
/// LogonType=InteractiveToken and default (least) run level — the same mechanism
/// Windows 11's own explorer uses to de-elevate itself (CreateExplorerShellUnelevatedTask).
///
/// The naive TokenLinkedToken route does NOT work here: without SeTcbPrivilege the
/// linked token is only a SecurityIdentification impersonation token and cannot be
/// converted to a primary token (fails with ERROR_BAD_IMPERSONATION_LEVEL) — verified
/// empirically. When UAC is disabled entirely there is no limited token to run as and
/// this (like every technique) cannot help.</summary>
internal static class UnelevatedLauncher
{
    public static bool TryStartViaScheduledTask(string exePath, string arguments = "")
    {
        // Recovery and legacy synchronous callers share the exact bounded implementation used by
        // the asynchronous desktop handoff. ConfigureAwait(false) throughout keeps this fixed sync
        // boundary independent of a UI synchronization context.
        ScheduledTaskLaunchDisposition disposition = TryStartViaScheduledTaskAsync(
            exePath,
            arguments,
            DateTimeOffset.UtcNow.AddSeconds(30),
            CancellationToken.None).GetAwaiter().GetResult();
        return disposition is ScheduledTaskLaunchDisposition.Dispatched;
    }

    /// <summary>Runs the scheduled-task handoff within a caller-owned absolute deadline. Task
    /// creation, dispatch, and best-effort deletion all consume the same remaining budget.</summary>
    internal static async Task<ScheduledTaskLaunchDisposition> TryStartViaScheduledTaskAsync(
        string exePath,
        string arguments,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        string suffix = $"{Environment.ProcessId}-{Random.Shared.Next():x8}";
        string taskName = $"WSGM_StartUnelevated_{suffix}";
        string xmlPath = Path.Combine(Log.Directory, $"wsgm-task-{suffix}.xml");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasRemainingBudget(deadline))
            {
                Log.Warn("De-elevated scheduled-task launch was not started because its shared deadline expired.");
                return ScheduledTaskLaunchDisposition.NotDispatched;
            }

            Directory.CreateDirectory(Log.Directory);
            string taskXml = BuildTaskXml(exePath, arguments);
            using (var writeCancellation = CreateBudgetCancellation(deadline, cancellationToken))
            {
                await File.WriteAllTextAsync(
                    xmlPath,
                    taskXml,
                    System.Text.Encoding.Unicode,
                    writeCancellation.Token).ConfigureAwait(false);
            }

            ScheduledTaskLaunchDisposition disposition = await RunScheduledTaskSequenceAsync(
                taskName,
                xmlPath,
                deadline,
                cancellationToken,
                RunSchtasksUntilAsync).ConfigureAwait(false);
            if (disposition is ScheduledTaskLaunchDisposition.Dispatched)
            {
                Log.Info($"Started via de-elevating scheduled task: {exePath}"
                    + (arguments.Length == 0 ? "" : $" {arguments}"));
            }
            return disposition;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Log.Warn("De-elevated scheduled-task launch did not finish before its shared deadline.");
            return ScheduledTaskLaunchDisposition.NotDispatched;
        }
        catch (Exception ex)
        {
            Log.Error("De-elevated launch via scheduled task failed", ex);
            return ScheduledTaskLaunchDisposition.NotDispatched;
        }
        finally
        {
            // Local deletion does not consume the Task Scheduler budget. Always remove the input
            // document even when dispatch was cancelled or timed out; otherwise every bounded
            // recovery failure leaves another XML file in the durable log directory.
            try
            {
                File.Delete(xmlPath);
            }
            catch (Exception ex)
            {
                Log.Warn($"Scheduled-task XML cleanup failed: {ex.Message}");
            }
        }
    }

    /// <summary>Runs create, dispatch, and cleanup commands through an injected runner so their
    /// shared-deadline contract can be verified without invoking Task Scheduler.</summary>
    internal static Task<ScheduledTaskLaunchDisposition> RunScheduledTaskSequenceAsync(
        string taskName,
        string xmlPath,
        DateTimeOffset deadline,
        CancellationToken cancellationToken,
        Func<string, DateTimeOffset, CancellationToken, Task<ConsoleToolRunOutcome>> runCommand) =>
        RunScheduledTaskSequenceAsync(
            taskName,
            xmlPath,
            deadline,
            cancellationToken,
            runCommand,
            static () => DateTimeOffset.UtcNow);

    /// <summary>Runs the scheduled-task sequence through an injected clock so deadline closure can
    /// be verified deterministically without invoking Task Scheduler.</summary>
    internal static async Task<ScheduledTaskLaunchDisposition> RunScheduledTaskSequenceAsync(
        string taskName,
        string xmlPath,
        DateTimeOffset deadline,
        CancellationToken cancellationToken,
        Func<string, DateTimeOffset, CancellationToken, Task<ConsoleToolRunOutcome>> runCommand,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(runCommand);
        ArgumentNullException.ThrowIfNull(utcNow);
        bool taskMayExist = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasRemainingBudget(deadline, utcNow))
            {
                return ScheduledTaskLaunchDisposition.NotDispatched;
            }

            ConsoleToolRunOutcome create = await runCommand(
                $"/Create /TN \"{taskName}\" /XML \"{xmlPath}\" /F",
                deadline,
                cancellationToken).ConfigureAwait(false);
            taskMayExist = create is ConsoleToolRunOutcome.Succeeded
                or ConsoleToolRunOutcome.Unknown;
            if (create is not ConsoleToolRunOutcome.Succeeded)
            {
                return ScheduledTaskLaunchDisposition.NotDispatched;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!HasRemainingBudget(deadline, utcNow))
            {
                return ScheduledTaskLaunchDisposition.NotDispatched;
            }

            try
            {
                ConsoleToolRunOutcome run = await runCommand(
                    $"/Run /TN \"{taskName}\"",
                    deadline,
                    cancellationToken).ConfigureAwait(false);
                return run switch
                {
                    ConsoleToolRunOutcome.Succeeded => ScheduledTaskLaunchDisposition.Dispatched,
                    ConsoleToolRunOutcome.Unknown => ScheduledTaskLaunchDisposition.Unknown,
                    _ => ScheduledTaskLaunchDisposition.NotDispatched,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Once /Run begins, an exception cannot prove Task Scheduler rejected the
                // request. The caller must suppress every competing shell launch surface.
                Log.Warn($"Scheduled-task dispatch result is unknown for {taskName}: {ex.Message}");
                return ScheduledTaskLaunchDisposition.Unknown;
            }
        }
        finally
        {
            if (taskMayExist)
            {
                if (cancellationToken.IsCancellationRequested || !HasRemainingBudget(deadline, utcNow))
                {
                    Log.Warn($"Scheduled-task cleanup skipped for {taskName}: the shared budget is closed.");
                }
                else
                {
                    try
                    {
                        ConsoleToolRunOutcome deleted = await runCommand(
                            $"/Delete /TN \"{taskName}\" /F",
                            deadline,
                            cancellationToken).ConfigureAwait(false);
                        if (deleted is not ConsoleToolRunOutcome.Succeeded)
                        {
                            Log.Warn($"Scheduled-task cleanup failed for {taskName}.");
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Scheduled-task cleanup failed for {taskName}: {ex.Message}");
                    }
                }
            }
        }
    }

    internal static string BuildTaskXml(string exePath, string arguments = "")
    {
        // InteractiveToken principal without a RunLevel element = the user's
        // filtered medium-IL token (RunLevel defaults to LeastPrivilege).
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var user = identity.Name;
        var argumentsElement = arguments.Length == 0
            ? ""
            : $"\n                  <Arguments>{System.Security.SecurityElement.Escape(arguments)}</Arguments>";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals>
                <Principal id="Author">
                  <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                </Principal>
              </Principals>
              <Settings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{System.Security.SecurityElement.Escape(exePath)}</Command>{argumentsElement}
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static Task<ConsoleToolRunOutcome> RunSchtasksUntilAsync(
        string arguments,
        DateTimeOffset deadline,
        CancellationToken cancellationToken) =>
        ConsoleTool.RunUntilAsync(
            ConsoleTool.System32("schtasks.exe"),
            arguments,
            deadline,
            cancellationToken);

    private static CancellationTokenSource CreateBudgetCancellation(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        source.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        return source;
    }

    private static bool HasRemainingBudget(DateTimeOffset deadline) =>
        deadline > DateTimeOffset.UtcNow;

    private static bool HasRemainingBudget(
        DateTimeOffset deadline,
        Func<DateTimeOffset> utcNow) =>
        deadline > utcNow();
}

/// <summary>Whether the scheduled recovery request definitely stayed local, definitely reached
/// Task Scheduler, or may have crossed the dispatch boundary.</summary>
internal enum ScheduledTaskLaunchDisposition
{
    /// <summary>No Explorer launch request reached Task Scheduler.</summary>
    NotDispatched,
    /// <summary>Task Scheduler accepted the Explorer launch request.</summary>
    Dispatched,
    /// <summary>The scheduler command began but its dispatch result could not be verified.</summary>
    Unknown,
}
