using WSGM.Overlay;

namespace WSGM.Tests;

public sealed class OverlayNavigationTests
{
    [Fact]
    public void DeviceDestinationIsAbsentUntilItsCapabilitySourceIsVisible()
    {
        OverlayNavigation navigation = new();

        Assert.Equal(
            new[]
            {
                OverlayDestination.QuickAccess, OverlayDestination.Home, OverlayDestination.Steam,
                OverlayDestination.System, OverlayDestination.Power,
            },
            navigation.VisibleDestinations);

        navigation.SetDeviceVisible(true);

        Assert.Equal(
            new[]
            {
                OverlayDestination.QuickAccess, OverlayDestination.Home, OverlayDestination.Steam,
                OverlayDestination.Device, OverlayDestination.System, OverlayDestination.Power,
            },
            navigation.VisibleDestinations);
    }

    [Fact]
    public void HidingDeviceWhileItIsSelectedReturnsToQuickAccessAndDropsItsPages()
    {
        OverlayNavigation navigation = new();
        navigation.SetDeviceVisible(true);
        navigation.Select(OverlayDestination.Device);

        navigation.SetDeviceVisible(false);

        Assert.Equal(OverlayDestination.QuickAccess, navigation.Destination);
        Assert.Equal(OverlayPage.QuickAccess, navigation.Page);
        Assert.Equal(1, navigation.Depth);
    }

    [Fact]
    public void TheSheetOpensOnQuickAccess()
    {
        OverlayNavigation navigation = new();

        Assert.Equal(OverlayDestination.QuickAccess, navigation.Destination);
        Assert.Equal(OverlayPage.QuickAccess, navigation.Page);
    }

    [Fact]
    public void APluginSectionPageCarriesItsSectionId()
    {
        OverlayNavigation navigation = new();
        navigation.SetDeviceVisible(true);
        Assert.True(navigation.Select(OverlayDestination.Device));

        Assert.True(navigation.Push(OverlayPage.DevicePluginSection, "menu.key", "cooling"));

        Assert.Equal("cooling", navigation.SectionId);
        Assert.Equal(OverlayDestination.Device, navigation.Destination);
        Assert.Equal("menu.key", navigation.Pop());
        Assert.Null(navigation.SectionId);
    }

    [Fact]
    public void NestedStackRejectsAnotherDestinationAndStopsAtItsBound()
    {
        OverlayNavigation navigation = new();
        navigation.Select(OverlayDestination.Steam);

        Assert.False(navigation.Push(OverlayPage.PowerWakeLocks, "wrong.destination"));
        for (int depth = 1; depth < OverlayNavigation.MaximumDepth; depth++)
        {
            Assert.True(navigation.Push(OverlayPage.SteamLibraryTabs, $"steam.row.{depth}"));
        }

        Assert.False(navigation.Push(OverlayPage.SteamArtwork, "one.too.many"));
        Assert.Equal(OverlayNavigation.MaximumDepth, navigation.Depth);
    }

    [Fact]
    public void BackPriorityIsPopupThenDialogThenNestedThenQuickAccessThenOverlay()
    {
        OverlayNavigation navigation = new();
        navigation.Select(OverlayDestination.Steam);
        navigation.Push(OverlayPage.SteamArtwork, "steam.artwork");

        Assert.Equal(OverlayBackAction.ClosePopup, navigation.BackAction(true, true));
        Assert.Equal(OverlayBackAction.CloseDialog, navigation.BackAction(false, true));
        Assert.Equal(OverlayBackAction.LeaveNestedPage, navigation.BackAction(false, false));

        Assert.Equal("steam.artwork", navigation.Pop());
        Assert.Equal(OverlayBackAction.ReturnHome, navigation.BackAction(false, false));

        // Session (the Home destination) is a root like any other now: Back returns to
        // Quick access from it, and only Quick access itself closes the sheet.
        navigation.Select(OverlayDestination.Home);
        Assert.Equal(OverlayBackAction.ReturnHome, navigation.BackAction(false, false));

        navigation.Select(OverlayDestination.QuickAccess);
        Assert.Equal(OverlayBackAction.CloseOverlay, navigation.BackAction(false, false));
    }

    [Fact]
    public void FocusMemoryRetainsSemanticKeysWithoutControlReferencesAndClampsScroll()
    {
        OverlayFocusMemory memory = new();

        memory.Remember(OverlayDestination.System, "system.shutdown", -50);

        Assert.Equal(
            new OverlayFocusState("system.shutdown", 0),
            memory.Recall(OverlayDestination.System));
        Assert.Equal(
            new OverlayFocusState(null, 0),
            memory.Recall(OverlayDestination.Home));
    }

    [Fact]
    public void LeavingANestedPageHandsBackTheKeyItWasEnteredFrom()
    {
        // This is the whole of focus restoration for a nested page: the caller stores where it was
        // on the way in, and Pop is what gives it back. A pop that discarded it would drop the user
        // at the top of the page they returned to.
        OverlayNavigation navigation = new();
        navigation.Select(OverlayDestination.Steam);
        Assert.True(navigation.Push(OverlayPage.SteamCardManager, "steam.cards"));

        Assert.Equal("steam.cards", navigation.Pop());
        Assert.Equal(OverlayPage.Steam, navigation.Page);
    }

    [Fact]
    public void PoppingARootReturnsNothingAndLeavesTheDestinationIntact()
    {
        // Back at a destination root is not a pop — it is ReturnHome or CloseOverlay — so this must
        // stay a no-op rather than emptying the stack and leaving Page with nothing to read.
        OverlayNavigation navigation = new();
        navigation.Select(OverlayDestination.System);

        Assert.Null(navigation.Pop());
        Assert.Equal(1, navigation.Depth);
        Assert.Equal(OverlayPage.System, navigation.Page);
    }

    [Fact]
    public void EveryDestinationHasARootPageAndEveryPageHasADestination()
    {
        // Both maps are exhaustive switches that throw on an unhandled value, so a page added to the
        // enum without being routed fails here rather than at the moment a user navigates to it.
        OverlayNavigation navigation = new();
        navigation.SetDeviceVisible(true);
        foreach (OverlayDestination destination in Enum.GetValues<OverlayDestination>())
        {
            Assert.True(navigation.Select(destination));
            Assert.Equal(1, navigation.Depth);
        }

        foreach (OverlayPage page in Enum.GetValues<OverlayPage>())
        {
            // Select the destination this page belongs to, then prove the page is reachable from it.
            // Push refuses a page whose destination is not current, so a successful push for every
            // page is exactly the statement that every page is routed.
            bool pushed = false;
            foreach (OverlayDestination destination in Enum.GetValues<OverlayDestination>())
            {
                navigation.Select(destination);
                if (navigation.Page == page || navigation.Push(page, null))
                {
                    pushed = true;
                    break;
                }
            }

            Assert.True(pushed, $"{page} is not reachable from any destination.");
        }
    }

    [Fact]
    public void SelectingADestinationResetsToItsRootPage()
    {
        // The Device panel's rows are built for whatever page the navigation is on, and the window
        // rebuilds them when the destination is shown. That contract only holds if selecting a
        // destination is guaranteed to land on its root: a Select that left a section page current
        // would have the window render a section's rows under the root's heading. Reaching an empty
        // "DEVICE CONTROLS" with sixteen live capabilities was the visible form of that mismatch.
        OverlayNavigation navigation = new();
        navigation.SetDeviceVisible(true);
        Assert.True(navigation.Select(OverlayDestination.Device));
        Assert.True(navigation.Push(OverlayPage.DevicePowerAndThermals, null));
        Assert.Equal(OverlayPage.DevicePowerAndThermals, navigation.Page);

        Assert.True(navigation.Select(OverlayDestination.Home));
        Assert.True(navigation.Select(OverlayDestination.Device));

        Assert.Equal(OverlayPage.Device, navigation.Page);
        Assert.Equal(1, navigation.Depth);
        Assert.Null(DeviceOverlaySectionPages.SectionFor(navigation.Page));
    }
}
