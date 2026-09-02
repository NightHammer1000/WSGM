using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

namespace WSGM.Core;

/// <summary>Relaunches WSGM elevated when the config starts elevated apps.
///
/// The point is the INHERITANCE CHAIN: children inherit elevation, so an
/// elevated WSGM yields an elevated Steam — which is what lets Steam Input
/// synthesize input into elevated windows and the Steam Overlay inject into
/// elevated games (UIPI blocks both for an unelevated Steam). Same chain
/// covers elevated startup apps, and WSGM matching that integrity keeps its
/// own overlay/edge swipes alive over elevated foreground windows (UIPI also
/// shields raw touch input and foreground from lower-integrity processes).</summary>
public static class SelfElevation
{
    private const string RelaunchMarker = "--elevated-relaunch";
    private const int ErrorCancelled = 1223;

    /// <summary>Returns the exit code to propagate when this process handed over to an
    /// elevated copy of itself, or null to continue running normally.</summary>
    public static int? EnsureElevatedIfConfigured(string[] args)
    {
        if (args.Contains(RelaunchMarker, StringComparer.OrdinalIgnoreCase))
        {
            // Already the relaunched copy — never loop, even if elevation was denied
            // in some unexpected way.
            return null;
        }

        AppConfig config;
        try
        {
            config = ConfigStore.Load();
        }
        catch
        {
            return null;
        }

        var elevatedStartupApps = config.StartupApps.Any(a => a.Enabled && a.Elevated);
        var elevatedSteam = Steam.RequiresElevatedShell;
        // The sole administrator-installed plugin inherits WSGM's token. Startup has
        // already enforced package-root cardinality; the package is not opened until after elevation.
        var wantsElevation = elevatedStartupApps || elevatedSteam || config.DeviceIntegration.Enabled;
        if (!wantsElevation ||
            ElevationCheck.IsCurrentProcessElevated() != false)
        {
            return null;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo(exe,
                string.Join(' ', args.Append(RelaunchMarker).Select(Quote)))
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            using var child = Process.Start(psi);
            if (child is null)
            {
                return null;
            }
            var reason = elevatedSteam ? "Steam requires matching elevation" : "config starts elevated apps";
            Log.Info($"{reason} — handed over to elevated instance (pid {child.Id}).");
            // Stay alive while the elevated instance runs: on a service boot (--boot)
            // the logon service watchdog holds THIS pid — the parent exiting only
            // when the elevated child does is what keeps the watchdog watching the
            // right tree.
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Warn("Elevation DECLINED — continuing non-elevated. Edge swipes will NOT " +
                     "work while an elevated app has the focus (UIPI).");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Self-elevation failed — continuing non-elevated", ex);
            return null;
        }
    }

    /// <summary>Starts an elevated copy of WSGM with the given arguments, waits for
    /// it to finish, and reports whether it succeeded (exit code 0). This is how the
    /// non-elevated settings UI performs one-shot HKLM writes: the elevated instance
    /// applies the change and exits. Device-plugin maintenance passes
    /// <see cref="System.Threading.Timeout.Infinite"/> because a bounded file copy can
    /// legitimately exceed the short settings-action window and must not keep running
    /// after its caller reports a false failure. Returns false when elevation was
    /// declined, the elevated instance outlived the wait, or the write failed.
    /// <paramref name="description"/> prefixes the log lines (e.g. "UAC change").</summary>
    public static bool RunElevatedAction(string argument, string description, int timeoutMs = 60_000)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return false;
        }
        try
        {
            var psi = new ProcessStartInfo(exe, argument)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            if (!p.WaitForExit(timeoutMs))
            {
                // ExitCode would throw on a still-running process.
                Log.Warn($"{description}: elevated instance still running after {timeoutMs / 1000} s — result unknown.");
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"{description} not applied: {ex.Message}");
            return false;
        }
    }

    /// <summary>Quotes one argument per CommandLineToArgvW's rules: embedded quotes
    /// are backslash-escaped, backslash runs before a quote (including the closing
    /// one) are doubled, and empty args become "" — a bare Contains-space wrap would
    /// corrupt args like a quoted path ending in a backslash.</summary>
    internal static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return arg;
        }
        var sb = new System.Text.StringBuilder(arg.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }
            sb.Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
