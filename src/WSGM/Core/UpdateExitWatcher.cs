using System;
using System.Threading;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Lets the (elevated) installer ask a running — possibly elevated —
/// WSGM to exit before an update. An event created with the elevated token's
/// DEFAULT security is unreachable from a second WSGM instance, so the event is
/// created with an explicit descriptor scoped to THIS user plus BUILTIN\
/// Administrators (EVENT_MODIFY_STATE | SYNCHRONIZE) and a medium mandatory label:
/// the elevated setup and every same-user WSGM instance can wait/signal/reset,
/// while low-IL sandboxed processes cannot force an exit. A graceful self-shutdown
/// runs the normal exit path, asks elevated Steam to exit so an updated injected
/// payload can unload, then lets the Steam Input lease release and posture restore
/// fire too.</summary>
public static class UpdateExitWatcher
{
    private static nint _updateCompletionEvent;
    private static nint _uninstallCompletionEvent;

    // CROSS-VERSION CONTRACT — do not rename this event and do not narrow the
    // 0x00100002 grant below. During an update the event object is created by the
    // OLD running build; the new installer only opens it BY NAME and signals it
    // (installer\WSGM.iss:216, StopRunningInstances — OpenEventW with
    // EVENT_MODIFY_STATE, then SetEvent). If either drifts, a future upgrade can no
    // longer stop WSGM gracefully, Steam is never asked to exit, and the injected
    // Steam Input payload stays mapped. No test can cover that pairing — only an
    // actual installer run against an older build can.

    /// <summary>Gets the per-session event used by an updater to request a graceful exit.</summary>
    public const string EventName = @"Local\WSGM.ExitForUpdate";

    /// <summary>Gets the per-session event used by the uninstaller for its longer cleanup budget.</summary>
    public const string UninstallEventName = @"Local\WSGM.ExitForUninstall";

    // The setup is ALWAYS elevated (PrivilegesRequired=admin): the user-SID ACE
    // covers every same-user WSGM instance (elevated or filtered token — the user
    // SID is never deny-only) and a setup elevated as this user; the BA ACE covers
    // a setup elevated via a different admin account. The medium label keeps the
    // unelevated settings instance able to wait/reset (it needs EVENT_MODIFY_STATE
    // for the stale-signal ResetEvent and the OpenEventW fallback below), while
    // low-IL/sandboxed processes can no longer force an exit. Internal rather than
    // private, and taking the SID rather than reading the token, so the "WD"
    // fallback is covered by a test instead of being asserted away.

    /// <summary>Builds the event's security descriptor: a DACL granting the given
    /// user SID and BUILTIN\Administrators EVENT_MODIFY_STATE | SYNCHRONIZE
    /// (0x00100002), plus a medium mandatory label with no-write-up.</summary>
    /// <param name="userSid">The current token's user SID in SDDL form, or
    /// <see langword="null"/> when it could not be read — practically impossible,
    /// and then that ACE falls back to the old Everyone ("WD") grant so an update
    /// can still stop this instance.</param>
    /// <returns>The SDDL string for <c>CreateEventW</c>'s security descriptor.</returns>
    internal static string BuildEventSddl(string? userSid)
        => $"D:(A;;0x00100002;;;{userSid ?? "WD"})(A;;0x00100002;;;BA)S:(ML;;NW;;;ME)";

    internal static string? HandoffEventNameFor(ApplicationShutdownReason reason) =>
        reason switch
        {
            ApplicationShutdownReason.Update => $"{EventName}.Completed",
            ApplicationShutdownReason.Uninstall => $"{UninstallEventName}.Completed",
            _ => null,
        };

    internal static void ReportHandoff(
        ApplicationShutdownReason reason,
        ApplicationShutdownOutcome outcome)
    {
        nint handoffEvent = reason switch
        {
            ApplicationShutdownReason.Update => _updateCompletionEvent,
            ApplicationShutdownReason.Uninstall => _uninstallCompletionEvent,
            _ => 0,
        };
        if (handoffEvent == 0
            && reason is not (ApplicationShutdownReason.Update
                or ApplicationShutdownReason.Uninstall))
        {
            return;
        }

        Log.Info($"Installer shutdown handoff completed: reason={reason}, outcome={outcome}.");
        if (handoffEvent == 0)
        {
            Log.Warn($"Installer shutdown completion channel was unavailable ({reason}).");
            return;
        }

        if (!NativeMethods.SetEvent(handoffEvent))
        {
            Log.Warn(
                $"Installer shutdown completion could not be published ({reason}; "
                + $"error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>Starts watching for the updater's graceful-exit request.</summary>
    /// <param name="onExitRequested">The callback that runs the normal application shutdown path.</param>
    /// <param name="onUninstallRequested">The callback that runs the uninstall shutdown path.</param>
    public static void Start(Action onExitRequested, Action? onUninstallRequested = null)
    {
        try
        {
            string? userSid;
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                userSid = identity.User?.Value;
            }
            _updateCompletionEvent = StartHandoffEvent(
                ApplicationShutdownReason.Update,
                "update",
                userSid);
            StartWatcher(EventName, "update", userSid, onExitRequested);
            if (onUninstallRequested is not null)
            {
                _uninstallCompletionEvent = StartHandoffEvent(
                    ApplicationShutdownReason.Uninstall,
                    "uninstall",
                    userSid);
                StartWatcher(
                    UninstallEventName,
                    "uninstall",
                    userSid,
                    onUninstallRequested);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Update-exit watcher not available: {ex.Message}");
        }
    }

    private static nint StartHandoffEvent(
        ApplicationShutdownReason reason,
        string operation,
        string? userSid)
    {
        string eventName = HandoffEventNameFor(reason)
            ?? throw new InvalidOperationException("Installer handoff reason has no event name.");
        return CreateOrOpenEvent(
            eventName,
            $"{operation} completion",
            userSid,
            clearStaleSignal: false);
    }

    private static void StartWatcher(
        string eventName,
        string operation,
        string? userSid,
        Action callback)
    {
        nint exitEvent = CreateOrOpenEvent(
            eventName,
            $"{operation}-exit watcher",
            userSid,
            clearStaleSignal: true);
        if (exitEvent == 0)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                NativeMethods.WaitForSingleObject(exitEvent, uint.MaxValue);
                Log.Info($"Exit requested by installer ({operation}).");
                callback();
            }
            catch (Exception ex)
            {
                Log.Warn($"{operation}-exit watcher: shutdown request failed: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = $"WSGM.{operation}Exit",
        };
        thread.Start();
    }

    private static nint CreateOrOpenEvent(
        string eventName,
        string operation,
        string? userSid,
        bool clearStaleSignal)
    {
        if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(
            BuildEventSddl(userSid),
            1,
            out nint securityDescriptor,
            out _))
        {
            Log.Warn(
                $"{operation}: SDDL conversion failed "
                + $"(error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
            return 0;
        }

        nint exitEvent;
        int createError;
        try
        {
            var attributes = new NativeMethods.SecurityAttributes
            {
                nLength = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SecurityAttributes>(),
                lpSecurityDescriptor = securityDescriptor,
                bInheritHandle = 0,
            };
            exitEvent = NativeMethods.CreateEventW(
                ref attributes,
                manualReset: true,
                initialState: false,
                eventName);
            createError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }
        finally
        {
            NativeMethods.LocalFree(securityDescriptor);
        }

        if (exitEvent == 0 && createError == 5 /* ERROR_ACCESS_DENIED */)
        {
            exitEvent = NativeMethods.OpenEventW(
                NativeMethods.Synchronize | NativeMethods.EventModifyState,
                false,
                eventName);
            if (exitEvent == 0)
            {
                Log.Warn(
                    $"{operation}: OpenEvent fallback failed "
                    + $"(error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
                return 0;
            }
        }
        else if (exitEvent == 0)
        {
            Log.Warn($"{operation}: CreateEvent failed (error {createError}).");
            return 0;
        }

        if (clearStaleSignal && !NativeMethods.ResetEvent(exitEvent))
        {
            Log.Warn(
                $"{operation}: could not clear stale signal "
                + $"(error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
        }

        return exitEvent;
    }
}
