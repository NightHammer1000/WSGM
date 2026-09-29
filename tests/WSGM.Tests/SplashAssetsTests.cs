using WSGM.Core;

namespace WSGM.Tests;

public sealed class SplashAssetsTests : IDisposable
{
    private readonly string _root = System.IO.Directory
        .CreateTempSubdirectory("wsgm-splash-assets-")
        .FullName;

    private string SourceDir => Path.Combine(_root, "source");
    private string TargetDir => Path.Combine(_root, "target");

    /// <summary>Prepare plus an immediate commit. Production never does this — the save path
    /// commits only after the config write succeeds — so the convenience belongs here rather
    /// than on <see cref="SplashAssets"/>.</summary>
    private static void Materialize(SplashConfig splash, string targetDirectory)
    {
        using SplashAssets.Transaction staged = SplashAssets.Prepare(splash, targetDirectory);
        staged.Commit();
    }

    public SplashAssetsTests()
    {
        System.IO.Directory.CreateDirectory(SourceDir);
        System.IO.Directory.CreateDirectory(TargetDir);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private string[] FileNames() =>
        System.IO.Directory
            .GetFiles(TargetDir)
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    private string[] SidecarNames() =>
        FileNames()
            .Where(f => f.EndsWith(".wsgmnew", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private string WriteSource(string name, string content = "image-bytes")
    {
        var path = Path.Combine(SourceDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void MaterializeCopiesBothSlotsToDeterministicNamesKeepingTheSourceExtension()
    {
        var splash = new SplashConfig
        {
            LogoImagePath = WriteSource("my logo.png", "logo-bytes"),
            BackgroundImagePath = WriteSource("wallpaper.JPG", "bg-bytes"),
        };

        Materialize(splash, TargetDir);

        Assert.Equal(Path.Combine(TargetDir, "logo.png"), splash.LogoImagePath);
        Assert.Equal(Path.Combine(TargetDir, "background.JPG"), splash.BackgroundImagePath);
        Assert.Equal("logo-bytes", File.ReadAllText(splash.LogoImagePath));
        Assert.Equal("bg-bytes", File.ReadAllText(splash.BackgroundImagePath));
    }

    [Fact]
    public void MaterializeOverwritesTheExistingCopyAndDeletesStaleSiblingExtensions()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.jpg"), "old-jpg");
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "old-png");
        File.WriteAllText(Path.Combine(TargetDir, "unrelated.bmp"), "not-a-slot-file");
        var splash = new SplashConfig { LogoImagePath = WriteSource("new.png", "new-png") };

        Materialize(splash, TargetDir);

        Assert.Equal(Path.Combine(TargetDir, "logo.png"), splash.LogoImagePath);
        Assert.Equal("new-png", File.ReadAllText(splash.LogoImagePath));
        Assert.False(File.Exists(Path.Combine(TargetDir, "logo.jpg")));
        Assert.True(File.Exists(Path.Combine(TargetDir, "unrelated.bmp")));
    }

    [Fact]
    public void MaterializeWithEmptyPathsDeletesStaleCopiesOfBothSlots()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "stale");
        File.WriteAllText(Path.Combine(TargetDir, "logo.gif"), "stale");
        File.WriteAllText(Path.Combine(TargetDir, "background.jpg"), "stale");
        var splash = new SplashConfig { LogoImagePath = "", BackgroundImagePath = "" };

        Materialize(splash, TargetDir);

        Assert.Equal("", splash.LogoImagePath);
        Assert.Equal("", splash.BackgroundImagePath);
        Assert.Empty(System.IO.Directory.GetFiles(TargetDir));
    }

    [Fact]
    public void MaterializeLeavesPathsAlreadyInsideTheTargetDirectoryUntouched()
    {
        var splash = new SplashConfig { LogoImagePath = WriteSource("logo.png", "logo-bytes") };
        Materialize(splash, TargetDir);
        var materialized = splash.LogoImagePath;
        var writeTime = File.GetLastWriteTimeUtc(materialized);

        Materialize(splash, TargetDir);

        Assert.Equal(materialized, splash.LogoImagePath);
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(materialized));
        Assert.Equal("logo-bytes", File.ReadAllText(materialized));
    }

    [Fact]
    public void MaterializeClearsTheSlotWhenItsOwnMaterializedCopyIsGoneFromTheSplashFolder()
    {
        // The "already inside the directory" short-circuit used to fire on the path
        // alone, so a copy deleted behind WSGM's back (cleanup tool, AV, a user
        // emptying the folder) stayed in config.json and was re-persisted by every
        // later save, forever naming a file that is not there.
        var splash = new SplashConfig { LogoImagePath = WriteSource("logo.png", "logo-bytes") };
        Materialize(splash, TargetDir);
        File.Delete(splash.LogoImagePath);

        using var staged = SplashAssets.Prepare(splash, TargetDir);

        Assert.Equal("", splash.LogoImagePath);
        // Nothing the user did failed, and nothing was written: reverting the save
        // would be dishonest, so the slot is simply cleared.
        Assert.Empty(staged.Commit());
    }

    [Fact]
    public void ASlotPointingAtTheOtherSlotsCopyGetsItsOwnFileInsteadOfLosingItToACleanup()
    {
        // Cross-slot aliasing: the background slot names the LOGO slot's live copy
        // (a hand-edited config, an imported theme, or the picker aimed at WSGM's own
        // folder). It used to short-circuit unstaged — and the logo slot's new ".jpg"
        // pick then deleted "logo.png" as a stale sibling, so the saved config named a
        // deleted file and nothing was reported.
        var first = new SplashConfig { LogoImagePath = WriteSource("first.png", "first-logo") };
        Materialize(first, TargetDir);
        var logoCopy = Path.Combine(TargetDir, "logo.png");

        var second = new SplashConfig
        {
            LogoImagePath = WriteSource("second.jpg", "second-logo"),
            BackgroundImagePath = logoCopy,
        };
        using var staged = SplashAssets.Prepare(second, TargetDir);

        Assert.Empty(staged.Commit());
        // The background slot owns a copy of its own now, under ITS base name…
        Assert.Equal(Path.Combine(TargetDir, "background.png"), second.BackgroundImagePath);
        Assert.Equal("first-logo", File.ReadAllText(second.BackgroundImagePath));
        // …and the logo slot's stale-sibling cleanup can no longer take it away.
        Assert.Equal(Path.Combine(TargetDir, "logo.jpg"), second.LogoImagePath);
        Assert.Equal("second-logo", File.ReadAllText(second.LogoImagePath));
        Assert.False(File.Exists(logoCopy));
        Assert.Equal(new[] { "background.png", "logo.jpg" }, FileNames());
    }

    [Fact]
    public void AWhitespaceOnlyPathIsPersistedAsNoImageRatherThanAsWhitespace()
    {
        var splash = new SplashConfig { LogoImagePath = "   ", BackgroundImagePath = "\t" };

        using var staged = SplashAssets.Prepare(splash, TargetDir);

        Assert.Equal("", splash.LogoImagePath);
        Assert.Equal("", splash.BackgroundImagePath);
        Assert.Empty(staged.Commit());
    }

    [Fact]
    public void MaterializeKeepsTheOriginalPathAndNeverThrowsWhenTheSourceIsUnreadable()
    {
        var missing = Path.Combine(SourceDir, "does-not-exist.png");
        var splash = new SplashConfig
        {
            LogoImagePath = missing,
            BackgroundImagePath = WriteSource("bg.png", "bg-bytes"),
        };

        Materialize(splash, TargetDir);

        Assert.Equal(missing, splash.LogoImagePath);
        Assert.False(File.Exists(Path.Combine(TargetDir, "logo.png")));
        Assert.Equal(Path.Combine(TargetDir, "background.png"), splash.BackgroundImagePath);
    }

    [Fact]
    public void MaterializeCreatesTheTargetDirectoryWhenItDoesNotExistYet()
    {
        var freshTarget = Path.Combine(_root, "fresh", "splash");
        var splash = new SplashConfig { BackgroundImagePath = WriteSource("bg.webp", "bg-bytes") };

        Materialize(splash, freshTarget);

        Assert.Equal(Path.Combine(freshTarget, "background.webp"), splash.BackgroundImagePath);
        Assert.Equal("bg-bytes", File.ReadAllText(splash.BackgroundImagePath));
    }

    [Fact]
    public void PrepareRewritesThePathsToTheFinalNamesWithoutTouchingTheLiveFilesYet()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "live-logo");
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };

        using var staged = SplashAssets.Prepare(splash, TargetDir);

        Assert.Equal(Path.Combine(TargetDir, "logo.png"), splash.LogoImagePath);
        Assert.Equal("live-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
    }

    [Fact]
    public void RollbackKeepsThePreviouslyMaterializedBytesAndLeavesNoSidecarBehind()
    {
        var first = new SplashConfig
        {
            LogoImagePath = WriteSource("first.png", "first-logo"),
            BackgroundImagePath = WriteSource("first-bg.png", "first-bg"),
        };
        Materialize(first, TargetDir);
        var liveFiles = System.IO.Directory.GetFiles(TargetDir).OrderBy(f => f).ToArray();

        // A save whose config write fails: stage, then roll back.
        var second = new SplashConfig
        {
            LogoImagePath = WriteSource("second.png", "second-logo"),
            BackgroundImagePath = WriteSource("second-bg.jpg", "second-bg"),
        };
        var staged = SplashAssets.Prepare(second, TargetDir);
        staged.Rollback();
        staged.Dispose();

        Assert.Equal(liveFiles, System.IO.Directory.GetFiles(TargetDir).OrderBy(f => f).ToArray());
        Assert.Equal("first-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
        Assert.Equal("first-bg", File.ReadAllText(Path.Combine(TargetDir, "background.png")));
    }

    [Fact]
    public void DisposeWithoutCommitRollsBackTheStagedCopies()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "live-logo");
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };

        using (SplashAssets.Prepare(splash, TargetDir)) { }

        Assert.Equal(new[] { "logo.png" }, FileNames());
        Assert.Equal("live-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
    }

    [Fact]
    public void CommitReplacesTheLiveFilesAndRemovesTheSidecars()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "live-logo");
        var splash = new SplashConfig
        {
            LogoImagePath = WriteSource("picked.jpg", "new-logo"),
            BackgroundImagePath = WriteSource("picked-bg.png", "new-bg"),
        };

        using var staged = SplashAssets.Prepare(splash, TargetDir);
        staged.Commit();

        Assert.Equal(Path.Combine(TargetDir, "logo.jpg"), splash.LogoImagePath);
        Assert.Equal("new-logo", File.ReadAllText(splash.LogoImagePath));
        Assert.Equal("new-bg", File.ReadAllText(splash.BackgroundImagePath));
        Assert.False(File.Exists(Path.Combine(TargetDir, "logo.png"))); // Stale extension gone.
        Assert.Equal(new[] { "background.png", "logo.jpg" }, FileNames());
    }

    [Fact]
    public void ClearingASlotRemovesTheLiveFileOnlyOnCommit()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "live-logo");
        var splash = new SplashConfig { LogoImagePath = "" };

        var staged = SplashAssets.Prepare(splash, TargetDir);
        Assert.True(File.Exists(Path.Combine(TargetDir, "logo.png")));

        // A failed save leaves the cleared slot's file in place, matching the
        // still-persisted config that still points at it.
        staged.Rollback();
        Assert.True(File.Exists(Path.Combine(TargetDir, "logo.png")));

        using var committed = SplashAssets.Prepare(splash, TargetDir);
        committed.Commit();
        Assert.False(File.Exists(Path.Combine(TargetDir, "logo.png")));
    }

    [Fact]
    public void PrepareSweepsASidecarOrphanedByACrashedSaveSoCommitLeavesOnlyTheNewCopy()
    {
        // A save killed between Prepare and Commit leaves "logo.jpg.wsgmnew" behind.
        // DeleteCopies can never match it — its file name without extension is
        // "logo.jpg", not "logo" — so only the sidecar sweep gets rid of it, and a
        // later ".png" pick would otherwise carry it forever.
        File.WriteAllText(Path.Combine(TargetDir, "logo.jpg.wsgmnew"), "orphan-sidecar");
        File.WriteAllText(Path.Combine(TargetDir, "logo.jpg"), "live-logo");
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };

        using var staged = SplashAssets.Prepare(splash, TargetDir);
        staged.Commit();

        Assert.Equal(new[] { "logo.png" }, FileNames());
        Assert.Equal("new-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
    }

    [Fact]
    public void PrepareSweepsAnOrphanedSidecarEvenWhenTheSaveIsRolledBack()
    {
        File.WriteAllText(Path.Combine(TargetDir, "logo.jpg.wsgmnew"), "orphan-sidecar");
        File.WriteAllText(Path.Combine(TargetDir, "logo.jpg"), "live-logo");
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };

        var staged = SplashAssets.Prepare(splash, TargetDir);
        staged.Rollback();
        staged.Dispose();

        Assert.DoesNotContain(
            FileNames(),
            name => name.EndsWith(".wsgmnew", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal(new[] { "logo.jpg" }, FileNames());
        Assert.Equal("live-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.jpg")));
    }

    [Fact]
    public void TheOrphanSweepDoesNotCollectALiveCopyLiterallyNamedAfterTheStagedSuffix()
    {
        // Matcher scope: "logo.wsgmnew" starts with "logo." and ends with ".wsgmnew",
        // so a naive StartsWith/EndsWith pair eats a LIVE materialized copy that just
        // happens to carry that extension. A sidecar always has a non-empty unique
        // segment in between ("logo.png.{guid}.wsgmnew"), and only that shape is swept.
        File.WriteAllText(Path.Combine(TargetDir, "logo.wsgmnew"), "live-copy");
        File.WriteAllText(Path.Combine(TargetDir, "background.wsgmnew"), "live-bg-copy");
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };

        var staged = SplashAssets.Prepare(splash, TargetDir);
        staged.Rollback();
        staged.Dispose();

        Assert.Equal("live-copy", File.ReadAllText(Path.Combine(TargetDir, "logo.wsgmnew")));
        Assert.Equal("live-bg-copy", File.ReadAllText(Path.Combine(TargetDir, "background.wsgmnew")));
    }

    [Fact]
    public void AFailedStagingIsReportedByCommitSoTheSaveCannotClaimSuccess()
    {
        // Staging can fail on its own (unreadable source here; a denied or full target
        // directory does the same). The path left in the config is then the user's
        // volatile pick — the very thing materialization exists to eliminate — so it
        // has to reach the SAME reported-failure path a failed promotion takes.
        var missing = Path.Combine(SourceDir, "does-not-exist.png");
        var splash = new SplashConfig
        {
            LogoImagePath = missing,
            BackgroundImagePath = WriteSource("bg.png", "bg-bytes"),
        };

        using var staged = SplashAssets.Prepare(splash, TargetDir);
        var failed = staged.Commit();

        Assert.Equal(new[] { SplashAssets.LogoSlot }, failed);
        // The picked path survives in the splash section the caller keeps, so the view
        // model can retry it; the healthy slot is materialized as usual.
        Assert.Equal(missing, splash.LogoImagePath);
        Assert.Equal(Path.Combine(TargetDir, "background.png"), splash.BackgroundImagePath);
        Assert.Equal("bg-bytes", File.ReadAllText(splash.BackgroundImagePath));
    }

    [Fact]
    public void AnUncreatableTargetDirectoryIsReportedForEverySlotThatHadAPick()
    {
        // A file where the splash directory belongs makes CreateDirectory throw the
        // same way a denied ACL or a full volume does.
        var blocked = Path.Combine(_root, "blocked-target");
        File.WriteAllText(blocked, "not-a-directory");
        var splash = new SplashConfig
        {
            LogoImagePath = WriteSource("a.png", "logo-bytes"),
            BackgroundImagePath = WriteSource("b.png", "bg-bytes"),
        };

        using var staged = SplashAssets.Prepare(splash, blocked);

        Assert.Equal(
            new[] { SplashAssets.LogoSlot, SplashAssets.BackgroundSlot },
            staged.Commit()
        );
    }

    [Fact]
    public void AClearedSlotIsNeverReportedAsAFailureEvenWhenItsDirectoryIsUnusable()
    {
        // Nothing to materialize means nothing that can fail to materialize: reporting
        // here would revert the config to an image the user just removed. The embedded
        // null character makes the very first path call throw, so both slots really do
        // run through Prepare's failure handling.
        var unusable = Path.Combine(_root, "bad\0dir");
        var splash = new SplashConfig { LogoImagePath = "", BackgroundImagePath = "" };

        using var staged = SplashAssets.Prepare(splash, unusable);

        Assert.Empty(staged.Commit());
    }

    [Fact]
    public void CommitReportsNoFailedSlotsWhenEverySidecarWentLive()
    {
        var splash = new SplashConfig
        {
            LogoImagePath = WriteSource("picked.png", "new-logo"),
            BackgroundImagePath = WriteSource("picked-bg.png", "new-bg"),
        };

        using var staged = SplashAssets.Prepare(splash, TargetDir);

        Assert.Empty(staged.Commit());
    }

    [Fact]
    public void CommitReportsTheSlotWhosePromotionFailedAndLeavesItsLiveContentAlone()
    {
        // A directory where the live file belongs makes the promoting File.Move throw
        // the same way a locked file, an AV hold, or a denied ACL does.
        System.IO.Directory.CreateDirectory(Path.Combine(TargetDir, "logo.png"));
        var splash = new SplashConfig
        {
            LogoImagePath = WriteSource("picked.png", "new-logo"),
            BackgroundImagePath = WriteSource("picked-bg.png", "new-bg"),
        };

        using var staged = SplashAssets.Prepare(splash, TargetDir);
        var failed = staged.Commit();

        Assert.Equal(new[] { SplashAssets.LogoSlot }, failed);
        // The background slot is unaffected by the logo slot's failure.
        Assert.Equal("new-bg", File.ReadAllText(splash.BackgroundImagePath));
        // No sidecar survives a failed promotion.
        Assert.DoesNotContain(
            FileNames(),
            name => name.EndsWith(".wsgmnew", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void CommitReportsBothSlotsWhenNeitherCanBePromoted()
    {
        System.IO.Directory.CreateDirectory(Path.Combine(TargetDir, "logo.png"));
        System.IO.Directory.CreateDirectory(Path.Combine(TargetDir, "background.png"));
        var splash = new SplashConfig
        {
            LogoImagePath = WriteSource("picked.png", "new-logo"),
            BackgroundImagePath = WriteSource("picked-bg.png", "new-bg"),
        };

        using var staged = SplashAssets.Prepare(splash, TargetDir);

        Assert.Equal(
            new[] { SplashAssets.LogoSlot, SplashAssets.BackgroundSlot },
            staged.Commit()
        );
    }

    [Fact]
    public void EachTransactionStagesUnderItsOwnSidecarNameSoConcurrentSaversCannotCollide()
    {
        // Staging now happens OUTSIDE the cross-process config lock (a picked image can
        // be tens of megabytes and the lock's timeout is 2 s), so two savers really can
        // stage the same slot at the same time. The per-transaction GUID is what keeps
        // them from writing each other's sidecar.
        var first = new SplashConfig { LogoImagePath = WriteSource("a.png", "first-logo") };
        var second = new SplashConfig { LogoImagePath = WriteSource("b.png", "second-logo") };

        using var firstStaged = SplashAssets.Prepare(first, TargetDir);
        var afterFirst = SidecarNames();
        using var secondStaged = SplashAssets.Prepare(second, TargetDir);

        var sidecars = SidecarNames();
        Assert.Single(afterFirst);
        Assert.Equal(2, sidecars.Length);
        Assert.All(sidecars, name => Assert.StartsWith("logo.png.", name, StringComparison.Ordinal));
        // The second Prepare's orphan sweep must NOT have taken the first's live sidecar.
        Assert.Contains(afterFirst[0], sidecars);

        // Both promotions still succeed; the later commit simply wins, and the config
        // path each saver persists is the same "logo.png" it points at.
        Assert.Empty(firstStaged.Commit());
        Assert.Equal("first-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
        Assert.Empty(secondStaged.Commit());
        Assert.Equal("second-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
        Assert.Equal(new[] { "logo.png" }, FileNames());
    }

    [Fact]
    public void TheOrphanSweepStillCollectsACrashedSavesSidecarWhileAnotherStagingIsLive()
    {
        // The sweep tells an orphan from a live staging by the open handle Prepare keeps
        // on its own sidecar — a crashed save's handle is closed by Windows, so its
        // leftover is collected even though a concurrent transaction is in flight.
        var live = new SplashConfig { LogoImagePath = WriteSource("a.png", "live-staging") };
        using var liveStaged = SplashAssets.Prepare(live, TargetDir);
        var liveSidecar = Assert.Single(SidecarNames());
        File.WriteAllText(Path.Combine(TargetDir, "logo.png.deadbeef.wsgmnew"), "orphan");

        var other = new SplashConfig { LogoImagePath = WriteSource("b.png", "other") };
        using var otherStaged = SplashAssets.Prepare(other, TargetDir);

        var sidecars = SidecarNames();
        Assert.DoesNotContain("logo.png.deadbeef.wsgmnew", sidecars);
        Assert.Contains(liveSidecar, sidecars);
    }

    [Fact]
    public void ASidecarStaysProtectedUntilAfterItsPromotingMoveSoASweepCannotStealIt()
    {
        // Micro-race: Commit used to close the sidecar's protecting handle BEFORE the
        // move, and Prepare runs outside the config lock — so a concurrent saver's
        // orphan sweep could delete the sidecar in that window and turn a healthy save
        // into "splash image not updated". The handle now grants FileShare.Delete
        // (which is what makes renaming a held-open file legal) and is closed only
        // after the move, so no window exists. Standing in for the concurrent sweep:
        // a delete attempt against the sidecar right before the move.
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };
        using var staged = SplashAssets.Prepare(splash, TargetDir);
        var sidecar = Path.Combine(TargetDir, Assert.Single(SidecarNames()));

        // The sweep's liveness probe (an exclusive open) must refuse the live sidecar…
        Assert.Throws<IOException>(() =>
            new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.None).Dispose()
        );
        // …and the promotion still succeeds with that same handle open.
        Assert.Empty(staged.Commit());
        Assert.Equal("new-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
        Assert.Empty(SidecarNames());
    }

    [Fact]
    public void ASidecarDeletedByAnOutsideActorIsRecoveredByCopyingTheOriginalSourceAgain()
    {
        // The protecting handle grants FileShare.Delete (that is what makes renaming a
        // held-open file legal), so AV, a cleanup tool, or an OLDER WSGM build's orphan
        // sweep — which does not know about the liveness probe — really can delete a
        // healthy in-flight sidecar. The promoting move then fails, and reporting that
        // as "splash image not updated" would revert a save that has nothing wrong with
        // it: the picked source is still on disk, so the copy is simply redone.
        var source = WriteSource("picked.png", "new-logo");
        var splash = new SplashConfig { LogoImagePath = source };
        using var staged = SplashAssets.Prepare(splash, TargetDir);
        File.Delete(Path.Combine(TargetDir, Assert.Single(SidecarNames())));

        Assert.Empty(staged.Commit());
        Assert.Equal("new-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
        Assert.Empty(SidecarNames());
    }

    [Fact]
    public void ADeletedSidecarIsStillReportedWhenTheOriginalSourceIsGoneToo()
    {
        // The fallback is the only thing standing between the vanished sidecar and a
        // reported failure: with the source gone as well nothing can produce the live
        // file, and the save must go back to reporting it.
        var source = WriteSource("picked.png", "new-logo");
        var splash = new SplashConfig { LogoImagePath = source };
        using var staged = SplashAssets.Prepare(splash, TargetDir);
        File.Delete(Path.Combine(TargetDir, Assert.Single(SidecarNames())));
        File.Delete(source);

        Assert.Equal(new[] { SplashAssets.LogoSlot }, staged.Commit());
        Assert.False(File.Exists(Path.Combine(TargetDir, "logo.png")));
    }

    [Fact]
    public void AStaleCopyCleanupThatThrowsAfterASuccessfulMoveIsNotReportedAsAFailedPromotion()
    {
        // The move is what decides promotion success: once it ran the new image IS the
        // live file, so reporting a failure would make the save revert its persisted
        // path to an image that is no longer on disk.
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };
        using var staged = SplashAssets.Prepare(splash, TargetDir);
        staged.CleanUpStaleCopiesOverride = (_, _, _) => throw new IOException("enumeration blew up");

        var failed = staged.Commit();

        Assert.Empty(failed);
        Assert.Equal("new-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
    }

    [Fact]
    public void AClearedSlotWhoseCleanupThrowsIsNotReportedAsAFailedPromotion()
    {
        // A cleared slot persists an empty path, so a leftover file is garbage the next
        // save sweeps — never a broken promotion the caller must revert.
        File.WriteAllText(Path.Combine(TargetDir, "logo.png"), "live-logo");
        var splash = new SplashConfig { LogoImagePath = "", BackgroundImagePath = "" };
        using var staged = SplashAssets.Prepare(splash, TargetDir);
        staged.CleanUpStaleCopiesOverride = (_, _, _) => throw new IOException("enumeration blew up");

        Assert.Empty(staged.Commit());
    }

    [Fact]
    public void CommitAfterRollbackIsANoOpAndRollbackAfterCommitKeepsTheCommittedFiles()
    {
        var splash = new SplashConfig { LogoImagePath = WriteSource("picked.png", "new-logo") };
        var rolledBack = SplashAssets.Prepare(splash, TargetDir);
        rolledBack.Rollback();
        rolledBack.Commit();
        Assert.Empty(System.IO.Directory.GetFiles(TargetDir));

        var again = new SplashConfig { LogoImagePath = WriteSource("picked2.png", "newer-logo") };
        var committed = SplashAssets.Prepare(again, TargetDir);
        committed.Commit();
        committed.Rollback();
        committed.Dispose();

        Assert.Equal("newer-logo", File.ReadAllText(Path.Combine(TargetDir, "logo.png")));
    }
}
