using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Outcome of a launch-configuration change.</summary>
/// <param name="Ok">Whether the change was applied.</param>
/// <param name="Detail">A user-facing note (why it failed, or what was done).</param>
public readonly record struct LaunchConfigResult(bool Ok, string Detail);

/// <summary>A game's launch configuration as the running Steam client holds it.</summary>
/// <param name="LaunchOptions">A Steam title's launch options.</param>
/// <param name="ShortcutTarget">A non-Steam shortcut's Target (Steam stores it quoted).</param>
/// <param name="ShortcutArguments">A non-Steam shortcut's Launch Arguments.</param>
/// <param name="ShortcutStartDir">A non-Steam shortcut's start directory.</param>
public readonly record struct SteamLaunchDetails(
    string LaunchOptions,
    string ShortcutTarget,
    string ShortcutArguments,
    string ShortcutStartDir);

/// <summary>
/// Reads and writes a game's launch configuration in the <em>running</em> Steam
/// client over the CEF leg (<see cref="SteamCef"/>), so WSGM can point a game at
/// <c>WSGM.Launch.exe</c> without the user editing anything by hand.
/// </summary>
/// <remarks>
/// <para>Two different Steam APIs, because Steam treats the two kinds of entry
/// differently. A real title takes <c>SteamClient.Apps.SetAppLaunchOptions</c>,
/// where <c>%command%</c> expands to the game's own command line. A non-Steam
/// shortcut ignores an exe-replacing launch option entirely and runs its original
/// Target anyway, so there the wrapper is written into the Target
/// (<c>SetShortcutExe</c>) and the real program moves into the Launch Arguments
/// (<c>SetShortcutLaunchOptions</c>).</para>
/// <para>Device-probed behaviour this depends on: Steam stores every one of these
/// values <em>verbatim</em> — it neither adds nor strips the surrounding quotes its
/// own shortcuts carry, and it does not touch backslashes — and it persists them to
/// <c>shortcuts.vdf</c>/<c>localconfig.vdf</c> immediately, so no restart is needed.
/// The start directory is deliberately never written: the game's own folder has to
/// stay the working directory.</para>
/// </remarks>
public static class SteamLaunchConfig
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    // RegisterForAppDetails is a subscription, not a getter: it calls back with the
    // current details and again on every change. Steam answers a live app almost
    // immediately, so a short bound is enough to keep an unknown id from hanging.
    private const int DetailsTimeoutMs = 3_000;

    // Steam applies each setter on its own thread; give the write a moment to land
    // before the caller reads the value back to confirm it.
    private const int WriteSettleMs = 400;

    /// <summary>Reads a game's current launch configuration from the running client.</summary>
    /// <param name="appId">The Steam app id, or a non-Steam shortcut's generated id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The details, or <see langword="null"/> when Steam is unreachable or
    /// does not know the id.</returns>
    public static async Task<SteamLaunchDetails?> ReadAsync(
        long appId, CancellationToken cancellationToken = default)
    {
        var expression =
            "(async()=>{try{const d=await " + DetailsPromiseJs(appId) + ";" +
            "if(!d){return JSON.stringify({ok:false,err:'Steam has no details for this game.'});}" +
            "return JSON.stringify({ok:true,launch:d.strLaunchOptions||'',exe:d.strShortcutExe||''," +
            "args:d.strShortcutLaunchOptions||'',dir:d.strShortcutStartDir||''});}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";

        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var err = root.TryGetProperty("err", out var e) ? e.GetString() : "unknown error";
                Log.Warn($"Could not read launch configuration for {appId}: {err}.");
                return null;
            }
            return new SteamLaunchDetails(
                root.GetProperty("launch").GetString() ?? "",
                root.GetProperty("exe").GetString() ?? "",
                root.GetProperty("args").GetString() ?? "",
                root.GetProperty("dir").GetString() ?? "");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not parse launch configuration for {appId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Points a game at the launch wrapper.</summary>
    /// <param name="appId">The Steam app id, or a non-Steam shortcut's generated id.</param>
    /// <param name="isShortcut">Whether the id is a non-Steam shortcut.</param>
    /// <param name="mode">Which wrapper behaviours to enable.</param>
    /// <param name="current">The game's current configuration, from <see cref="ReadAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether Steam accepted the change.</returns>
    public static async Task<LaunchConfigResult> ApplyAsync(
        long appId,
        bool isShortcut,
        LaunchWrapperMode mode,
        SteamLaunchDetails current,
        CancellationToken cancellationToken = default)
    {
        var helper = LaunchWrapperCommand.HelperPathForCurrentDeployment();
        if (!System.IO.File.Exists(helper))
        {
            return new LaunchConfigResult(false, "The launch wrapper is missing from this install.");
        }

        string expression;
        if (isShortcut)
        {
            // Re-applying to an already-wrapped shortcut must not wrap the wrapper:
            // the original program lives in the arguments by then, not the Target.
            var original = LaunchWrapperCommand.TargetsHelper(current.ShortcutTarget)
                ? OriginalFromWrappedArguments(current.ShortcutArguments)
                : (current.ShortcutTarget, current.ShortcutArguments);
            if (string.IsNullOrWhiteSpace(original.Item1))
            {
                return new LaunchConfigResult(
                    false, "Steam did not report what this shortcut points at.");
            }

            var target = SteamCef.JsString(LaunchWrapperCommand.ShortcutTarget(helper));
            var arguments = SteamCef.JsString(
                LaunchWrapperCommand.ShortcutArguments(mode, original.Item1, original.Item2));
            expression =
                "(async()=>{try{const app=" + Unsigned(appId) + ";" +
                "await SteamClient.Apps.SetShortcutExe(app," + target + ");" +
                "await SteamClient.Apps.SetShortcutLaunchOptions(app," + arguments + ");" +
                SettleJs +
                "return JSON.stringify({ok:true});}" +
                "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";
        }
        else
        {
            // Real titles only (%command% is meaningless on a non-Steam shortcut —
            // see the shortcut branch above). Re-applying reads the user's own
            // options back out of the existing wrapper value instead of nesting it.
            var originals = LaunchWrapperCommand.OriginalLaunchOptions(current.LaunchOptions);
            // Diagnosability, not a warning: a profiler/RTSS shim ahead of %command%
            // is a supported configuration, so this is what a healthy apply looks
            // like. It is logged because the prefix keeps running at Steam's own
            // integrity level in front of the wrapper, and a pasted wsgm.log is the
            // only way to see that from here. Emitted BEFORE the evaluate, so the
            // wording claims nothing about the outcome. The prefix is bounded and
            // control-character stripped by PreservedPrefix; the value handed to
            // SteamLaunchOptions is untouched, because Steam stores it verbatim.
            var prefix = LaunchWrapperCommand.PreservedPrefix(originals);
            if (prefix.Length > 0)
            {
                Log.Info(
                    $"Applying launch options for {appId} with a user-placed prefix ahead of " +
                    $"%command%, preserved and run at Steam's integrity before the wrapper: {prefix}");
            }
            var options = SteamCef.JsString(LaunchWrapperCommand.SteamLaunchOptions(
                helper, mode, originals));
            expression =
                "(async()=>{try{await SteamClient.Apps.SetAppLaunchOptions(" +
                Unsigned(appId) + "," + options + ");" +
                SettleJs +
                "return JSON.stringify({ok:true});}" +
                "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";
        }

        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        return Interpret(result, "Applied. Launch the game from Steam as usual.");
    }

    /// <summary>Replaces a game's launch action using Steam's native fields.</summary>
    /// <param name="appId">The Steam app id or generated shortcut id.</param>
    /// <param name="isShortcut">Whether the app is a non-Steam shortcut.</param>
    /// <param name="path">The selected executable or script.</param>
    /// <param name="arguments">Verbatim custom arguments for the selected action.</param>
    /// <param name="cancellationToken">Cancels the Steam operation.</param>
    /// <returns>Whether Steam accepted the native launch fields.</returns>
    public static async Task<LaunchConfigResult> ApplyCustomAsync(
        long appId, bool isShortcut, string path, string arguments,
        CancellationToken cancellationToken = default)
    {
        var fields = SteamCustomLaunchCommand.Build(path, arguments);
        string expression;
        if (isShortcut)
        {
            expression =
                "(async()=>{try{const app=" + Unsigned(appId) + ";" +
                "await SteamClient.Apps.SetShortcutExe(app," +
                SteamCef.JsString(fields.ShortcutTarget) + ");" +
                "await SteamClient.Apps.SetShortcutLaunchOptions(app," +
                SteamCef.JsString(fields.ShortcutArguments) + ");" + SettleJs +
                "return JSON.stringify({ok:true});}" +
                "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";
        }
        else
        {
            expression =
                "(async()=>{try{await SteamClient.Apps.SetAppLaunchOptions(" +
                Unsigned(appId) + "," + SteamCef.JsString(fields.LaunchOptions) + ");" +
                SettleJs + "return JSON.stringify({ok:true});}" +
                "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";
        }
        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        return Interpret(result, "Applied. Launch the game from Steam as usual.");
    }

    /// <summary>Restores the launch configuration a game had before WSGM changed it.</summary>
    /// <param name="snapshot">What was recorded when the wrapper was applied.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether Steam accepted the change.</returns>
    public static async Task<LaunchConfigResult> RestoreAsync(
        LaunchWrapperConfig snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var app = Unsigned(snapshot.AppId);
        string expression;
        if (snapshot.IsShortcut)
        {
            if (string.IsNullOrWhiteSpace(snapshot.OriginalTarget))
            {
                return new LaunchConfigResult(
                    false, "The original program for this shortcut was not recorded.");
            }
            // Written back exactly as Steam reported it, quotes included — Steam
            // stores these verbatim, so anything else changes the shortcut.
            expression =
                "(async()=>{try{const app=" + app + ";" +
                "await SteamClient.Apps.SetShortcutExe(app," +
                SteamCef.JsString(snapshot.OriginalTarget) + ");" +
                "await SteamClient.Apps.SetShortcutLaunchOptions(app," +
                SteamCef.JsString(snapshot.OriginalLaunchOptions ?? "") + ");" +
                SettleJs +
                "return JSON.stringify({ok:true});}" +
                "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";
        }
        else
        {
            expression =
                "(async()=>{try{await SteamClient.Apps.SetAppLaunchOptions(" + app + "," +
                SteamCef.JsString(snapshot.OriginalLaunchOptions ?? "") + ");" +
                SettleJs +
                "return JSON.stringify({ok:true});}" +
                "catch(e){return JSON.stringify({ok:false,err:String((e&&e.message)||e)});}})()";
        }

        var result = await SteamUiTransportSession.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        return Interpret(result, "Removed. The game launches the way it did before.");
    }

    /// <summary>Reports which wrapper behaviours a game is currently configured with.</summary>
    /// <param name="isShortcut">Whether the id is a non-Steam shortcut.</param>
    /// <param name="details">The game's current configuration.</param>
    /// <returns>The active behaviours, or <see cref="LaunchWrapperMode.None"/>.</returns>
    public static LaunchWrapperMode ModeFor(bool isShortcut, SteamLaunchDetails details)
    {
        if (!isShortcut)
        {
            return LaunchWrapperCommand.ModeFor(details.LaunchOptions);
        }
        // A shortcut splits the two halves WSGM wrote: the wrapper path sits in the
        // Target and the behaviour flags in the arguments, which never repeat the
        // path. Read them as one string so the mode is recognised.
        return LaunchWrapperCommand.TargetsHelper(details.ShortcutTarget)
            ? LaunchWrapperCommand.ModeFor(
                details.ShortcutTarget + " " + details.ShortcutArguments)
            : LaunchWrapperMode.None;
    }

    /// <summary>Derives what a game's launch configuration looked like before any
    /// wrapper was written into it, so a snapshot taken for an already-wrapped game
    /// records the real program rather than WSGM's own values. Needed whenever a
    /// game is wrapped but WSGM holds no snapshot: the command was pasted by hand
    /// from the clipboard fallback, or the configuration was restored/reset.</summary>
    /// <param name="isShortcut">Whether the entry is a non-Steam shortcut.</param>
    /// <param name="details">The game's current configuration, from <see cref="ReadAsync"/>.</param>
    /// <returns>The pre-wrapper target, launch options/arguments and start directory.
    /// An unwrapped game's values are returned unchanged.</returns>
    public static (string Target, string LaunchOptions, string StartDir) OriginalsFrom(
        bool isShortcut, SteamLaunchDetails details)
    {
        if (ModeFor(isShortcut, details) == LaunchWrapperMode.None)
        {
            return (details.ShortcutTarget, isShortcut
                ? details.ShortcutArguments
                : details.LaunchOptions, details.ShortcutStartDir);
        }
        if (!isShortcut)
        {
            return (details.ShortcutTarget,
                LaunchWrapperCommand.OriginalLaunchOptions(details.LaunchOptions),
                details.ShortcutStartDir);
        }
        var (target, arguments) = OriginalFromWrappedArguments(details.ShortcutArguments);
        return (target, arguments, details.ShortcutStartDir);
    }

    /// <summary>Recovers the program a wrapped shortcut actually runs.</summary>
    /// <param name="arguments">The shortcut's current Launch Arguments.</param>
    /// <returns>The original target and its own arguments, both empty when the value
    /// does not look like something WSGM wrote.</returns>
    internal static (string, string) OriginalFromWrappedArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return ("", "");
        }
        // Everything after the separator is what the shortcut used to launch: a
        // (possibly quoted) program path followed by its own arguments.
        var separator = arguments.IndexOf(" -- ", StringComparison.Ordinal);
        var rest = separator < 0
            ? arguments.StartsWith("-- ", StringComparison.Ordinal) ? arguments[3..] : ""
            : arguments[(separator + 4)..];
        rest = rest.Trim();
        if (rest.Length == 0)
        {
            return ("", "");
        }

        if (rest[0] == '"')
        {
            var end = rest.IndexOf('"', 1);
            return end < 0
                ? (rest, "")
                : (rest[..(end + 1)], rest[(end + 1)..].Trim());
        }
        var space = rest.IndexOf(' ');
        return space < 0 ? (rest, "") : (rest[..space], rest[(space + 1)..].Trim());
    }

    // Steam answers RegisterForAppDetails through a callback and keeps calling it,
    // so the subscription has to be unregistered on both paths or it leaks.
    private static string DetailsPromiseJs(long appId) =>
        "new Promise(res=>{let t;try{const h=SteamClient.Apps.RegisterForAppDetails(" +
        Unsigned(appId) + ",d=>{clearTimeout(t);try{h.unregister();}catch(_){}res(d);});" +
        "t=setTimeout(()=>{try{h.unregister();}catch(_){}res(null);}," + DetailsTimeoutMs + ");}" +
        "catch(_){res(null);}})";

    private static string SettleJs =>
        "await new Promise(r=>setTimeout(r," +
        WriteSettleMs.ToString(CultureInfo.InvariantCulture) + "));";

    // appStore uses the unsigned 32-bit app id; a shortcut id stored in a signed int
    // reads back negative, so normalize to the unsigned value the client expects.
    private static string Unsigned(long appId)
        => (appId < 0 ? (uint)appId : appId).ToString(CultureInfo.InvariantCulture);

    private static LaunchConfigResult Interpret(CefEvalResult result, string okMessage)
    {
        // An unreachable Steam is not a rejected change: the caller keeps the
        // request and can retry, so it must never be reported as a failure to apply.
        if (!result.Reachable)
        {
            return new LaunchConfigResult(false, "Steam isn't reachable — is it running?");
        }
        if (result.Value is null)
        {
            return new LaunchConfigResult(false, "No response from Steam.");
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return new LaunchConfigResult(true, okMessage);
            }
            var err = root.TryGetProperty("err", out var e) ? e.GetString() : "unknown error";
            Log.Warn($"Launch configuration change failed: {err}.");
            return new LaunchConfigResult(false, err ?? "Steam rejected the change.");
        }
        catch (Exception ex)
        {
            return new LaunchConfigResult(false, ex.Message);
        }
    }
}
