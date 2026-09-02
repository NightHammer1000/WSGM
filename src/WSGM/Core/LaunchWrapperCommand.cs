using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WSGM.Core;

/// <summary>Which behaviours a game's launch wrapper should apply.</summary>
[Flags]
public enum LaunchWrapperMode
{
    /// <summary>No wrapper; the game launches the way Steam normally would.</summary>
    None = 0,

    /// <summary>Run the game at medium integrity under elevated Steam.</summary>
    Deelevate = 1,

    /// <summary>Block Steam Input for the game's lifetime through the resident
    /// shim Steam loaded itself. Fails open when no shim is loaded - the wrapper
    /// never injects on this route.</summary>
    InputLease = 2,

    /// <summary>Both behaviours in one wrapper process.</summary>
    Both = Deelevate | InputLease,

    /// <summary>Block Steam Input by injecting the gate into Steam for the game's
    /// lifetime. Written only when Steam Input Management is off, because then no
    /// resident shim exists to connect to.</summary>
    InputLeaseInject = 4,

    /// <summary>De-elevation plus the injecting lease.</summary>
    BothInject = Deelevate | InputLeaseInject,
}

/// <summary>
/// Builds the launch configuration that hands a game to <c>WSGM.Launch.exe</c>.
/// </summary>
/// <remarks>
/// Steam takes two different routes to the same wrapper. A real Steam title uses
/// its launch options, where <c>%command%</c> expands to the game's own command.
/// A non-Steam shortcut cannot: Steam ignores an exe-replacing launch option there
/// and runs the original target anyway (device-verified), so the wrapper goes in
/// the shortcut's Target and the real program moves into its Launch Arguments.
/// </remarks>
internal static class LaunchWrapperCommand
{
    internal const string HelperFileName = "WSGM.Launch.exe";

    /// <summary>Resolves the wrapper beside the running WSGM executable.</summary>
    /// <returns>The absolute path a configured game will reference.</returns>
    internal static string HelperPathForCurrentDeployment()
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath);
        return Path.Combine(directory ?? Installer.InstallDir, HelperFileName);
    }

    /// <summary>The token Steam expands to a game's own command. REAL TITLES ONLY —
    /// a non-Steam shortcut ignores an exe-replacing launch option and runs its
    /// original Target anyway (device-verified), which is why the shortcut path puts
    /// the wrapper in the Target and never builds a value containing this.</summary>
    internal const string CommandPlaceholder = "%command%";

    /// <summary>Builds the value written into a real Steam title's launch options,
    /// preserving any launch options the user already had.</summary>
    /// <param name="helperPath">Absolute path of the wrapper executable.</param>
    /// <param name="mode">Which wrapper behaviours to enable.</param>
    /// <param name="originalOptions">The game's pre-existing launch options, if any.
    /// A value of its own that positions <c>%command%</c> keeps its prefix and suffix
    /// (the wrapper is substituted for the placeholder); a plain value becomes extra
    /// arguments after the placeholder, which the wrapper forwards to the game.</param>
    /// <returns>The launch-option string, quoted for paths containing spaces.</returns>
    /// <exception cref="ArgumentException"><paramref name="helperPath"/> is missing, or
    /// <paramref name="mode"/> selects no behaviour.</exception>
    internal static string SteamLaunchOptions(
        string helperPath, LaunchWrapperMode mode, string? originalOptions = null)
    {
        var wrapper = $"{Quote(helperPath)} {FlagsFor(mode)} -- {CommandPlaceholder}";
        var original = originalOptions?.Trim() ?? "";
        if (original.Length == 0)
        {
            return wrapper;
        }
        var placeholder = original.IndexOf(CommandPlaceholder, StringComparison.Ordinal);
        if (placeholder < 0)
        {
            return $"{wrapper} {original}";
        }
        // Substitute the first placeholder only: the user's value already says where
        // the game command belongs, so their own prefix (a profiler, an env shim) and
        // trailing arguments both survive.
        return string.Concat(
            original.AsSpan(0, placeholder),
            wrapper,
            original.AsSpan(placeholder + CommandPlaceholder.Length));
    }

    /// <summary>Reports the text a user placed AHEAD of their own <c>%command%</c>,
    /// sanitized and bounded so it is safe to put in the log.</summary>
    /// <param name="originalOptions">The user's own launch options, as
    /// <see cref="OriginalLaunchOptions"/> recovered them.</param>
    /// <returns>The prefix, control characters removed and length capped; an empty
    /// string when the value is blank or does not position <c>%command%</c> itself.</returns>
    /// <remarks>
    /// <para>Diagnosability only — nothing here feeds <see cref="SteamLaunchOptions"/>,
    /// whose output must stay byte-identical because Steam stores it verbatim. A
    /// prefix is never stripped, reordered, escaped or refused: it is how
    /// <c>-dx11</c>/<c>-nolauncher</c> shims, profilers and RTSS-style overlays keep
    /// working, and it runs at Steam's own integrity level whether or not WSGM wraps
    /// the game — applying a wrapper only ever REDUCES that, by moving the game
    /// itself to medium.</para>
    /// <para>Sanitized because <see cref="Log"/> interpolates its message raw: an
    /// options string carrying a newline could otherwise forge whole log lines in the
    /// only remote-diagnosis surface WSGM has. Control characters are dropped rather
    /// than escaped, and the result is capped so one pathological value cannot bury a
    /// pasted <c>wsgm.log</c>.</para>
    /// </remarks>
    internal static string PreservedPrefix(string? originalOptions)
    {
        if (string.IsNullOrWhiteSpace(originalOptions))
        {
            return "";
        }
        var placeholder = originalOptions.IndexOf(CommandPlaceholder, StringComparison.Ordinal);
        if (placeholder <= 0)
        {
            return "";
        }
        var prefix = originalOptions[..placeholder].Trim();
        var builder = new StringBuilder(Math.Min(prefix.Length, PrefixLogLimit));
        foreach (var character in prefix)
        {
            if (char.IsControl(character))
            {
                continue;
            }
            if (builder.Length == PrefixLogLimit)
            {
                return builder.Append("...").ToString();
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    /// <summary>Recovers the launch options a real Steam title had before the wrapper
    /// was written into them, so re-applying with a different mode does not nest the
    /// wrapper inside itself or drop the user's arguments.</summary>
    /// <param name="wrapped">The title's current launch options.</param>
    /// <returns>The user's own options, or an empty string when nothing was preserved.</returns>
    internal static string OriginalLaunchOptions(string? wrapped)
    {
        if (string.IsNullOrWhiteSpace(wrapped) || ModeFor(wrapped) == LaunchWrapperMode.None)
        {
            return wrapped?.Trim() ?? "";
        }
        var placeholder = wrapped.IndexOf(CommandPlaceholder, StringComparison.Ordinal);
        var helper = wrapped.IndexOf(HelperFileName, StringComparison.OrdinalIgnoreCase);
        if (placeholder < 0 || helper < 0)
        {
            return "";
        }
        // Everything WSGM contributed sits between the helper path and the
        // placeholder; what brackets it is the user's. The match above lands on the
        // file name inside the (quoted) path, so walk back to where that token
        // starts — otherwise the rest of the path would be read as a user prefix.
        var quote = wrapped.LastIndexOf('"', helper);
        var start = quote >= 0 ? quote : wrapped.LastIndexOf(' ', helper) + 1;
        var prefix = wrapped[..start].TrimEnd();
        var suffix = wrapped[(placeholder + CommandPlaceholder.Length)..].Trim();
        return prefix.Length == 0
            ? suffix
            : $"{prefix} {CommandPlaceholder} {suffix}".TrimEnd();
    }

    /// <summary>Builds the value written into a non-Steam shortcut's Target field.</summary>
    /// <param name="helperPath">Absolute path of the wrapper executable.</param>
    /// <returns>The quoted wrapper path.</returns>
    /// <remarks>
    /// Steam stores this verbatim — it neither adds nor strips quotes — and its own
    /// shortcuts carry the quoted form, so the quotes must be supplied here.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="helperPath"/> is missing.</exception>
    internal static string ShortcutTarget(string helperPath) => Quote(helperPath);

    /// <summary>Builds the value written into a non-Steam shortcut's Launch Arguments.</summary>
    /// <param name="mode">Which wrapper behaviours to enable.</param>
    /// <param name="originalTarget">The shortcut's original Target, quoted or bare.</param>
    /// <param name="originalArguments">The shortcut's original Launch Arguments, if any.</param>
    /// <returns>The wrapper flags, the separator, then the program the shortcut used to run.</returns>
    /// <exception cref="ArgumentException"><paramref name="originalTarget"/> is missing, or
    /// <paramref name="mode"/> selects no behaviour.</exception>
    internal static string ShortcutArguments(
        LaunchWrapperMode mode,
        string originalTarget,
        string? originalArguments)
    {
        if (string.IsNullOrWhiteSpace(originalTarget))
        {
            throw new ArgumentException("An original target is required.", nameof(originalTarget));
        }

        // Steam's own Exe field is already quoted, so re-quoting it would produce a
        // doubly quoted path the wrapper could not resolve. Quote only bare values.
        var target = originalTarget.Trim();
        var command = target.StartsWith('"') ? target : Quote(target);
        return string.IsNullOrWhiteSpace(originalArguments)
            ? $"{FlagsFor(mode)} -- {command}"
            : $"{FlagsFor(mode)} -- {command} {originalArguments.Trim()}";
    }

    /// <summary>Reads back which behaviours a stored launch configuration selects.</summary>
    /// <param name="value">A launch-option or shortcut-argument string, possibly empty.</param>
    /// <returns>The behaviours the value enables, or <see cref="LaunchWrapperMode.None"/>.</returns>
    /// <remarks>
    /// Used to show what a game is already configured with. A value that does not
    /// reference the wrapper reports <see cref="LaunchWrapperMode.None"/> even when
    /// it contains the flag words, so unrelated launch options are never misread.
    /// </remarks>
    internal static LaunchWrapperMode ModeFor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.IndexOf(HelperFileName, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return LaunchWrapperMode.None;
        }

        var mode = LaunchWrapperMode.None;
        if (HasFlagToken(value, DeelevateFlag))
        {
            mode |= LaunchWrapperMode.Deelevate;
        }
        // Test the injecting flag FIRST and match on token boundaries: a plain
        // Contains would read "--input-lease" out of "--input-lease-inject" and
        // report both lease behaviours at once, which then trips the mutual
        // exclusion in FlagsFor the next time the game is re-applied.
        if (HasFlagToken(value, InputLeaseInjectFlag))
        {
            mode |= LaunchWrapperMode.InputLeaseInject;
        }
        else if (HasFlagToken(value, InputLeaseFlag))
        {
            mode |= LaunchWrapperMode.InputLease;
        }
        return mode;
    }

    /// <summary>Whether a value contains a flag as a whole command-line token.</summary>
    /// <param name="value">The launch-option or argument string to search.</param>
    /// <param name="flag">The flag to look for.</param>
    /// <returns>Whether the flag appears bounded by whitespace, a quote, or an end.</returns>
    private static bool HasFlagToken(string value, string flag)
    {
        for (var index = value.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = value.IndexOf(flag, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = index + flag.Length;
            var startsToken = index == 0 || char.IsWhiteSpace(value[index - 1]) || value[index - 1] == '"';
            var endsToken = end >= value.Length || char.IsWhiteSpace(value[end]) || value[end] == '"';
            if (startsToken && endsToken)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Selects the lease behaviour matching the current Steam Input
    /// Management setting.</summary>
    /// <param name="mode">The behaviours the user asked for.</param>
    /// <param name="shimManaged">Whether Steam Input Management is on.</param>
    /// <returns>The mode to actually write.</returns>
    /// <remarks>
    /// With the shim deployed a game rides the payload Steam already loaded; with it
    /// off there is nothing to ride, so the wrapper injects the way it always did.
    /// Applied exactly once, where the fix is written, so the clipboard text, the
    /// value written into Steam and the persisted snapshot can never disagree.
    /// </remarks>
    internal static LaunchWrapperMode ForCurrentInputMode(
        LaunchWrapperMode mode, bool shimManaged) =>
        shimManaged || !mode.HasFlag(LaunchWrapperMode.InputLease)
            ? mode
            : (mode & ~LaunchWrapperMode.InputLease) | LaunchWrapperMode.InputLeaseInject;

    /// <summary>Whether a shortcut's Target already points at the wrapper.</summary>
    /// <param name="target">The shortcut's current Target value.</param>
    /// <returns>Whether WSGM owns this shortcut's Target.</returns>
    internal static bool TargetsHelper(string? target) =>
        !string.IsNullOrWhiteSpace(target) &&
        target.IndexOf(HelperFileName, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Stops any running wrapper processes.</summary>
    /// <param name="reason">Why they are being stopped, for the log.</param>
    internal static void StopRunningHelpers(string reason) =>
        StopRunningHelpers(reason, timeout: null);

    /// <summary>Stops running wrappers while sharing one optional caller-owned wait budget.</summary>
    /// <param name="reason">Why they are being stopped, for the log.</param>
    /// <param name="timeout">Maximum combined process-exit wait, or null for the ordinary per-process bound.</param>
    internal static void StopRunningHelpers(string reason, TimeSpan timeout) =>
        StopRunningHelpers(reason, (TimeSpan?)timeout);

    private static void StopRunningHelpers(string reason, TimeSpan? timeout)
    {
        int currentSession = Process.GetCurrentProcess().SessionId;
        foreach (Process process in Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(HelperFileName)))
        {
            try
            {
                if (process.SessionId != currentSession)
                {
                    continue;
                }

                Log.Warn(
                    $"Launch wrapper pid {process.Id} is still active ({reason}); setup must "
                        + "defer replacement until its game exits.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not inspect launch wrapper pid {process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    // Long enough to identify the shim a user put in front of %command%, short
    // enough that a pathological launch-option value cannot flood wsgm.log.
    private const int PrefixLogLimit = 200;

    private const string DeelevateFlag = "--deelevate";
    private const string InputLeaseFlag = "--input-lease";
    private const string InputLeaseInjectFlag = "--input-lease-inject";

    private static string Quote(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A helper path is required.", nameof(path));
        }
        return SteamCustomLaunchCommand.Quote(path);
    }

    private static string FlagsFor(LaunchWrapperMode mode)
    {
        var flags = new List<string>(2);
        if (mode.HasFlag(LaunchWrapperMode.Deelevate))
        {
            flags.Add(DeelevateFlag);
        }
        if (mode.HasFlag(LaunchWrapperMode.InputLease))
        {
            flags.Add(InputLeaseFlag);
        }
        if (mode.HasFlag(LaunchWrapperMode.InputLeaseInject))
        {
            flags.Add(InputLeaseInjectFlag);
        }
        if (mode.HasFlag(LaunchWrapperMode.InputLease)
            && mode.HasFlag(LaunchWrapperMode.InputLeaseInject))
        {
            throw new ArgumentException(
                "A wrapper cannot both use the resident shim and inject.", nameof(mode));
        }
        if (flags.Count == 0)
        {
            throw new ArgumentException("At least one wrapper behaviour is required.", nameof(mode));
        }
        return string.Join(' ', flags);
    }
}
