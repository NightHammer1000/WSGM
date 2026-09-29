using System;
using System.Runtime.InteropServices;
using System.Threading;
using WSGM.LogonService.Interop;

namespace WSGM.LogonService;

/// <summary>Hosts the service directly on the small SCM API surface.</summary>
/// <remarks>The control handler only enqueues work because SCM handlers must return quickly.</remarks>
internal static class ServiceHost
{
    internal const string ServiceName = "WSGMLogonService";

    /// <summary>Start argument the installer passes through <c>StartService</c> to mark
    /// a start it initiated itself. Not a command line: SCM delivers it to ServiceMain.</summary>
    internal const string InstallStartArgument = "--installed-start";

    private static nint _statusHandle;
    private static NativeMethods.ServiceStatus _status;
    private static readonly ManualResetEventSlim StopRequested = new(false);

    /// <summary>Reads the SCM-delivered start arguments (argv[0] is the service name)
    /// and reports whether the installer marked this start as its own.</summary>
    private static unsafe bool IsInstallStart(uint argc, nint argv)
    {
        if (argv == 0)
        {
            return false;
        }
        var vector = (nint*)argv;
        for (var i = 1u; i < argc; i++)
        {
            if (string.Equals(Marshal.PtrToStringUni(vector[i]), InstallStartArgument,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Connects to the SCM dispatcher (blocks until the service stops).
    /// Returns nonzero when started from a console instead of the SCM.</summary>
    internal static unsafe int RunDispatcher()
    {
        fixed (char* name = ServiceName)
        {
            var table = stackalloc NativeMethods.ServiceTableEntryW[2];
            table[0] = new NativeMethods.ServiceTableEntryW
            {
                lpServiceName = (nint)name,
                lpServiceProc = &ServiceMain,
            };
            table[1] = default;
            if (!NativeMethods.StartServiceCtrlDispatcherW(table))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == NativeMethods.ErrorFailedServiceControllerConnect)
                {
                    ServiceLog.Warn("Started from a console — this exe is a Windows service. " +
                                    "Use --install / --uninstall (elevated), or let the SCM start it.");
                }
                else
                {
                    ServiceLog.Error($"StartServiceCtrlDispatcherW failed (error {error}).");
                }
                return 1;
            }
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static unsafe void ServiceMain(uint argc, nint argv)
    {
        try
        {
            _statusHandle = NativeMethods.RegisterServiceCtrlHandlerExW(ServiceName, &HandlerEx, 0);
            if (_statusHandle == 0)
            {
                ServiceLog.Error($"RegisterServiceCtrlHandlerExW failed (error {Marshal.GetLastWin32Error()}).");
                return;
            }

            _status = new NativeMethods.ServiceStatus
            {
                dwServiceType = NativeMethods.ServiceWin32OwnProcess,
                dwCurrentState = NativeMethods.ServiceStartPending,
                dwWaitHint = 3000,
            };
            ReportStatus();

            _status.dwCurrentState = NativeMethods.ServiceRunning;
            _status.dwControlsAccepted = NativeMethods.ServiceAcceptStop |
                                         NativeMethods.ServiceAcceptShutdown |
                                         NativeMethods.ServiceAcceptSessionChange;
            _status.dwWaitHint = 0;
            ReportStatus();
            ServiceLog.Info($"WSGM logon service v{typeof(ServiceHost).Assembly.GetName().Version?.ToString(3) ?? "?"} started.");

            // An auto-start service can come up after an autologon already signed
            // the user in — sweep existing sessions once. NOT when the installer
            // started us: that start happens long after logon, with the per-session
            // memory cleared by the stop the installer just performed, so the sweep
            // would take over a session the user is actively using mid-setup.
            if (IsInstallStart(argc, argv))
            {
                ServiceLog.Info("Installer-initiated start — skipping the logon catch-up sweep.");
            }
            else
            {
                ThreadPool.QueueUserWorkItem(static _ =>
                {
                    try
                    {
                        SessionLauncher.CatchUpExistingSessions();
                    }
                    catch (Exception ex)
                    {
                        ServiceLog.Error($"Startup catch-up failed: {ex.Message}");
                    }
                });
            }

            StopRequested.Wait();

            _status.dwCurrentState = NativeMethods.ServiceStopped;
            _status.dwControlsAccepted = 0;
            ReportStatus();
            ServiceLog.Info("WSGM logon service stopped.");
        }
        catch (Exception ex)
        {
            // An exception must never escape an unmanaged callback.
            ServiceLog.Error($"ServiceMain failed: {ex}");
            try
            {
                _status.dwCurrentState = NativeMethods.ServiceStopped;
                _status.dwWin32ExitCode = 1;
                ReportStatus();
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [UnmanagedCallersOnly]
    private static int HandlerEx(uint control, uint eventType, nint eventData, nint context)
    {
        try
        {
            switch (control)
            {
                case NativeMethods.ServiceControlStop:
                case NativeMethods.ServiceControlShutdown:
                    _status.dwCurrentState = NativeMethods.ServiceStopPending;
                    _status.dwWaitHint = 2000;
                    ReportStatus();
                    StopRequested.Set();
                    return NativeMethods.NoError;

                case NativeMethods.ServiceControlInterrogate:
                    return NativeMethods.NoError;

                case NativeMethods.ServiceControlSessionChange:
                    if (eventData != 0)
                    {
                        var sessionId = Marshal.PtrToStructure<NativeMethods.WtsSessionNotification>(eventData).dwSessionId;
                        // Logon and logoff only. Deliberately NOT WTS_CONSOLE_CONNECT:
                        // a fast-user-switch reconnect keeps whatever was running —
                        // game-mode boot is a per-logon event.
                        if (eventType == NativeMethods.WtsSessionLogon)
                        {
                            ServiceLog.Info($"Session {sessionId} logon.");
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    SessionLauncher.OnSessionLogon(sessionId, logonAge: null);
                                }
                                catch (Exception ex)
                                {
                                    ServiceLog.Error($"Session {sessionId} logon handling failed: {ex.Message}");
                                }
                            });
                        }
                        else if (eventType == NativeMethods.WtsSessionLogoff)
                        {
                            ThreadPool.QueueUserWorkItem(_ => SessionLauncher.OnSessionLogoff(sessionId));
                        }
                    }
                    return NativeMethods.NoError;

                default:
                    return NativeMethods.ErrorCallNotImplemented;
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"Service control handler failed (control {control}): {ex.Message}");
            return NativeMethods.NoError;
        }
    }

    private static void ReportStatus()
    {
        _status.dwCheckPoint = _status.dwCurrentState is NativeMethods.ServiceStartPending or NativeMethods.ServiceStopPending
            ? _status.dwCheckPoint + 1
            : 0;
        NativeMethods.SetServiceStatus(_statusHandle, ref _status);
    }
}
