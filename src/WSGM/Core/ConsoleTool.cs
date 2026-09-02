using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Bounded console-process outcome preserving whether process start crossed an uncertain
/// side-effect boundary.</summary>
internal enum ConsoleToolRunOutcome
{
    /// <summary>The process was never started.</summary>
    NotStarted,
    /// <summary>The process exited successfully before the deadline.</summary>
    Succeeded,
    /// <summary>The process exited with a known failure before the deadline.</summary>
    Failed,
    /// <summary>The process started but its result could not be verified.</summary>
    Unknown,
}

/// <summary>Narrow owned-process surface used to verify bounded console-tool cleanup without
/// starting a live operating-system command from tests.</summary>
internal interface IConsoleToolProcess : IDisposable
{
    /// <summary>Gets the process exit code after exit.</summary>
    int ExitCode { get; }

    /// <summary>Waits for the exact process to exit.</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Requests termination of the exact process and its descendants.</summary>
    void KillTree();
}

/// <summary>One home for the "run a hidden console tool and wait" pattern
/// (schtasks, powercfg, powershell one-shots), so every caller gets the same
/// exit-code and timeout checks. The absolute-deadline path preserves a timeout as
/// unknown after process start, because a side-effecting command may already have crossed its
/// dispatch boundary even though reading ExitCode from the still-running process is impossible.</summary>
internal static class ConsoleTool
{
    // How long a killed tool's output pipes may take to close before the
    // captured output is given up on.
    private const int DrainTimeoutMs = 2000;

    /// <summary>True only when the tool started, exited within the timeout, and
    /// returned 0. Never throws; failures are logged with the leading argument so
    /// pasted logs show WHICH invocation failed.</summary>
    public static bool Run(string exe, string arguments, int timeoutMs = 15_000)
    {
        var what = $"{exe} {FirstToken(arguments)}";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            });
            if (p is null)
            {
                Log.Warn($"{what} did not start.");
                return false;
            }
            if (!p.WaitForExit(timeoutMs))
            {
                Log.Warn($"{what} still running after {timeoutMs / 1000} s — treated as failed.");
                return false;
            }
            if (p.ExitCode != 0)
            {
                Log.Warn($"{what} exited with {p.ExitCode}.");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"{what} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Runs a hidden console tool within a caller-owned absolute deadline. The process
    /// tree is stopped when that budget expires or the caller cancels, so sequential recovery
    /// commands cannot each acquire a fresh timeout.</summary>
    /// <param name="exe">The executable to run.</param>
    /// <param name="arguments">Its command line.</param>
    /// <param name="deadline">The shared absolute deadline for the surrounding workflow.</param>
    /// <param name="cancellationToken">Cancels the command and stops its process tree.</param>
    /// <returns>Whether the tool did not start, completed successfully, completed with a known
    /// failure, or crossed process start without a verifiable result.</returns>
    internal static Task<ConsoleToolRunOutcome> RunUntilAsync(
        string exe,
        string arguments,
        DateTimeOffset deadline,
        CancellationToken cancellationToken) =>
        RunUntilAsync(
            exe,
            arguments,
            deadline,
            cancellationToken,
            static startInfo =>
            {
                Process? process = Process.Start(startInfo);
                return process is null ? null : new SystemConsoleToolProcess(process);
            });

    /// <summary>Runs through an injected process owner so process-start, wait-fault, and cleanup
    /// boundaries can be verified without invoking a live console tool.</summary>
    internal static async Task<ConsoleToolRunOutcome> RunUntilAsync(
        string exe,
        string arguments,
        DateTimeOffset deadline,
        CancellationToken cancellationToken,
        Func<ProcessStartInfo, IConsoleToolProcess?> startProcess)
    {
        ArgumentNullException.ThrowIfNull(startProcess);
        string what = $"{exe} {FirstToken(arguments)}";
        var processStarted = false;
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline <= DateTimeOffset.UtcNow)
        {
            Log.Warn($"{what} was not started because the shared deadline expired.");
            return ConsoleToolRunOutcome.NotStarted;
        }

        try
        {
            using IConsoleToolProcess? process = startProcess(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            });
            if (process is null)
            {
                Log.Warn($"{what} did not start.");
                return ConsoleToolRunOutcome.NotStarted;
            }
            processStarted = true;

            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCancellation.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            try
            {
                await process.WaitForExitAsync(waitCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warn($"{what} did not finish before the shared deadline — killing it.");
                TryKill(process, what);
                await WaitForKilledProcessAsync(
                    process,
                    what,
                    deadline,
                    cancellationToken).ConfigureAwait(false);
                return ConsoleToolRunOutcome.Unknown;
            }
            catch (OperationCanceledException)
            {
                TryKill(process, what);
                throw;
            }
            catch (Exception ex)
            {
                // Disposing Process closes only our handle; it does not terminate the process.
                // Once start crossed the side-effect boundary, an unobservable wait must still
                // retire the exact tool tree before the caller continues with later recovery work.
                // Kill is asynchronous, so wait for the exact process under the same deadline;
                // otherwise a following schtasks /Delete can race a still-finishing /Create.
                Log.Warn($"{what} wait failed after process start — killing it: {ex.Message}");
                TryKill(process, what);
                await WaitForKilledProcessAsync(
                    process,
                    what,
                    deadline,
                    cancellationToken).ConfigureAwait(false);
                return ConsoleToolRunOutcome.Unknown;
            }

            if (process.ExitCode != 0)
            {
                Log.Warn($"{what} exited with {process.ExitCode}.");
                return ConsoleToolRunOutcome.Failed;
            }
            return ConsoleToolRunOutcome.Succeeded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"{what} failed: {ex.Message}");
            return processStarted
                ? ConsoleToolRunOutcome.Unknown
                : ConsoleToolRunOutcome.NotStarted;
        }
    }

    /// <summary>Runs a hidden console tool, captures its combined stdout/stderr,
    /// and returns the exit code — for tools whose OUTPUT matters (diskpart).
    /// A timeout kills the process tree and reports exit code -1. Never throws.</summary>
    /// <param name="exe">The executable to run.</param>
    /// <param name="arguments">Its command line.</param>
    /// <param name="timeoutMs">How long the tool may run.</param>
    public static async Task<(int ExitCode, string Output)> RunCapturedAsync(
        string exe, string arguments, int timeoutMs)
    {
        var what = $"{exe} {FirstToken(arguments)}";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
            });
            if (p is null)
            {
                Log.Warn($"{what} did not start.");
                return (-1, "");
            }
            // Read both streams concurrently — a tool that fills one pipe while
            // the caller waits on the other deadlocks otherwise.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warn($"{what} still running after {timeoutMs / 1000} s — killing it.");
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"{what} could not be killed: {ex.Message}");
                }
                // A read completes only once every writer handle on the pipe is
                // gone, so a kill that failed (or a child still holding the
                // inherited handle) would leave these awaits pending forever and
                // hang the caller. Bound the drain: the documented contract is
                // (-1, output), never a wait without end.
                var drain = Task.WhenAll(stdout, stderr);
                if (await Task.WhenAny(drain, Task.Delay(DrainTimeoutMs)) != drain)
                {
                    Log.Warn($"{what} output could not be drained after the kill.");
                    return (-1, "");
                }
                return (-1, $"{await stdout}{await stderr}");
            }
            var output = $"{await stdout}{await stderr}";
            if (p.ExitCode != 0)
            {
                Log.Warn($"{what} exited with {p.ExitCode}.");
            }
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            Log.Warn($"{what} failed: {ex.Message}");
            return (-1, "");
        }
    }

    /// <summary>Absolute System32 path for a Windows console tool. A relative exe
    /// name is resolved from the application directory first, which for a per-user
    /// install is user-writable — an elevated caller must never search it.</summary>
    public static string System32(string exeName) =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName);

    internal static string FirstToken(string arguments)
    {
        var space = arguments.IndexOf(' ');
        return space < 0 ? arguments : arguments[..space];
    }

    private static void TryKill(IConsoleToolProcess process, string what)
    {
        try
        {
            // Kill is safe to attempt unconditionally: an already-exited Process reports
            // InvalidOperationException, while querying HasExited first can itself fail and
            // must not turn a wait fault into an unretired side-effecting process.
            process.KillTree();
        }
        catch (InvalidOperationException)
        {
            // The process already exited before the kill reached it.
        }
        catch (Exception ex)
        {
            Log.Warn($"{what} could not be killed: {ex.Message}");
        }
    }

    private static async Task WaitForKilledProcessAsync(
        IConsoleToolProcess process,
        string what,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        using var exitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        exitCancellation.CancelAfter(remaining);
        try
        {
            await process.WaitForExitAsync(exitCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"{what} termination could not be confirmed before the shared deadline.");
            await DelayUntilDeadlineAsync(deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A second wait fault still must not let a later side-effecting command run beside this
            // process. Consume the remaining shared budget; its caller will then skip cleanup.
            Log.Warn($"{what} termination wait failed: {ex.Message}");
            await DelayUntilDeadlineAsync(deadline, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DelayUntilDeadlineAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining;
        while ((remaining = deadline - DateTimeOffset.UtcNow) > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class SystemConsoleToolProcess : IConsoleToolProcess
    {
        private readonly Process _process;

        internal SystemConsoleToolProcess(Process process)
        {
            _process = process;
        }

        public int ExitCode => _process.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.WaitForExitAsync(cancellationToken);

        public void KillTree() => _process.Kill(entireProcessTree: true);

        public void Dispose() => _process.Dispose();
    }
}
