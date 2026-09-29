using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using static WSGM.Interop.NativeDisplay;

namespace WSGM.Core;

/// <summary>Captures and applies per-monitor resolution, refresh-rate and DPI profiles.</summary>
public static unsafe class DisplayProfiles
{
    /// <summary>Smallest resolution worth offering. Below this is legacy driver noise.</summary>
    private const uint MinimumUsableWidth = 800;

    /// <summary>Smallest resolution height worth offering.</summary>
    private const uint MinimumUsableHeight = 600;

    /// <summary>Captures the mode being left when automatic, then applies the mode being entered.</summary>
    public static void Transition(AppConfig config, bool enteringGameMode)
    {
        if (config.DisplayManagement == DisplayManagementMode.AutomaticProfiles)
        {
            Capture(config, game: !enteringGameMode);
            Persist(config);
        }
        Apply(config.DisplayProfiles, enteringGameMode);
    }

    /// <summary>Applies an already captured profile without taking a new snapshot.
    /// Recovery and uninstall paths use this because the current mode may be only
    /// partially initialized and must never overwrite the known-good profile.</summary>
    /// <param name="profiles">Per-monitor profiles to apply.</param>
    /// <param name="game">Whether to apply Game rather than Desktop values.</param>
    public static void ApplySaved(IEnumerable<MonitorDisplayProfile> profiles, bool game)
        => Apply(profiles, game);

    /// <summary>Returns current active display values for profile editing and automatic capture.</summary>
    public static List<MonitorDisplayProfile> ReadActiveProfiles()
    {
        var dpi = DisplayScale.ReadActivePercentages();
        var hdr = DisplayScale.ReadActiveHdr();
        var result = new List<MonitorDisplayProfile>();
        for (uint i = 0; ; i++)
        {
            var device = new DisplayDevice { Size = (uint)sizeof(DisplayDevice) };
            if (!EnumDisplayDevices(null, i, ref device, 0))
            {
                break;
            }
            if ((device.StateFlags & DisplayDeviceActive) == 0)
            {
                continue;
            }
            var name = FixedString(device.DeviceName, 32);
            var label = FixedString(device.DeviceString, 128);
            var mode = new DevMode { Size = (ushort)sizeof(DevMode) };
            if (!EnumDisplaySettingsEx(device.DeviceName, EnumCurrentSettings, ref mode, 0))
            {
                continue;
            }
            var monitor = new DisplayDevice { Size = (uint)sizeof(DisplayDevice) };
            string monitorId = "";
            if (EnumDisplayDevices(device.DeviceName, 0, ref monitor, 0))
            {
                monitorId = FixedString(monitor.DeviceKey, 128);
                label = FixedString(monitor.DeviceString, 128);
            }
            var hdrState = hdr.GetValueOrDefault(name);
            var values = new DisplayModeValues { Width = (int)mode.PelsWidth, Height = (int)mode.PelsHeight, RefreshRate = (int)mode.DisplayFrequency, DpiPercent = dpi.GetValueOrDefault(name, 100), HdrEnabled = hdrState.Enabled };
            result.Add(new MonitorDisplayProfile { MonitorId = monitorId, DeviceName = name, DisplayName = label, HdrAvailable = hdrState.Available, Desktop = Clone(values), Game = Clone(values) });
        }
        return result;
    }

    /// <summary>
    /// Discovers the resolutions the driver accepts on the primary display, at its current refresh
    /// rate and colour depth.
    /// </summary>
    /// <returns>
    /// Accepted resolutions, ascending by pixel count and deduplicated. Empty when the display
    /// cannot be read.
    /// </returns>
    /// <remarks>
    /// The same discover-then-test discipline as <see cref="EnumerateAcceptedRefreshRates"/>, and
    /// for the same reason: an enumerated mode is a claim, not a promise, and `CDS_TEST` changes
    /// nothing so this is safe to call while a game is running.
    /// <para>
    /// Held at the current refresh rate on purpose. A resolution row that also moved the refresh
    /// rate would fight the frame-limit pairing, which owns that axis; the two are separate
    /// controls precisely so one change is one change.
    /// </para>
    /// <para>
    /// Anything below 800x600 is dropped. Drivers enumerate legacy modes no handheld panel should
    /// offer, and a resolution list whose first entry is 640x480 is a list the user has to scroll
    /// past rather than one they can use.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DisplayResolution> EnumerateAcceptedResolutions()
        => EnumerateAccepted(
            "resolutions",
            "accepted resolutions",
            static (mode, current) => mode.BitsPerPel == current.BitsPerPel
                && mode.DisplayFrequency == current.DisplayFrequency
                && mode.PelsWidth >= MinimumUsableWidth
                && mode.PelsHeight >= MinimumUsableHeight,
            static (mode, current) => new CandidateMode(mode.PelsWidth, mode.PelsHeight, current.DisplayFrequency),
            static mode => $"{mode.Width}x{mode.Height}")
            .Select(mode => new DisplayResolution((int)mode.Width, (int)mode.Height))
            .ToArray();

    /// <summary>
    /// The refresh rates the primary display will actually accept at its current resolution.
    /// </summary>
    /// <returns>Accepted rates, ascending and deduplicated. Empty when the display cannot be read.</returns>
    /// <remarks>
    /// Enumerated and then <em>tested</em>, never assumed: a driver commonly offers rates the panel
    /// never advertises — the reference Claw accepts 30/48/60/75/100/120 while its EDID lists only
    /// 60 and 120 — and equally may refuse one it enumerated. `CDS_TEST` changes nothing, so this is
    /// safe to call while a game is running.
    /// <para>
    /// Hardcoding a rate list is the one thing this must never become: a panel without variable
    /// refresh will likely accept nothing but what it advertises, and that is exactly the case the
    /// frame-limit strategies exist to serve.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<int> EnumerateAcceptedRefreshRates()
        => EnumerateAccepted(
            "refresh rates",
            "accepted",
            static (mode, current) => mode.PelsWidth == current.PelsWidth
                && mode.PelsHeight == current.PelsHeight
                && mode.BitsPerPel == current.BitsPerPel
                && mode.DisplayFrequency > 1,
            static (mode, current) => new CandidateMode(current.PelsWidth, current.PelsHeight, mode.DisplayFrequency),
            static mode => mode.RefreshHz.ToString())
            .Select(mode => (int)mode.RefreshHz)
            .ToArray();

    /// <summary>A full candidate mode, so one test path serves both discovery axes.</summary>
    private readonly record struct CandidateMode(uint Width, uint Height, uint RefreshHz);

    private static IReadOnlyList<CandidateMode> EnumerateAccepted(
        string noun,
        string acceptedLabel,
        Func<DevMode, DevMode, bool> keep,
        Func<DevMode, DevMode, CandidateMode> candidate,
        Func<CandidateMode, string> describe)
    {
        var current = new DevMode { Size = (ushort)sizeof(DevMode) };
        if (!EnumDisplaySettingsEx(null, EnumCurrentSettings, ref current, 0))
        {
            Log.Warn($"Display modes: current settings unreadable; no {noun} discovered.");
            return [];
        }

        HashSet<CandidateMode> enumerated = [];
        for (uint index = 0; ; index++)
        {
            var mode = new DevMode { Size = (ushort)sizeof(DevMode) };
            if (!EnumDisplaySettingsEx(null, index, ref mode, 0))
            {
                break;
            }

            if (keep(mode, current))
            {
                enumerated.Add(candidate(mode, current));
            }
        }

        List<CandidateMode> accepted = [];
        List<string> refused = [];
        foreach (CandidateMode mode in enumerated
            .OrderBy(entry => (long)entry.Width * entry.Height)
            .ThenBy(entry => entry.Width)
            .ThenBy(entry => entry.RefreshHz))
        {
            bool isCurrent = mode.Width == current.PelsWidth
                && mode.Height == current.PelsHeight
                && mode.RefreshHz == current.DisplayFrequency;
            if (isCurrent || Test(current, mode))
            {
                accepted.Add(mode);
            }
            else
            {
                refused.Add(describe(mode));
            }
        }

        Log.Info(
            $"Display modes: {current.PelsWidth}x{current.PelsHeight} at {current.DisplayFrequency} Hz, "
            + $"{acceptedLabel} [{string.Join(",", accepted.Select(describe))}]"
            + (refused.Count is 0 ? "" : $", refused [{string.Join(",", refused)}]"));
        return accepted;
    }

    /// <summary>
    /// Applies a refresh rate to the primary display without persisting it.
    /// </summary>
    /// <param name="refreshHz">The rate to apply.</param>
    /// <returns><see langword="true"/> when the display reports the new rate afterwards.</returns>
    /// <remarks>
    /// Deliberately dynamic: no `CDS_UPDATEREGISTRY`, so the user's saved display configuration is
    /// untouched and exit, a crash, or a reboot all restore it without WSGM doing anything. That is
    /// what makes a game-scoped refresh change safe to make at all.
    /// <para>
    /// Distinct from the display-profile path above, which deliberately does persist. Do not merge
    /// them: a profile is the user's chosen configuration, and this is a transient pairing WSGM owns
    /// for the duration of a cap.
    /// </para>
    /// </remarks>
    public static bool TryApplyTransientRefreshRate(int refreshHz)
        => TryApplyTransient(
            $"{refreshHz} Hz",
            current => current.DisplayFrequency == (uint)refreshHz,
            current =>
            {
                var target = current;
                target.DisplayFrequency = (uint)refreshHz;
                return target;
            },
            static current => $"{current.DisplayFrequency} Hz",
            current => $"{current.DisplayFrequency} Hz -> {refreshHz} Hz");

    /// <summary>
    /// Applies a resolution to the primary display without persisting it.
    /// </summary>
    /// <param name="width">Target width in pixels.</param>
    /// <param name="height">Target height in pixels.</param>
    /// <returns>Whether the display is now at that resolution.</returns>
    /// <remarks>
    /// The same discipline as <see cref="TryApplyTransientRefreshRate"/>, and for the same reason:
    /// no <c>CDS_UPDATEREGISTRY</c>, so exit, crash, and reboot all restore the user's own
    /// persisted configuration without WSGM having to remember to.
    /// <para>
    /// The refresh rate is carried over from the current mode rather than left to the driver's
    /// default for the new resolution. Changing one axis must not silently change the other, or a
    /// resolution change would undo whatever the frame-limit pairing had just set.
    /// </para>
    /// </remarks>
    public static bool TryApplyTransientResolution(int width, int height)
        => TryApplyTransient(
            $"{width}x{height}",
            current => current.PelsWidth == (uint)width && current.PelsHeight == (uint)height,
            current =>
            {
                var target = current;
                target.PelsWidth = (uint)width;
                target.PelsHeight = (uint)height;
                return target;
            },
            static current => $"{current.PelsWidth}x{current.PelsHeight} at {current.DisplayFrequency} Hz",
            current => $"{current.PelsWidth}x{current.PelsHeight} -> {width}x{height} "
                + $"at {current.DisplayFrequency} Hz");

    private static bool TryApplyTransient(
        string what,
        Func<DevMode, bool> alreadyApplied,
        Func<DevMode, DevMode> retarget,
        Func<DevMode, string> was,
        Func<DevMode, string> transition)
    {
        var current = new DevMode { Size = (ushort)sizeof(DevMode) };
        if (!EnumDisplaySettingsEx(null, EnumCurrentSettings, ref current, 0))
        {
            Log.Warn($"Display modes: refusing {what}; current settings unreadable.");
            return false;
        }

        if (alreadyApplied(current))
        {
            return true;
        }

        DevMode target = retarget(current);
        target.Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        int status = ChangeDisplaySettingsEx(null, &target, 0, 0, 0);
        if (status != 0)
        {
            Log.Warn($"Display modes: {what} refused with status {status} (was {was(current)}).");
            return false;
        }

        Log.Info($"Display modes: {transition(current)} (transient).");
        return true;
    }

    /// <summary>The resolution the primary display is running at.</summary>
    /// <returns>The resolution, or null when it cannot be read.</returns>
    public static DisplayResolution? ReadCurrentResolution()
    {
        var current = new DevMode { Size = (ushort)sizeof(DevMode) };
        return EnumDisplaySettingsEx(null, EnumCurrentSettings, ref current, 0)
            ? new DisplayResolution((int)current.PelsWidth, (int)current.PelsHeight)
            : null;
    }

    /// <summary>The refresh rate the primary display is running at.</summary>
    /// <returns>The rate in Hz, or null when it cannot be read.</returns>
    public static int? ReadCurrentRefreshRate()
    {
        var current = new DevMode { Size = (ushort)sizeof(DevMode) };
        return EnumDisplaySettingsEx(null, EnumCurrentSettings, ref current, 0)
            ? (int)current.DisplayFrequency
            : null;
    }

    /// <summary>
    /// The refresh rates the primary panel advertises in its own EDID.
    /// </summary>
    /// <returns>Advertised rates, ascending; empty when the EDID cannot be read.</returns>
    /// <remarks>
    /// Distinct from <see cref="EnumerateAcceptedRefreshRates"/>, and the difference is the point:
    /// the driver accepts rates the panel never advertised, so only the EDID can say which modes are
    /// the panel's own. An empty result makes the native-modes strategy offer nothing rather than
    /// guess, which is the correct failure — a wrong list would pair caps against timings the panel
    /// does not really have.
    /// </remarks>
    public static IReadOnlyList<int> ReadAdvertisedRefreshRates()
    {
        string? instance = ReadPrimaryMonitorInstanceId();
        if (instance is null)
        {
            Log.Warn("Display modes: primary monitor instance unreadable; no advertised rates.");
            return [];
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{instance}\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] edid)
            {
                Log.Warn($"Display modes: no EDID under '{instance}'.");
                return [];
            }

            IReadOnlyList<int> rates = EdidModes.ReadAdvertisedRefreshRates(edid);
            Log.Info($"Display modes: panel advertises [{string.Join(",", rates)}].");
            return rates;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Warn($"Display modes: EDID unreadable for '{instance}': {ex.Message}");
            return [];
        }
    }

    /// <remarks>
    /// The interface name is asked for specifically, because the default form of the monitor's
    /// device id is a class path that does not identify the enum key the EDID lives under.
    /// </remarks>
    private static string? ReadPrimaryMonitorInstanceId()
    {
        for (uint i = 0; ; i++)
        {
            var device = new DisplayDevice { Size = (uint)sizeof(DisplayDevice) };
            if (!EnumDisplayDevices(null, i, ref device, 0))
            {
                return null;
            }

            if ((device.StateFlags & DisplayDevicePrimary) == 0)
            {
                continue;
            }

            var monitor = new DisplayDevice { Size = (uint)sizeof(DisplayDevice) };
            if (!EnumDisplayDevices(device.DeviceName, 0, ref monitor, GetDeviceInterfaceName))
            {
                return null;
            }

            // \\?\DISPLAY#CSW0801#4&8f346&1&UID8388688#{guid} -> DISPLAY\CSW0801\4&8f346&1&UID8388688
            string id = FixedString(monitor.DeviceId, 128);
            int start = id.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }

            string trimmed = id[start..];
            int guid = trimmed.IndexOf("#{", StringComparison.Ordinal);
            if (guid > 0)
            {
                trimmed = trimmed[..guid];
            }

            return trimmed.Replace('#', '\\');
        }
    }

    private static bool Test(DevMode current, CandidateMode mode)
    {
        var candidate = current;
        candidate.Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        candidate.PelsWidth = mode.Width;
        candidate.PelsHeight = mode.Height;
        candidate.DisplayFrequency = mode.RefreshHz;
        return ChangeDisplaySettingsEx(null, &candidate, 0, CdsTest, 0) == 0;
    }

    private static void Capture(AppConfig config, bool game)
    {
        foreach (var current in ReadActiveProfiles())
        {
            var profile = config.DisplayProfiles.FirstOrDefault(p => SameMonitor(p, current));
            if (profile is null)
            {
                profile = current;
                config.DisplayProfiles.Add(profile);
            }
            var value = game ? current.Game : current.Desktop;
            if (game)
            {
                profile.Game = Clone(value);
            }
            else
            {
                profile.Desktop = Clone(value);
            }
            profile.MonitorId = current.MonitorId;
            profile.DeviceName = current.DeviceName;
            profile.DisplayName = current.DisplayName;
            profile.HdrAvailable = current.HdrAvailable;
            Log.Info($"Display profile captured ({(game ? "Game" : "Desktop")}): {current.DeviceName} {value.Width}x{value.Height} @ {value.RefreshRate} Hz, {value.DpiPercent}% DPI.");
        }
    }

    private static void Apply(IEnumerable<MonitorDisplayProfile> profiles, bool game)
    {
        var active = ReadActiveProfiles();
        var pending = new List<(MonitorDisplayProfile Profile, MonitorDisplayProfile Current, DisplayModeValues Value)>();
        foreach (var profile in profiles)
        {
            var current = active.FirstOrDefault(candidate => SameMonitor(profile, candidate));
            if (current is null)
            {
                continue;
            }
            var value = game ? profile.Game : profile.Desktop;
            if (value.Width <= 0 || value.Height <= 0 || value.RefreshRate <= 0)
            {
                continue;
            }
            var mode = CreateMode(value);
            fixed (char* name = current.DeviceName)
            {
                var status = ChangeDisplaySettingsEx(name, &mode, 0, CdsTest, 0);
                if (status != 0)
                {
                    Log.Warn($"Display profile: {current.DeviceName} rejected {value.Width}x{value.Height} @ {value.RefreshRate} Hz ({status}); leaving every display mode unchanged.");
                    return;
                }
            }
            pending.Add((profile, current, value));
        }

        var changed = false;
        var staged = new List<MonitorDisplayProfile>();
        foreach (var (profile, current, value) in pending)
        {
            profile.DeviceName = current.DeviceName;
            var mode = CreateMode(value);
            fixed (char* name = current.DeviceName)
            {
                var status = ChangeDisplaySettingsEx(name, &mode, 0, CdsUpdateRegistry | CdsNoReset, 0);
                if (status == 0)
                {
                    changed = true;
                    staged.Add(current);
                    Log.Info($"Display profile staged ({(game ? "Game" : "Desktop")}): {current.DeviceName} {value.Width}x{value.Height} @ {value.RefreshRate} Hz.");
                }
                else
                {
                    Log.Warn($"Display profile: {profile.DeviceName} mode apply failed ({status}).");
                    RestoreStagedRegistry(staged);
                    return;
                }
            }
        }
        if (changed)
        {
            var status = ChangeDisplaySettingsEx(null, null, 0, 0, 0);
            if (status != 0)
            {
                Log.Warn($"Display profile: committing staged modes failed ({status}).");
                RestoreStagedRegistry(staged);
                return;
            }
        }
        DisplayScale.ApplyPercentages(profiles, game);
        DisplayScale.ApplyHdr(profiles, game);
    }

    private static DevMode CreateMode(DisplayModeValues value)
        => new() { Size = (ushort)sizeof(DevMode), Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency, PelsWidth = (uint)value.Width, PelsHeight = (uint)value.Height, DisplayFrequency = (uint)value.RefreshRate };

    private static void RestoreStagedRegistry(IEnumerable<MonitorDisplayProfile> staged)
    {
        var restored = false;
        foreach (var current in staged)
        {
            var mode = CreateMode(current.Desktop);
            fixed (char* name = current.DeviceName)
            {
                var status = ChangeDisplaySettingsEx(name, &mode, 0, CdsUpdateRegistry | CdsNoReset, 0);
                if (status != 0)
                {
                    Log.Warn($"Display profile: rollback for {current.DeviceName} failed ({status}).");
                }
                else
                {
                    restored = true;
                }
            }
        }
        if (restored)
        {
            var status = ChangeDisplaySettingsEx(null, null, 0, 0, 0);
            if (status != 0)
            {
                Log.Warn($"Display profile: committing rollback failed ({status}).");
            }
        }
    }

    private static void Persist(AppConfig config) => ConfigStore.Mutate(fresh => fresh.DisplayProfiles = config.DisplayProfiles);
    private static bool SameMonitor(MonitorDisplayProfile left, MonitorDisplayProfile right)
        => !string.IsNullOrEmpty(left.MonitorId) && !string.IsNullOrEmpty(right.MonitorId)
            ? string.Equals(left.MonitorId, right.MonitorId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(left.DeviceName, right.DeviceName, StringComparison.OrdinalIgnoreCase);
    private static DisplayModeValues Clone(DisplayModeValues value) => new() { Width = value.Width, Height = value.Height, RefreshRate = value.RefreshRate, DpiPercent = value.DpiPercent, HdrEnabled = value.HdrEnabled };
    private static string FixedString(char* value, int length) { var span = new ReadOnlySpan<char>(value, length); var end = span.IndexOf('\0'); return new string(end < 0 ? span : span[..end]); }
}

/// <summary>One display resolution the driver accepted.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct DisplayResolution(int Width, int Height)
{
    /// <summary>Renders the resolution the way a user reads it.</summary>
    /// <returns>Width and height separated by an <c>x</c>.</returns>
    public override string ToString() => $"{Width}x{Height}";
}
