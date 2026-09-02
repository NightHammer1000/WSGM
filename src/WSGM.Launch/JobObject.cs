using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Launch;

/// <summary>
/// A Windows job object holding the launched target and everything it spawns, so
/// the wrapper's lifetime tracks the whole process tree instead of the process it
/// started directly.
/// </summary>
/// <remarks>
/// Games fronted by a launcher (emulators, store front-ends, some anti-cheat
/// bootstrappers) exit their root process seconds in and leave the real game
/// running. Waiting on that root alone ends the wrapper early: Steam marks the game
/// as stopped and, with <c>--input-lease</c>, the Steam Input block is released
/// mid-session. The native lease wrapper already solves this by starting the target
/// suspended and job-assigning it before resume; the medium-integrity child cannot
/// (it starts the process through <c>Process.Start</c> to keep Steam's environment
/// and the RunAsInvoker layer), so it assigns immediately after start. Descendants
/// created in that sub-millisecond window are not captured — a launcher takes far
/// longer than that to spawn anything.
/// </remarks>
internal sealed partial class JobObject : IDisposable
{
    private const uint JobObjectBasicAccountingInformation = 1;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private nint _handle;

    private JobObject(nint handle) => _handle = handle;

    /// <summary>Creates a job and assigns an already-started process to it.
    /// Returns null when the platform refuses either step. Callers must terminate the freshly
    /// started target and fail the launch; continuing without tree ownership would release Steam
    /// state as soon as a bootstrapper exits.</summary>
    /// <param name="processHandle">Handle of the freshly started process.</param>
    internal static JobObject? TryCapture(nint processHandle)
    {
        var handle = CreateJobObjectW(0, null);
        if (handle == 0)
        {
            LaunchLog.Error($"Could not create a job object (error {Marshal.GetLastPInvokeError()}); "
                + "the wrapper will only track the process it started.");
            return null;
        }
        if (!AssignProcessToJobObject(handle, processHandle))
        {
            LaunchLog.Error($"Could not assign the target to a job object "
                + $"(error {Marshal.GetLastPInvokeError()}); the wrapper will only track the "
                + "process it started.");
            CloseHandle(handle);
            return null;
        }
        return new JobObject(handle);
    }

    /// <summary>Completes once no process in the job is left running.</summary>
    /// <param name="cancellationToken">Stops waiting (the caller is shutting down).</param>
    internal async Task WaitUntilEmptyAsync(CancellationToken cancellationToken)
    {
        // Polled, like the native wrapper: a job object has no "became empty"
        // waitable state without an IO completion port, and the resolution here
        // only decides how quickly the wrapper notices a finished game.
        while (!cancellationToken.IsCancellationRequested && ActiveProcesses() > 0)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Ends every process still in the job.</summary>
    internal bool TerminateTree()
    {
        if (_handle != 0 && !TerminateJobObject(_handle, 1))
        {
            LaunchLog.Error($"Could not terminate the job object (error {Marshal.GetLastPInvokeError()}).");
            return false;
        }

        return _handle != 0;
    }

    private uint ActiveProcesses()
    {
        if (_handle == 0)
        {
            return 0;
        }
        var info = default(JobObjectBasicAccountingInfo);
        if (!QueryInformationJobObject(
                _handle,
                JobObjectBasicAccountingInformation,
                ref info,
                (uint)Marshal.SizeOf<JobObjectBasicAccountingInfo>(),
                0))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not query the wrapper's target job object.");
        }

        return info.ActiveProcesses;
    }

    /// <summary>Closes the job handle. The job is deliberately created without
    /// kill-on-close, so anything still running outlives the wrapper instead of
    /// dying with it.</summary>
    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
            _handle = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInfo
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(
        nint job, uint infoClass, ref JobObjectBasicAccountingInfo info, uint length, nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
