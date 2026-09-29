using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Core;

/// <summary>Describes one optional program started as part of a shell session.</summary>
public sealed class StartupAppConfig
{
    /// <summary>Executable path or protocol URL to launch.</summary>
    public string Path { get; set; } = "";

    /// <summary>Command-line arguments passed to an executable target.</summary>
    public string Args { get; set; } = "";

    /// <summary>Whether this entry participates in the shell startup sequence.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether the executable must inherit WSGM's elevated token.</summary>
    public bool Elevated { get; set; }
    /// <summary>Relaunch this tool automatically when its process dies (e.g. a
    /// crashed Handheld Companion leaves the device without controller input).</summary>
    public bool AutoRelaunch { get; set; }
}

/// <summary>Describes the optional system-wide keyboard shortcut for quick access.</summary>
public sealed class HotkeyConfig
{
    /// <summary>False = no keyboard shortcut at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether Control is required.</summary>
    public bool Ctrl { get; set; } = true;

    /// <summary>Whether Alt is required.</summary>
    public bool Alt { get; set; } = true;

    /// <summary>Whether Shift is required.</summary>
    public bool Shift { get; set; }

    /// <summary>Whether either Windows key is required.</summary>
    public bool Win { get; set; }
    /// <summary>Win32 virtual-key code. Default VK_HOME (0x24). 0 = unset.</summary>
    public int VirtualKey { get; set; } = 0x24;
}

/// <summary>Controller shortcut: a set of buttons pressed together, optionally held.
/// Modelled on Handheld Companion's chords — buttons accumulate until every button is
/// released, so they don't have to be pressed on the same frame.</summary>
public sealed class GamepadChordConfig
{
    /// <summary>False = no controller shortcut.</summary>
    public bool Enabled { get; set; }
    /// <summary>Bit mask of XInput buttons (see Input.GamepadButtons).</summary>
    public int Buttons { get; set; }
    /// <summary>True = must be held (~600 ms); false = a normal press.</summary>
    public bool Hold { get; set; }
}

/// <summary>Controls the raw-input edge-swipe activation areas.</summary>
/// <remarks>
/// The SteamOS edge map: top and bottom open WSGM's quick access sheet (bottom lands on
/// the Open apps strip and is ignored in desktop mode, where explorer's taskbar owns
/// that edge); left and right send Steam Big Picture's own menu shortcuts.
/// </remarks>
public sealed class GestureConfig
{
    /// <summary>Whether a swipe up from the bottom edge opens the quick access sheet
    /// with focus on its Open apps strip (game mode only).</summary>
    public bool BottomEdge { get; set; } = true;

    /// <summary>Whether a swipe down from the top edge opens the quick access sheet.</summary>
    public bool TopEdge { get; set; } = true;

    /// <summary>Whether a swipe from the left edge opens Steam's Big Picture menu.</summary>
    public bool LeftEdgeSteamMenu { get; set; } = true;

    /// <summary>Whether a swipe from the right edge opens Steam's Big Picture quick-access menu.</summary>
    public bool RightEdgeSteamQuickAccess { get; set; } = true;

    /// <summary>Strip thickness in physical pixels.</summary>
    public int StripThickness { get; set; } = 16;
}

/// <summary>Selects the controller-button glyph family rendered by the UI.</summary>
public enum GlyphStyle
{
    /// <summary>Xbox ABXY labels and artwork.</summary>
    Xbox,

    /// <summary>PlayStation Cross/Circle/Square/Triangle artwork.</summary>
    PlayStation,

    /// <summary>Nintendo ABXY labels and artwork.</summary>
    Nintendo,
}

/// <summary>One display's pre-game scaling, keyed by the GDI source device name
/// (\\.\DISPLAYn) so restore survives topology changes and later boots.</summary>
public sealed class DisplayScaleEntry
{
    /// <summary>GDI source device name, such as <c>\\.\DISPLAY1</c>.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Saved scale percentage to restore for this display.</summary>
    public int Percent { get; set; }
}

/// <summary>Selects how WSGM manages display settings during session-mode transitions.</summary>
public enum DisplayManagementMode
{
    /// <summary>Never change display settings.</summary>
    Off,
    /// <summary>Force game mode to 100% DPI and restore desktop DPI.</summary>
    DpiOnly,
    /// <summary>Capture the mode being left and restore the last profile for the mode entered.</summary>
    AutomaticProfiles,
    /// <summary>Apply the user-configured desktop and game profiles.</summary>
    FixedProfiles,
}

/// <summary>Resolution, refresh rate and DPI for one monitor in one session mode.</summary>
public sealed class DisplayModeValues
{
    /// <summary>Horizontal resolution in pixels.</summary>
    public int Width { get; set; }
    /// <summary>Vertical resolution in pixels.</summary>
    public int Height { get; set; }
    /// <summary>Refresh rate in hertz.</summary>
    public int RefreshRate { get; set; }
    /// <summary>Windows display scaling percentage.</summary>
    public int DpiPercent { get; set; } = 100;

    /// <summary>Whether HDR/advanced color is enabled when the monitor supports it.</summary>
    public bool HdrEnabled { get; set; }
}

/// <summary>Desktop and game-mode values for one GDI display source.</summary>
public sealed class MonitorDisplayProfile
{
    /// <summary>Stable monitor device identity used across topology reorderings.</summary>
    public string MonitorId { get; set; } = "";

    /// <summary>GDI source name, such as <c>\\.\DISPLAY1</c>.</summary>
    public string DeviceName { get; set; } = "";
    /// <summary>Friendly monitor label captured for the settings UI.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Whether Windows reports HDR/advanced-color support for this monitor.</summary>
    public bool HdrAvailable { get; set; }
    /// <summary>Values applied or captured in desktop mode.</summary>
    public DisplayModeValues Desktop { get; set; } = new();
    /// <summary>Values applied or captured in game mode.</summary>
    public DisplayModeValues Game { get; set; } = new();
}

/// <summary>One power scheme's CONSOLELOCK values as they were before WSGM wrote
/// them. -1 = value absent (Windows default applies).</summary>
public sealed class PowerSchemeConsoleLock
{
    /// <summary>Power-scheme GUID without surrounding braces.</summary>
    public string SchemeGuid { get; set; } = "";

    /// <summary>Saved AC value; <c>-1</c> means the setting was absent.</summary>
    public int AcValue { get; set; } = -1;

    /// <summary>Saved DC value; <c>-1</c> means the setting was absent.</summary>
    public int DcValue { get; set; } = -1;
}

/// <summary>Nine-grid anchor positions for a boot-splash element.</summary>
public enum SplashPlacementAnchor
{
    /// <summary>Top-left corner of the screen.</summary>
    TopLeft,

    /// <summary>Top edge, horizontally centered.</summary>
    TopCenter,

    /// <summary>Top-right corner of the screen.</summary>
    TopRight,

    /// <summary>Left edge, vertically centered.</summary>
    CenterLeft,

    /// <summary>Center of the screen.</summary>
    Center,

    /// <summary>Right edge, vertically centered.</summary>
    CenterRight,

    /// <summary>Bottom-left corner of the screen.</summary>
    BottomLeft,

    /// <summary>Bottom edge, horizontally centered.</summary>
    BottomCenter,

    /// <summary>Bottom-right corner of the screen.</summary>
    BottomRight,
}

/// <summary>How a boot-splash element is positioned on screen.</summary>
public enum SplashPlacementMode
{
    /// <summary>Positioned by a nine-grid anchor plus edge padding — the portable
    /// option that adapts to any screen size.</summary>
    Anchor,

    /// <summary>Positioned at absolute logical pixel coordinates (device-specific;
    /// anchors are the portable option).</summary>
    Absolute,

    /// <summary>Rendered inside the text stack (spinner/logo only), following the
    /// text element wherever it is placed.</summary>
    WithText,
}

/// <summary>Visual style of the boot-splash progress spinner.</summary>
public enum SplashSpinnerStyle
{
    /// <summary>The classic in-repo rotating arc ring.</summary>
    Ring,

    /// <summary>LoadingIndicators.Avalonia "Arc" mode.</summary>
    LiArc,

    /// <summary>LoadingIndicators.Avalonia "Arcs" mode.</summary>
    LiArcs,

    /// <summary>LoadingIndicators.Avalonia "ArcsRing" mode.</summary>
    LiArcsRing,

    /// <summary>LoadingIndicators.Avalonia "DoubleBounce" mode.</summary>
    LiDoubleBounce,

    /// <summary>LoadingIndicators.Avalonia "FlipPlane" mode.</summary>
    LiFlipPlane,

    /// <summary>LoadingIndicators.Avalonia "Pulse" mode.</summary>
    LiPulse,

    /// <summary>LoadingIndicators.Avalonia "Ring" mode.</summary>
    LiRing,

    /// <summary>LoadingIndicators.Avalonia "ThreeDots" mode.</summary>
    LiThreeDots,

    /// <summary>LoadingIndicators.Avalonia "Wave" mode.</summary>
    LiWave,

    /// <summary>In-repo sweeping line along a screen edge (see
    /// <see cref="SplashConfig.SweepEdge"/>).</summary>
    SweepLine,

    /// <summary>No spinner at all (no animation timer is created).</summary>
    Off,
}

/// <summary>Which screen edge the sweep-line spinner travels along.</summary>
public enum SweepEdge
{
    /// <summary>Sweep along the bottom edge of the screen.</summary>
    Bottom,

    /// <summary>Sweep along the top edge of the screen.</summary>
    Top,
}

/// <summary>Position of one boot-splash element (text, spinner, or logo).</summary>
public sealed class SplashElementPlacement
{
    /// <summary>How this element is positioned. <see cref="SplashPlacementMode.WithText"/>
    /// is honored for the spinner and logo only.</summary>
    public SplashPlacementMode Mode { get; set; } = SplashPlacementMode.Anchor;

    /// <summary>Nine-grid anchor used in <see cref="SplashPlacementMode.Anchor"/> mode.</summary>
    public SplashPlacementAnchor Anchor { get; set; } = SplashPlacementAnchor.Center;

    /// <summary>Horizontal padding in logical pixels from the anchored edge; ignored
    /// on a horizontally centered axis.</summary>
    public int PaddingX { get; set; } = 64;

    /// <summary>Vertical padding in logical pixels from the anchored edge; ignored
    /// on a vertically centered axis.</summary>
    public int PaddingY { get; set; } = 64;

    /// <summary>Absolute X coordinate in logical pixels for
    /// <see cref="SplashPlacementMode.Absolute"/> mode (device-specific — anchors
    /// are the portable option).</summary>
    public int X { get; set; }

    /// <summary>Absolute Y coordinate in logical pixels for
    /// <see cref="SplashPlacementMode.Absolute"/> mode (device-specific — anchors
    /// are the portable option).</summary>
    public int Y { get; set; }
}

/// <summary>Boot-splash customization: text, spinner, background, logo, and per-element
/// placement. Defaults reproduce the classic look (black background, white "Please
/// wait" with a ring spinner, centered). Colors are <c>#RRGGBB</c> strings parsed
/// with a logged fallback, so a bad value can never break the boot cover.</summary>
public sealed class SplashConfig
{
    /// <summary>Title text shown on the splash.</summary>
    public string Text { get; set; } = "Please wait";

    /// <summary>Whether the text block (title + caption) is rendered at all.</summary>
    public bool TextEnabled { get; set; } = true;

    /// <summary>Title text color as a <c>#RRGGBB</c> string.</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>Title font size in logical pixels.</summary>
    public int TitleFontSize { get; set; } = 26;

    /// <summary>Optional caption line rendered under the title; empty = no caption.</summary>
    public string Caption { get; set; } = "";

    /// <summary>Caption text color as a <c>#RRGGBB</c> string.</summary>
    public string CaptionColor { get; set; } = "#666666";

    /// <summary>Caption font size in logical pixels.</summary>
    public int CaptionFontSize { get; set; } = 12;

    /// <summary>Spinner style; <see cref="SplashSpinnerStyle.Off"/> disables the
    /// spinner and its animation timer.</summary>
    public SplashSpinnerStyle SpinnerStyle { get; set; } = SplashSpinnerStyle.Ring;

    /// <summary>Spinner color as a <c>#RRGGBB</c> string.</summary>
    public string SpinnerColor { get; set; } = "#FFFFFF";

    /// <summary>Spinner size (diameter/height) in logical pixels.</summary>
    public int SpinnerSize { get; set; } = 36;

    /// <summary>Edge the <see cref="SplashSpinnerStyle.SweepLine"/> spinner travels along.</summary>
    public SweepEdge SweepEdge { get; set; } = SweepEdge.Bottom;

    /// <summary>Background fill color as a <c>#RRGGBB</c> string.</summary>
    public string BackgroundColor { get; set; } = "#000000";

    /// <summary>Whether a radial vignette overlay darkens the background edges.</summary>
    public bool VignetteEnabled { get; set; }

    /// <summary>Full-screen background image path; empty = solid color only. A
    /// missing or unreadable file falls back to the color with a logged warning.</summary>
    public string BackgroundImagePath { get; set; } = "";

    /// <summary>Logo image path; empty = no logo. A missing or unreadable file is
    /// skipped with a logged warning.</summary>
    public string LogoImagePath { get; set; } = "";

    /// <summary>Maximum logo edge length in logical pixels (aspect ratio preserved).</summary>
    public int LogoMaxSize { get; set; } = 200;

    /// <summary>Placement of the text stack (title + caption).</summary>
    public SplashElementPlacement TextPlacement { get; set; } = new();

    /// <summary>Placement of the spinner; defaults to riding inside the text stack.</summary>
    public SplashElementPlacement SpinnerPlacement { get; set; } = new() { Mode = SplashPlacementMode.WithText };

    /// <summary>Placement of the logo; defaults to riding inside the text stack.</summary>
    public SplashElementPlacement LogoPlacement { get; set; } = new() { Mode = SplashPlacementMode.WithText };
}

/// <summary>One remembered SteamGridDB match: which SGDB game supplies artwork for a
/// local target app (typically a non-Steam shortcut, whose generated id has no SGDB
/// page). Re-picking a match in the artwork changer overwrites the entry.</summary>
public sealed class SgdbLinkConfig
{
    /// <summary>The local target app id (a shortcut's generated id).</summary>
    public long AppId { get; set; }

    /// <summary>The SteamGridDB game id supplying the art.</summary>
    public int SgdbGameId { get; set; }

    /// <summary>The SGDB game's display name (shown as the art source).</summary>
    public string Name { get; set; } = "";
}

/// <summary>What one game's launch configuration looked like before WSGM changed it.
/// Restoring the launch action writes these values back.</summary>
/// <remarks>
/// Load-bearing for non-Steam shortcuts: configuring one overwrites its Target with
/// the wrapper path, so without this snapshot the real program's path would survive
/// only inside the arguments string WSGM itself generated. Steam apps only need
/// <see cref="OriginalLaunchOptions"/>, which is usually empty.
/// </remarks>
public sealed class LaunchWrapperConfig
{
    /// <summary>The configured app id (a shortcut's generated id, or a Steam app id).</summary>
    public long AppId { get; set; }

    /// <summary>Whether this entry is a non-Steam shortcut rather than a Steam title.</summary>
    public bool IsShortcut { get; set; }

    /// <summary>Which wrapper behaviours were applied.</summary>
    public LaunchWrapperMode Mode { get; set; }

    /// <summary>Whether WSGM applied its launch wrapper or a Steam-native custom action.</summary>
    public LaunchConfigurationKind Kind { get; set; }

    /// <summary>The executable or script selected for a Steam-native custom action.</summary>
    public string CustomActionPath { get; set; } = "";

    /// <summary>User-supplied native arguments appended to the custom action.</summary>
    public string CustomArguments { get; set; } = "";

    /// <summary>The shortcut's Target before WSGM replaced it (shortcuts only).</summary>
    public string OriginalTarget { get; set; } = "";

    /// <summary>The launch options / launch arguments before WSGM replaced them.</summary>
    public string OriginalLaunchOptions { get; set; } = "";

    /// <summary>The shortcut's start directory at configuration time, recorded for
    /// diagnostics; WSGM deliberately never changes it.</summary>
    public string OriginalStartDir { get; set; } = "";

    /// <summary>Display name at configuration time, so the overlay can name an entry
    /// whose game Steam can no longer resolve.</summary>
    public string Name { get; set; } = "";
}

/// <summary>Identifies the kind of per-game launch configuration WSGM owns.</summary>
public enum LaunchConfigurationKind
{
    /// <summary>The existing de-elevation or Steam Input wrapper.</summary>
    Wrapper,

    /// <summary>A Steam-native executable or script launch action.</summary>
    CustomAction,
}

/// <summary>One Steam library on a removable drive (a MicroSD card or external
/// drive), tracked so WSGM can render it as a Steam collection ("library tab").
/// Keyed by the card's <c>libraryfolder.vdf</c> content id, which is stable across
/// drive-letter changes and reinserts. Games are remembered so the tab persists
/// while the card is ejected.</summary>
public sealed class CardLibraryConfig
{
    /// <summary>The card's library content id — its stable identity.</summary>
    public string ContentId { get; set; } = "";

    /// <summary>Display/collection name (the card's label, or a fallback).</summary>
    public string Name { get; set; } = "";

    /// <summary>Whether a Steam collection ("tab") is maintained for this card.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hidden from the card manager list (still tracked, no tab). Mirrors
    /// MicroSDeck's per-card hide.</summary>
    public bool Hidden { get; set; }

    /// <summary>App ids installed on the card (remembered while it is ejected).</summary>
    public List<long> AppIds { get; set; } = [];

    /// <summary>The Steam-side library label as last seen in sync with
    /// <see cref="Name"/>. Names follow Steam only while Name equals this value;
    /// a WSGM-side rename leaves it stale until Steam's config catches up, which
    /// is what stops a lagging libraryfolders.vdf from reverting the rename.</summary>
    public string LastSteamLabel { get; set; } = "";
}

/// <summary>One user-built custom library tab: a WSGM-owned Steam collection whose
/// membership is recomputed by evaluating <see cref="FilterTree"/> over the library.
/// The TabMaster analog, materialized as a native Steam collection.</summary>
public sealed class CustomTabConfig
{
    /// <summary>Stable unique identity, independent of the editable display name.</summary>
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");

    /// <summary>Display name (also the Steam collection's name).</summary>
    public string Name { get; set; } = "";

    /// <summary>Whether the tab is synced (a disabled tab's collection is removed).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sort order in the builder list (ascending).</summary>
    public int Position { get; set; }

    /// <summary>Category prefilter bitfield (see <see cref="LibraryFilter.Categories"/>);
    /// 0 defaults to Games at evaluation time.</summary>
    public int Categories { get; set; } = (int)LibraryFilter.Categories.Games;

    /// <summary>The top-level filter group. Its <see cref="FilterNode.Mode"/> is the
    /// tab's AND/OR; its children are the filters.</summary>
    public FilterNode FilterTree { get; set; } = new() { Kind = FilterKind.Merge };
}

/// <summary>One of Steam's own library tabs as last observed in the tab strip,
/// remembered so the tab-order UI can list native tabs with their real (localized)
/// titles even while Steam is closed.</summary>
public sealed class NativeTabConfig
{
    /// <summary>Steam's stable tab id (e.g. <c>AllGames</c>, <c>Collections</c>).</summary>
    public string Id { get; set; } = "";

    /// <summary>The tab's display title as Steam rendered it.</summary>
    public string Title { get; set; } = "";
}

/// <summary>Persistent shared RTSS policy projected into overlay and native QAM.</summary>
public sealed class PerformanceConfig
{
    /// <summary>Whether WSGM may observe and change supported RTSS profile properties.</summary>
    public bool Enabled { get; set; }

    /// <summary>Global RTSS frame limit; zero disables limiting.</summary>
    public int? FrameLimit { get; set; }

    /// <summary>Global performance-overlay level when the adapter advertises a verified mapping.</summary>
    public int? OverlayLevel { get; set; }

    /// <summary>How a frame limit relates to the panel's refresh rate.</summary>
    /// <remarks>
    /// Global rather than per-application: it decides whether WSGM changes display modes at all,
    /// which is a tolerance for mode-change risk the user holds once, not per game.
    /// </remarks>
    public FrameLimitStrategy FrameLimitStrategy { get; set; } = FrameLimitStrategy.FrameLimitOnly;

    /// <summary>Global sustained power limit in watts, or null to leave it to the device.</summary>
    public int? TdpWatts { get; set; }

    /// <summary>Global variable-refresh preference, or null to leave the panel as found.</summary>
    public bool? VariableRefreshRate { get; set; }

    /// <summary>Custom overlay (level 4) widget order — HandheldCompanion's widget names,
    /// comma-separated; unknown names are ignored.</summary>
    public string OsdCustomOrder { get; set; } = "Time,GPU,CPU,VRAM,RAM,BATT,FPS";

    /// <summary>Custom overlay clock detail: 0 hidden, 1 short time, 2 full timestamp.</summary>
    public int OsdCustomTime { get; set; } = 2;

    /// <summary>Custom overlay framerate detail: 0 hidden, 1 FPS, 2 FPS and frametime.</summary>
    public int OsdCustomFps { get; set; } = 2;

    /// <summary>Custom overlay CPU detail: 0 hidden, 1 load and power, 2 adds temperature.</summary>
    public int OsdCustomCpu { get; set; } = 2;

    /// <summary>Custom overlay memory detail: 0 hidden, 1 used, 2 used of total.</summary>
    public int OsdCustomRam { get; set; } = 2;

    /// <summary>Custom overlay GPU detail: 0 hidden, 1 load and power, 2 adds temperature.</summary>
    public int OsdCustomGpu { get; set; } = 2;

    /// <summary>Custom overlay video-memory detail: 0 hidden, 1 used, 2 used of total.</summary>
    public int OsdCustomVram { get; set; } = 2;

    /// <summary>Custom overlay battery detail: 0 hidden, 1 percent and remaining time, 2 adds
    /// the charge rate.</summary>
    public int OsdCustomBattery { get; set; } = 2;

    /// <summary>Per-application overrides keyed by WSGM's canonical application identity.</summary>
    public List<PerformanceApplicationConfig> Applications { get; set; } = [];
}

/// <summary>One persistent RTSS application-profile override.</summary>
public sealed class PerformanceApplicationConfig
{
    /// <summary>Canonical WSGM application identity.</summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>Exact executable profile name understood by RTSS.</summary>
    public string RtssProfileName { get; set; } = string.Empty;

    /// <summary>Application frame-limit override, or null to inherit global policy.</summary>
    public int? FrameLimit { get; set; }

    /// <summary>Application overlay-level override, or null to inherit global policy.</summary>
    public int? OverlayLevel { get; set; }

    /// <summary>
    /// Whether this application's own values apply at all.
    /// </summary>
    /// <remarks>
    /// The switch behind Steam's "Use per-game profile". Off keeps the stored values so turning it
    /// back on restores what the user set up rather than starting from the global defaults again —
    /// the same reversibility the device master switch has.
    /// </remarks>
    public bool UsePerGameProfile { get; set; }

    /// <summary>Application sustained power limit in watts, or null to inherit.</summary>
    public int? TdpWatts { get; set; }

    /// <summary>Application variable-refresh preference, or null to inherit.</summary>
    public bool? VariableRefreshRate { get; set; }
}

/// <summary>Persisted user settings and exact Windows-state snapshots for WSGM.</summary>
public sealed class AppConfig
{
    /// <summary>Optional device-plugin platform, ownership, and desired-state settings.</summary>
    public DeviceIntegrationConfig DeviceIntegration { get; set; } = new();

    /// <summary>Optional RTSS policy, independent from Device Integration.</summary>
    public PerformanceConfig Performance { get; set; } = new();

    /// <summary>Restart Steam automatically when it exits. Steam itself is located
    /// via the registry (see Core.Steam) — there is nothing else to configure.</summary>
    public bool SteamAutoRelaunch { get; set; }

    /// <summary>Whether the complete Steam client is launched at medium integrity.</summary>
    /// <remarks>
    /// Off by default, which starts Steam at WSGM's own integrity. Elevated Steam is the deliberate
    /// default because several WSGM mechanisms drive the running client and a mismatched pair loses
    /// UIPI messages, but it also elevates every game Steam starts. This is the user-owned choice
    /// between the two, and it applies to Steam itself rather than to individual games, which
    /// <c>WSGM.Launch</c> de-elevates independently.
    /// </remarks>
    public bool SteamLaunchUnelevated { get; set; }

    /// <summary>Keep a card's injected library tab after the card is
    /// ejected. The games show as not-installed until it is reinserted.</summary>
    public bool KeepEjectedCardTabs { get; set; } = true;

    /// <summary>Tracked removable Steam libraries, keyed by content id, used to
    /// maintain per-card injected library tabs.</summary>
    public List<CardLibraryConfig> CardLibraries { get; set; } = [];

    /// <summary>Cards forgotten while still inserted. Discovery skips these identities
    /// until a scan observes them absent, so Forget does not immediately undo itself.</summary>
    public List<string> ForgottenInsertedCardIds { get; set; } = [];

    /// <summary>User-built custom filter tabs (the TabMaster analog).</summary>
    public List<CustomTabConfig> CustomTabs { get; set; } = [];

    /// <summary>The library tab strip's display order as tab keys — Steam's native ids
    /// (<c>AllGames</c>, <c>Collections</c>, …) and injected WSGM ids
    /// (<c>wsgm-custom-…</c>, <c>wsgm-card-…</c>) mixed freely. Tabs not listed keep
    /// their natural order after the listed ones; empty means Steam's default order
    /// with WSGM tabs appended.</summary>
    public List<string> LibraryTabOrder { get; set; } = [];

    /// <summary>Native Steam tab ids the user removed from the library tab strip.</summary>
    public List<string> HiddenNativeTabs { get; set; } = [];

    /// <summary>Native tabs as last observed in Steam's strip (id + localized title),
    /// captured on every tab sync so the tab-order UI reflects the running Steam.</summary>
    public List<NativeTabConfig> KnownNativeTabs { get; set; } = [];

    /// <summary>Optional SteamGridDB API key. No key is bundled; set a free personal
    /// key from steamgriddb.com to enable artwork search.</summary>
    public string SteamGridDbApiKey { get; set; } = "";

    /// <summary>Remembered SteamGridDB game matches for targets whose Steam app id
    /// cannot be looked up there (non-Steam shortcuts) — so the artwork changer does
    /// not re-ask which game a shortcut is on every visit.</summary>
    public List<SgdbLinkConfig> SgdbLinks { get; set; } = [];

    /// <summary>Games WSGM has pointed at the launch wrapper, with the launch
    /// configuration each had beforehand so removing the wrapper can restore it.</summary>
    public List<LaunchWrapperConfig> LaunchWrappers { get; set; } = [];

    /// <summary>Programs to start before Steam, in launch order.</summary>
    public List<StartupAppConfig> StartupApps { get; set; } = [];
    /// <summary>Delay before the FIRST startup app. Apps launch a few hundred ms
    /// into the logon session, right after the game-mode display-scale change —
    /// tools started into that window can hang (device-observed with Handheld
    /// Companion, intermittent). This lets the session and the DPI change settle.</summary>
    public int StartupDelayMs { get; set; } = 3000;

    /// <summary>Delay between enabled startup-app launches, in milliseconds.</summary>
    public int StaggerDelayMs { get; set; } = 1500;

    /// <summary>Extra delay before Steam Big Picture is started at logon.</summary>
    public int SteamDelayMs { get; set; } = 0;
    /// <summary>Mute system audio only while the screen is off and Steam reports an
    /// active download (see Shell\DisplayOffMuteService). Screen-off alone stays
    /// audible; download completion restores after a short grace period, and display
    /// wake restores immediately. Only a mute WSGM applied itself is undone.</summary>
    public bool MuteWhileDisplayOff { get; set; }
    /// <summary>Fullscreen "Please wait" cover at logon that hides startup-app
    /// window flashes until Steam Big Picture is on screen (see Shell\BootSplash).</summary>
    public bool BootSplashEnabled { get; set; } = true;

    /// <summary>Boot-splash appearance customization (see <see cref="SplashConfig"/>);
    /// <see cref="BootSplashEnabled"/> controls whether the splash runs at all.</summary>
    public SplashConfig Splash { get; set; } = new();

    /// <summary>UI accent color as an <c>#AARRGGBB</c>/<c>#RRGGBB</c> string, applied
    /// to the Fluent theme and the Hc accent tokens at startup and on save.</summary>
    public string AccentColor { get; set; } = Themes.AccentPalette.DefaultAccent;
    /// <summary>Whether the logon service boots the session into game mode. Projected
    /// into boot.json (see Core\BootManifest) because the SYSTEM service never parses
    /// this file. False = sign-in leaves the plain desktop alone.</summary>
    public bool GameModeBootEnabled { get; set; } = true;

    /// <summary>Settle delay after explorer's shell window and taskbar both exist,
    /// before the boot takeover cleanly shuts explorer down. Covers the logon prep
    /// (Run keys, Startup folder, session services) that must complete once per
    /// sign-in for touch features to survive game mode.</summary>
    public int ExplorerLogonSettleMs { get; set; } = 5000;

    /// <summary>Whether WSGM manages the Steam Input lease around its focused
    /// surfaces (overlay/taskbar). Off = the lease is never acquired: Steam Input's
    /// desktop profile may take the controller while a WSGM panel is open, but
    /// nothing is ever injected into Steam.</summary>
    /// <remarks>
    /// Read when a surface opens, so turning it off takes effect at the NEXT surface
    /// open — no restart, but not mid-surface either. A lease already applied for a
    /// surface that is still on screen is deliberately never released early: the
    /// release hands the controller back to Steam's desktop profile, which swallows
    /// it from SDL system-wide, so the user who just turned this off from an open
    /// Settings window would lose controller navigation on that very click. The lease
    /// is scoped to the surface lifetime by specification — see docs\steam-input.md
    /// and src\WSGM\Overlay\AGENTS.md.
    /// </remarks>
    public bool SteamInputLeaseEnabled { get; set; } = true;

    /// <summary>Whether WSGM deploys its Steam Input shim into Steam's own install
    /// directory as a search-order proxy DLL, so Steam loads it itself and WSGM
    /// never writes into the Steam process.</summary>
    /// <remarks>
    /// Off parks the deployed file beside itself instead of deleting it, and every
    /// lease WSGM takes for its own surfaces fails open. <see cref="SteamInputLeaseEnabled"/>
    /// still decides whether the block is asked for at all; this decides how it gets
    /// into Steam. Absent from an older config.json it defaults on, which is what
    /// carries an upgrading device across without losing controller navigation.
    /// </remarks>
    public bool SteamInputManagementEnabled { get; set; } = true;

    /// <summary>Which revision of the first-run Quick Setup this device has completed.</summary>
    /// <remarks>
    /// An int rather than a bool so a later build that adds a setting needing an
    /// explicit decision can raise <see cref="QuickSetup.CurrentRevision"/> and have
    /// the panel appear once more, showing only what is new. Zero means the panel
    /// has never been completed.
    /// </remarks>
    public int QuickSetupRevision { get; set; }

    /// <summary>Steam CEF integration master switch and per-feature sub-toggles
    /// (see <see cref="CefConfig"/>).</summary>
    public CefConfig Cef { get; set; } = new();

    /// <summary>Keyboard shortcut configuration for opening the overlay.</summary>
    public HotkeyConfig Hotkey { get; set; } = new();

    /// <summary>Controller shortcut configuration for opening the overlay.</summary>
    public GamepadChordConfig GamepadChord { get; set; } = new();

    /// <summary>Touch-edge gesture configuration for opening the overlay.</summary>
    public GestureConfig Gestures { get; set; } = new();

    /// <summary>Rows pinned to the quick access sheet's home tab, as stable row ids
    /// (a row's <c>Tag</c>: <c>home.steam</c>, <c>system.keep-awake</c>, a device
    /// capability key, …) in display order. An id the running build cannot resolve
    /// is kept (a device plugin's row survives the device being unplugged) but not
    /// rendered.</summary>
    public List<string> QuickAccessPins { get; set; } = [];

    /// <summary>Controller glyph family displayed by the UI.</summary>
    public GlyphStyle GlyphStyle { get; set; } = GlyphStyle.Xbox;

    /// <summary>Per-display scaling captured before game mode forced 100%. Non-empty
    /// means "not yet restored" — survives crashes so recovery paths can put
    /// scaling back, matched per display via the GDI source device name.</summary>
    public List<DisplayScaleEntry> SavedDisplayScaleEntries { get; set; } = [];

    /// <summary>Controls whether WSGM leaves displays alone, changes DPI only, or manages full profiles.</summary>
    public DisplayManagementMode DisplayManagement { get; set; } = DisplayManagementMode.DpiOnly;

    /// <summary>Per-monitor desktop and game-mode display profiles.</summary>
    public List<MonitorDisplayProfile> DisplayProfiles { get; set; } = [];
    /// <summary>The Winlogon Shell snapshot that existed before WSGM installed itself.
    /// Presence is separate from the string so an empty value remains distinguishable
    /// from an absent value; kind preserves REG_EXPAND_SZ as well as REG_SZ.</summary>
    public string? PreviousShellValue { get; set; }

    /// <summary>Whether the original Winlogon Shell value has been captured.</summary>
    public bool PreviousShellSnapshotCaptured { get; set; }

    /// <summary>Whether the original Winlogon Shell value existed.</summary>
    public bool PreviousShellValueExists { get; set; }

    /// <summary>Registry type of the original Winlogon Shell value.</summary>
    public RegistryValueKind PreviousShellValueKind { get; set; } = RegistryValueKind.String;

    /// <summary>Snapshot of GamingConfiguration\StartupToGamingHome, which is changed
    /// while WSGM is installed to keep Xbox Full Screen Experience from competing
    /// for the session.</summary>
    public int PreviousStartupToGamingHomeValue { get; set; }

    /// <summary>Whether the original GamingConfiguration value has been captured.</summary>
    public bool PreviousStartupToGamingHomeSnapshotCaptured { get; set; }

    /// <summary>Whether the original GamingConfiguration value existed.</summary>
    public bool PreviousStartupToGamingHomeValueExists { get; set; }

    /// <summary>Registry type of the original GamingConfiguration value.</summary>
    public RegistryValueKind PreviousStartupToGamingHomeValueKind { get; set; } = RegistryValueKind.DWord;

    /// <summary>UAC prompt-level values as they were before WSGM lowered them,
    /// so the change can be undone exactly.</summary>
    public bool PreviousUacSnapshotCaptured { get; set; }

    /// <summary>Original administrator-consent prompt level.</summary>
    public int PreviousUacConsentPrompt { get; set; } = 5;

    /// <summary>Original secure-desktop prompt setting.</summary>
    public int PreviousUacSecureDesktop { get; set; } = 1;

    /// <summary>Whether the lock-on-wake state was captured before WSGM changed it
    /// (the exact per-scheme values live in the fields below).</summary>
    public bool PreviousLockOnWakeSnapshotCaptured { get; set; }

    /// <summary>Previous HKLM Personalization\NoLockScreen value (-1 = absent).</summary>
    public int PreviousNoLockScreen { get; set; } = -1;

    /// <summary>Per-power-scheme CONSOLELOCK values (AC and DC) as they were before
    /// WSGM flattened them to 0, so restore is exact even for mixed setups.</summary>
    public List<PowerSchemeConsoleLock> PreviousConsoleLockSchemeValues { get; set; } = [];
    /// <summary>True when the CONSOLELOCK policy key already existed before WSGM;
    /// false means WSGM created it and restore deletes the whole key.</summary>
    public bool PreviousConsoleLockPolicyKeyExisted { get; set; }

    /// <summary>Pre-existing CONSOLELOCK policy values (-1 = value absent).</summary>
    public int PreviousConsoleLockPolicyAc { get; set; } = -1;

    /// <summary>Pre-existing DC CONSOLELOCK policy value; <c>-1</c> means absent.</summary>
    public int PreviousConsoleLockPolicyDc { get; set; } = -1;
}

/// <summary>Decides when the first-run Quick Setup panel is shown.</summary>
/// <remarks>
/// Keyed on a revision rather than a "seen it" flag so the panel can come back
/// exactly once when a later build adds a setting that needs an explicit decision -
/// the same way Steam Input Management needed one. Raising
/// <see cref="CurrentRevision"/> is the whole trigger; everything else follows from
/// the comparison, and a user who has already answered a revision is never asked
/// about it again.
/// </remarks>
public static class QuickSetup
{
    /// <summary>The revision this build asks about.</summary>
    /// <remarks>
    /// Revision 1 introduced Steam Input Management, which writes a file into
    /// Steam's own install directory, and the Steam CEF integration master switch.
    /// Raise this only when a NEW setting genuinely needs the user's decision -
    /// every raise interrupts every existing device once.
    /// </remarks>
    public const int CurrentRevision = 1;

    /// <summary>Whether the panel should be shown for the given configuration.</summary>
    /// <param name="config">The configuration to test.</param>
    /// <returns><see langword="true"/> when this device has not answered the current revision.</returns>
    public static bool ShouldShow(AppConfig config) =>
        config.QuickSetupRevision < CurrentRevision;

    /// <summary>Records that the current revision has been answered.</summary>
    /// <param name="config">The configuration to stamp.</param>
    public static void MarkCompleted(AppConfig config) =>
        config.QuickSetupRevision = CurrentRevision;
}

/// <summary>Master switch and per-feature sub-toggles for WSGM's Steam CEF
/// (Chromium Embedded Framework) integration — everything WSGM injects into Steam
/// over its debug port. <see cref="Enabled"/> off means WSGM never writes or uses
/// the CEF debug flag at all (no injection, and the sub-features are hidden from
/// the overlay); the sub-toggles gate individual injected features while CEF is on.
/// Every flag defaults on, so an existing install behaves exactly as before.</summary>
public sealed class CefConfig
{
    /// <summary>Master CEF switch. Off = the debug-port flag is never written, no
    /// injection is attempted, and every CEF feature below is hidden.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Injected library filter tabs plus the tab-strip order and native-tab
    /// hiding — one subsystem (<c>SteamLibraryTabs</c>).</summary>
    public bool LibraryTabs { get; set; } = true;

    /// <summary>The SD-card library manager: per-card injected library tabs, the
    /// "On: &lt;card&gt;" game-page badges, and live library relabeling.</summary>
    public bool CardManager { get; set; } = true;

    /// <summary>Format SD Card and register its library into the running Steam. The
    /// whole feature (native disk format included) is hidden when off.</summary>
    public bool SdFormat { get; set; } = true;

    /// <summary>Shortcut artwork changer (SteamGridDB) applied via Steam's client API.</summary>
    public bool Artwork { get; set; } = true;

    /// <summary>Big Picture header Wi-Fi indicator (feeds Steam's <c>SystemNetworkStore</c>).</summary>
    public bool WifiIndicator { get; set; } = true;

    /// <summary>
    /// Narrow, fingerprint-gated native Quick Access bootstrap over the persistent Steam UI host.
    /// </summary>
    public bool NativeQuickAccess { get; set; } = true;

    /// <summary>Automatic wake lock while the running Steam client reports an active
    /// download (polled over the CEF bridge), so the device finishes downloading
    /// instead of entering standby. The quick-access Power tab's manual Keep Awake
    /// cycle works regardless of this flag.</summary>
    public bool DownloadKeepAwake { get; set; } = true;

    /// <summary>Name/Size/Type sort buttons injected into the header of Big Picture's
    /// download queue (<c>SteamDownloadSort</c>).</summary>
    public bool DownloadQueueSort { get; set; } = true;
}

/// <summary>Source-generated JSON metadata for the persisted <see cref="AppConfig"/> contract.</summary>
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(CefConfig))]
[JsonSerializable(typeof(SplashConfig))]
[JsonSerializable(typeof(SgdbLinkConfig))]
[JsonSerializable(typeof(LaunchWrapperConfig))]
[JsonSerializable(typeof(CardLibraryConfig))]
[JsonSerializable(typeof(CustomTabConfig))]
[JsonSerializable(typeof(NativeTabConfig))]
[JsonSerializable(typeof(DeviceIntegrationConfig))]
[JsonSerializable(typeof(DeviceDesiredProfile))]
[JsonSerializable(typeof(DeviceCapabilityPreference))]
[JsonSerializable(typeof(PluginSettingsScope))]
// The SDK's own manifest types, so the cached declaration keeps one shape owned by the SDK rather
// than a WSGM-side copy that would have to be kept in step with it.
[JsonSerializable(typeof(PluginSettingsManifest))]
[JsonSerializable(typeof(PluginSettingValue))]
[JsonSerializable(typeof(DeviceAuthoredProfile))]
[JsonSerializable(typeof(AuthoredCurvePoint))]
[JsonSerializable(typeof(DeviceProfileSelection))]
[JsonSerializable(typeof(DeviceApplicationProfileSelection))]
[JsonSerializable(typeof(PerformanceConfig))]
[JsonSerializable(typeof(PerformanceApplicationConfig))]
[JsonSerializable(typeof(FilterNode))]
[JsonSerializable(typeof(DeviceCoordinatorDiagnosticsSnapshot))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal partial class ConfigJsonContext : JsonSerializerContext
{
}
