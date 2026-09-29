using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What a foreground window means for per-application policy.</summary>
public enum ForegroundApplicationKind
{
    /// <summary>An ordinary application whose identity should drive per-application settings.</summary>
    Application,

    /// <summary>
    /// Shell furniture, a system dialog, or WSGM itself: the foreground changed, but what the user
    /// is <em>doing</em> did not.
    /// </summary>
    Restricted,
}

/// <summary>
/// Decides whether a foreground window is an application worth switching profiles for.
/// </summary>
/// <remarks>
/// The whole point of the restricted answer is that it is not the same as "no application". Alt-
/// tabbing to Task Manager, opening the Start menu, or WSGM's own overlay taking focus must leave
/// the running game's profile in force — dropping to the global profile because the user glanced at
/// a system window would change the power limit and frame cap underneath a running game.
/// <para>
/// Adopted from HandheldCompanion's process filter, which solves the same problem on the same
/// hardware, and kept deliberately short: this list exists to catch the windows that steal focus
/// without being what the user is using, not to enumerate every system executable.
/// </para>
/// </remarks>
public static class ForegroundApplicationFilter
{
    /// <summary>
    /// Window class of the UWP host. The real application lives in a different process.
    /// </summary>
    /// <remarks>
    /// Without resolving through it, every UWP application reports as
    /// <c>ApplicationFrameHost.exe</c> and they all share one profile.
    /// </remarks>
    public const string UwpHostWindowClass = "ApplicationFrameWindow";

    private static readonly HashSet<string> Restricted = new(StringComparer.OrdinalIgnoreCase)
    {
        // WSGM's own surfaces. Its overlay takes focus by design, and treating that as an
        // application switch would drop the game's profile every time the user opened the overlay —
        // which is the one moment they are most likely to be changing that profile.
        "wsgm.exe",
        "wsgm.launch.exe",
        "wsgm.devicelab.exe",

        // Launchers and their embedded browsers, adopted from HandheldCompanion's launcher list.
        // steamwebhelper.exe is the load-bearing entry: it renders Big Picture itself, so it is the
        // foreground process at the exact moment Steam reports a launch — and the identity-only
        // upgrade then paired it as the running game's executable until the game exited (observed
        // 2026-09-02: AppID 220 latched "RTSS profile steamwebhelper.exe").
        "steam.exe",
        "steamwebhelper.exe",
        "steamservice.exe",
        "gameoverlayui.exe",
        "epicgameslauncher.exe",
        "epicwebhelper.exe",
        "battle.net.exe",
        "agent.exe",
        "ubisoftconnect.exe",
        "upc.exe",
        "uplaywebcore.exe",
        "eadesktop.exe",
        "ealauncher.exe",
        "ealaunchhelper.exe",
        "eabackgroundservice.exe",
        "link2ea.exe",
        "galaxyclient.exe",
        "rockstarservice.exe",
        "bethesdanetlauncher.exe",
        "agsgamelaunchhelper.exe",
        "gamecenter.exe",
        "qtwebengineprocess.exe",

        // The shell itself, and the windows it puts in front of things.
        "explorer.exe",
        "searchhost.exe",
        "startmenuexperiencehost.exe",
        "shellexperiencehost.exe",
        "applicationframehost.exe",
        "systemsettings.exe",
        "textinputhost.exe",
        "widgets.exe",
        "lockapp.exe",

        // Focus-stealing system surfaces.
        "taskmgr.exe",
        "consent.exe",
        "credentialuibroker.exe",
        "dwm.exe",
        "sihost.exe",
        "ctfmon.exe",
        "fontdrvhost.exe",
        "csrss.exe",
        "winlogon.exe",
    };

    /// <summary>
    /// Classifies a foreground executable.
    /// </summary>
    /// <param name="executableName">File name of the foreground process, with extension.</param>
    /// <returns>Whether it should drive per-application policy.</returns>
    /// <remarks>
    /// An unreadable or empty name is restricted rather than treated as an application: a window
    /// whose process could not be resolved is exactly the case where guessing would attach the
    /// wrong profile.
    /// </remarks>
    public static ForegroundApplicationKind Classify(string? executableName) =>
        string.IsNullOrWhiteSpace(executableName) || Restricted.Contains(executableName.Trim())
            ? ForegroundApplicationKind.Restricted
            : ForegroundApplicationKind.Application;
}
