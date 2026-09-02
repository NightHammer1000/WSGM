using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using WSGM.Core;
using WSGM.LogonService.Interop;

namespace WSGM.LogonService;

/// <summary>Per-session launch state and the CreateProcessAsUser plumbing. One WSGM
/// launch per logon; a watchdog thread restores explorer if WSGM dies dirty in an
/// explorer-less session. All token work is legal here because the service runs as
/// SYSTEM (SeTcbPrivilege) — the linked-token route that fails with error 1346
/// from user land works fine from this side.</summary>
internal static class SessionLauncher
{
    /// <summary>Startup catch-up window: sessions logged on longer ago are stale.</summary>
    internal static readonly TimeSpan CatchUpWindow = TimeSpan.FromSeconds(60);

    private const int LaunchRetries = 5;

    /// <summary>WAIT_OBJECT_0 — anything else out of the watchdog's wait means the
    /// process state could not be observed.</summary>
    private const uint WaitObject0 = 0;

    private static readonly TimeSpan LaunchRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AnchorRecoveryGrace = TimeSpan.FromSeconds(5);

    private sealed class SessionState
    {
        public nint UserToken;
        public nint ProcessHandle;
        public uint ProcessId;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<uint, SessionState> Sessions = new();
    private static readonly HashSet<uint> InFlight = new();

    /// <summary>Handles a logon (live SESSIONCHANGE event: <paramref name="logonAge"/>
    /// null; startup catch-up: the measured age). Runs on a worker thread.</summary>
    internal static void OnSessionLogon(uint sessionId, TimeSpan? logonAge)
    {
        bool alreadyLaunched;
        lock (Gate)
        {
            // Claim the slot under the SAME lock as the check. A live
            // WTS_SESSION_LOGON and the startup catch-up sweep can process one
            // session concurrently; claiming only after the launch let both
            // observe "not launched yet", start two WSGM --boot processes and
            // leak the loser's user token plus its process handle.
            alreadyLaunched = Sessions.ContainsKey(sessionId) || !InFlight.Add(sessionId);
        }

        try
        {
            HandleLogon(sessionId, logonAge, alreadyLaunched);
        }
        finally
        {
            if (!alreadyLaunched)
            {
                lock (Gate)
                {
                    InFlight.Remove(sessionId);
                }
            }
        }
    }

    private static void HandleLogon(uint sessionId, TimeSpan? logonAge, bool alreadyLaunched)
    {
        if (!NativeMethods.WTSQueryUserToken(sessionId, out var userToken))
        {
            ServiceLog.Warn($"Session {sessionId}: WTSQueryUserToken failed (error {Marshal.GetLastWin32Error()}).");
            return;
        }

        var launched = false;
        try
        {
            var profile = GetUserProfileDirectory(userToken);
            var manifest = profile is null
                ? null
                : BootManifestStore.TryLoad(Path.Combine(profile, "AppData", "Local", "WSGM", BootManifestStore.FileName));
            if (manifest is not null && !File.Exists(manifest.ExePath))
            {
                ServiceLog.Warn($"Session {sessionId}: manifest exe missing ({manifest.ExePath}) — treating as no manifest.");
                manifest = null;
            }

            var action = LogonDecision.Decide(manifest, sessionActive: true, alreadyLaunched, logonAge, CatchUpWindow);
            ServiceLog.Info($"Session {sessionId} ({GetSessionUser(sessionId)}): manifest " +
                (manifest is null ? "absent/unusable" : $"enabled={manifest.GameModeBoot} elevate={manifest.Elevate} exe={manifest.ExePath}") +
                $" -> {action}.");
            if (action is not (LogonAction.Launch or LogonAction.LaunchElevated))
            {
                return;
            }

            var launchToken = userToken;
            var tokenKind = "user token";
            if (action == LogonAction.LaunchElevated)
            {
                launchToken = TryGetElevatedToken(userToken, sessionId, out tokenKind);
            }

            try
            {
                if (!TryLaunchWithRetries(launchToken, manifest!.ExePath, "--boot", sessionId,
                        out var hProcess, out var pid))
                {
                    return;
                }
                ServiceLog.Info($"Launching WSGM --boot into session {sessionId} ({tokenKind}) — pid {pid}.");

                var state = new SessionState { UserToken = userToken, ProcessHandle = hProcess, ProcessId = pid };
                lock (Gate)
                {
                    Sessions[sessionId] = state;
                }
                launched = true;

                var watchdog = new Thread(() => Watch(sessionId, state)) { IsBackground = true, Name = $"wsgm-watchdog-{sessionId}" };
                watchdog.Start();
            }
            finally
            {
                if (launchToken != userToken)
                {
                    NativeMethods.CloseHandle(launchToken);
                }
            }
        }
        finally
        {
            // The unlinked user token stays alive inside the session state (the
            // watchdog's explorer fallback needs it); close it only on skip paths.
            if (!launched)
            {
                NativeMethods.CloseHandle(userToken);
            }
        }
    }

    /// <summary>Clears one session's state on logoff. The handles belong to the
    /// watchdog thread, which may still be waiting on them — it closes them when
    /// the launched process exits.</summary>
    internal static void OnSessionLogoff(uint sessionId)
    {
        lock (Gate)
        {
            if (Sessions.Remove(sessionId))
            {
                ServiceLog.Info($"Session {sessionId} logoff — clearing state.");
            }
        }
    }

    /// <summary>Startup catch-up: an auto-start service can lose the race against an
    /// autologon — launch into any session that logged on within the window and has
    /// no WSGM yet. Sessions the service already knows are skipped by the decision.</summary>
    internal static void CatchUpExistingSessions()
    {
        if (!NativeMethods.WTSEnumerateSessionsW(0, 0, 1, out var pSessions, out var count))
        {
            ServiceLog.Warn($"Startup catch-up: WTSEnumerateSessionsW failed (error {Marshal.GetLastWin32Error()}).");
            return;
        }
        try
        {
            var size = Marshal.SizeOf<NativeMethods.WtsSessionInfoW>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<NativeMethods.WtsSessionInfoW>(pSessions + i * size);
                if (info.State != NativeMethods.WtsActive)
                {
                    continue;
                }
                var logonAge = GetLogonAge(info.SessionId);
                if (logonAge is null)
                {
                    continue;
                }
                ServiceLog.Info($"Startup catch-up: session {info.SessionId} logged on {(int)logonAge.Value.TotalSeconds} s ago.");
                OnSessionLogon(info.SessionId, logonAge);
            }
        }
        finally
        {
            NativeMethods.WTSFreeMemory(pSessions);
        }
    }

    private static void Watch(uint sessionId, SessionState state)
    {
        try
        {
            var waitResult = NativeMethods.WaitForSingleObject(state.ProcessHandle, NativeMethods.Infinite);
            if (waitResult != WaitObject0)
            {
                ServiceLog.Warn($"Session {sessionId}: waiting on WSGM (pid {state.ProcessId}) returned " +
                                $"0x{waitResult:X8} (error {Marshal.GetLastWin32Error()}).");
            }
            var exitKnown = NativeMethods.GetExitCodeProcess(state.ProcessHandle, out var exitCode);
            if (!exitKnown)
            {
                ServiceLog.Warn($"Session {sessionId}: GetExitCodeProcess for pid {state.ProcessId} failed " +
                                $"(error {Marshal.GetLastWin32Error()}).");
            }
            // An unknown exit status must fail TOWARDS the fallback: this is the
            // path that keeps a user from sitting in front of a desktop-less
            // session, so "we could not tell" counts as a dirty exit.
            var dirtyExit = !exitKnown || waitResult != WaitObject0 || exitCode != 0;
            var sessionActive = IsSessionActive(sessionId);
            var explorerRunning = IsExplorerInSession(sessionId);
            ServiceLog.Info($"WSGM (pid {state.ProcessId}, session {sessionId}) exited code " +
                            $"{(exitKnown ? exitCode.ToString() : "unknown")} — " +
                            $"session active={sessionActive}, explorer running={explorerRunning}.");
            if (sessionActive && dirtyExit && !explorerRunning)
            {
                // A normal shell session owns a medium/jobless anchor that observes the same WSGM
                // process handle and restores Explorer after owner loss. Give that narrow path one
                // bounded window to publish its shell before the SYSTEM watchdog uses its robust
                // token fallback; otherwise both creators race and the fallback can win with the
                // job-bound process semantics the anchor exists to avoid.
                var recoveryDeadline = DateTime.UtcNow + AnchorRecoveryGrace;
                while (DateTime.UtcNow < recoveryDeadline
                    && IsSessionActive(sessionId)
                    && !IsExplorerInSession(sessionId))
                {
                    Thread.Sleep(250);
                }
                sessionActive = IsSessionActive(sessionId);
                explorerRunning = IsExplorerInSession(sessionId);
                if (!sessionActive)
                {
                    ServiceLog.Info(
                        $"Session {sessionId}: explorer fallback skipped because the session ended during anchor grace.");
                }
                else if (explorerRunning)
                {
                    ServiceLog.Info(
                        $"Session {sessionId}: explorer appeared during anchor grace; SYSTEM fallback skipped.");
                }
            }
            if (sessionActive && dirtyExit && !explorerRunning)
            {
                // One explorer fallback per logon, always with the UNLINKED user
                // token — explorer must run unelevated (elevated explorer breaks
                // UWP / the touch keyboard). WSGM itself is never relaunched here;
                // its crash-loop breaker owns that story across sign-ins.
                ServiceLog.Warn($"Session {sessionId}: WSGM died dirty without a desktop — starting explorer fallback.");
                var explorer = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                if (!TryLaunch(state.UserToken, explorer, "", out var hExplorer, out _, out var error))
                {
                    ServiceLog.Error($"Session {sessionId}: explorer fallback failed (error {error}).");
                }
                else
                {
                    NativeMethods.CloseHandle(hExplorer);
                }
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"Watchdog for session {sessionId} failed: {ex.Message}");
        }
        finally
        {
            // The watchdog OWNS both handles for its whole lifetime — logoff only
            // drops the dictionary entry. Closing them from there would pull them
            // out from under the wait/query above, and a recycled handle value
            // could then be handed to CreateProcessAsUser as a foreign token.
            var processHandle = state.ProcessHandle;
            var userToken = state.UserToken;
            state.ProcessHandle = 0;
            state.UserToken = 0;
            if (processHandle != 0)
            {
                NativeMethods.CloseHandle(processHandle);
            }
            if (userToken != 0)
            {
                NativeMethods.CloseHandle(userToken);
            }
        }
    }

    private static nint TryGetElevatedToken(nint userToken, uint sessionId, out string tokenKind)
    {
        tokenKind = "user token";
        if (!NativeMethods.GetTokenInformationDword(userToken, NativeMethods.TokenElevationTypeClass,
                out var elevationType, sizeof(int), out _))
        {
            ServiceLog.Warn($"Session {sessionId}: TokenElevationType query failed (error {Marshal.GetLastWin32Error()}) — launching unelevated.");
            return userToken;
        }
        if (elevationType == NativeMethods.TokenElevationTypeFull)
        {
            tokenKind = "already-elevated user token";
            return userToken;
        }
        if (elevationType != NativeMethods.TokenElevationTypeLimited)
        {
            // Standard user or UAC off: no linked token exists. WSGM's own runas
            // fallback still applies once it is running.
            ServiceLog.Info($"Session {sessionId}: user is elevation-incapable (type {elevationType}) — launching unelevated.");
            return userToken;
        }
        if (!NativeMethods.GetTokenInformationHandle(userToken, NativeMethods.TokenLinkedTokenClass,
                out var linked, (uint)nint.Size, out _))
        {
            ServiceLog.Warn($"Session {sessionId}: TokenLinkedToken query failed (error {Marshal.GetLastWin32Error()}) — launching unelevated.");
            return userToken;
        }
        try
        {
            if (!NativeMethods.DuplicateTokenEx(linked, NativeMethods.MaximumAllowed, 0,
                    NativeMethods.SecurityImpersonation, NativeMethods.TokenPrimary, out var primary))
            {
                ServiceLog.Warn($"Session {sessionId}: DuplicateTokenEx failed (error {Marshal.GetLastWin32Error()}) — launching unelevated.");
                return userToken;
            }
            // Defensive: pin the primary token to the target session (legal under
            // SeTcbPrivilege). A failure only logs — the token usually already
            // carries the right session id.
            var sid = sessionId;
            if (!NativeMethods.SetTokenInformation(primary, NativeMethods.TokenSessionIdClass, ref sid, sizeof(uint)))
            {
                ServiceLog.Warn($"Session {sessionId}: SetTokenInformation(TokenSessionId) failed (error {Marshal.GetLastWin32Error()}).");
            }
            tokenKind = "linked token";
            return primary;
        }
        finally
        {
            NativeMethods.CloseHandle(linked);
        }
    }

    private static bool TryLaunchWithRetries(nint token, string exePath, string arguments, uint sessionId,
        out nint hProcess, out uint pid)
    {
        hProcess = 0;
        pid = 0;
        for (var attempt = 1; attempt <= LaunchRetries; attempt++)
        {
            if (TryLaunch(token, exePath, arguments, out hProcess, out pid, out var error))
            {
                return true;
            }
            ServiceLog.Warn($"Session {sessionId}: CreateProcessAsUser failed (error {error}), retry {attempt}/{LaunchRetries}.");
            Thread.Sleep(LaunchRetryDelay);
        }
        ServiceLog.Error($"Session {sessionId}: giving up after {LaunchRetries} launch attempts.");
        return false;
    }

    private static bool TryLaunch(nint token, string exePath, string arguments,
        out nint hProcess, out uint pid, out int error)
    {
        hProcess = 0;
        pid = 0;
        error = 0;

        if (!NativeMethods.CreateEnvironmentBlock(out var environment, token, false))
        {
            error = Marshal.GetLastWin32Error();
            ServiceLog.Warn(
                $"CreateEnvironmentBlock failed (error {error}); refusing to launch an "
                    + "interactive process with the SYSTEM service environment.");
            return false;
        }
        var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
        try
        {
            var startupInfo = new NativeMethods.StartupInfoW
            {
                cb = (uint)Marshal.SizeOf<NativeMethods.StartupInfoW>(),
                lpDesktop = desktop,
            };
            var commandLine = string.IsNullOrEmpty(arguments) ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments}";
            if (!NativeMethods.CreateProcessAsUserW(token, exePath, commandLine, 0, 0, false,
                    NativeMethods.CreateUnicodeEnvironment, environment, Path.GetDirectoryName(exePath),
                    ref startupInfo, out var processInfo))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            NativeMethods.CloseHandle(processInfo.hThread);
            hProcess = processInfo.hProcess;
            pid = processInfo.dwProcessId;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(desktop);
            if (environment != 0)
            {
                NativeMethods.DestroyEnvironmentBlock(environment);
            }
        }
    }

    private static string? GetUserProfileDirectory(nint token)
    {
        uint size = 0;
        _ = NativeMethods.GetUserProfileDirectoryW(token, null, ref size);
        if (size == 0)
        {
            return null;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new char[size];
            uint available = size;
            if (NativeMethods.GetUserProfileDirectoryW(token, buffer, ref available))
            {
                int terminator = Array.IndexOf(buffer, '\0');
                return new string(buffer, 0, terminator < 0 ? buffer.Length : terminator);
            }

            if (available <= size)
            {
                return null;
            }

            size = available;
        }

        return null;
    }

    private static TimeSpan? GetLogonAge(uint sessionId)
    {
        if (!NativeMethods.WTSQuerySessionInformationW(0, sessionId,
                NativeMethods.WtsInfoClassSessionInfo, out var buffer, out var bytes) ||
            bytes < Marshal.SizeOf<NativeMethods.WtsInfoW>())
        {
            return null;
        }
        try
        {
            var info = Marshal.PtrToStructure<NativeMethods.WtsInfoW>(buffer);
            if (info.LogonTime == 0)
            {
                return null;
            }
            var logonUtc = DateTime.FromFileTimeUtc(info.LogonTime);
            var age = DateTime.UtcNow - logonUtc;
            return age < TimeSpan.Zero ? TimeSpan.Zero : age;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    private static bool IsSessionActive(uint sessionId)
    {
        if (!NativeMethods.WTSEnumerateSessionsW(0, 0, 1, out var pSessions, out var count))
        {
            return false;
        }
        try
        {
            var size = Marshal.SizeOf<NativeMethods.WtsSessionInfoW>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<NativeMethods.WtsSessionInfoW>(pSessions + i * size);
                if (info.SessionId == sessionId)
                {
                    return info.State == NativeMethods.WtsActive;
                }
            }
            return false;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(pSessions);
        }
    }

    private static bool IsExplorerInSession(uint sessionId)
    {
        if (!NativeMethods.WTSEnumerateProcessesW(0, 0, 1, out var pProcesses, out var count))
        {
            return false;
        }
        try
        {
            var size = Marshal.SizeOf<NativeMethods.WtsProcessInfoW>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<NativeMethods.WtsProcessInfoW>(pProcesses + i * size);
                if (info.SessionId != sessionId)
                {
                    continue;
                }
                var name = Marshal.PtrToStringUni(info.pProcessName);
                if (string.Equals(name, "explorer.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(pProcesses);
        }
    }

    private static string GetSessionUser(uint sessionId)
    {
        var domain = QuerySessionString(sessionId, NativeMethods.WtsInfoClassDomainName);
        var user = QuerySessionString(sessionId, NativeMethods.WtsInfoClassUserName);
        return string.IsNullOrEmpty(user) ? "unknown user" : $"{domain}\\{user}";
    }

    private static string QuerySessionString(uint sessionId, int infoClass)
    {
        if (!NativeMethods.WTSQuerySessionInformationW(0, sessionId, infoClass, out var buffer, out _))
        {
            return "";
        }
        try
        {
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }
}
