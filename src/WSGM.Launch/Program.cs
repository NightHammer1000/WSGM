using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Launch;

internal static class Program
{
    private const string ChildArgument = "--medium-child";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LaunchReportTimeout = TimeSpan.FromMinutes(2);

    // Carried in the medium child's failure message so the elevated parent can tell
    // "de-elevation is impossible on this machine" (UAC off) from a transient error
    // and fail open instead of leaving the game unlaunchable.
    internal const string NoMediumTokenMarker = "UAC appears to be disabled";

    /// <summary>The failure the medium child reports when Task Scheduler could not
    /// give it a limited token, which is what UAC being switched off looks like.</summary>
    internal const string DisabledUacFailureMessage = NoMediumTokenMarker
        + "; Task Scheduler did not provide a medium-integrity token.";

    /// <summary>Decides whether the elevated parent may launch the target itself after
    /// the helper reported a failure. The marker alone is not enough: it arrives over an
    /// unauthenticated pipe, so anything able to connect could ask this elevated process
    /// to start an arbitrary command at high integrity — the exact outcome
    /// <c>--deelevate</c> exists to prevent. The parent's OWN token is the second,
    /// unspoofable condition.</summary>
    /// <param name="error">The failure text the medium-integrity helper reported.</param>
    /// <param name="hasLinkedLimitedToken">Whether this process holds a full split token
    /// with a linked limited token (<see cref="Elevation.HasLinkedLimitedToken"/>);
    /// <c>null</c> when the token could not be queried.</param>
    /// <returns><c>true</c> when the target may be launched as-is.</returns>
    // The parent reads its OWN token rather than the peer's on purpose. Identifying the
    // peer (GetNamedPipeClientProcessId + OpenProcess) races the genuine child, which
    // exits milliseconds after writing its report, and the pipe DACL grants the user SID
    // full control by device mandate (docs\elevation.md) so the peer can never be
    // authenticated anyway. The parent's token answers the only question that matters —
    // "could this machine have produced a limited token at all?" — and it answers it the
    // same way for the built-in Administrator and UAC-off cases the fail-open serves:
    // both report TokenElevationTypeDefault, so the launch still goes ahead. An
    // unqueryable token (null) also keeps failing open; only a confirmed split token,
    // where de-elevation genuinely was possible, refuses.
    internal static bool ShouldFailOpen(string error, bool? hasLinkedLimitedToken)
        => error.Contains(NoMediumTokenMarker, StringComparison.Ordinal)
            && hasLinkedLimitedToken != true;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 &&
                string.Equals(args[0], ChildArgument, StringComparison.OrdinalIgnoreCase))
            {
                return await RunMediumChildAsync(args[1]);
            }

            if (!CommandLine.TryParse(args, out var options, out var error))
            {
                LaunchLog.Error(error!);
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
                Console.Error.WriteLine(CommandLine.UsageText);
                return 64;
            }

            if (options.Help)
            {
                Console.WriteLine(CommandLine.UsageText);
                return 0;
            }
            if (options.Status)
            {
                return RunStatus(options);
            }
            if (options.Rescan)
            {
                return RunRescan(options);
            }

            return await RunWrapperAsync(options);
        }
        catch (Exception ex)
        {
            LaunchLog.Error($"Unhandled wrapper failure: {ex}");
            return 1;
        }
    }

    private static async Task<int> RunWrapperAsync(LaunchOptions options)
    {
        var elevated = Elevation.IsCurrentProcessElevated();
        LaunchLog.Info($"Steam wrapper invoked (elevated={elevated?.ToString() ?? "unknown"}, " +
                       $"deelevate={options.Deelevate}, inputLease={options.InputLease}, " +
                       $"inputLeaseInject={options.InputLeaseInject}, " +
                       $"target={Path.GetFileName(options.Command[0])}, " +
                       $"argumentCount={options.Command.Length - 1}).");

        // Without de-elevation the native wrapper is strictly better: it starts the
        // target suspended, assigns it to a job object and waits for the whole
        // process tree, so a launcher that spawns the real game and exits still
        // holds the lease. The de-elevation path cannot use it — it would create
        // the process from this elevated parent, which is the thing we are avoiding.
        if (options.AnyLease && !options.Deelevate)
        {
            return await RunLeaseWrappedAsync(options);
        }

        using var lease = options.AnyLease ? SteamInputLeaseHost.TryAcquire(options) : null;
        var payload = LaunchPayload.Capture(options.Command);
        return elevated == false
            ? await LaunchAndWaitAsync(payload)
            : await RunElevatedParentAsync(payload);
    }

    private static async Task<int> RunLeaseWrappedAsync(LaunchOptions options)
    {
        try
        {
            using var client = SteamInputLeaseHost.CreateClient(options);
            Console.WriteLine("Acquiring Steam Input block lease...");
            var run = client.RunWrapped(options.Command);
            Console.WriteLine("Game process tree exited; Steam Input unblocked.");
            LaunchLog.Info($"Steam Input lease wrapper finished with exit code {run.ExitCode}.");
            // Blocking is lifted either way (the lease is a pipe Windows closes with
            // this process), but if Steam was never asked to rediscover controllers it
            // will not see the pad again until then — and launch.log is the only place
            // that can be diagnosed from.
            if (run.Release.RecoveryRequested)
            {
                LaunchLog.Info($"Steam Input lease released ({run.Release.Recovery}).");
            }
            else
            {
                LaunchLog.Warn("Steam Input lease released, but Steam controller recovery did not run"
                    + $" ({run.Release.Recovery}: {run.Release.RecoveryMessage ?? "no reason reported"}).");
            }
            return unchecked((int)run.ExitCode);
        }
        catch (Exception ex)
        {
            // Fail open: a controller Steam refuses to let go of is a degraded
            // experience, but a game that never starts is a broken one. Safe to
            // relaunch because RunWrapped only reports failure when the target
            // never started — a release handshake that fails after the game has
            // exited returns its exit code instead (see run_wrapped).
            LaunchLog.Error($"Steam Input lease wrapper failed: {ex.Message}. Launching without it.");
            Console.Error.WriteLine($"Steam Input block unavailable: {ex.Message}");
            var payload = LaunchPayload.Capture(options.Command);
            return await LaunchAndWaitAsync(payload);
        }
    }

    private static int RunStatus(LaunchOptions options)
    {
        try
        {
            using var client = SteamInputLeaseHost.CreateClient(options);
            var status = client.GetStatus();
            Console.WriteLine(
                $"Payload active; leases={status.LeaseCount}, tracked HID handles={status.HidHandleCount}, " +
                $"handles revoked by last transition={status.LastRevokedHandleCount}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunRescan(LaunchOptions options)
    {
        try
        {
            using var client = SteamInputLeaseHost.CreateClient(options);
            var result = client.Rescan();
            Console.WriteLine(
                $"Requested Steam controller discovery (scan counter {result.ScanCountBefore} -> " +
                $"{result.ScanCountAfter}).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunElevatedParentAsync(LaunchPayload payload)
    {
        var pipeName = $"WSGM.Launch.{Environment.ProcessId}.{Guid.NewGuid():N}";
        // NOT CurrentUserOnly: this parent is elevated, and CurrentUserOnly grants
        // the pipe to the token's OWNER — for an elevated admin that is
        // BUILTIN\Administrators, a deny-only SID in the medium child's filtered
        // token, so the child's connect fails "Access to the path is denied"
        // (device-observed). Grant the real USER SID explicitly; it is enabled in
        // both the elevated parent's and the medium child's token.
        var pipeSecurity = new PipeSecurity();
        using (var identity = WindowsIdentity.GetCurrent())
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        using var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity);

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
        {
            LaunchLog.Error("Cannot determine the de-elevation helper path.");
            return 1;
        }

        var taskName = ScheduledTaskLauncher.Start(executablePath, pipeName);
        if (taskName is null)
        {
            return 1;
        }

        try
        {
            using var handshake = new CancellationTokenSource(HandshakeTimeout);
            await pipe.WaitForConnectionAsync(handshake.Token);
            // /Run has already created the helper process; deleting the task does
            // not terminate its running action and prevents stale task buildup.
            if (ScheduledTaskLauncher.Delete(taskName))
            {
                taskName = null;
            }

            // The child reports its readiness before anything else, and this read
            // has to come before the payload write: the protocol only stays
            // deadlock-free while exactly one side writes at a time. A child that
            // reports "no medium token" (UAC off) never reads the payload, so a
            // parent writing it concurrently would block against the child's flush
            // until the handshake expires — and the fail-open below, which is the
            // whole point of that report, would never be reached.
            var ready = await PipeProtocol.ReadInt32Async(pipe, handshake.Token);
            if (ready != 1)
            {
                var reason = ready == 0
                    ? await PipeProtocol.ReadStringAsync(pipe, 64 * 1024, handshake.Token)
                    : $"invalid readiness status {ready}";
                LaunchLog.Error($"Medium-integrity helper is not usable: {reason}");
                return await FailOpenOrGiveUpAsync(reason, payload);
            }

            await payload.WriteAsync(pipe, handshake.Token);
            await pipe.FlushAsync(handshake.Token);

            // Process creation is deliberately outside the readiness/payload deadline. On-access
            // scanning of a large executable can exceed 20 seconds without meaning the helper is
            // wedged; abandoning the pipe then would make the child kill a correctly started game.
            using var launchReport = new CancellationTokenSource(LaunchReportTimeout);
            var started = await PipeProtocol.ReadInt32Async(pipe, launchReport.Token);
            if (started == 0)
            {
                var error = await PipeProtocol.ReadStringAsync(pipe, 64 * 1024, launchReport.Token);
                LaunchLog.Error($"Medium-integrity launch failed: {error}");
                return await FailOpenOrGiveUpAsync(error, payload);
            }
            if (started != 1)
            {
                LaunchLog.Error($"Medium-integrity helper returned invalid status {started}.");
                return 1;
            }

            var processId = await PipeProtocol.ReadInt32Async(pipe, launchReport.Token);
            LaunchLog.Info($"Medium-integrity target started (pid {processId}); waiting for exit.");
            // No timeout after launch: Steam expects its launch-option wrapper to
            // remain alive for the entire game/emulator lifetime.
            var exitCode = await PipeProtocol.ReadInt32Async(pipe, CancellationToken.None);
            LaunchLog.Info($"Medium-integrity target pid {processId} exited with {exitCode}.");
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            LaunchLog.Error("Timed out waiting for the medium-integrity helper.");
            return 1;
        }
        catch (Exception ex)
        {
            LaunchLog.Error($"Medium-integrity helper communication failed: {ex.Message}");
            return 1;
        }
        finally
        {
            ScheduledTaskLauncher.Delete(taskName);
        }
    }

    /// <summary>Launches the target as-is when the medium child reported that
    /// de-elevation is impossible on this machine, and gives up otherwise.</summary>
    private static async Task<int> FailOpenOrGiveUpAsync(string error, LaunchPayload payload)
    {
        // Fail open, on the same rule the lease follows: a game that never starts
        // is a broken one. With UAC switched off entirely there is no limited token
        // for the scheduled task to hand out, so de-elevation is impossible on this
        // machine and the game would simply never run. Launch it the way it would
        // have run without the wrapper.
        if (!error.Contains(NoMediumTokenMarker, StringComparison.Ordinal))
        {
            return 1;
        }

        // The marker is the peer's word, so it decides nothing on its own: confirm
        // against this process's own token that no limited token existed to hand out.
        // Checking the marker first keeps the token query off the common path and
        // leaves the non-marker outcome byte-identical to what it always was.
        var hasLinkedLimitedToken = Elevation.HasLinkedLimitedToken();
        if (!ShouldFailOpen(error, hasLinkedLimitedToken))
        {
            LaunchLog.Error(
                "Refusing to launch the game from the elevated wrapper: the helper reported " +
                $"\"{NoMediumTokenMarker}\", but this process's TOKEN_ELEVATION_TYPE is " +
                "TokenElevationTypeFull (a full split token WITH a linked limited token, " +
                $"elevated={Elevation.IsCurrentProcessElevated()?.ToString() ?? "unknown"}), so " +
                "de-elevation was possible and the report cannot be trusted.");
            Console.Error.WriteLine(
                "De-elevation failed and the reported reason does not match this machine, so the "
                + "game was not started elevated. Remove --deelevate from this game's launch "
                + "options if you want it to run without de-elevation.");
            return 1;
        }

        Console.Error.WriteLine(
            "De-elevation is unavailable because UAC is disabled; starting the game as-is.");
        return await LaunchAndWaitAsync(payload);
    }

    private static async Task<int> RunMediumChildAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.None);
        var launchResponseSent = false;
        try
        {
            using var handshake = new CancellationTokenSource(HandshakeTimeout);
            await pipe.ConnectAsync(handshake.Token);
            if (Elevation.IsCurrentProcessElevated() != false)
            {
                // Marker text, matched by the parent to fail open: with UAC disabled
                // every token is a full one, so no amount of retrying produces a
                // medium-integrity child on this machine.
                LaunchLog.Error(DisabledUacFailureMessage);
                await WriteLaunchFailureAsync(pipe, DisabledUacFailureMessage, handshake.Token);
                return 1;
            }

            // Readiness first, and the parent reads it before it sends anything:
            // the failure above takes the same slot, so the two sides never write
            // at the same time and a report the parent cannot receive is impossible.
            await PipeProtocol.WriteInt32Async(pipe, 1, handshake.Token);
            await pipe.FlushAsync(handshake.Token);

            var payload = await LaunchPayload.ReadAsync(pipe, handshake.Token);

            using var process = Start(payload);
            if (process is null)
            {
                await WriteLaunchFailureAsync(
                    pipe,
                    "Process.Start returned no process.",
                    CancellationToken.None);
                return 1;
            }

            // Track the whole tree, not just the process Steam's command names: a
            // launcher that spawns the real game and exits would otherwise end this
            // child seconds in, which releases the elevated parent's Steam Input
            // lease mid-session and tells Steam the game stopped.
            using var job = JobObject.TryCapture(process.Handle);
            if (job is null)
            {
                LaunchLog.Error(
                    $"Target pid {process.Id} could not be captured before wrapper publication; "
                        + "stopping it rather than running an untracked game tree.");
                StopTargetTree(process, job);
                await WaitForExitBoundedAsync(process).ConfigureAwait(false);
                await WriteLaunchFailureAsync(
                    pipe,
                    "The target process tree could not be captured safely.",
                    CancellationToken.None).ConfigureAwait(false);
                return 1;
            }

            // The handshake deadline covers connecting, readiness and the payload
            // read, but must not cover this response: a slow CreateProcess (an
            // on-access scan of a large game image) would otherwise cancel the
            // report and leave the game running with no wrapper — the parent times
            // out, releases the Steam Input lease mid-session and tells Steam the
            // game stopped. If the report cannot be delivered at all, the tree is
            // stopped so nothing survives unwrapped.
            try
            {
                await PipeProtocol.WriteInt32Async(pipe, 1, CancellationToken.None);
                await PipeProtocol.WriteInt32Async(pipe, process.Id, CancellationToken.None);
                await pipe.FlushAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LaunchLog.Error($"Could not report the started target pid {process.Id} to the Steam " +
                                $"wrapper: {ex.Message}; stopping its process tree.");
                StopTargetTree(process, job);
                await WaitForExitBoundedAsync(process).ConfigureAwait(false);
                return 1;
            }
            launchResponseSent = true;
            LaunchLog.Info($"Launched {Path.GetFileName(payload.Arguments[0])} at medium integrity " +
                              $"(pid {process.Id}); preserving Steam wrapper lifetime" +
                              " for its process tree.");

            using var disconnectCancellation = new CancellationTokenSource();
            var parentDisconnected = WaitForParentDisconnectAsync(pipe, disconnectCancellation.Token);
            using var treeCancellation = new CancellationTokenSource();
            var targetFinished = WaitForTreeAsync(process, job, treeCancellation.Token);
            var completed = await Task.WhenAny(targetFinished, parentDisconnected);
            if (completed == parentDisconnected)
            {
                LaunchLog.Info($"Steam wrapper exited before target pid {process.Id}; stopping its process tree.");
                treeCancellation.Cancel();
                StopTargetTree(process, job);
                await WaitForExitBoundedAsync(process).ConfigureAwait(false);
                return 1;
            }

            await targetFinished.ConfigureAwait(false);

            disconnectCancellation.Cancel();
            try { await parentDisconnected; } catch (OperationCanceledException) { }
            await PipeProtocol.WriteInt32Async(pipe, process.ExitCode, CancellationToken.None);
            await pipe.FlushAsync(CancellationToken.None);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            LaunchLog.Error($"Medium-integrity child failed: {ex.Message}");
            if (!launchResponseSent && pipe.IsConnected)
            {
                try
                {
                    await WriteLaunchFailureAsync(pipe, ex.Message, CancellationToken.None);
                }
                catch (Exception reportEx)
                {
                    LaunchLog.Error($"Could not report the failure to the Steam wrapper: {reportEx.Message}");
                }
            }
            return 1;
        }
    }

    /// <summary>Ends the target and everything it spawned, so nothing keeps running
    /// once the wrapper Steam is watching can no longer track it.</summary>
    private static void StopTargetTree(Process process, JobObject? job)
    {
        // The job reaches descendants whose intermediate parent already exited,
        // which Kill(entireProcessTree) cannot.
        if (job?.TerminateTree() == true)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            LaunchLog.Error($"Could not stop the target tree of pid {process.Id}: {ex.Message}");
        }
    }

    private static async Task<int> LaunchAndWaitAsync(LaunchPayload payload)
    {
        using var process = Start(payload);
        if (process is null)
        {
            return 1;
        }
        using var job = JobObject.TryCapture(process.Handle);
        if (job is null)
        {
            LaunchLog.Error(
                $"Target pid {process.Id} could not be captured; stopping it rather than "
                    + "running an untracked game tree.");
            StopTargetTree(process, job);
            await WaitForExitBoundedAsync(process).ConfigureAwait(false);
            return 1;
        }
        LaunchLog.Info($"Wrapper already has medium integrity; target started directly (pid {process.Id}).");
        await WaitForTreeAsync(process, job, CancellationToken.None);
        return process.ExitCode;
    }

    /// <summary>Waits for the started process AND anything it spawned, so a game
    /// behind a launcher keeps the wrapper (and with it Steam's idea of a running
    /// game, and any held Steam Input lease) alive for its real lifetime.</summary>
    private static async Task WaitForTreeAsync(
        Process process, JobObject job, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await job.WaitUntilEmptyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            StopTargetTree(process, job);
            await WaitForExitBoundedAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WaitForExitBoundedAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LaunchLog.Error($"Target pid {process.Id} did not exit within five seconds after termination.");
        }
        catch (InvalidOperationException)
        {
            // The process already exited before a wait handle could be opened.
        }
    }

    internal static Process? Start(LaunchPayload payload)
    {
        if (payload.Arguments.Length == 0)
        {
            throw new InvalidDataException("The launch payload contains no target command.");
        }

        var workingDirectory = Directory.Exists(payload.WorkingDirectory)
            ? payload.WorkingDirectory
            : SafeTargetDirectory(payload.Arguments[0]);
        var target = payload.Arguments[0];
        if (!Path.IsPathFullyQualified(target))
        {
            var candidate = Path.Combine(workingDirectory, target);
            if (File.Exists(candidate))
            {
                target = candidate;
            }
        }

        var startInfo = new ProcessStartInfo(target)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        for (var i = 1; i < payload.Arguments.Length; i++)
        {
            startInfo.ArgumentList.Add(payload.Arguments[i]);
        }

        // Task Scheduler supplies a clean user environment, not Steam's dynamic
        // SteamAppId/GameId variables. Recreate the elevated wrapper's environment
        // so the target observes the same launch contract as a direct Steam child.
        startInfo.Environment.Clear();
        foreach (var pair in payload.EnvironmentVariables)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        // De-elevation is the whole point, so run the target at THIS (medium)
        // integrity even when it carries a RUNASADMIN AppCompat flag or a
        // highestAvailable/requireAdministrator manifest. Without this a medium
        // CreateProcess fails ERROR_ELEVATION_REQUIRED (740, device-observed) —
        // UseShellExecute=false cannot elevate. RunAsInvoker tells the AppCompat
        // engine to drop the elevation requirement and run as the caller. Set it
        // both on this process and in the child's environment so the shim sees it
        // whichever it reads; prepend so any existing layer is preserved.
        startInfo.Environment.TryGetValue("__COMPAT_LAYER", out var existingLayer);
        var compatLayer = string.IsNullOrEmpty(existingLayer)
            ? "RunAsInvoker"
            : $"RunAsInvoker {existingLayer}";
        startInfo.Environment["__COMPAT_LAYER"] = compatLayer;
        Environment.SetEnvironmentVariable("__COMPAT_LAYER", compatLayer);
        return Process.Start(startInfo);
    }

    private static string SafeTargetDirectory(string target)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(target)) ?? Environment.CurrentDirectory;
        }
        catch
        {
            return Environment.CurrentDirectory;
        }
    }

    private static async Task WriteLaunchFailureAsync(
        Stream pipe,
        string error,
        CancellationToken cancellationToken)
    {
        await PipeProtocol.WriteInt32Async(pipe, 0, cancellationToken);
        await PipeProtocol.WriteStringAsync(pipe, error, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }

    private static async Task WaitForParentDisconnectAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        try
        {
            while (await pipe.ReadAsync(buffer, cancellationToken) != 0)
            {
                // The parent deliberately sends no bytes after the payload. Ignore
                // anything unexpected and continue monitoring its pipe lifetime.
            }
        }
        catch (IOException)
        {
            // A broken pipe is the expected signal when Steam kills the wrapper.
        }
    }
}
