using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>One Device section as the root page presents it.</summary>
/// <param name="Section">The section this entry opens.</param>
/// <param name="Page">The navigation page it pushes.</param>
/// <param name="Title">Heading shown on the card and the page.</param>
/// <param name="Description">What the user finds there.</param>
/// <param name="Count">How many capabilities the section currently holds.</param>
/// <param name="Status">The most serious status among them.</param>
internal sealed record DeviceOverlaySectionEntry(
    DeviceOverlaySection Section,
    OverlayPage Page,
    string Title,
    string Description,
    int Count,
    DescriptorStatus Status)
{
    /// <summary>The plugin-declared section this entry opens, or null for a WSGM-owned one.</summary>
    /// <remarks>When set, <see cref="Section"/> is meaningless and <see cref="Page"/> is
    /// <see cref="OverlayPage.DevicePluginSection"/>.</remarks>
    public string? PluginSectionId { get; init; }

    /// <summary>The declared icon for a plugin section's card.</summary>
    public SectionIcon Icon { get; init; } = SectionIcon.None;
}

/// <summary>
/// Turns a Device snapshot into the section list the destination's root page shows.
/// </summary>
/// <remarks>
/// The Device destination is a menu of pages rather than one long scrolling list, because a
/// handheld's whole surface is a few rows tall and a list that needs scrolling is a list a
/// controller cannot navigate quickly.
/// <para>
/// A section appears only when the plugin published something for it. That keeps the menu honest on
/// every device — a handheld with no lighting shows no Lighting page rather than an empty one — and
/// it means no section here is a fixture that a future plugin has to satisfy.
/// </para>
/// </remarks>
internal static class DeviceOverlaySectionPages
{
    /// <summary>The id of the per-application enable toggle among the performance profile rows.</summary>
    /// <remarks>
    /// It is the headline toggle on the Device root — the one control that turns a per-application
    /// profile on — so it is pulled out of the section rows both here (it is not counted into any
    /// section) and in the renderer (it is drawn at the top of the root, not in a page). One id, so
    /// the two never disagree about which row is the toggle.
    /// </remarks>
    internal const string ApplicationProfileRowId = "application-profile";

    /// <summary>The reset action among the performance profile rows.</summary>
    internal const string ResetProfileRowId = "reset-profile";

    /// <summary>The fixed order sections are offered in.</summary>
    /// <remarks>
    /// Ordered by how often a handheld user reaches for them, not by the enum. Power comes first
    /// because it is the reason the Device page is opened mid-game; diagnostics comes last because
    /// it is the reason it is opened when something is wrong.
    /// </remarks>
    private static readonly DeviceOverlaySection[] Order =
    [
        DeviceOverlaySection.Overview,
        DeviceOverlaySection.PowerAndThermals,
        DeviceOverlaySection.ControllerAndMotion,
        DeviceOverlaySection.Oem,
        DeviceOverlaySection.LightingAndFeatures,
        DeviceOverlaySection.Glyphs,
        DeviceOverlaySection.Diagnostics,
    ];

    /// <summary>The page a section opens.</summary>
    /// <param name="section">The section.</param>
    /// <returns>Its navigation page.</returns>
    internal static OverlayPage PageFor(DeviceOverlaySection section) => section switch
    {
        DeviceOverlaySection.Overview => OverlayPage.DeviceOverview,
        DeviceOverlaySection.Profiles => OverlayPage.DeviceProfiles,
        DeviceOverlaySection.PowerAndThermals => OverlayPage.DevicePowerAndThermals,
        DeviceOverlaySection.ControllerAndMotion => OverlayPage.DeviceControllerAndMotion,
        DeviceOverlaySection.Oem => OverlayPage.DeviceOem,
        DeviceOverlaySection.LightingAndFeatures => OverlayPage.DeviceLightingAndFeatures,
        DeviceOverlaySection.Glyphs => OverlayPage.DeviceGlyphs,
        DeviceOverlaySection.Diagnostics => OverlayPage.DeviceDiagnostics,
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    /// <summary>The section a page belongs to, or null when the page is not a Device section.</summary>
    /// <param name="page">The navigation page.</param>
    /// <returns>Its section.</returns>
    internal static DeviceOverlaySection? SectionFor(OverlayPage page) => page switch
    {
        OverlayPage.DeviceOverview => DeviceOverlaySection.Overview,
        OverlayPage.DeviceProfiles => DeviceOverlaySection.Profiles,
        OverlayPage.DevicePowerAndThermals => DeviceOverlaySection.PowerAndThermals,
        OverlayPage.DeviceControllerAndMotion => DeviceOverlaySection.ControllerAndMotion,
        OverlayPage.DeviceOem => DeviceOverlaySection.Oem,
        OverlayPage.DeviceLightingAndFeatures => DeviceOverlaySection.LightingAndFeatures,
        OverlayPage.DeviceGlyphs => DeviceOverlaySection.Glyphs,
        OverlayPage.DeviceDiagnostics => DeviceOverlaySection.Diagnostics,
        _ => null,
    };

    /// <summary>The stable focus key for a section's card on the root page.</summary>
    /// <returns>Its focus key.</returns>
    /// <summary>The stable focus key for an entry's card on the root page.</summary>
    /// <param name="entry">The menu entry.</param>
    /// <returns>Its focus key.</returns>
    internal static string FocusKey(DeviceOverlaySectionEntry entry) =>
        entry.PluginSectionId is { } id
            ? "device.section.plugin." + id
            : FocusKey(entry.Section);

    internal static string FocusKey(DeviceOverlaySection section) =>
        "device.section." + section switch
        {
            DeviceOverlaySection.Overview => "overview",
            DeviceOverlaySection.Profiles => "profiles",
            DeviceOverlaySection.PowerAndThermals => "power",
            DeviceOverlaySection.ControllerAndMotion => "controller",
            DeviceOverlaySection.Oem => "oem",
            DeviceOverlaySection.LightingAndFeatures => "lighting",
            DeviceOverlaySection.Glyphs => "glyphs",
            DeviceOverlaySection.Diagnostics => "diagnostics",
            _ => "unknown",
        };

    /// <summary>Builds the section menu for a snapshot.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="performance">The shared performance rows that also live on Profiles.</param>
    /// <returns>Sections that currently have something to show, in presentation order.</returns>
    internal static IReadOnlyList<DeviceOverlaySectionEntry> Build(
        DeviceOverlaySnapshot snapshot,
        PerformanceOverlaySnapshot? performance = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Dictionary<DeviceOverlaySection, int> counts = [];
        Dictionary<DeviceOverlaySection, DescriptorStatus> statuses = [];
        Dictionary<string, int> pluginCounts = [];
        Dictionary<string, DescriptorStatus> pluginStatuses = [];
        foreach (DeviceOverlayCapability capability in snapshot.Capabilities)
        {
            if (capability.PluginSectionId is { } pluginSection)
            {
                pluginCounts[pluginSection] = pluginCounts.GetValueOrDefault(pluginSection) + 1;
                pluginStatuses[pluginSection] = MoreSerious(
                    pluginStatuses.GetValueOrDefault(pluginSection, DescriptorStatus.None),
                    capability.Status);
                continue;
            }

            counts[capability.Section] = counts.GetValueOrDefault(capability.Section) + 1;
            statuses[capability.Section] = MoreSerious(
                statuses.GetValueOrDefault(capability.Section, DescriptorStatus.None),
                capability.Status);
        }

        // WSGM's own rows never appear in the capability list, so each has to be counted into its
        // section explicitly. Without this a section holding only a direct row has a count of zero
        // and is dropped from the menu, which makes the row unreachable — the case for AutoTDP on a
        // device that publishes no power capability, and for the controller target on any device,
        // since no plugin publishes one.
        foreach ((DeviceOverlaySection section, DescriptorRow? row) in DirectRows(snapshot))
        {
            if (row is null)
            {
                continue;
            }

            counts[section] = counts.GetValueOrDefault(section) + 1;
            statuses[section] = MoreSerious(
                statuses.GetValueOrDefault(section, DescriptorStatus.None),
                row.Status);
        }

        if (performance is { Visible: true })
        {
            // The performance rows render on Power and thermals (frame limit, overlay level, and the
            // per-application detail rows), so they count toward that page. The per-application enable
            // toggle is not counted here at all: it is the headline toggle on the Device root, not a
            // row inside any section.
            foreach (DescriptorRow row in performance.ProfileRows
                .Where(row => !string.Equals(row.Id, ApplicationProfileRowId, StringComparison.Ordinal))
                .Concat(performance.Rows))
            {
                counts[DeviceOverlaySection.PowerAndThermals] =
                    counts.GetValueOrDefault(DeviceOverlaySection.PowerAndThermals) + 1;
                statuses[DeviceOverlaySection.PowerAndThermals] = MoreSerious(
                    statuses.GetValueOrDefault(
                        DeviceOverlaySection.PowerAndThermals,
                        DescriptorStatus.None),
                    row.Status);
            }
        }

        List<DeviceOverlaySectionEntry> entries = [];

        // The plugin's declared layout leads: it is the device describing itself. The WSGM-owned
        // sections that remain — profiles, glyphs, diagnostics, and any unplaced rows — follow it.
        foreach (DeviceOverlayPluginSection pluginSection in snapshot.PluginSections)
        {
            int pluginCount = pluginCounts.GetValueOrDefault(pluginSection.SectionId);
            if (pluginCount == 0)
            {
                continue;
            }

            entries.Add(new DeviceOverlaySectionEntry(
                DeviceOverlaySection.Overview,
                OverlayPage.DevicePluginSection,
                pluginSection.Title,
                pluginSection.Description,
                pluginCount,
                pluginStatuses.GetValueOrDefault(pluginSection.SectionId, DescriptorStatus.None))
            {
                PluginSectionId = pluginSection.SectionId,
                Icon = pluginSection.Icon,
            });
        }

        foreach (DeviceOverlaySection section in Order)
        {
            int count = counts.GetValueOrDefault(section);
            if (count == 0)
            {
                continue;
            }

            entries.Add(new DeviceOverlaySectionEntry(
                section,
                PageFor(section),
                TitleFor(section),
                DescriptionFor(section),
                count,
                statuses.GetValueOrDefault(section, DescriptorStatus.None)));
        }

        return entries;
    }

    /// <summary>The direct rows and the section each belongs to, in presentation order.</summary>
    /// <remarks>One table for the menu counting above and for the section renderer, so a row can
    /// never be counted into one section and drawn on another.</remarks>
    internal static IEnumerable<(DeviceOverlaySection Section, DescriptorRow? Row)> DirectRows(
        DeviceOverlaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        yield return (DeviceOverlaySection.PowerAndThermals, snapshot.AutoTdp);
        // The hardware performance profile and the authored fan curve sit with power and thermals:
        // the per-application profile is now the toggle on the Device root, so there is no Profiles
        // page to hold them, and both are decisions about how the device performs and cools.
        yield return (DeviceOverlaySection.PowerAndThermals, snapshot.Profile);
        yield return (DeviceOverlaySection.PowerAndThermals, snapshot.AuthoredProfile);
        yield return (DeviceOverlaySection.ControllerAndMotion, snapshot.Controller);
        yield return (DeviceOverlaySection.Diagnostics, snapshot.Recovery);
        yield return (DeviceOverlaySection.Glyphs, snapshot.GlyphSelection);
    }

    /// <summary>Selects the capabilities belonging to one section.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="section">The section to filter to.</param>
    /// <returns>That section's capabilities, in snapshot order.</returns>
    internal static IReadOnlyList<DeviceOverlayCapability> CapabilitiesIn(
        DeviceOverlaySnapshot snapshot,
        DeviceOverlaySection section)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<DeviceOverlayCapability> matching = [];
        foreach (DeviceOverlayCapability capability in snapshot.Capabilities)
        {
            if (capability.PluginSectionId is null && capability.Section == section)
            {
                matching.Add(capability);
            }
        }

        return matching;
    }

    /// <summary>Selects the capabilities of one plugin-declared section, in placement order.</summary>
    /// <param name="snapshot">The current Device snapshot.</param>
    /// <param name="sectionId">The declared section.</param>
    /// <returns>That section's capabilities ordered by sort order, then snapshot order.</returns>
    internal static IReadOnlyList<DeviceOverlayCapability> CapabilitiesInPluginSection(
        DeviceOverlaySnapshot snapshot,
        string sectionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Capabilities
            .Select((capability, index) => (Capability: capability, Index: index))
            .Where(item => string.Equals(
                item.Capability.PluginSectionId,
                sectionId,
                StringComparison.Ordinal))
            .OrderBy(item => item.Capability.SortOrder)
            .ThenBy(item => item.Index)
            .Select(item => item.Capability)
            .ToList();
    }

    /// <summary>Picks the status a section should advertise from those of its rows.</summary>
    /// <param name="left">One status.</param>
    /// <param name="right">The other.</param>
    /// <returns>The more serious of the two.</returns>
    /// <remarks>
    /// A section card shows the worst thing inside it. Showing the best, or the first, would let a
    /// faulted control hide behind a healthy one on a page the user has not opened.
    /// </remarks>
    internal static DescriptorStatus MoreSerious(
        DescriptorStatus left,
        DescriptorStatus right) =>
        Severity(right) > Severity(left) ? right : left;

    private static int Severity(DescriptorStatus status) => status switch
    {
        DescriptorStatus.Faulted => 5,
        DescriptorStatus.ExternallyOwned => 4,
        DescriptorStatus.Warning => 3,
        DescriptorStatus.Stale => 2,
        DescriptorStatus.Unsupported => 1,
        _ => 0,
    };

    private static string TitleFor(DeviceOverlaySection section) => section switch
    {
        DeviceOverlaySection.Overview => "Overview",
        DeviceOverlaySection.Profiles => "Profiles",
        DeviceOverlaySection.PowerAndThermals => "Power and thermals",
        DeviceOverlaySection.ControllerAndMotion => "Controller and motion",
        DeviceOverlaySection.Oem => "OEM buttons",
        DeviceOverlaySection.LightingAndFeatures => "Lighting and features",
        DeviceOverlaySection.Glyphs => "Glyphs",
        DeviceOverlaySection.Diagnostics => "Diagnostics and recovery",
        _ => "Device",
    };

    private static string DescriptionFor(DeviceOverlaySection section) => section switch
    {
        DeviceOverlaySection.Overview => "Device identity and performance mode",
        DeviceOverlaySection.Profiles => "Hardware and per-application performance profiles",
        DeviceOverlaySection.PowerAndThermals => "Power limits, fans, charging, and temperatures",
        DeviceOverlaySection.ControllerAndMotion => "Built-in controller, motion, and rumble",
        DeviceOverlaySection.Oem => "Device buttons and their assignments",
        DeviceOverlaySection.LightingAndFeatures => "Lighting and remaining device features",
        DeviceOverlaySection.Glyphs => "Button artwork, preview, and input test",
        DeviceOverlaySection.Diagnostics => "Health, readings, and recovery",
        _ => string.Empty,
    };
}
