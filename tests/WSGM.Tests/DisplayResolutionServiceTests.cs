using WSGM.Core;

namespace WSGM.Tests;

public sealed class DisplayResolutionServiceTests
{
    private static readonly DisplayResolution[] Accepted =
    [
        new(1280, 800),
        new(1920, 1200),
    ];

    private static DisplayResolutionService Service(
        List<DisplayResolution> applied,
        int[]? discoveryCount = null,
        bool applySucceeds = true)
    {
        int[] counter = discoveryCount ?? new int[1];
        return new DisplayResolutionService(
            () =>
            {
                counter[0]++;
                return Accepted;
            },
            (width, height) =>
            {
                if (applySucceeds)
                {
                    applied.Add(new DisplayResolution(width, height));
                }

                return applySucceeds;
            },
            // Injected rather than read from the real display, so the suite does not depend on the
            // machine having one.
            () => new DisplayResolution(1920, 1200));
    }

    [Fact]
    public void OnlyDiscoveredResolutionsAreOffered()
    {
        List<DisplayResolution> applied = [];

        Assert.Equal(Accepted, Service(applied).Options());
    }

    [Fact]
    public void DiscoveryIsCachedBecauseTestingEveryModeIsNotCheap()
    {
        int[] count = [0];
        DisplayResolutionService service = Service([], count);

        service.Options();
        service.Options();

        Assert.Equal(1, count[0]);
    }

    [Fact]
    public void AResolutionDiscoveryDidNotAcceptIsNeverSentToTheDriver()
    {
        // One that was never validated may not display at all, and recovering from a mode the user
        // cannot see is not something to leave them to do.
        List<DisplayResolution> applied = [];
        DisplayResolutionService service = Service(applied);

        Assert.False(service.Apply(new DisplayResolution(3840, 2160)));
        Assert.Empty(applied);
    }

    [Fact]
    public void AnAcceptedResolutionIsApplied()
    {
        List<DisplayResolution> applied = [];
        DisplayResolutionService service = Service(applied);

        Assert.True(service.Apply(new DisplayResolution(1280, 800)));
        Assert.Equal(new DisplayResolution(1280, 800), Assert.Single(applied));
    }

    [Fact]
    public void RestoringWithoutHavingMovedTheDisplayDoesNothing()
    {
        List<DisplayResolution> applied = [];
        DisplayResolutionService service = Service(applied);

        Assert.True(service.Restore());
        Assert.Empty(applied);
    }

    [Fact]
    public void ApplyingMarksTheDisplayAsHeldUntilRestored()
    {
        List<DisplayResolution> applied = [];
        DisplayResolutionService service = Service(applied);

        service.Apply(new DisplayResolution(1280, 800));
        applied.Clear();

        Assert.True(service.Restore());
        Assert.Equal(new DisplayResolution(1920, 1200), Assert.Single(applied));
    }

    [Fact]
    public void RestorePutsBackTheModeFoundBeforeTheFirstApply()
    {
        // Captured once: applying a second resolution before restoring must not overwrite the
        // user's own mode with the first one WSGM chose.
        List<DisplayResolution> applied = [];
        DisplayResolutionService service = Service(applied);

        service.Apply(new DisplayResolution(1280, 800));
        service.Apply(new DisplayResolution(1920, 1200));
        applied.Clear();
        service.Restore();

        Assert.Equal(new DisplayResolution(1920, 1200), Assert.Single(applied));
    }

    [Fact]
    public void RestoreIsIdempotentSoASecondCallCannotReapplyAnOldMode()
    {
        List<DisplayResolution> applied = [];
        DisplayResolutionService service = Service(applied);
        service.Apply(new DisplayResolution(1280, 800));

        service.Restore();
        int afterFirst = applied.Count;
        service.Restore();

        Assert.Equal(afterFirst, applied.Count);
    }

    [Fact]
    public void FailedRestoreKeepsTheDisplayHeldForARetry()
    {
        int attempts = 0;
        DisplayResolutionService service = new(
            () => Accepted,
            (_, _) => ++attempts != 2,
            () => new DisplayResolution(1920, 1200));
        Assert.True(service.Apply(new DisplayResolution(1280, 800)));

        Assert.False(service.Restore());
        Assert.True(service.Restore());
        // The snapshot was consumed by the successful restore, so a third call has nothing to do.
        Assert.True(service.Restore());
        Assert.Equal(3, attempts);
    }
}
