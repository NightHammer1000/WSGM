using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>The rule that decides what a card swap means for Steam's install-folder
/// list. The reader reuses one drive letter for every card, so path alone can never
/// answer "is this still the same library".</summary>
public class CardLibraryDecisionTests
{
    [Fact]
    public void ACardWhoseLibraryIsAlreadyRegisteredNeedsNothing()
    {
        Assert.Equal(
            CardLibraryAction.None,
            CardVolumeMonitor.Decide("777", ["777"]));
    }

    [Fact]
    public void ACardStreamHasNeverSeenIsAdded()
    {
        Assert.Equal(
            CardLibraryAction.Add,
            CardVolumeMonitor.Decide("777", []));
    }

    [Fact]
    public void APreviousCardsRegistrationAtTheSameLetterIsReplaced()
    {
        // The reported bug: Steam still holds the card that was pulled out, so a
        // plain add would append a SECOND registration at the same path and show the
        // old card's games beside the new card's capacity.
        Assert.Equal(
            CardLibraryAction.Replace,
            CardVolumeMonitor.Decide("777", ["222"]));
    }

    [Fact]
    public void AnAlreadyDuplicatedPathIsRebuiltEvenWhenThisCardIsOneOfTheEntries()
    {
        // Steam offers no way to drop one of several registrations at a path by
        // identity, so the correct entry surviving next to a phantom is still wrong.
        Assert.Equal(
            CardLibraryAction.Replace,
            CardVolumeMonitor.Decide("777", ["222", "777"]));
    }

    [Fact]
    public void ABlankCardInAReaderStreamStillHasALibraryForIsPurged()
    {
        Assert.Equal(
            CardLibraryAction.Purge,
            CardVolumeMonitor.Decide(null, ["222"]));
    }

    [Fact]
    public void ABlankCardWithNothingRegisteredIsLeftAlone()
    {
        Assert.Equal(
            CardLibraryAction.None,
            CardVolumeMonitor.Decide(null, []));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnreadableMarkerCountsAsNoLibraryRatherThanAnIdentity(string contentId)
    {
        Assert.Equal(
            CardLibraryAction.Purge,
            CardVolumeMonitor.Decide(contentId, ["222"]));
    }

    [Fact]
    public void BlankRegisteredIdsAreIgnoredSoAMalformedEntryCannotForceAReplace()
    {
        Assert.Equal(
            CardLibraryAction.Add,
            CardVolumeMonitor.Decide("777", ["", "  "]));
    }

    [Fact]
    public void ContentIdComparisonIsExactBecauseTheIdIsAnOpaqueNumber()
    {
        Assert.Equal(
            CardLibraryAction.Replace,
            CardVolumeMonitor.Decide("777", ["7770"]));
    }

    [Fact]
    public void ANullRegistrationListIsAProgrammingErrorNotAnEmptyOne()
    {
        Assert.Throws<ArgumentNullException>(() => CardVolumeMonitor.Decide("777", null!));
    }
}
