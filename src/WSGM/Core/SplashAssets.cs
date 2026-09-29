using System;
using System.Collections.Generic;
using System.IO;

namespace WSGM.Core;

/// <summary>Copies user-picked splash images into WSGM's per-user splash asset
/// directory at save time, so the boot splash never depends on the originally
/// picked file staying in place (removable drive, Downloads cleanup, …).
/// The copy is a two-phase transaction (<see cref="Prepare(SplashConfig)"/> →
/// <see cref="Transaction.Commit"/>): the live files are only replaced once the
/// config write that points at them succeeded, so a failed save can never leave
/// the persisted config referring to already-replaced images.
/// <para><see cref="Prepare(SplashConfig)"/> is the SLOW half (a picked image may be
/// tens of megabytes) and is deliberately safe to run WITHOUT the cross-process
/// config lock: every staged sidecar carries a per-transaction GUID, so two savers
/// can never write the same sidecar. Only <see cref="Transaction.Commit"/> — a pair
/// of same-directory <c>File.Move</c> calls — has to run under that lock, together
/// with the config write it belongs to.</para></summary>
public static class SplashAssets
{
    // Suffix of the staged sidecar copies Prepare writes next to the live files.
    // The full name is "{baseName}{ext}.{guid}.wsgmnew": the extension keeps the
    // source's, and the GUID makes the name unique per transaction so concurrent
    // savers (two Settings windows, a shell + an elevated one-shot) can stage at
    // the same time without sharing — and therefore corrupting — a sidecar.
    private const string StagedSuffix = ".wsgmnew";

    /// <summary>Base file name of the logo slot — also the name
    /// <see cref="Transaction.Commit"/> reports a failed promotion under.</summary>
    internal const string LogoSlot = "logo";

    /// <summary>Base file name of the background slot — also the name
    /// <see cref="Transaction.Commit"/> reports a failed promotion under.</summary>
    internal const string BackgroundSlot = "background";

    /// <summary>Gets the per-user directory that holds the materialized splash images.</summary>
    public static string Directory => Path.Combine(Log.Directory, "splash");

    /// <summary>Stages the images referenced by <paramref name="splash"/> as sidecar
    /// files inside <see cref="Directory"/> and rewrites the config paths to the FINAL
    /// names the sidecars will take on commit. The live files stay untouched until
    /// <see cref="Transaction.Commit"/>; disposing or rolling back deletes the sidecars.
    /// Never throws: an IO failure is logged, leaves the original path in place AND
    /// marks that slot as failed, so <see cref="Transaction.Commit"/> reports it and
    /// the save cannot claim a success that only exists in memory.</summary>
    /// <param name="splash">The splash section whose image paths are rewritten in place.</param>
    /// <returns>The handle that commits or rolls back the staged copies.</returns>
    internal static Transaction Prepare(SplashConfig splash) => Prepare(splash, Directory);

    /// <summary>Stages into an explicit target directory (test seam).</summary>
    /// <param name="splash">The splash section whose image paths are rewritten in place.</param>
    /// <param name="targetDirectory">The directory that receives the stable copies.</param>
    /// <returns>The handle that commits or rolls back the staged copies.</returns>
    internal static Transaction Prepare(SplashConfig splash, string targetDirectory)
    {
        var transaction = new Transaction();
        splash.LogoImagePath = PrepareSlot(transaction, splash.LogoImagePath, LogoSlot, targetDirectory);
        splash.BackgroundImagePath = PrepareSlot(
            transaction,
            splash.BackgroundImagePath,
            BackgroundSlot,
            targetDirectory
        );
        return transaction;
    }

    /// <summary>Stages one image slot: an empty path queues the removal of stale
    /// copies, THIS slot's own live copy is left untouched, and any other path is
    /// copied to <c>{baseName}{ext}.{guid}{StagedSuffix}</c> with the FINAL
    /// <c>{baseName}{ext}</c> path returned as the new config value.</summary>
    private static string PrepareSlot(
        Transaction transaction,
        string sourcePath,
        string baseName,
        string targetDirectory
    )
    {
        string? stagedPath = null;
        FileStream? stagedHandle = null;
        // A slot with a picked source MUST end up as a copy inside the target
        // directory. If it does not, the config would keep naming the user's volatile
        // pick (Downloads, a removable drive) — exactly what materialization exists to
        // eliminate — so the failure has to be REPORTED, not just logged (see the
        // catch below). A cleared slot has nothing to stage and never reports.
        var mustStage = !string.IsNullOrWhiteSpace(sourcePath);
        try
        {
            var fullTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
            DeleteStaleSidecars(baseName, fullTarget);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                // Cleared slot: the live copies only go away once the save succeeded.
                // A whitespace-only path is a cleared slot too — every consumer treats
                // it as "no image" (IsNullOrWhiteSpace) — so it is normalized to ""
                // here rather than persisted verbatim, which would put a path nobody
                // can act on into config.json and into every shared splash theme.
                transaction.AddClear(baseName, fullTarget);
                return "";
            }

            var fullSource = Path.GetFullPath(sourcePath);
            if (IsThisSlotsOwnCopy(fullSource, baseName, fullTarget))
            {
                if (File.Exists(fullSource))
                {
                    return sourcePath; // Already a materialized copy — idempotent, nothing to stage.
                }

                // This slot's own copy is GONE (deleted by a cleanup tool, AV, or a
                // user emptying the splash folder). Short-circuiting on the path alone
                // would keep re-persisting the name of a file that no longer exists,
                // forever. It is NOT a save failure: the user changed nothing, nothing
                // could be written, and the splash already skips an element whose image
                // is missing — the honest outcome is a config that stops naming it.
                Log.Warn(
                    $"Splash image '{sourcePath}' is gone from WSGM's splash folder — clearing the '{baseName}' slot."
                );
                transaction.AddClear(baseName, fullTarget);
                return "";
            }

            var destination = Path.Combine(fullTarget, baseName + Path.GetExtension(fullSource));
            stagedPath = $"{destination}.{Guid.NewGuid():N}{StagedSuffix}";
            System.IO.Directory.CreateDirectory(fullTarget);
            stagedHandle = CopyToSidecar(fullSource, stagedPath);
            transaction.AddStaged(
                baseName,
                fullTarget,
                stagedPath,
                destination,
                stagedHandle,
                fullSource
            );
            return destination;
        }
        catch (Exception ex)
        {
            stagedHandle?.Dispose();
            if (stagedPath is not null)
            {
                TryDelete(stagedPath); // A half-written sidecar must never survive.
            }
            Log.Warn(
                $"Couldn't copy splash image '{sourcePath}' into '{targetDirectory}', keeping the original path: {ex.Message}"
            );
            if (mustStage)
            {
                // Same reported-failure path a failed PROMOTION takes: the caller puts
                // the previously persisted path back into the config and fails the save
                // instead of reporting a success it did not achieve. Without this a
                // denied/full target directory or an unreadable source silently
                // persisted the volatile source path under a "Settings saved." line.
                transaction.AddFailed(baseName);
            }
            return sourcePath ?? "";
        }
    }

    /// <summary>True only when <paramref name="fullSource"/> is the live copy THIS
    /// slot owns: inside the splash directory AND named <c>{baseName}.{ext}</c>, the
    /// exact shape <see cref="DeleteCopies"/> claims for the slot.
    /// <para>Being inside the directory is not enough. A config whose background slot
    /// points at the LOGO slot's copy (<c>…\splash\logo.png</c> — reachable through the
    /// file picker, a hand-edited config, or an imported theme) used to short-circuit
    /// on the directory alone: nothing was staged, the path was persisted as-is, and
    /// the logo slot's stale-sibling cleanup then deleted that very file on commit,
    /// leaving the saved config naming a deleted image with no failure reported.
    /// Anything that is not this slot's own copy is therefore staged like any other
    /// source, which gives the slot its own independent copy.</para></summary>
    private static bool IsThisSlotsOwnCopy(string fullSource, string baseName, string fullTarget) =>
        string.Equals(
            Path.GetDirectoryName(fullSource),
            fullTarget,
            StringComparison.OrdinalIgnoreCase
        )
        && string.Equals(
            Path.GetFileNameWithoutExtension(fullSource),
            baseName,
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>Copies the picked image into its sidecar and KEEPS the destination
    /// handle open until AFTER <see cref="Transaction.Commit"/> renamed it over the
    /// live file. That open handle is what tells a concurrent
    /// <see cref="DeleteStaleSidecars"/> sweep in another process (or another save in
    /// this one) that the sidecar belongs to a LIVE transaction and must not be swept
    /// — a GUID in the name cannot express that. Windows closes the handle if the
    /// process dies, which is exactly when the sidecar really has become an orphan
    /// the sweep should collect.
    /// <para>The share mode includes <see cref="FileShare.Delete"/> because Windows
    /// only permits renaming a file while a handle to it is open when every open
    /// handle granted delete sharing — and holding the handle ACROSS the promoting
    /// rename is what closes the micro-race that releasing it first opened (a
    /// concurrent sweep could delete the sidecar between the release and the move,
    /// turning a healthy save into a spurious "splash image not updated"). Granting
    /// delete sharing means a plain <c>File.Delete</c> would now succeed on a live
    /// sidecar, so the sweep no longer probes liveness by trying to delete: it opens
    /// the candidate with <see cref="FileShare.None"/> first (see
    /// <see cref="DeleteStaleSidecars"/>).</para></summary>
    private static FileStream CopyToSidecar(string sourcePath, string stagedPath)
    {
        var staged = new FileStream(
            stagedPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete
        );
        try
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            );
            source.CopyTo(staged);
            // Flush here, not at close: after Commit's rename the handle points at the
            // LIVE file, so a first flush at that point could fail (full volume) with
            // the live image already half-written.
            staged.Flush();
            return staged;
        }
        catch
        {
            staged.Dispose();
            throw;
        }
    }

    /// <summary>Removes sidecars orphaned by an earlier crashed or killed save
    /// (<c>{baseName}.{unique}{StagedSuffix}</c>), which <see cref="DeleteCopies"/>
    /// cannot match. Matching is done in managed code rather than through a Win32
    /// search pattern so 8.3 short names can never widen it. A sidecar still held open
    /// by a live transaction is skipped; that is expected and deliberately silent,
    /// unlike the other delete paths.</summary>
    private static void DeleteStaleSidecars(string baseName, string targetDirectory)
    {
        if (!System.IO.Directory.Exists(targetDirectory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(targetDirectory))
        {
            if (!IsSidecarOf(Path.GetFileName(file), baseName) || IsHeldByALiveTransaction(file))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch
            {
                // Locked by something else entirely: leaving it costs one stale file
                // until the next Prepare, and warning about it would put a routine
                // condition on the remote-diagnosis log.
            }
        }
    }

    /// <summary>Tells a staged sidecar of <paramref name="baseName"/> from every other
    /// file in the directory. The name has to be <c>{baseName}.{something}{suffix}</c>
    /// with a NON-EMPTY middle segment: a live materialized copy that merely happens to
    /// be named <c>{baseName}{StagedSuffix}</c> (a user picking an image with that
    /// extension) is not a sidecar and must never be swept.</summary>
    private static bool IsSidecarOf(string name, string baseName) =>
        name.Length > baseName.Length + 1 + StagedSuffix.Length
        && name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(StagedSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Probes whether a sidecar is still owned by a live transaction — in
    /// this process or another one — by opening it with no sharing at all: that only
    /// succeeds when nobody else holds a handle. It replaces the old "the delete
    /// failed, so it must be live" test, which stopped being a liveness signal when
    /// <see cref="CopyToSidecar"/> started granting <see cref="FileShare.Delete"/> so
    /// the promoting rename can run with the handle still open. A failure to open for
    /// any other reason is treated as live too — never deleting is the safe side.</summary>
    private static bool IsHeldByALiveTransaction(string path)
    {
        try
        {
            using var probe = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None
            );
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Deletes every file in the target directory named <c>{baseName}.*</c>
    /// (any extension, including none) except <paramref name="keep"/>. Staged
    /// sidecars are never matched — their name carries the full live file name.</summary>
    private static void DeleteCopies(string baseName, string targetDirectory, string? keep)
    {
        if (!System.IO.Directory.Exists(targetDirectory))
        {
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(targetDirectory))
        {
            if (
                !string.Equals(
                    Path.GetFileNameWithoutExtension(file),
                    baseName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            if (
                keep is not null
                && string.Equals(Path.GetFullPath(file), keep, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            TryDelete(file);
        }
    }

    /// <summary>Best-effort delete shared with the splash-theme import/export
    /// cleanup paths: a failure is logged, never thrown.</summary>
    /// <param name="path">The file to delete.</param>
    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't delete stale splash file '{path}': {ex.Message}");
        }
    }

    /// <summary>The handle returned by <see cref="Prepare(SplashConfig)"/>: it owns the
    /// staged sidecar copies until the caller either commits them over the live files
    /// or throws them away. Neither operation throws — a commit failure is logged AND
    /// returned per slot, so the save that owns the transaction can put the previously
    /// persisted path back instead of reporting a success it did not achieve.</summary>
    internal sealed class Transaction : IDisposable
    {
        private readonly List<PendingSlot> _pending = [];
        private bool _completed;

        /// <summary>Test seam replacing the stale-copy cleanup (base name, directory,
        /// path to keep). Production never sets it; it exists so a test can prove that a
        /// THROWING cleanup cannot turn a successful promotion into a reported failure,
        /// which no arrangement of real files can force.</summary>
        internal Action<string, string, string?>? CleanUpStaleCopiesOverride { get; set; }

        /// <summary>Queues the removal of a slot's live copies (the slot was cleared).</summary>
        /// <param name="baseName">The slot's file base name.</param>
        /// <param name="targetDirectory">The directory holding the live copies.</param>
        internal void AddClear(string baseName, string targetDirectory) =>
            _pending.Add(new PendingSlot(baseName, targetDirectory, null, null, null));

        /// <summary>Records a slot whose STAGING already failed, so
        /// <see cref="Commit"/> reports it exactly like a failed promotion. Nothing
        /// was written for it, so there is nothing to promote or roll back — but the
        /// config path for this slot still names the user's volatile pick instead of a
        /// copy inside the splash directory, which the caller must not persist as a
        /// success.</summary>
        /// <param name="baseName">The slot's file base name.</param>
        internal void AddFailed(string baseName) =>
            _pending.Add(new PendingSlot(baseName, "", null, null, null, StagingFailed: true));

        /// <summary>Queues a staged sidecar for promotion over the live file.</summary>
        /// <param name="baseName">The slot's file base name.</param>
        /// <param name="targetDirectory">The directory holding the live copies.</param>
        /// <param name="stagedPath">The sidecar written by Prepare.</param>
        /// <param name="livePath">The final path the sidecar is moved to.</param>
        /// <param name="stagedHandle">The still-open write handle that marks the sidecar
        /// as owned by this live transaction; released after the promoting move.</param>
        /// <param name="sourcePath">The picked image the sidecar was copied from — kept
        /// so <see cref="Commit"/> can still produce the live file if the sidecar itself
        /// is deleted by an outside actor (see <see cref="TryCopyFromSource"/>).</param>
        internal void AddStaged(
            string baseName,
            string targetDirectory,
            string stagedPath,
            string livePath,
            FileStream stagedHandle,
            string sourcePath
        ) =>
            _pending.Add(
                new PendingSlot(
                    baseName,
                    targetDirectory,
                    stagedPath,
                    livePath,
                    stagedHandle,
                    SourcePath: sourcePath
                )
            );

        /// <summary>Atomically moves every staged sidecar over its live file and drops
        /// the copies of cleared slots. Call only after the config write succeeded, and
        /// under the same cross-process config lock as that write. Never throws.</summary>
        /// <returns>The base names (<see cref="LogoSlot"/>, <see cref="BackgroundSlot"/>)
        /// of the slots that did NOT end up as a live copy — empty when every slot went
        /// live. That covers both halves of the transaction: a failed promotion (a
        /// locked file, an AV hold, a permission error), whose slot still holds its
        /// previous content, and a slot whose STAGING already failed in
        /// <see cref="Prepare(SplashConfig)"/> (unreadable source, uncreatable or full
        /// target directory), whose config path is still the user's picked path. In
        /// both cases the caller must put that slot's previously persisted path back
        /// into the config and fail the save instead of reporting success: the
        /// persisted config must never name an image that was never written, nor the
        /// volatile source path that materialization exists to eliminate.
        /// <para>For a slot that WAS staged, only the promoting move — or, when the
        /// sidecar vanished under it, <see cref="TryCopyFromSource"/> — decides this: the stale-sibling cleanup that
        /// follows it (an old <c>logo.jpg</c> left behind by a new <c>logo.png</c>) is
        /// best effort and is logged, never reported — the new image IS live by then,
        /// and reporting a failure would make the caller revert the persisted path to
        /// an image that is no longer on disk.</para></returns>
        internal IReadOnlyList<string> Commit()
        {
            if (_completed)
            {
                return [];
            }
            _completed = true;

            List<string>? failed = null;
            foreach (var slot in _pending)
            {
                if (slot.StagingFailed)
                {
                    // Never staged (unreadable source, uncreatable/full target): the
                    // config path for this slot is still the user's picked path, so it
                    // is reported the same way a failed promotion is.
                    (failed ??= []).Add(slot.BaseName);
                    continue;
                }

                if (slot.StagedPath is null || slot.LivePath is null)
                {
                    // Cleared slot: the config now names no image, so leftover copies are
                    // garbage, not a broken promotion — never reported as a failure.
                    CleanUpStaleCopies(slot.BaseName, slot.TargetDirectory, keep: null);
                    continue;
                }

                try
                {
                    // Atomic replace (MoveFileEx REPLACE_EXISTING) — same directory,
                    // so the live file is never observed half-written. Deliberately
                    // performed with the sidecar's own handle STILL OPEN (it granted
                    // FileShare.Delete for exactly this): releasing first left a window
                    // in which a concurrent saver's orphan sweep could delete the
                    // sidecar and turn this move into a spurious failure report.
                    File.Move(slot.StagedPath, slot.LivePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    // Close before probing or deleting: a delete against a still-open
                    // handle only marks the file for deletion, so the sidecar stays
                    // visible — and the promoting move keeps failing — until the last
                    // handle closes.
                    ReleaseHandle(slot);
                    if (TryCopyFromSource(slot))
                    {
                        CleanUpStaleCopies(slot.BaseName, slot.TargetDirectory, keep: slot.LivePath);
                        continue;
                    }

                    // Reported to the caller: this slot's live file is still the OLD
                    // image, so the path this save is about to persist would point at
                    // content that never landed.
                    Log.Warn(
                        $"Couldn't apply the new splash image for '{slot.BaseName}' in '{slot.TargetDirectory}': {ex.Message}"
                    );
                    (failed ??= []).Add(slot.BaseName);
                    TryDelete(slot.StagedPath);
                    continue;
                }

                // The handle now refers to the live file; nothing is written through it
                // any more (Prepare already flushed), so closing it is pure cleanup.
                ReleaseHandle(slot);
                CleanUpStaleCopies(slot.BaseName, slot.TargetDirectory, keep: slot.LivePath);
            }
            _pending.Clear();
            return failed ?? (IReadOnlyList<string>)[];
        }

        /// <summary>Last resort for a promotion whose sidecar VANISHED: copies the
        /// originally picked image straight to the live path.
        /// <para>Why this exists: the sidecar's handle grants <see cref="FileShare.Delete"/>
        /// (that is what makes renaming a held-open file legal), so a plain delete
        /// against it now succeeds — AV, a cleanup tool, or an older WSGM build's
        /// orphan sweep can remove a perfectly healthy in-flight sidecar and turn a
        /// good save into a reported "splash image not updated". The source the sidecar
        /// was copied from is still on disk in that case, so the save can simply finish
        /// the job from it.</para>
        /// <para>Deliberately narrow: it runs ONLY when the sidecar is really gone
        /// (called after the handle is closed, so a delete-pending name is resolved by
        /// then). Every other promotion failure — a locked, denied, or occupied live
        /// path — would fail this copy too and must keep being reported. The copy
        /// writes the live file directly and is therefore not the atomic replace the
        /// rename gives: with the sidecar gone that transaction is already lost, and a
        /// half-written live image is caught by the splash's own load fallback, whereas
        /// a false failure report reverts the user's pick.</para></summary>
        /// <param name="slot">The slot whose promotion just failed.</param>
        /// <returns>True when the live file now holds the new image.</returns>
        private static bool TryCopyFromSource(PendingSlot slot)
        {
            if (slot.SourcePath is null || slot.LivePath is null || File.Exists(slot.StagedPath))
            {
                return false;
            }

            try
            {
                File.Copy(slot.SourcePath, slot.LivePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn(
                    $"Couldn't re-copy the splash image for '{slot.BaseName}' from '{slot.SourcePath}': {ex.Message}"
                );
                return false;
            }

            Log.Warn(
                $"Staged splash image for '{slot.BaseName}' disappeared before it could be applied — "
                    + $"copied '{slot.SourcePath}' directly to '{slot.LivePath}' instead."
            );
            return true;
        }

        /// <summary>Closes the sidecar's own write handle. Disposing a
        /// <see cref="FileStream"/> flushes, so it CAN throw (a full or disconnected
        /// volume) — and both <see cref="Commit"/> and <see cref="Rollback"/> promise
        /// never to. The bytes were already flushed when the sidecar was written, so a
        /// failure here cannot have lost image content; the handle itself is closed by
        /// <see cref="FileStream"/> either way.</summary>
        private static void ReleaseHandle(PendingSlot slot)
        {
            try
            {
                slot.StagedHandle?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn(
                    $"Couldn't finish writing the staged splash image '{slot.StagedPath}': {ex.Message}"
                );
            }
        }

        /// <summary>Drops the slot's other-extension leftovers. Best effort by contract:
        /// the promotion already succeeded, so a failure here is logged and swallowed.</summary>
        private void CleanUpStaleCopies(string baseName, string targetDirectory, string? keep)
        {
            try
            {
                (CleanUpStaleCopiesOverride ?? DeleteCopies)(baseName, targetDirectory, keep);
            }
            catch (Exception ex)
            {
                Log.Warn(
                    $"Couldn't clean up stale splash copies for '{baseName}' in '{targetDirectory}': {ex.Message}"
                );
            }
        }

        /// <summary>Discards the staged sidecars, leaving every live file untouched.
        /// Idempotent, and a no-op once <see cref="Commit"/> ran.</summary>
        internal void Rollback()
        {
            if (_completed)
            {
                return;
            }
            _completed = true;

            foreach (var slot in _pending)
            {
                ReleaseHandle(slot);
                if (slot.StagedPath is not null)
                {
                    TryDelete(slot.StagedPath);
                }
            }
            _pending.Clear();
        }

        /// <summary>Rolls back unless the transaction was already committed.</summary>
        public void Dispose() => Rollback();

        private sealed record PendingSlot(
            string BaseName,
            string TargetDirectory,
            string? StagedPath,
            string? LivePath,
            FileStream? StagedHandle,
            string? SourcePath = null,
            bool StagingFailed = false
        );
    }
}
