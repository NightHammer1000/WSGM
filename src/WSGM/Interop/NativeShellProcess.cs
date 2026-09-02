using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Interop;

/// <summary>Owns the Win32 process inspection and parent-process launch primitives used to
/// preserve normal Explorer shell process semantics across game-mode transitions.</summary>
internal static partial class NativeShellProcess
{
    private const uint ProcessCreateProcess = 0x0080;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const nuint ProcThreadAttributeParentProcess = 0x00020000;
    private const uint WaitObject0 = 0;

    /// <summary>Inspects a process without retaining a handle.</summary>
    /// <param name="processId">Process identifier to inspect.</param>
    /// <returns>The values Windows exposed, including explicit unknown states.</returns>
    internal static NativeShellProcessInfo Inspect(uint processId)
    {
        nint process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return NativeShellProcessInfo.Unavailable(processId, Marshal.GetLastPInvokeError());
        }

        try
        {
            string? imagePath = QueryImagePath(process, out int imageError);
            bool sessionKnown = ProcessIdToSessionId(processId, out uint session);
            int sessionError = sessionKnown ? 0 : Marshal.GetLastPInvokeError();
            int? sessionId = sessionKnown ? checked((int)session) : null;
            bool jobKnown = IsProcessInJob(process, 0, out bool inJob);
            int jobError = jobKnown ? 0 : Marshal.GetLastPInvokeError();
            NativeJobMembership jobMembership = jobKnown
                ? inJob ? NativeJobMembership.InJob : NativeJobMembership.NotInJob
                : NativeJobMembership.Unknown;
            NativeIntegrityLevel integrity = QueryIntegrity(process, out int integrityError);
            return new NativeShellProcessInfo(
                processId,
                imagePath,
                sessionId,
                integrity,
                jobMembership,
                new NativeShellProcessErrors(0, imageError, sessionError, integrityError, jobError));
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>Reads a process's full image path, opening it with the limited query right.
    /// Null when the process cannot be opened or queried — ordinary for an elevated or
    /// protected process. The one shared image-path primitive for every caller that only
    /// needs the path, not the full inspection.</summary>
    internal static string? TryGetImagePath(uint processId)
    {
        nint process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return null;
        }
        try
        {
            return QueryImagePath(process, out _);
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>Opens the process and token rights required to use a verified shell as a
    /// designated process-creation parent.</summary>
    /// <param name="processId">Verified taskbar-owner process identifier.</param>
    /// <param name="parent">Owned launch-parent handle on success.</param>
    /// <param name="error">Win32 error on failure.</param>
    /// <returns>Whether both process and token handles were opened.</returns>
    internal static bool TryOpenLaunchParent(
        uint processId,
        out NativeShellLaunchParent? parent,
        out int error)
    {
        parent = null;
        nint process = NativeMethods.OpenProcess(
            ProcessCreateProcess | ProcessQueryLimitedInformation,
            false,
            processId);
        if (process == 0)
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        if (!NativeMethods.OpenProcessToken(process, TokenQuery | TokenDuplicate, out nint token))
        {
            error = Marshal.GetLastPInvokeError();
            NativeMethods.CloseHandle(process);
            return false;
        }

        parent = new NativeShellLaunchParent(processId, process, token);
        error = 0;
        return true;
    }

    /// <summary>Starts a fixed executable with the designated process as its creation parent and
    /// with the designated parent's user environment.</summary>
    /// <param name="parent">The retained canonical shell parent.</param>
    /// <param name="applicationPath">Absolute executable path.</param>
    /// <param name="commandLine">Mutable Windows command line including argv[0].</param>
    /// <param name="workingDirectory">Absolute working directory.</param>
    /// <param name="process">Owned handle for the exact created process on success.</param>
    /// <param name="error">Win32 error on failure.</param>
    /// <returns>Whether process creation succeeded.</returns>
    internal static unsafe bool TryStartWithParent(
        NativeShellLaunchParent parent,
        string applicationPath,
        string commandLine,
        string workingDirectory,
        out NativeShellChildProcess? process,
        out int error)
    {
        ArgumentNullException.ThrowIfNull(parent);
        process = null;
        error = 0;
        nint environment = 0;
        nint attributeList = 0;
        bool attributeListInitialized = false;

        try
        {
            if (!CreateEnvironmentBlock(out environment, parent.TokenHandle, false))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }

            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref attributeListSize);
            if (attributeListSize == 0)
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }

            attributeList = (nint)NativeMemory.Alloc(attributeListSize);
            if (attributeList == 0)
            {
                error = 8; // ERROR_NOT_ENOUGH_MEMORY
                return false;
            }

            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }
            attributeListInitialized = true;

            nint parentHandle = parent.ProcessHandle;
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributeParentProcess,
                    (nint)(&parentHandle),
                    (nuint)sizeof(nint),
                    0,
                    0))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }

            StartupInfoEx startup = new()
            {
                StartupInfo = new StartupInfo
                {
                    Size = checked((uint)sizeof(StartupInfoEx)),
                },
                AttributeList = attributeList,
            };

            char[] mutableCommandLine = [.. commandLine, '\0'];
            fixed (char* application = applicationPath)
            fixed (char* command = mutableCommandLine)
            fixed (char* directory = workingDirectory)
            {
                if (!CreateProcessW(
                        application,
                        command,
                        0,
                        0,
                        false,
                        CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                        environment,
                        directory,
                        in startup,
                        out ProcessInformation processInformation))
                {
                    error = Marshal.GetLastPInvokeError();
                    return false;
                }

                NativeMethods.CloseHandle(processInformation.Thread);
                process = new NativeShellChildProcess(
                    processInformation.ProcessId,
                    processInformation.Process);
                return true;
            }
        }
        finally
        {
            if (attributeListInitialized)
            {
                DeleteProcThreadAttributeList(attributeList);
            }
            if (attributeList != 0)
            {
                NativeMemory.Free((void*)attributeList);
            }
            if (environment != 0)
            {
                DestroyEnvironmentBlock(environment);
            }
        }
    }

    private static string? QueryImagePath(nint process, out int error)
    {
        char[] buffer = new char[32768];
        uint length = checked((uint)buffer.Length);
        if (NativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref length))
        {
            error = 0;
            return new string(buffer, 0, checked((int)length));
        }

        error = Marshal.GetLastPInvokeError();
        return null;
    }

    private static unsafe NativeIntegrityLevel QueryIntegrity(nint process, out int error)
    {
        if (!NativeMethods.OpenProcessToken(process, TokenQuery, out nint token))
        {
            error = Marshal.GetLastPInvokeError();
            return NativeIntegrityLevel.Unknown;
        }

        try
        {
            _ = NativeMethods.GetTokenInformation(token, TokenIntegrityLevel, (nint)0, 0, out uint required);
            if (required < (uint)sizeof(nint))
            {
                error = Marshal.GetLastPInvokeError();
                return NativeIntegrityLevel.Unknown;
            }

            void* buffer = NativeMemory.Alloc(required);
            if (buffer == null)
            {
                error = 8; // ERROR_NOT_ENOUGH_MEMORY
                return NativeIntegrityLevel.Unknown;
            }

            try
            {
                if (!NativeMethods.GetTokenInformation(token, TokenIntegrityLevel, (nint)buffer, required, out _))
                {
                    error = Marshal.GetLastPInvokeError();
                    return NativeIntegrityLevel.Unknown;
                }

                nint sid = *(nint*)buffer;
                if (sid == 0)
                {
                    error = 13; // ERROR_INVALID_DATA
                    return NativeIntegrityLevel.Unknown;
                }

                byte subAuthorityCount = *(((byte*)sid) + 1);
                if (subAuthorityCount == 0)
                {
                    error = 13; // ERROR_INVALID_DATA
                    return NativeIntegrityLevel.Unknown;
                }

                uint rid = *(uint*)(((byte*)sid) + 8 + ((subAuthorityCount - 1) * sizeof(uint)));
                error = 0;
                return rid switch
                {
                    < 0x1000 => NativeIntegrityLevel.Untrusted,
                    < 0x2000 => NativeIntegrityLevel.Low,
                    < 0x3000 => NativeIntegrityLevel.Medium,
                    < 0x4000 => NativeIntegrityLevel.High,
                    _ => NativeIntegrityLevel.System,
                };
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Size;
        internal nint Reserved;
        internal nint Desktop;
        internal nint Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2;
        internal nint Reserved2Pointer;
        internal nint StandardInput;
        internal nint StandardOutput;
        internal nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal nint Process;
        internal nint Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(
        nint process,
        nint job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(
        out nint environment,
        nint token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(nint environment);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        nint attributeList,
        int attributeCount,
        uint flags,
        ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(
        char* applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        char* currentDirectory,
        in StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(nint process, uint exitCode);

    /// <summary>Waits for one owned process handle without blocking the caller.</summary>
    internal static async Task<bool> WaitForExitAsync(
        nint processHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        uint milliseconds = timeout <= TimeSpan.Zero
            ? 0
            : checked((uint)Math.Min(timeout.TotalMilliseconds, uint.MaxValue - 1));
        uint result = await Task.Run(
            () => NativeMethods.WaitForSingleObject(processHandle, milliseconds),
            cancellationToken).ConfigureAwait(false);
        return result == WaitObject0;
    }

    /// <summary>Gets whether an owned process handle has signaled.</summary>
    internal static bool HasExited(nint processHandle) =>
        NativeMethods.WaitForSingleObject(processHandle, 0) == WaitObject0;

    /// <summary>Queries whether a terminal-services session is currently active. Recovery callers
    /// use this after owner loss so logoff never causes a replacement desktop to be launched.</summary>
    internal static bool IsSessionActive(int sessionId, out int error)
    {
        if (!WTSQuerySessionInformationW(
                0,
                checked((uint)sessionId),
                8, // WTSConnectState
                out nint buffer,
                out uint bytes))
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        try
        {
            if (bytes < sizeof(int))
            {
                error = 13; // ERROR_INVALID_DATA
                return false;
            }
            error = 0;
            return Marshal.ReadInt32(buffer) == 0; // WTSActive
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(
        nint server,
        uint sessionId,
        int informationClass,
        out nint buffer,
        out uint bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}

/// <summary>Process attributes relevant to accepting a normal desktop shell or launch owner.</summary>
internal readonly record struct NativeShellProcessInfo(
    uint ProcessId,
    string? ImagePath,
    int? SessionId,
    NativeIntegrityLevel Integrity,
    NativeJobMembership JobMembership,
    NativeShellProcessErrors Errors)
{
    /// <summary>Creates an unavailable inspection result.</summary>
    internal static NativeShellProcessInfo Unavailable(uint processId, int error) =>
        new(
            processId,
            null,
            null,
            NativeIntegrityLevel.Unknown,
            NativeJobMembership.Unknown,
            new NativeShellProcessErrors(error, 0, 0, 0, 0));
}

/// <summary>Exact Win32 failures produced by each independent process-inspection query.</summary>
internal readonly record struct NativeShellProcessErrors(
    int Open,
    int Image,
    int Session,
    int Integrity,
    int Job);

/// <summary>Windows mandatory integrity classification.</summary>
internal enum NativeIntegrityLevel
{
    /// <summary>The token could not be inspected.</summary>
    Unknown,
    /// <summary>Untrusted integrity.</summary>
    Untrusted,
    /// <summary>Low integrity.</summary>
    Low,
    /// <summary>Medium or medium-plus integrity.</summary>
    Medium,
    /// <summary>High integrity.</summary>
    High,
    /// <summary>System or protected integrity.</summary>
    System,
}

/// <summary>Tri-state process job membership; a failed query never becomes jobless.</summary>
internal enum NativeJobMembership
{
    /// <summary>Windows did not answer the query.</summary>
    Unknown,
    /// <summary>The process is not associated with a job.</summary>
    NotInJob,
    /// <summary>The process is associated with a job.</summary>
    InJob,
}

/// <summary>Retained native handles for a verified process-creation parent.</summary>
internal sealed class NativeShellLaunchParent : IDisposable
{
    private nint _processHandle;
    private nint _tokenHandle;

    /// <summary>Creates the owned handle pair.</summary>
    internal NativeShellLaunchParent(uint processId, nint processHandle, nint tokenHandle)
    {
        ProcessId = processId;
        _processHandle = processHandle;
        _tokenHandle = tokenHandle;
    }

    /// <summary>Gets the designated parent's process identifier.</summary>
    internal uint ProcessId { get; }

    /// <summary>Gets the retained process handle.</summary>
    internal nint ProcessHandle => _processHandle;

    /// <summary>Gets the retained token handle.</summary>
    internal nint TokenHandle => _tokenHandle;

    /// <inheritdoc />
    public void Dispose()
    {
        nint token = System.Threading.Interlocked.Exchange(ref _tokenHandle, 0);
        if (token != 0)
        {
            NativeMethods.CloseHandle(token);
        }

        nint process = System.Threading.Interlocked.Exchange(ref _processHandle, 0);
        if (process != 0)
        {
            NativeMethods.CloseHandle(process);
        }
    }
}

/// <summary>Owns the exact process handle returned by CreateProcessW so a failed anchor startup
/// can stop only the child it created and cannot act on a recycled process identifier.</summary>
internal sealed class NativeShellChildProcess : IDisposable
{
    private nint _processHandle;

    /// <summary>Creates an owned child-process handle.</summary>
    internal NativeShellChildProcess(uint processId, nint processHandle)
    {
        ProcessId = processId;
        _processHandle = processHandle;
    }

    /// <summary>Gets the created process identifier for diagnostics only.</summary>
    internal uint ProcessId { get; }

    /// <summary>Gets whether the exact created process has exited.</summary>
    internal bool HasExited => _processHandle == 0 || NativeShellProcess.HasExited(_processHandle);

    /// <summary>Waits boundedly for the exact created process to exit.</summary>
    internal Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        nint handle = _processHandle;
        return handle == 0
            ? Task.FromResult(true)
            : NativeShellProcess.WaitForExitAsync(handle, timeout, cancellationToken);
    }

    /// <summary>Terminates only the exact owned child. Used solely when anchor setup or its
    /// authenticated stop handshake failed before the child could be released normally.</summary>
    internal bool TryTerminate(out int error)
    {
        nint handle = _processHandle;
        if (handle == 0 || HasExited)
        {
            error = 0;
            return true;
        }
        if (NativeShellProcess.TerminateProcess(handle, 1))
        {
            error = 0;
            return true;
        }
        error = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        nint process = Interlocked.Exchange(ref _processHandle, 0);
        if (process != 0)
        {
            NativeMethods.CloseHandle(process);
        }
    }
}
