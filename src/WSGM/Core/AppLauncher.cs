using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WSGM.Core;

/// <summary>Starts apps normally, elevated (runas), or via protocol. Ported from the
/// battle-tested AnyFSE launch logic (MIT).</summary>
public static class AppLauncher
{
    private const int ErrorCancelled = 1223;
    private const int ErrorElevationRequired = 740;

    /// <summary>Reports whether a launch request started and whether UAC was declined.</summary>
    public sealed record LaunchResult(Process? Process, bool Started, bool ElevationDeclined);

    /// <summary>Whether a configured launch target is a protocol URL rather than a
    /// file path (protocols carry no args/elevation and cannot be relaunch-watched).</summary>
    public static bool IsProtocol(string path) => path.Contains("://");

    /// <summary>Starts a configured target, dispatching protocol URLs and elevated
    /// executables to their respective launch mechanisms.</summary>
    /// <param name="path">Executable path or protocol URL.</param>
    /// <param name="args">Arguments for an executable target.</param>
    /// <param name="elevated">Whether the executable should be launched through UAC.</param>
    /// <returns>The outcome of the launch attempt.</returns>
    public static LaunchResult Start(string path, string args, bool elevated)
    {
        if (IsProtocol(path))
        {
            // ShellExecute on a URL cannot carry separate args, and elevation does
            // not apply — warn so the misconfiguration shows up in a pasted log.
            if (!string.IsNullOrWhiteSpace(args) || elevated)
            {
                Log.Warn($"Protocol launch ignores configured args/elevation: {path} (args \"{args}\", elevated {elevated})");
            }
            return StartProtocol(path);
        }
        return elevated ? StartElevated(path, args) : StartNormal(path, args);
    }

    /// <summary>Activates a registered URL protocol through the Windows shell.</summary>
    /// <param name="protocol">The complete protocol URL to activate.</param>
    /// <returns>The outcome of the shell activation.</returns>
    public static LaunchResult StartProtocol(string protocol)
    {
        try
        {
            using Process? activation = Process.Start(
                new ProcessStartInfo(protocol) { UseShellExecute = true });
            Log.Info($"Started protocol: {protocol}");
            return new LaunchResult(null, true, false);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start protocol {protocol}", ex);
            return new LaunchResult(null, false, false);
        }
    }

    /// <summary>ShellExecute-open with the standard try/catch-log contract, for
    /// targets that manage their own activation/elevation (auto-elevating system
    /// exes like Task Manager, TabTip, handing over to another WSGM copy).</summary>
    /// <param name="path">Path to open through the Windows shell.</param>
    /// <param name="args">Optional arguments for the target.</param>
    /// <returns>The outcome of the shell activation.</returns>
    public static LaunchResult Open(string path, string args = "")
    {
        try
        {
            using Process? activation = Process.Start(
                new ProcessStartInfo(path, args) { UseShellExecute = true });
            Log.Info($"Started via shell: {path}{(args.Length == 0 ? "" : " " + args)}");
            return new LaunchResult(null, true, false);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start {path}", ex);
            return new LaunchResult(null, false, false);
        }
    }

    private static LaunchResult StartNormal(string path, string args, bool retryWithElevation = true)
    {
        try
        {
            var psi = new ProcessStartInfo(path, args)
            {
                UseShellExecute = false,
                WorkingDirectory = SafeDirectory(path),
            };
            var process = Process.Start(psi);
            Log.Info($"Started: {path} {args} (pid {process?.Id.ToString() ?? "?"})");
            return new LaunchResult(process, process is not null, false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired && retryWithElevation)
        {
            // Exe has the "Run as administrator" compat flag — honor it.
            Log.Warn($"{path} requires elevation (740), retrying via runas");
            return StartElevated(path, args);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            Log.Error($"{path} still requires elevation after the UAC prompt was declined", ex);
            return new LaunchResult(null, false, true);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start {path}", ex);
            return new LaunchResult(null, false, false);
        }
    }

    private static LaunchResult StartElevated(string path, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(path, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = SafeDirectory(path),
            };
            var process = Process.Start(psi); // may be null (no new process resource)
            Log.Info($"Started elevated: {path} {args} (pid {process?.Id.ToString() ?? "?"})");
            return new LaunchResult(process, true, false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Warn($"Elevation DECLINED for {path} — falling back to a normal start. " +
                     "Controller input over elevated windows will NOT work this session.");
            // Do not retry StartElevated from the fallback: a compatibility flag
            // can return 740 again, otherwise creating a 740 -> cancel loop.
            var fallback = StartNormal(path, args, retryWithElevation: false);
            return fallback with { ElevationDeclined = true };
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start elevated {path}", ex);
            return new LaunchResult(null, false, false);
        }
    }

    internal static string SafeDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        }
        catch
        {
            return "";
        }
    }
}
