using System.Runtime.InteropServices;

namespace WSGM.LogonService.Interop;

/// <summary>Flat Win32 surface for the logon service: SCM plumbing, WTS session
/// queries, token handling, and CreateProcessAsUser. The service keeps this boundary
/// source-generated and free of process-wide COM state.</summary>
internal static partial class NativeMethods
{
    // ---- Service control manager: hosting ----
    internal const uint ServiceWin32OwnProcess = 0x00000010;
    internal const uint ServiceAutoStart = 0x00000002;
    internal const uint ServiceErrorNormal = 0x00000001;
    internal const uint ServiceControlStop = 0x00000001;
    internal const uint ServiceControlInterrogate = 0x00000004;
    internal const uint ServiceControlShutdown = 0x00000005;
    internal const uint ServiceControlSessionChange = 0x0000000E;
    internal const uint ServiceAcceptStop = 0x00000001;
    internal const uint ServiceAcceptShutdown = 0x00000004;
    internal const uint ServiceAcceptSessionChange = 0x00000080;
    internal const uint ServiceStopped = 0x00000001;
    internal const uint ServiceStartPending = 0x00000002;
    internal const uint ServiceStopPending = 0x00000003;
    internal const uint ServiceRunning = 0x00000004;
    internal const int NoError = 0;
    internal const int ErrorCallNotImplemented = 120;
    internal const int ErrorFailedServiceControllerConnect = 1063;
    internal const int ErrorServiceAlreadyRunning = 1056;
    internal const int ErrorServiceNotActive = 1062;
    internal const uint WtsSessionLogon = 5;
    internal const uint WtsSessionLogoff = 6;

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ServiceTableEntryW
    {
        public nint lpServiceName;
        public delegate* unmanaged<uint, nint, void> lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    /// <summary>WTSSESSION_NOTIFICATION — lParam of SERVICE_CONTROL_SESSIONCHANGE.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WtsSessionNotification
    {
        public uint cbSize;
        public uint dwSessionId;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool StartServiceCtrlDispatcherW(ServiceTableEntryW* lpServiceStartTable);

    [LibraryImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial nint RegisterServiceCtrlHandlerExW(
        string lpServiceName, delegate* unmanaged<uint, uint, nint, nint, int> lpHandlerProc, nint lpContext);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetServiceStatus(nint hServiceStatus, ref ServiceStatus lpServiceStatus);

    // ---- Service control manager: install/uninstall ----
    internal const uint ScManagerAllAccess = 0x000F003F;
    internal const uint ServiceAllAccess = 0x000F01FF;
    internal const int ServiceConfigDescription = 1;
    internal const int ServiceConfigFailureActions = 2;
    internal const int ScActionRestart = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScAction
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceFailureActionsW
    {
        public uint dwResetPeriod;
        public nint lpRebootMsg;
        public nint lpCommand;
        public uint cActions;
        public nint lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceDescriptionW
    {
        public nint lpDescription;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenServiceW(nint hSCManager, string lpServiceName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateServiceW(
        nint hSCManager, string lpServiceName, string lpDisplayName, uint dwDesiredAccess,
        uint dwServiceType, uint dwStartType, uint dwErrorControl, string lpBinaryPathName,
        string? lpLoadOrderGroup, nint lpdwTagId, string? lpDependencies,
        string? lpServiceStartName, string? lpPassword);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeServiceConfigW(
        nint hService, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string? lpBinaryPathName, string? lpLoadOrderGroup, nint lpdwTagId, string? lpDependencies,
        string? lpServiceStartName, string? lpPassword, string? lpDisplayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeServiceConfig2W(nint hService, int dwInfoLevel, nint lpInfo);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StartServiceW(nint hService, uint dwNumServiceArgs, nint lpServiceArgVectors);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ControlService(nint hService, uint dwControl, out ServiceStatus lpServiceStatus);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceStatus(nint hService, out ServiceStatus lpServiceStatus);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteService(nint hService);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint hSCObject);

    // ---- WTS sessions ----
    internal const int WtsActive = 0; // WTS_CONNECTSTATE_CLASS.WTSActive
    internal const int WtsInfoClassSessionInfo = 24; // WTSSessionInfo -> WTSINFOW
    internal const int WtsInfoClassUserName = 5;
    internal const int WtsInfoClassDomainName = 7;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WtsSessionInfoW
    {
        public uint SessionId;
        public nint pWinStationName;
        public int State;
    }

    /// <summary>WTSINFOW — only LogonTime and State are consumed; the fixed-size
    /// string fields exist to keep the native layout (natural alignment pads the
    /// LARGE_INTEGER block exactly like the C compiler does).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct WtsInfoW
    {
        public int State;
        public uint SessionId;
        public uint IncomingBytes;
        public uint OutgoingBytes;
        public uint IncomingFrames;
        public uint OutgoingFrames;
        public uint IncomingCompressedBytes;
        public uint OutgoingCompressedBytes;
        public fixed char WinStationName[32];
        public fixed char Domain[17];
        public fixed char UserName[21];
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WtsProcessInfoW
    {
        public uint SessionId;
        public uint ProcessId;
        public nint pProcessName;
        public nint pUserSid;
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQueryUserToken(uint sessionId, out nint phToken);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSEnumerateSessionsW(
        nint hServer, uint reserved, uint version, out nint ppSessionInfo, out uint pCount);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQuerySessionInformationW(
        nint hServer, uint sessionId, int wtsInfoClass, out nint ppBuffer, out uint pBytesReturned);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateProcessesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSEnumerateProcessesW(
        nint hServer, uint reserved, uint version, out nint ppProcessInfo, out uint pCount);

    [LibraryImport("wtsapi32.dll")]
    internal static partial void WTSFreeMemory(nint pMemory);

    // ---- Tokens ----
    internal const int TokenSessionIdClass = 12;
    internal const int TokenElevationTypeClass = 18;
    internal const int TokenLinkedTokenClass = 19;
    internal const int TokenElevationTypeDefault = 1;
    internal const int TokenElevationTypeFull = 2;
    internal const int TokenElevationTypeLimited = 3;
    internal const uint MaximumAllowed = 0x02000000;
    internal const int SecurityImpersonation = 2;
    internal const int TokenPrimary = 1;

    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformationDword(
        nint tokenHandle, int tokenInformationClass, out int tokenInformation, uint tokenInformationLength, out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformationHandle(
        nint tokenHandle, int tokenInformationClass, out nint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetTokenInformation(
        nint tokenHandle, int tokenInformationClass, ref uint tokenInformation, uint tokenInformationLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(
        nint hExistingToken, uint dwDesiredAccess, nint lpTokenAttributes,
        int impersonationLevel, int tokenType, out nint phNewToken);

    // ---- Launch into the session ----
    internal const uint CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoW
    {
        public uint cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessAsUserW(
        nint hToken, string? lpApplicationName, string? lpCommandLine,
        nint lpProcessAttributes, nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags,
        nint lpEnvironment, string? lpCurrentDirectory,
        ref StartupInfoW lpStartupInfo, out ProcessInformation lpProcessInformation);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateEnvironmentBlock(out nint lpEnvironment, nint hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyEnvironmentBlock(nint lpEnvironment);

    [LibraryImport("userenv.dll", EntryPoint = "GetUserProfileDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetUserProfileDirectoryW(nint hToken, [Out] char[]? lpProfileDir, ref uint lpcchSize);

    // ---- Handles / waits ----
    internal const uint Infinite = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);
}
