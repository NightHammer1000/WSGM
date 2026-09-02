using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>The WakeWatch color vocabulary for the system-wide wake-lock state
/// (maintainer's WakeWatch project, reused deliberately so both tools read the
/// same): grey = unknown, green = free, yellow = standby blocked, red = display
/// pinned on. Only DISPLAY drives red and SYSTEM/AWAYMODE drive yellow; EXECUTION
/// and friends deliberately do not affect the state.</summary>
public enum WakeLockState
{
    /// <summary>No trustworthy answer (unelevated, or an unrecognized layout).</summary>
    Unknown,

    /// <summary>No locks — display and sleep are free.</summary>
    Free,

    /// <summary>At least one standby lock: the system cannot sleep.</summary>
    SystemHeld,

    /// <summary>At least one display lock: the screen cannot turn off.</summary>
    DisplayHeld,
}

/// <summary>The quick-access Keep Awake cycle: off → block standby → block standby
/// and keep the display on → off.</summary>
public enum ManualWakeMode
{
    /// <summary>No manual hold.</summary>
    Off,

    /// <summary>Standby blocked; the display still times out.</summary>
    Standby,

    /// <summary>Standby blocked and the display pinned on.</summary>
    StandbyAndDisplay,
}

/// <summary>Pure mapping from a power-request snapshot to the indicator state and
/// a compact holder summary for the quick-access row.</summary>
public static class WakeLockStatus
{
    private const int MaxNamedHolders = 3;

    /// <summary>Computes the indicator state and a holder summary such as
    /// "Standby blocked by steam.exe ×3, chrome.exe". WSGM's own requests count
    /// toward the state (the color must reflect reality) but are excluded from the
    /// summary — the row's own description already explains WSGM's holds.</summary>
    /// <param name="entries">The decoded request list; null = unknown.</param>
    /// <param name="selfPid">WSGM's own process id, excluded from the summary.</param>
    public static (WakeLockState State, string Summary) Compute(
        IReadOnlyList<PowerRequestEntry>? entries, uint selfPid)
    {
        if (entries is null)
        {
            return (WakeLockState.Unknown, "");
        }
        var display = entries.Any(e => e.HoldsDisplay);
        var system = entries.Any(e => e.HoldsSystem || e.HoldsAwayMode);
        if (!display && !system)
        {
            return (WakeLockState.Free, "");
        }
        var state = display ? WakeLockState.DisplayHeld : WakeLockState.SystemHeld;
        var holders = entries
            .Where(e => (display ? e.HoldsDisplay : e.HoldsSystem || e.HoldsAwayMode)
                && e.Pid != selfPid)
            .Select(HolderName)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Count() > 1 ? $"{group.Key} ×{group.Count()}" : group.Key)
            .ToList();
        if (holders.Count == 0)
        {
            return (state, "");
        }
        var listed = string.Join(", ", holders.Take(MaxNamedHolders));
        if (holders.Count > MaxNamedHolders)
        {
            listed += $" +{holders.Count - MaxNamedHolders} more";
        }
        return (state, (display ? "Screen held on by " : "Standby blocked by ") + listed);
    }

    /// <summary>Shortens an NT-device-form image path to its file name; kernel
    /// requesters without a name become "(kernel)".</summary>
    internal static string HolderName(PowerRequestEntry entry)
    {
        var name = entry.Name;
        var cut = name.LastIndexOfAny(['\\', '/']);
        if (cut >= 0)
        {
            name = name[(cut + 1)..];
        }
        return name.Length > 0 ? name : "(kernel)";
    }
}

/// <summary>One deduplicated requester within a lock kind.</summary>
/// <param name="Label">Short name, e.g. <c>steam.exe</c>.</param>
/// <param name="Detail">Caller kind, pid and full path, for the row's description.</param>
/// <param name="Reason">The requester's reason string, when it supplied one.</param>
/// <param name="Count">How many identical requests collapsed into this row.</param>
public sealed record WakeLockHolder(string Label, string Detail, string? Reason, int Count);

/// <summary>The holders of one kind of lock, in the order they should be listed.</summary>
/// <param name="Title">User-facing name of the lock kind.</param>
/// <param name="Holders">Deduplicated requesters, most numerous first.</param>
public sealed record WakeLockHolderGroup(string Title, IReadOnlyList<WakeLockHolder> Holders);

/// <summary>Groups a power-request snapshot into the per-lock holder list shown by the
/// quick-access Power tab. Mirrors the maintainer's WakeWatch aggregation deliberately
/// (same tool, same vocabulary): dedupe on the identity a user perceives, so Steam's
/// thirty identical standby requests read as <c>steam.exe ×30</c> rather than thirty
/// rows.
///
/// <para>Unlike <see cref="WakeLockStatus.Compute"/> this does NOT hide WSGM's own
/// requests: the summary line omits them because the row above already explains
/// WSGM's holds, but a user opening the full list is asking what is holding the
/// device awake and WSGM's own keep-awake hold is part of that answer.</para></summary>
public static class WakeLockHolders
{
    /// <summary>Builds the grouped holder list. Returns an empty list when the
    /// snapshot is unknown (unelevated or an unrecognized layout) — callers must
    /// distinguish that from "nothing holds a lock" using the null entries.</summary>
    /// <param name="entries">The decoded request list; null = unknown.</param>
    public static IReadOnlyList<WakeLockHolderGroup> Build(IReadOnlyList<PowerRequestEntry>? entries)
    {
        if (entries is null)
        {
            return [];
        }
        var groups = new List<WakeLockHolderGroup>();
        AddGroup(groups, "Screen kept on", entries.Where(e => e.HoldsDisplay));
        AddGroup(groups, "Standby blocked", entries.Where(e => e.HoldsSystem));
        AddGroup(groups, "Away mode", entries.Where(e => e.HoldsAwayMode));
        return groups;
    }

    private static void AddGroup(
        List<WakeLockHolderGroup> groups, string title, IEnumerable<PowerRequestEntry> matching)
    {
        var holders = new List<WakeLockHolder>();
        foreach (var entry in matching)
        {
            var label = WakeLockStatus.HolderName(entry);
            var detail = Describe(entry);
            var reason = string.IsNullOrWhiteSpace(entry.Reason) ? null : entry.Reason;
            var existing = holders.FindIndex(h =>
                string.Equals(h.Label, label, StringComparison.OrdinalIgnoreCase)
                && h.Detail == detail && h.Reason == reason);
            if (existing >= 0)
            {
                holders[existing] = holders[existing] with { Count = holders[existing].Count + 1 };
            }
            else
            {
                holders.Add(new WakeLockHolder(label, detail, reason, 1));
            }
        }
        if (holders.Count == 0)
        {
            return;
        }
        holders.Sort((a, b) => b.Count.CompareTo(a.Count) is var c && c != 0
            ? c
            : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        groups.Add(new WakeLockHolderGroup(title, holders));
    }

    /// <summary>Formats the secondary line: caller kind, pid, and the full requester
    /// name as the kernel reported it.</summary>
    /// <param name="entry">The request to describe.</param>
    internal static string Describe(PowerRequestEntry entry)
    {
        // REQUESTER_TYPE: 0 kernel, 1 process, 2 service.
        if (entry.CallerType == 0)
        {
            return entry.Name.Length > 0 ? $"Driver: {entry.Name}" : "Kernel driver";
        }
        var kind = entry.CallerType == 2 ? "Service" : "Process";
        var name = entry.Name.Length > 0 ? entry.Name : "(unknown)";
        return entry.Pid is { } pid ? $"{kind} (pid {pid}): {name}" : $"{kind}: {name}";
    }
}
