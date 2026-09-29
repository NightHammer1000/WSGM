using System;
using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>Stable top-level destinations in the quick access sheet, in strip order.</summary>
internal enum OverlayDestination
{
    /// <summary>The pinned rows — the sheet's home and the Back target of every other root.</summary>
    QuickAccess,

    /// <summary>Steam start/focus, desktop, Big Picture and Steam exit (labelled "Session").</summary>
    Home,
    Steam,
    Device,

    /// <summary>System and storage tools (labelled "Tools").</summary>
    System,

    /// <summary>Wake, idle timeouts and the session-ending actions.</summary>
    Power,
}

/// <summary>Stable page identifiers used by the bounded in-overlay navigation stack.</summary>
internal enum OverlayPage
{
    QuickAccess,
    Home,
    Steam,
    SteamLibraryTabs,
    SteamCardManager,
    SteamArtwork,
    SteamLaunchConfiguration,
    SteamStorageFormat,
    Device,
    DeviceOverview,
    DeviceProfiles,
    DevicePowerAndThermals,
    DeviceControllerAndMotion,
    DeviceOem,
    DeviceLightingAndFeatures,
    DeviceColor,
    DeviceGlyphs,
    DeviceDiagnostics,

    /// <summary>One plugin-declared Device section; the route carries which one.</summary>
    DevicePluginSection,
    System,
    Power,
    PowerWakeLocks,
}

/// <summary>The single action selected by Back/B after higher-priority UI has been considered.</summary>
internal enum OverlayBackAction
{
    ClosePopup,
    CloseDialog,
    LeaveNestedPage,

    /// <summary>Return to the Quick access root from another destination's root.</summary>
    ReturnHome,
    CloseOverlay,
}

/// <summary>A navigation-stack entry with a semantic focus target for its caller.</summary>
internal readonly record struct OverlayRoute(
    OverlayDestination Destination,
    OverlayPage Page,
    string? ReturnFocusKey,
    string? SectionId = null);

/// <summary>
/// Owns top-level destination visibility and the bounded nested-page stack without retaining
/// controls, device descriptors, or service generations.
/// </summary>
internal sealed class OverlayNavigation
{
    internal const int MaximumDepth = 8;

    private readonly List<OverlayRoute> _stack = new(MaximumDepth);
    private bool _deviceVisible;

    internal OverlayNavigation()
    {
        Select(OverlayDestination.QuickAccess);
    }

    internal OverlayDestination Destination { get; private set; }

    internal OverlayPage Page => _stack[^1].Page;

    /// <summary>The plugin section id carried by the current page, when it is one.</summary>
    internal string? SectionId => _stack[^1].SectionId;

    internal int Depth => _stack.Count;

    internal IReadOnlyList<OverlayDestination> VisibleDestinations => _deviceVisible
        ? [OverlayDestination.QuickAccess, OverlayDestination.Home, OverlayDestination.Steam,
            OverlayDestination.Device, OverlayDestination.System, OverlayDestination.Power]
        : [OverlayDestination.QuickAccess, OverlayDestination.Home, OverlayDestination.Steam,
            OverlayDestination.System, OverlayDestination.Power];

    internal bool IsVisible(OverlayDestination destination)
        => destination != OverlayDestination.Device || _deviceVisible;

    internal bool SetDeviceVisible(bool visible)
    {
        if (_deviceVisible == visible)
        {
            return false;
        }

        _deviceVisible = visible;
        if (!visible && Destination == OverlayDestination.Device)
        {
            Select(OverlayDestination.QuickAccess);
        }

        return true;
    }

    // Every refusal below is a button press that visibly does nothing, which is the hardest kind of
    // bug to diagnose from a pasted log and the easiest to log. The successful transitions are
    // traced too: without them a refusal further along cannot be placed, because nothing else in
    // the overlay records which page the user was actually on.
    internal bool Select(OverlayDestination destination)
    {
        if (!IsVisible(destination))
        {
            Log.Warn($"Overlay nav: destination {destination} refused (not visible).");
            return false;
        }

        Destination = destination;
        _stack.Clear();
        _stack.Add(new OverlayRoute(destination, RootPage(destination), null));
        Log.Info($"Overlay nav: destination {destination}, page {Page}.");
        return true;
    }

    internal bool Push(OverlayPage page, string? returnFocusKey, string? sectionId = null)
    {
        if (_stack.Count >= MaximumDepth || DestinationFor(page) != Destination)
        {
            Log.Warn($"Overlay nav: push {page} refused from {Destination}/{Page} "
                + $"(depth={_stack.Count}, pageDestination={DestinationFor(page)}).");
            return false;
        }

        _stack.Add(new OverlayRoute(Destination, page, returnFocusKey, sectionId));
        Log.Info($"Overlay nav: pushed {page} (depth={_stack.Count}).");
        return true;
    }

    internal string? Pop()
    {
        if (_stack.Count <= 1)
        {
            return null;
        }

        string? returnFocusKey = _stack[^1].ReturnFocusKey;
        _stack.RemoveAt(_stack.Count - 1);
        Log.Info($"Overlay nav: popped to {Page} (depth={_stack.Count}).");
        return returnFocusKey;
    }

    internal OverlayBackAction BackAction(bool popupOpen, bool dialogOpen)
    {
        if (popupOpen)
        {
            return OverlayBackAction.ClosePopup;
        }

        if (dialogOpen)
        {
            return OverlayBackAction.CloseDialog;
        }

        if (_stack.Count > 1)
        {
            return OverlayBackAction.LeaveNestedPage;
        }

        // Quick access is the sheet's home: Back from any other root returns there, and Back
        // from it closes the sheet.
        return Destination == OverlayDestination.QuickAccess
            ? OverlayBackAction.CloseOverlay
            : OverlayBackAction.ReturnHome;
    }

    private static OverlayPage RootPage(OverlayDestination destination) => destination switch
    {
        OverlayDestination.QuickAccess => OverlayPage.QuickAccess,
        OverlayDestination.Home => OverlayPage.Home,
        OverlayDestination.Steam => OverlayPage.Steam,
        OverlayDestination.Device => OverlayPage.Device,
        OverlayDestination.System => OverlayPage.System,
        OverlayDestination.Power => OverlayPage.Power,
        _ => throw new ArgumentOutOfRangeException(nameof(destination)),
    };

    private static OverlayDestination DestinationFor(OverlayPage page) => page switch
    {
        OverlayPage.QuickAccess => OverlayDestination.QuickAccess,
        OverlayPage.Home => OverlayDestination.Home,
        OverlayPage.Steam or OverlayPage.SteamLibraryTabs or OverlayPage.SteamCardManager
            or OverlayPage.SteamArtwork or OverlayPage.SteamLaunchConfiguration
            or OverlayPage.SteamStorageFormat => OverlayDestination.Steam,
        OverlayPage.Device or OverlayPage.DeviceOverview or OverlayPage.DeviceProfiles
            or OverlayPage.DevicePowerAndThermals or OverlayPage.DeviceControllerAndMotion
            or OverlayPage.DeviceOem or OverlayPage.DeviceLightingAndFeatures
            or OverlayPage.DeviceColor
            or OverlayPage.DeviceGlyphs or OverlayPage.DeviceDiagnostics
            or OverlayPage.DevicePluginSection
            => OverlayDestination.Device,
        OverlayPage.System => OverlayDestination.System,
        OverlayPage.Power or OverlayPage.PowerWakeLocks => OverlayDestination.Power,
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };
}

/// <summary>Semantic focus and scroll state retained without keeping a page or control alive.</summary>
internal readonly record struct OverlayFocusState(string? SemanticKey, double ScrollOffset);

/// <summary>Stores bounded destination-local focus state across overlay window recreation.</summary>
internal sealed class OverlayFocusMemory
{
    private readonly Dictionary<OverlayDestination, OverlayFocusState> _states = new();

    internal void Remember(OverlayDestination destination, string? semanticKey, double scrollOffset)
        => _states[destination] = new OverlayFocusState(
            string.IsNullOrWhiteSpace(semanticKey) ? null : semanticKey,
            Math.Max(0, scrollOffset));

    internal OverlayFocusState Recall(OverlayDestination destination)
        => _states.TryGetValue(destination, out OverlayFocusState state)
            ? state
            : new OverlayFocusState(null, 0);
}
