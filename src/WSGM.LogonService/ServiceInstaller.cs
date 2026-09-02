using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using WSGM.LogonService.Interop;

namespace WSGM.LogonService;

/// <summary>Elevated one-shots the installer drives: create-or-reconfigure (also
/// adopts a leftover preview registration of the same name), failure actions, and
/// stop+delete on uninstall.</summary>
internal static class ServiceInstaller
{
    private const string DisplayName = "WSGM Logon Service";
    private const string Description = "Starts WSGM game mode at sign-in and restores the desktop if it fails.";
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Creates or reconfigures + starts the service. Auto-start on purpose
    /// (NOT delayed auto-start — delayed would lose the logon race; the startup
    /// catch-up only covers the autologon remainder).</summary>
    internal static int Install()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            ServiceLog.Error("Install: cannot determine own executable path.");
            return 1;
        }
        var binPath = $"\"{exe}\"";

        var scm = NativeMethods.OpenSCManagerW(null, null, NativeMethods.ScManagerAllAccess);
        if (scm == 0)
        {
            ServiceLog.Error($"Install: OpenSCManager failed (error {Marshal.GetLastWin32Error()}) — run elevated.");
            return 1;
        }
        try
        {
            var service = NativeMethods.OpenServiceW(scm, ServiceHost.ServiceName, NativeMethods.ServiceAllAccess);
            if (service == 0)
            {
                service = NativeMethods.CreateServiceW(scm, ServiceHost.ServiceName, DisplayName,
                    NativeMethods.ServiceAllAccess, NativeMethods.ServiceWin32OwnProcess,
                    NativeMethods.ServiceAutoStart, NativeMethods.ServiceErrorNormal,
                    binPath, null, 0, null, null, null);
                if (service == 0)
                {
                    ServiceLog.Error($"Install: CreateService failed (error {Marshal.GetLastWin32Error()}).");
                    return 1;
                }
                ServiceLog.Info($"Install: service created ({binPath}).");
            }
            else
            {
                // Upgrades and abandoned preview installations land here — repoint
                // the existing registration at this binary.
                if (!NativeMethods.ChangeServiceConfigW(service, NativeMethods.ServiceWin32OwnProcess,
                        NativeMethods.ServiceAutoStart, NativeMethods.ServiceErrorNormal,
                        binPath, null, 0, null, null, null, DisplayName))
                {
                    ServiceLog.Error($"Install: ChangeServiceConfig failed (error {Marshal.GetLastWin32Error()}).");
                    NativeMethods.CloseServiceHandle(service);
                    return 1;
                }
                ServiceLog.Info($"Install: existing service reconfigured ({binPath}).");
            }

            try
            {
                ApplyDescription(service);
                ApplyFailureActions(service);
                if (!StartForInstall(service))
                {
                    return 1;
                }
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
            return 0;
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }
    }

    /// <summary>Starts the service, tagging the start as installer-initiated.
    /// <para>The tag matters because <c>--install</c> runs from inside Setup, minutes
    /// or hours after the user signed in, and the installer stopped the service first
    /// — so its per-session "already launched" memory is gone. Without the tag the
    /// host's catch-up sweep would treat the live session as an autologon that beat
    /// the service and run a full game-mode boot takeover in the middle of setup.</para></summary>
    /// <param name="service">An open service handle with start rights.</param>
    private static unsafe bool StartForInstall(nint service)
    {
        var started = false;
        fixed (char* tag = ServiceHost.InstallStartArgument)
        {
            var argv = stackalloc nint[1];
            argv[0] = (nint)tag;
            started = NativeMethods.StartServiceW(service, 1, (nint)argv);
        }
        if (started)
        {
            return true;
        }
        var error = Marshal.GetLastWin32Error();
        if (error == NativeMethods.ErrorServiceAlreadyRunning)
        {
            return true;
        }

        ServiceLog.Error($"Install: StartService failed (error {error}).");
        return false;
    }

    /// <summary>Stops (bounded) and deletes the service. A missing service is
    /// success — the uninstaller must be idempotent.</summary>
    internal static int Uninstall()
    {
        var scm = NativeMethods.OpenSCManagerW(null, null, NativeMethods.ScManagerAllAccess);
        if (scm == 0)
        {
            ServiceLog.Error($"Uninstall: OpenSCManager failed (error {Marshal.GetLastWin32Error()}) — run elevated.");
            return 1;
        }
        try
        {
            var service = NativeMethods.OpenServiceW(scm, ServiceHost.ServiceName, NativeMethods.ServiceAllAccess);
            if (service == 0)
            {
                ServiceLog.Info("Uninstall: service not installed — nothing to do.");
                return 0;
            }
            try
            {
                if (!NativeMethods.QueryServiceStatus(service, out var status))
                {
                    ServiceLog.Error(
                        $"Uninstall: initial service-state query failed "
                            + $"(error {Marshal.GetLastWin32Error()}).");
                    return 1;
                }

                if (status.dwCurrentState != NativeMethods.ServiceStopped)
                {
                    if (!NativeMethods.ControlService(service, NativeMethods.ServiceControlStop, out _)
                        && Marshal.GetLastWin32Error() != NativeMethods.ErrorServiceNotActive)
                    {
                        ServiceLog.Error(
                            $"Uninstall: service stop failed (error {Marshal.GetLastWin32Error()}).");
                        return 1;
                    }

                    Stopwatch elapsed = Stopwatch.StartNew();
                    do
                    {
                        Thread.Sleep(250);
                        if (!NativeMethods.QueryServiceStatus(service, out status))
                        {
                            ServiceLog.Error(
                                $"Uninstall: service-state query failed while stopping "
                                    + $"(error {Marshal.GetLastWin32Error()}).");
                            return 1;
                        }
                    }
                    while (status.dwCurrentState != NativeMethods.ServiceStopped
                        && elapsed.Elapsed < StopTimeout);

                    if (status.dwCurrentState != NativeMethods.ServiceStopped)
                    {
                        ServiceLog.Error(
                            "Uninstall: service did not reach the stopped state; deletion refused.");
                        return 1;
                    }
                }
                if (!NativeMethods.DeleteService(service))
                {
                    ServiceLog.Error($"Uninstall: DeleteService failed (error {Marshal.GetLastWin32Error()}).");
                    return 1;
                }
                ServiceLog.Info("Uninstall: service deleted.");
                return 0;
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scm);
        }
    }

    private static void ApplyDescription(nint service)
    {
        var text = Marshal.StringToHGlobalUni(Description);
        var info = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.ServiceDescriptionW>());
        try
        {
            Marshal.StructureToPtr(new NativeMethods.ServiceDescriptionW { lpDescription = text }, info, false);
            if (!NativeMethods.ChangeServiceConfig2W(service, NativeMethods.ServiceConfigDescription, info))
            {
                ServiceLog.Warn($"Install: description not applied (error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            Marshal.FreeHGlobal(text);
        }
    }

    /// <summary>Restart at 5 s / 30 s / 60 s, counters reset after a clean day.</summary>
    private static void ApplyFailureActions(nint service)
    {
        var actionSize = Marshal.SizeOf<NativeMethods.ScAction>();
        var actions = Marshal.AllocHGlobal(actionSize * 3);
        var info = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.ServiceFailureActionsW>());
        try
        {
            var delays = new uint[] { 5000, 30000, 60000 };
            for (var i = 0; i < delays.Length; i++)
            {
                Marshal.StructureToPtr(new NativeMethods.ScAction
                {
                    Type = NativeMethods.ScActionRestart,
                    Delay = delays[i],
                }, actions + i * actionSize, false);
            }
            Marshal.StructureToPtr(new NativeMethods.ServiceFailureActionsW
            {
                dwResetPeriod = 86400,
                cActions = 3,
                lpsaActions = actions,
            }, info, false);
            if (!NativeMethods.ChangeServiceConfig2W(service, NativeMethods.ServiceConfigFailureActions, info))
            {
                ServiceLog.Warn($"Install: failure actions not applied (error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            Marshal.FreeHGlobal(actions);
        }
    }
}
