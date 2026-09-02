using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using static WSGM.Interop.NativeDisplay;

namespace WSGM.Core;

/// <summary>Owns the display state WSGM changes through DisplayConfig packets: per-monitor
/// scaling (the 100%/125%/150% setting) via the undocumented-but-ABI-stable DPI packets — the
/// same mechanism the Settings app uses, applying INSTANTLY with no logoff (live-verified) and
/// PERSISTING in the registry — and Windows HDR (advanced color) per active display. Scaling
/// persistence is why the pre-game values are stored in WSGM's config and restored on desktop
/// mode, clean exit, panic, and recovery. Game mode runs at 100% so DPI-unaware games render
/// 1:1 on the panel. HDR is queried and set on the path TARGET, never the GDI source; see
/// docs\power-and-display.md. This class also dispatches the Game/Desktop display modes;
/// profile-based modes delegate to <see cref="DisplayProfiles"/>.</summary>
public static unsafe class DisplayScale
{
    // Index 0 = 100%. Recommended = DpiVals[abs(MinScaleRel)].
    private static readonly uint[] DpiVals = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    /// <summary>Game mode: capture ALL current per-display scalings into the config
    /// (unless a crashed session already left captured values there), persist them,
    /// and only then drop every display to 100% — capture-then-set ordering so a
    /// crash between the two can never lose the originals. When the save fails,
    /// scaling is left untouched.</summary>
    public static void ApplyGameMode(AppConfig config)
    {
        if (config.DisplayManagement == DisplayManagementMode.Off)
        {
            return;
        }
        if (config.DisplayManagement is DisplayManagementMode.AutomaticProfiles or DisplayManagementMode.FixedProfiles)
        {
            DisplayProfiles.Transition(config, enteringGameMode: true);
            return;
        }
        var sources = GetActiveSources();
        if (sources.Count == 0)
        {
            Log.Warn("Display scale: no active display sources found.");
            return;
        }

        var freshCapture = config.SavedDisplayScaleEntries.Count == 0;
        var captured = new List<DisplayScaleEntry>();
        var toLower = new List<((Luid Adapter, uint SourceId) Source, uint Current)>();
        foreach (var source in sources)
        {
            if (!TryGetScale(source, out var current, out _, out _) || current == 100)
            {
                continue;
            }
            var name = GetSourceDeviceName(source.Adapter, source.SourceId);
            if (name.Length == 0)
            {
                // A ""-named entry can never be matched by name on restore, so it
                // would sit in the config forever, re-logged on every restore.
                // Leave this display's scaling untouched rather than lower a value
                // we could not identify for restore.
                Log.Warn($"Display scale: device name query failed for a display at {current}% — leaving it unchanged.");
                continue;
            }
            captured.Add(new DisplayScaleEntry { DeviceName = name, Percent = (int)current });
            // A surviving snapshot means the previous game-mode session did not
            // finish restoring every display.  A dock/undock can expose a different
            // active source before recovery runs; never force that new display to
            // 100% without first owning its desktop-scale snapshot.  Existing named
            // entries are safe to lower again because their original value is still
            // recoverable.
            if (ShouldLowerDisplay(freshCapture, config.SavedDisplayScaleEntries, name))
            {
                toLower.Add((source, current));
            }
        }

        if (freshCapture && captured.Count > 0)
        {
            try
            {
                PersistScaleEntries(captured);
                config.SavedDisplayScaleEntries = captured;
            }
            catch (Exception ex)
            {
                Log.Warn($"Display scale: could not persist saved values — leaving scaling unchanged: {ex.Message}");
                return;
            }
        }

        foreach (var (source, current) in toLower)
        {
            if (TrySetScale(source, 100))
            {
                Log.Info($"Display scale -> 100% (was {current}%).");
            }
        }
    }

    /// <summary>Recovery path for clean exit, panic, uninstall and shell repair.
    /// Full-profile modes apply the last known Desktop profile without capturing;
    /// any pending DPI snapshot is restored regardless of the current mode.</summary>
    public static void RestoreSaved(AppConfig config)
    {
        // Consume a DPI-only snapshot first when the user changed modes while game
        // mode was active. A configured Desktop profile then wins as the final
        // state rather than being overwritten by that cleanup.
        RestoreDpiSnapshot(config);
        if (config.DisplayManagement is DisplayManagementMode.AutomaticProfiles or DisplayManagementMode.FixedProfiles)
        {
            // Recovery must never capture the possibly half-torn-down mode as a
            // new preference. Apply the last known desktop profile directly.
            DisplayProfiles.ApplySaved(config.DisplayProfiles, game: false);
        }
    }

    /// <summary>Handles an intentional transition into desktop mode. Automatic
    /// profiles capture the game values being left; Off performs no new display
    /// changes, while still leaving crash recovery to <see cref="RestoreSaved"/>.</summary>
    public static void ApplyDesktopMode(AppConfig config)
    {
        if (config.DisplayManagement == DisplayManagementMode.Off)
        {
            return;
        }
        if (config.DisplayManagement is DisplayManagementMode.AutomaticProfiles or DisplayManagementMode.FixedProfiles)
        {
            DisplayProfiles.Transition(config, enteringGameMode: false);
            return;
        }
        RestoreDpiSnapshot(config);
    }

    private static void RestoreDpiSnapshot(AppConfig config)
    {
        if (config.SavedDisplayScaleEntries.Count == 0)
        {
            return;
        }
        var sources = GetActiveSources();
        if (sources.Count == 0)
        {
            Log.Warn("Display scale: no active display sources — keeping saved values for a later restore.");
            return;
        }
        var named = new List<((Luid Adapter, uint SourceId) Source, string Name)>();
        foreach (var source in sources)
        {
            named.Add((source, GetSourceDeviceName(source.Adapter, source.SourceId)));
        }

        var remaining = new List<DisplayScaleEntry>();
        var positional = 0;   // next active source for ""-named entries
        foreach (var entry in config.SavedDisplayScaleEntries)
        {
            if (entry.Percent is not (>= 100 and <= 500))
            {
                continue;   // garbage value — dropping it is the only safe move
            }
            if (string.IsNullOrEmpty(entry.DeviceName))
            {
                // Written by an older build whose name query failed: "" can never
                // match by name, so pair it positionally by enumeration order —
                // and never re-save it, or it would block in the config forever,
                // warned about on every restore.
                if (positional < named.Count &&
                    TrySetScale(named[positional].Source, (uint)entry.Percent))
                {
                    Log.Info($"Display scale restored to {entry.Percent}% (unnamed legacy entry, positional -> '{named[positional].Name}').");
                }
                else
                {
                    Log.Warn($"Display scale: dropping unmatchable unnamed entry ({entry.Percent}%).");
                }
                positional++;
                continue;
            }
            var idx = named.FindIndex(s => string.Equals(s.Name, entry.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                Log.Warn($"Display scale: display '{entry.DeviceName}' not active — keeping {entry.Percent}% for a later restore.");
                remaining.Add(entry);
                continue;
            }
            if (TrySetScale(named[idx].Source, (uint)entry.Percent))
            {
                Log.Info($"Display scale restored to {entry.Percent}% ({entry.DeviceName}).");
            }
            else
            {
                remaining.Add(entry);   // transient set failure — retry on the next restore path
            }
        }
        config.SavedDisplayScaleEntries = remaining;
        try { PersistScaleEntries(remaining); } catch (Exception ex) { Log.Warn($"Display scale: could not persist restore: {ex.Message}"); }
    }

    /// <summary>Persists ONLY the display-scale snapshot, through the config store's
    /// read-modify-write path. Callers hand in a LONG-LIVED <see cref="AppConfig"/> —
    /// the shell session's, on every game/desktop transition — and saving that whole
    /// object would overwrite every field another process persisted since it was
    /// loaded (see ConfigStore's contract), so a mode switch could silently revert
    /// settings the user had just saved. Callers mirror the same values onto their own
    /// instance so it stays in step with what went to disk.</summary>
    /// <param name="entries">The scale entries to persist (empty clears the snapshot).</param>
    private static void PersistScaleEntries(List<DisplayScaleEntry> entries)
        => ConfigStore.Mutate(fresh => fresh.SavedDisplayScaleEntries = entries);

    /// <summary>The display-scale percent WSGM's own UI should render at. Game
    /// mode forces every display to 100%, which makes DIP-sized WSGM surfaces
    /// physically tiny on dense handheld panels — so the overlay/taskbar upscale
    /// themselves to the user's DESKTOP scaling: the pre-game-mode snapshot when
    /// one exists, otherwise the panel's Windows-recommended scale (the snapshot
    /// only captures displays that weren't already at 100%). Returns 100 when
    /// nothing better is known.</summary>
    /// <param name="config">The configuration holding the pre-game-mode scale snapshot.</param>
    public static uint GetUiScalePercent(AppConfig config)
    {
        try
        {
            var sources = GetActiveSources();
            foreach (var source in sources)
            {
                var name = GetSourceDeviceName(source.Adapter, source.SourceId);
                if (config.DisplayManagement is DisplayManagementMode.AutomaticProfiles or DisplayManagementMode.FixedProfiles)
                {
                    var desktop = config.DisplayProfiles.Find(
                        profile => string.Equals(profile.DeviceName, name, StringComparison.OrdinalIgnoreCase))?.Desktop.DpiPercent;
                    if (desktop is >= 100 and <= 500)
                    {
                        return (uint)NormalizeConfiguredPercent(desktop.Value);
                    }
                }
                var saved = config.SavedDisplayScaleEntries.Find(
                    e => string.Equals(e.DeviceName, name, StringComparison.OrdinalIgnoreCase));
                if (saved is { Percent: >= 100 and <= 500 })
                {
                    return (uint)saved.Percent;
                }
            }
            if (sources.Count > 0 && TryGetScale(sources[0], out var current, out var recommended, out _))
            {
                return PickUiScalePercent(null, current, recommended);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Display scale: UI scale query failed: {ex.Message}");
        }
        return 100;
    }

    /// <summary>The pure UI-scale decision: a valid saved desktop percent wins;
    /// otherwise the larger of the current and Windows-recommended scale (in game
    /// mode current is the forced 100, so recommended carries panels whose
    /// desktop already ran at 100%).</summary>
    /// <param name="savedPercent">The pre-game-mode snapshot value, or null.</param>
    /// <param name="currentPercent">The display's current scale percent.</param>
    /// <param name="recommendedPercent">The display's Windows-recommended percent.</param>
    public static uint PickUiScalePercent(int? savedPercent, uint currentPercent, uint recommendedPercent)
        => savedPercent is >= 100 and <= 500
            ? (uint)savedPercent
            : Math.Max(Math.Max(currentPercent, recommendedPercent), 100);

    /// <summary>Decides whether a display may be lowered after its scale was read.
    /// A fresh capture owns every successfully identified value; during crash
    /// recovery only a source already present in the durable snapshot is owned.</summary>
    /// <param name="freshCapture">Whether no earlier recovery snapshot exists.</param>
    /// <param name="savedEntries">The durable, device-keyed recovery snapshot.</param>
    /// <param name="deviceName">The active source's GDI device name.</param>
    internal static bool ShouldLowerDisplay(
        bool freshCapture,
        IReadOnlyList<DisplayScaleEntry> savedEntries,
        string deviceName)
        => freshCapture || savedEntries.Any(entry =>
            string.Equals(entry.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    internal static Dictionary<string, int> ReadActivePercentages()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in GetActiveSources())
        {
            var name = GetSourceDeviceName(source.Adapter, source.SourceId);
            if (name.Length > 0 && TryGetScale(source, out var current, out _, out _))
            {
                result[name] = (int)current;
            }
        }
        return result;
    }

    internal static void ApplyPercentages(IEnumerable<MonitorDisplayProfile> profiles, bool game)
    {
        foreach (var source in GetActiveSources())
        {
            var name = GetSourceDeviceName(source.Adapter, source.SourceId);
            var profile = profiles.FirstOrDefault(p => string.Equals(p.DeviceName, name, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                continue;
            }
            var percent = NormalizeConfiguredPercent(game ? profile.Game.DpiPercent : profile.Desktop.DpiPercent);
            if (TrySetScale(source, (uint)percent))
            {
                Log.Info($"Display profile: {name} DPI -> {percent}%.");
            }
        }
    }

    internal static int NormalizeConfiguredPercent(int percent)
        => (int)DpiVals.MinBy(candidate => Math.Abs((int)candidate - percent));

    private static bool TryGetScale((Luid Adapter, uint SourceId) source, out uint currentPct, out uint recommendedPct, out uint maxPct)
    {
        currentPct = recommendedPct = maxPct = 0;
        var get = new DpiScaleGet
        {
            Header =
            {
                Type = GetDpiScaleType,
                Size = (uint)Marshal.SizeOf<DpiScaleGet>(),
                AdapterId = source.Adapter,
                Id = source.SourceId,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref get) != 0)
        {
            return false;
        }
        var cur = Math.Clamp(get.CurScaleRel, get.MinScaleRel, get.MaxScaleRel);
        var rec = Math.Abs(get.MinScaleRel);
        if (rec + get.MaxScaleRel + 1 > DpiVals.Length)
        {
            return false;
        }
        currentPct = DpiVals[rec + cur];
        recommendedPct = DpiVals[rec];
        maxPct = DpiVals[rec + get.MaxScaleRel];
        return true;
    }

    private static bool TrySetScale((Luid Adapter, uint SourceId) source, uint percent)
    {
        if (!TryGetScale(source, out var current, out var recommended, out var max))
        {
            return false;
        }
        if (percent == current)
        {
            return true;
        }
        percent = Math.Clamp(percent, 100u, max);
        var idx = Array.IndexOf(DpiVals, percent);
        var recIdx = Array.IndexOf(DpiVals, recommended);
        if (idx < 0 || recIdx < 0)
        {
            return false;
        }
        var set = new DpiScaleSet
        {
            Header =
            {
                Type = SetDpiScaleType,
                Size = (uint)Marshal.SizeOf<DpiScaleSet>(),
                AdapterId = source.Adapter,
                Id = source.SourceId,
            },
            ScaleRel = idx - recIdx,
        };
        var ok = DisplayConfigSetDeviceInfo(ref set) == 0;
        if (!ok)
        {
            Log.Warn($"Display scale: set {percent}% failed.");
        }
        return ok;
    }

    private static List<(Luid Adapter, uint SourceId)> GetActiveSources()
    {
        var result = new List<(Luid, uint)>();
        try
        {
            var paths = QueryActivePaths("Display scale", out var numPaths);
            if (paths is null)
            {
                return result;
            }
            for (var i = 0; i < numPaths; i++)
            {
                var key = (paths[i].SourceInfo.AdapterId, paths[i].SourceInfo.Id);
                if (!result.Exists(x => x.Item2 == key.Id
                    && x.Item1.LowPart == key.AdapterId.LowPart && x.Item1.HighPart == key.AdapterId.HighPart))
                {
                    result.Add(key);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Display scale: enumeration failed: {ex.Message}");
        }
        return result;
    }

    // ---- HDR (DisplayConfig advanced color) ------------------------------------------------
    // Queried and set on the path TARGET; the GDI source name only keys the profile lookup.

    private readonly record struct HdrTarget(string DeviceName, Luid AdapterId, uint TargetId, bool Supported, bool Enabled);

    /// <summary>Gets HDR availability and current state keyed by GDI source name.</summary>
    internal static Dictionary<string, (bool Available, bool Enabled)> ReadActiveHdr()
    {
        var result = new Dictionary<string, (bool, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in EnumerateHdrTargets())
        {
            result.TryAdd(target.DeviceName, (target.Supported, target.Enabled));
        }
        return result;
    }

    /// <summary>Applies the selected profile's HDR flag only to monitors that currently support HDR.</summary>
    internal static void ApplyHdr(IEnumerable<MonitorDisplayProfile> profiles, bool game)
    {
        foreach (var target in EnumerateHdrTargets())
        {
            MonitorDisplayProfile? profile = null;
            foreach (var candidate in profiles)
            {
                if (string.Equals(candidate.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    profile = candidate;
                    break;
                }
            }
            if (profile is null)
            {
                continue;
            }
            var enabled = game ? profile.Game.HdrEnabled : profile.Desktop.HdrEnabled;
            if (!ShouldChange(target.Supported, target.Enabled, enabled))
            {
                continue;
            }
            var packet = new AdvancedColorState
            {
                Header =
                {
                    Type = SetAdvancedColorStateType,
                    Size = (uint)sizeof(AdvancedColorState),
                    AdapterId = target.AdapterId,
                    Id = target.TargetId,
                },
                EnableAdvancedColor = enabled ? 1u : 0u,
            };
            var status = DisplayConfigSetDeviceInfo(ref packet);
            if (status == 0)
            {
                Log.Info($"Display profile: {target.DeviceName} HDR -> {(enabled ? "on" : "off")}.");
            }
            else
            {
                Log.Warn($"Display profile: {target.DeviceName} HDR {(enabled ? "enable" : "disable")} failed ({status}).");
            }
        }
    }

    internal static bool ShouldChange(bool available, bool current, bool requested)
        => available && current != requested;

    private static List<HdrTarget> EnumerateHdrTargets()
    {
        var result = new List<HdrTarget>();
        var paths = QueryActivePaths("Display HDR", out var numPaths);
        if (paths is null)
        {
            return result;
        }
        for (var i = 0; i < numPaths; i++)
        {
            var path = paths[i];
            var name = GetSourceDeviceName(path.SourceInfo.AdapterId, path.SourceInfo.Id);
            if (name.Length == 0)
            {
                continue;
            }
            var info = new AdvancedColorInfo
            {
                Header =
                {
                    Type = GetAdvancedColorInfoType,
                    Size = (uint)sizeof(AdvancedColorInfo),
                    AdapterId = path.TargetInfo.AdapterId,
                    Id = path.TargetInfo.Id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref info) != 0)
            {
                continue;
            }
            result.Add(new HdrTarget(name, path.TargetInfo.AdapterId, path.TargetInfo.Id,
                Supported: (info.Value & 1) != 0,
                Enabled: (info.Value & 2) != 0));
        }
        return result;
    }

    // ---- shared DisplayConfig plumbing ------------------------------------------------------

    private static PathInfo[]? QueryActivePaths(string context, out uint numPaths)
    {
        // The path set can grow between the sizing call and the query (dock/
        // undock is exactly when this code tends to run), so retry on
        // ERROR_INSUFFICIENT_BUFFER — the documented pattern for this API.
        int status;
        PathInfo[] paths;
        var attempts = 0;
        do
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out numPaths, out var numModes) != 0)
            {
                return null;
            }
            paths = new PathInfo[numPaths];
            var modes = new ModeInfo[numModes];
            status = QueryDisplayConfig(QdcOnlyActivePaths, ref numPaths, paths, ref numModes, modes, 0);
        } while (status == ErrorInsufficientBuffer && ++attempts < 5);
        if (status != 0)
        {
            Log.Warn($"{context}: QueryDisplayConfig failed with {status}.");
            return null;
        }
        return paths;
    }

    private static string GetSourceDeviceName(Luid adapterId, uint sourceId)
    {
        var packet = new SourceDeviceName
        {
            Header =
            {
                Type = GetSourceNameType,
                Size = (uint)sizeof(SourceDeviceName),
                AdapterId = adapterId,
                Id = sourceId,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref packet) != 0)
        {
            return "";
        }
        var name = new ReadOnlySpan<char>(packet.ViewGdiDeviceName, 32);
        var len = name.IndexOf('\0');
        return new string(len >= 0 ? name[..len] : name);
    }
}
