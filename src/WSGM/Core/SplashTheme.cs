using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>Export/import of <c>.wsgmsplash</c> splash-theme files: a zip archive
/// containing <c>splash.json</c> (the serialized <see cref="SplashConfig"/>) plus the
/// referenced logo/background images bundled under their deterministic names
/// (<c>logo.*</c>/<c>background.*</c>). Theme files are untrusted user-shared
/// content: entry names are strictly whitelisted, decompression is size-bounded,
/// and import stages images into a fresh temp directory — never over the live
/// splash assets — so only a later Save materializes them into the stable copies.
/// Malformed, oversized, or unexpected archives return null with a logged warning —
/// never an exception, so a bad theme file can never break Settings.</summary>
internal static class SplashTheme
{
    private const string ConfigEntryName = "splash.json";
    private const string LogoEntryBaseName = "logo";
    private const string BackgroundEntryBaseName = "background";

    /// <summary>Decompressed-size cap for the <c>splash.json</c> entry.</summary>
    private const long MaxConfigEntryBytes = 1024 * 1024;

    /// <summary>Decompressed-size cap for each bundled image entry.</summary>
    private const long MaxImageEntryBytes = 64L * 1024 * 1024;

    /// <summary>Decompressed-size cap for the whole archive.</summary>
    private const long MaxTotalBytes = 160L * 1024 * 1024;

    /// <summary>Image extensions a theme archive may bundle.</summary>
    private static readonly string[] AllowedImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    /// <summary>Name of the ownership marker an import drops inside the staging
    /// directory it just filled. Never collides with a staged image: the entry-name
    /// whitelist only ever lets <c>logo.*</c>/<c>background.*</c> be written.</summary>
    internal const string OwnerMarkerName = ".wsgm-import-owner";

    /// <summary>How old a staging directory has to be before the sweep collects it
    /// WITHOUT a usable liveness signal — no marker at all (an older build, or an
    /// import whose marker could not be written), or a marker that cannot be opened
    /// for some reason other than an owner holding it (ACLs, AV). Generous on purpose:
    /// the only cost of waiting is a few megabytes in the temp folder, while deleting
    /// too early destroys an unsaved import in another Settings window.</summary>
    private static readonly TimeSpan StagingStaleAfter = TimeSpan.FromHours(24);

    /// <summary>The ownership markers THIS process holds open, keyed by staging
    /// directory. A staging directory stays owned for as long as a window could still
    /// be pointing at its staged images — an import that was never saved must stay
    /// materializable until its Settings window is gone — so the handles are held for
    /// the whole bracket <see cref="BeginImportSession()"/>/<see cref="EndImportSession()"/>
    /// spans and released when the LAST such session ends
    /// (<see cref="ReleaseTrackedStagingOwnership"/>). Windows closes whatever is still
    /// open when the process ends — clean exit or crash alike — so a directory can
    /// never become immortal either way.
    /// <para>Doubles as the lock for <see cref="_openImportSessions"/>.</para></summary>
    private static readonly Dictionary<string, FileStream> OwnedStagingMarkers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many windows that can still display staged imports are open. The
    /// shell opens Settings IN-PROCESS and more than one window can be open at a time,
    /// so ownership is released on the last one, not the first to close.</summary>
    private static int _openImportSessions;

    /// <summary>Parent directory holding one staging directory per import, under the
    /// user's temp folder — deliberately outside the live splash assets.</summary>
    internal static string StagingRoot => Path.Combine(Path.GetTempPath(), "WSGM.splash-import");

    /// <summary>Writes a splash theme archive to <paramref name="path"/> atomically:
    /// the archive is built in a sibling temp file and moved over the destination
    /// only once fully written, so a failed export leaves any existing file intact.</summary>
    /// <returns>True when the file was written; false (logged) on any failure.</returns>
    internal static bool Export(SplashConfig splash, string path)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            bool written;
            using (var stream = File.Create(tempPath))
            {
                written = Export(splash, stream);
            }
            if (written)
            {
                File.Move(tempPath, path, overwrite: true);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash theme export to '{path}' failed: {ex.Message}");
        }
        SplashAssets.TryDelete(tempPath);
        return false;
    }

    /// <summary>Writes a splash theme archive to an open stream. The stream is left
    /// open for the caller to dispose.</summary>
    /// <returns>True when the archive was written; false (logged) on any failure,
    /// including a selected image above the per-image import cap.</returns>
    internal static bool Export(SplashConfig splash, Stream destination)
    {
        try
        {
            // Refuse up front — before a single byte is written — what the importer
            // would always reject, so an oversized image can never produce (or
            // replace) an archive nobody can import.
            if (!ImageIsBundleable(splash.LogoImagePath) || !ImageIsBundleable(splash.BackgroundImagePath))
            {
                return false;
            }

            // The bundled copy gets its image paths rewritten to the archive entry
            // names; the caller's instance is never mutated.
            var bundled = ConfigStore.CloneJson(splash, ConfigJsonContext.Default.SplashConfig);
            using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
            bundled.LogoImagePath = BundleImage(archive, splash.LogoImagePath, LogoEntryBaseName);
            bundled.BackgroundImagePath = BundleImage(archive, splash.BackgroundImagePath, BackgroundEntryBaseName);
            var entry = archive.CreateEntry(ConfigEntryName);
            using var entryStream = entry.Open();
            JsonSerializer.Serialize(entryStream, bundled, ConfigJsonContext.Default.SplashConfig);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash theme export failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads a splash theme archive, staging any bundled images into a fresh
    /// per-import directory under the user's temp folder — deliberately outside the
    /// live splash assets, which are only touched when a later Save materializes the
    /// staged copies. Older staging directories are swept best-effort only AFTER a
    /// successful import: a failed import leaves the caller pointing at the previous
    /// import's staged files, which must survive. The same is true across Settings
    /// windows and processes: the fresh directory is claimed with an ownership marker
    /// first, and the sweep only collects directories nobody owns any more (see
    /// <see cref="CleanUpStaleStagingDirectories"/>). The claim is released — and the
    /// directory therefore actually freed — when the last import session ends
    /// (<see cref="BeginImportSession()"/>/<see cref="EndImportSession()"/>), which is
    /// also where the sweep runs for a session that imported nothing.</summary>
    /// <returns>The imported configuration, or null (logged) when the file is not an
    /// acceptable splash theme.</returns>
    internal static SplashConfig? Import(string path)
    {
        var stagingDirectory = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        var imported = Import(path, stagingDirectory);
        if (imported is not null)
        {
            // Claim BEFORE sweeping: a concurrent sweep in another process must never
            // see this directory unowned, and our own sweep skips it by name anyway.
            TrackStagingOwnership(stagingDirectory);
            CleanUpStaleStagingDirectories(StagingRoot, keep: stagingDirectory);
        }
        return imported;
    }

    /// <summary>Opens an import session: called by every window that can display the
    /// images an import staged (Settings), for its whole lifetime. Sessions are
    /// counted, so ownership of the staged directories is released when the LAST one
    /// ends and never while a second Settings window in this process could still be
    /// showing an unsaved import.
    /// <para>Opening one also sweeps the staging root, which is what collects orphans
    /// from a crashed or force-killed earlier session even if this session never
    /// imports anything: <see cref="Import(string)"/> is otherwise the only place the
    /// sweep runs, so without this a process that stopped importing left every
    /// directory it had staged on disk until something imported again.</para></summary>
    internal static void BeginImportSession() => BeginImportSession(StagingRoot);

    /// <summary>Test seam for <see cref="BeginImportSession()"/> against a temp
    /// staging root instead of the real one.</summary>
    /// <param name="stagingRoot">The staging root to sweep.</param>
    internal static void BeginImportSession(string stagingRoot)
    {
        lock (OwnedStagingMarkers)
        {
            _openImportSessions++;
        }
        // Our own live directories are protected by the very handles we hold: the
        // sweep's probe cannot open a marker this process still owns.
        CleanUpStaleStagingDirectories(stagingRoot, keep: null);
    }

    /// <summary>Closes an import session. When it was the last one, the staged images
    /// nobody can be pointing at any more are released (the handles are closed) and the
    /// staging root is swept, which is what actually frees them — up to
    /// <see cref="MaxTotalBytes"/> per import, previously pinned until the process
    /// exited. Another PROCESS's staging directories are untouched: this only ever
    /// closes handles this process opened, and their markers keep them alive.</summary>
    internal static void EndImportSession() => EndImportSession(StagingRoot);

    /// <summary>Test seam for <see cref="EndImportSession()"/> against a temp staging
    /// root instead of the real one.</summary>
    /// <param name="stagingRoot">The staging root to sweep.</param>
    internal static void EndImportSession(string stagingRoot)
    {
        lock (OwnedStagingMarkers)
        {
            if (_openImportSessions > 0)
            {
                _openImportSessions--;
            }
            if (_openImportSessions > 0)
            {
                return;
            }
        }
        ReleaseTrackedStagingOwnership();
        CleanUpStaleStagingDirectories(stagingRoot, keep: null);
    }

    /// <summary>Drops this process's claim on every staging directory it owns, which
    /// is what makes the next sweep — here or in another process — able to collect
    /// them. Best effort and never throws; a handle that fails to close simply keeps
    /// its directory owned until the process ends.</summary>
    internal static void ReleaseTrackedStagingOwnership()
    {
        List<FileStream> markers;
        lock (OwnedStagingMarkers)
        {
            markers = [.. OwnedStagingMarkers.Values];
            OwnedStagingMarkers.Clear();
        }
        foreach (var marker in markers)
        {
            try
            {
                marker.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't release a splash staging claim: {ex.Message}");
            }
        }
    }

    /// <summary>Reads a splash theme archive, extracting any bundled images into
    /// <paramref name="targetImageDirectory"/> and rewriting the returned config's
    /// image paths to the extracted copies. Every entry must be one of the
    /// whitelisted names within its size cap; extraction is bounded so a lying
    /// central directory cannot decompress past the caps. A failed import removes
    /// anything it staged.</summary>
    /// <returns>The imported configuration, or null (logged) when the file is not an
    /// acceptable splash theme.</returns>
    internal static SplashConfig? Import(string path, string targetImageDirectory)
    {
        var targetExistedBefore = Directory.Exists(targetImageDirectory);
        var extractedFiles = new List<string>();
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (!EntriesAreAcceptable(archive, path))
            {
                return null;
            }

            var configEntry = FindConfigEntry(archive);
            if (configEntry is null)
            {
                Log.Warn($"Splash theme '{path}' contains no {ConfigEntryName} — not a splash theme file.");
                return null;
            }

            SplashConfig? splash;
            using (var buffer = new MemoryStream())
            {
                using (var entryStream = configEntry.Open())
                {
                    CopyBounded(entryStream, buffer, MaxConfigEntryBytes, ConfigEntryName);
                }
                buffer.Position = 0;
                splash = JsonSerializer.Deserialize(buffer, ConfigJsonContext.Default.SplashConfig);
            }
            if (splash is null)
            {
                Log.Warn($"Splash theme '{path}' has an empty {ConfigEntryName}.");
                return null;
            }

            // Apply the same explicit-null repairs as a loaded config.json.
            ConfigStore.NormalizeSplash(splash);
            // Archive paths never escape the import transaction: only files staged by this import
            // are returned, or an empty path when the archive omits the image.
            splash.LogoImagePath = ExtractImage(archive, LogoEntryBaseName, targetImageDirectory, extractedFiles) ?? "";
            splash.BackgroundImagePath =
                ExtractImage(archive, BackgroundEntryBaseName, targetImageDirectory, extractedFiles) ?? "";
            return splash;
        }
        catch (Exception ex)
        {
            Log.Warn($"Splash theme import from '{path}' failed: {ex.Message}");
            CleanUpFailedImport(targetImageDirectory, targetExistedBefore, extractedFiles);
            return null;
        }
    }

    /// <summary>Validates every archive entry against the whitelist (exactly
    /// <c>splash.json</c>, <c>logo.&lt;image-ext&gt;</c>, or
    /// <c>background.&lt;image-ext&gt;</c>; no directories, separators, or traversal)
    /// and the per-entry/total declared-size caps.</summary>
    private static bool EntriesAreAcceptable(ZipArchive archive, string path)
    {
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var limit = AllowedEntryBytes(entry.FullName);
            if (limit is null)
            {
                Log.Warn($"Splash theme '{path}' rejected: unexpected entry '{entry.FullName}'.");
                return false;
            }
            if (entry.Length > limit)
            {
                Log.Warn(
                    $"Splash theme '{path}' rejected: entry '{entry.FullName}' declares {entry.Length} bytes (limit {limit})."
                );
                return false;
            }
            totalBytes += entry.Length;
            if (totalBytes > MaxTotalBytes)
            {
                Log.Warn($"Splash theme '{path}' rejected: total declared size exceeds {MaxTotalBytes} bytes.");
                return false;
            }
        }
        return true;
    }

    /// <summary>Returns the decompressed-size cap for a whitelisted entry name, or
    /// null when the name is not acceptable (unknown name, disallowed extension, or
    /// anything that is not a bare file name).</summary>
    private static long? AllowedEntryBytes(string entryName)
    {
        // Not just separators: a drive-relative name like "D:logo.png" is rooted,
        // and Path.Combine would then DISCARD the staging directory and write it
        // wherever that drive's current directory points. Requiring the name to
        // equal its own file name rejects separators, roots and volume-relative
        // forms in one check.
        if (entryName.Length == 0
            || !string.Equals(entryName, Path.GetFileName(entryName), StringComparison.Ordinal))
        {
            return null;
        }
        if (string.Equals(entryName, ConfigEntryName, StringComparison.OrdinalIgnoreCase))
        {
            return MaxConfigEntryBytes;
        }
        var stem = Path.GetFileNameWithoutExtension(entryName);
        var extension = Path.GetExtension(entryName).ToLowerInvariant();
        if (
            (
                string.Equals(stem, LogoEntryBaseName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, BackgroundEntryBaseName, StringComparison.OrdinalIgnoreCase)
            )
            && Array.IndexOf(AllowedImageExtensions, extension) >= 0
        )
        {
            return MaxImageEntryBytes;
        }
        return null;
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/>,
    /// aborting once more than <paramref name="limit"/> bytes actually decompress —
    /// the declared entry length in the central directory can lie.</summary>
    private static void CopyBounded(Stream source, Stream destination, long limit, string entryName)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException($"entry '{entryName}' exceeds {limit} bytes when decompressed");
            }
            destination.Write(buffer, 0, read);
        }
    }

    /// <summary>Checks a to-be-bundled image against the very rules
    /// <see cref="Import(string, string)"/> enforces — its size cap AND its
    /// extension whitelist — so an export can never yield an archive the importer
    /// rejects. A blank, missing or unreadable source bundles nothing and passes.</summary>
    private static bool ImageIsBundleable(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return true;
        }
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            return true;
        }
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (Array.IndexOf(AllowedImageExtensions, extension) < 0)
        {
            Log.Warn(
                $"Splash theme export refused: image '{sourcePath}' has an unsupported extension "
                    + $"('{extension}'; allowed: {string.Join(", ", AllowedImageExtensions)})."
            );
            return false;
        }
        if (info.Length > MaxImageEntryBytes)
        {
            Log.Warn(
                $"Splash theme export refused: image '{sourcePath}' is {info.Length} bytes (limit {MaxImageEntryBytes})."
            );
            return false;
        }
        return true;
    }

    /// <summary>Copies the referenced image into the archive under its deterministic
    /// entry name and returns that name; a blank or missing source bundles nothing
    /// and returns "". Returning the source path instead would leak the author's
    /// absolute local path into a file meant to be shared, for an image the archive
    /// does not even contain; the importer treats an absent entry as "no image"
    /// either way.</summary>
    private static string BundleImage(ZipArchive archive, string sourcePath, string entryBaseName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return "";
        }
        var entryName = entryBaseName + Path.GetExtension(sourcePath).ToLowerInvariant();
        var entry = archive.CreateEntry(entryName);
        using var target = entry.Open();
        using var source = File.OpenRead(sourcePath);
        source.CopyTo(target);
        return entryName;
    }

    /// <summary>Extracts the (already whitelisted) image entry with the given base
    /// name into the target directory through the bounded copy and returns the
    /// extracted file's full path, or null when the archive has no such entry.
    /// The destination is recorded in <paramref name="extractedFiles"/> before the
    /// copy starts so a partial file is cleaned up on failure.</summary>
    private static string? ExtractImage(
        ZipArchive archive, string entryBaseName, string targetDirectory, List<string> extractedFiles)
    {
        foreach (var entry in archive.Entries)
        {
            if (!string.Equals(
                    Path.GetFileNameWithoutExtension(entry.FullName),
                    entryBaseName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Directory.CreateDirectory(targetDirectory);
            var destination = Path.Combine(targetDirectory, Path.GetFileName(entry.FullName).ToLowerInvariant());
            // Belt and braces behind the name whitelist: never write outside the
            // staging directory, whatever the archive claims an entry is called.
            if (!Path.GetFullPath(destination).StartsWith(
                    Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"entry '{entry.FullName}' escapes the staging directory");
            }
            extractedFiles.Add(destination);
            using var source = entry.Open();
            using var target = File.Create(destination);
            CopyBounded(source, target, MaxImageEntryBytes, entry.FullName);
            return destination;
        }
        return null;
    }

    /// <summary>Removes whatever a failed import staged: a directory the import
    /// created is deleted wholesale, while a pre-existing directory only loses the
    /// files this import wrote. Best effort — never throws.</summary>
    private static void CleanUpFailedImport(
        string targetImageDirectory, bool targetExistedBefore, List<string> extractedFiles)
    {
        try
        {
            if (!targetExistedBefore)
            {
                if (Directory.Exists(targetImageDirectory))
                {
                    Directory.Delete(targetImageDirectory, recursive: true);
                }
                return;
            }
            foreach (var file in extractedFiles)
            {
                SplashAssets.TryDelete(file);
            }
        }
        catch
        {
            // Cleanup after a failed import is best effort.
        }
    }

    /// <summary>Claims a staging directory and remembers the marker handle so it stays
    /// open until the last import session ends (see <see cref="OwnedStagingMarkers"/>).
    /// Best effort: a directory whose marker cannot be written is simply protected by
    /// the age rule instead.</summary>
    /// <param name="stagingDirectory">The directory this import just staged into.</param>
    internal static void TrackStagingOwnership(string stagingDirectory)
    {
        var marker = ClaimStagingDirectory(stagingDirectory);
        if (marker is null)
        {
            return;
        }
        lock (OwnedStagingMarkers)
        {
            // Keyed by full path so a directory can be tracked exactly once. Claiming
            // one this process already owns cannot happen anyway: the handle it holds
            // denies all sharing, so ClaimStagingDirectory returns null above.
            OwnedStagingMarkers[NormalizeDirectoryPath(stagingDirectory) ?? stagingDirectory] = marker;
        }
    }

    /// <summary>Writes the ownership marker into a staging directory and returns the
    /// handle that keeps it claimed — open with <see cref="FileShare.None"/>, so no
    /// other import (in this process or any other) can either probe it or delete it
    /// while the handle lives. Production keeps the handle forever
    /// (<see cref="OwnedStagingMarkers"/>); the test seam exists so a test can drop
    /// ownership deliberately, which is what a crashed process looks like on disk.</summary>
    /// <param name="stagingDirectory">The directory to claim. A directory that does not
    /// exist (a theme that bundled no images staged nothing) is not claimed.</param>
    /// <returns>The open marker handle, or null when nothing was claimed.</returns>
    internal static FileStream? ClaimStagingDirectory(string stagingDirectory)
    {
        if (!Directory.Exists(stagingDirectory))
        {
            return null;
        }
        try
        {
            return new FileStream(
                Path.Combine(stagingDirectory, OwnerMarkerName),
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
        }
        catch (Exception ex)
        {
            Log.Warn(
                $"Couldn't mark the splash staging directory '{stagingDirectory}' as in use: {ex.Message}"
            );
            return null;
        }
    }

    /// <summary>Best-effort removal of staging directories left behind by earlier
    /// imports (e.g. imports that were never saved). Never throws.
    /// <para>OWNERSHIP: every import that staged images drops
    /// <see cref="OwnerMarkerName"/> in its directory and holds that file open with
    /// <see cref="FileShare.None"/> for as long as a window could still be pointing at
    /// the staged images — until the last import session in that process ends
    /// (<see cref="EndImportSession()"/>), and in any case no longer than the process
    /// itself. That open handle — the same trick <c>SplashAssets</c> uses for in-flight
    /// sidecars — is the only thing that can distinguish "a second Settings window still
    /// has these images on screen and unsaved" from "nobody will ever look at these
    /// again"; a name or a timestamp cannot. The handle is never released while any such
    /// window is open, and the OS releases it on a crash, so no directory can become
    /// immortal.</para>
    /// <para>CLEANUP, per candidate directory (<paramref name="keep"/> — the import
    /// that just ran — is always skipped):</para>
    /// <list type="bullet">
    /// <item>Marker present and openable → nobody owns it (a released, crashed or
    /// exited owner): delete.</item>
    /// <item>Marker present and NOT openable → a live owner: keep, however old. The
    /// deletion attempt is still made once the directory is ancient, in case the
    /// marker was unopenable for some other reason — the owner's handle makes that
    /// attempt fail harmlessly.</item>
    /// <item>No marker → keep until it is older than <see cref="StagingStaleAfter"/>;
    /// an import that could not write its marker (or one from an older build) must not
    /// be deleted out from under a window that is still using it.</item>
    /// </list>
    /// <para>The marker is always deleted FIRST and on its own, never as part of the
    /// recursive delete: while an owner holds it that single delete fails and the
    /// staged images are still untouched. A recursive delete that tripped over the
    /// marker halfway would already have destroyed the images the marker exists to
    /// protect.</para></summary>
    /// <param name="stagingRoot">The parent directory holding every import's staging directory.</param>
    /// <param name="keep">The staging directory of the import that just succeeded, or
    /// null when the sweep does not belong to an import (session start/end) and only
    /// the ownership and age rules decide.</param>
    internal static void CleanUpStaleStagingDirectories(string stagingRoot, string? keep)
    {
        try
        {
            if (!Directory.Exists(stagingRoot))
            {
                return;
            }
            // A reparse point where our staging root should be is an attack surface:
            // a same-user process can repoint %TEMP%\WSGM.splash-import at any
            // directory before an elevated sweep, which would then enumerate and
            // recursively delete the junction target's children. The root is always a
            // plain directory WSGM created — never follow a junction through it.
            if (IsReparsePoint(stagingRoot))
            {
                Log.Warn($"Splash staging root '{stagingRoot}' is a reparse point; skipping cleanup.");
                return;
            }
            var keepFullPath = NormalizeDirectoryPath(keep);
            foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
            {
                if (keepFullPath is not null
                    && string.Equals(
                        NormalizeDirectoryPath(directory),
                        keepFullPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                TryDeleteUnownedStagingDirectory(directory);
            }
        }
        catch
        {
            // Staging cleanup is best effort.
        }
    }

    /// <summary>Canonical comparison form of a directory path: full path with any
    /// trailing separator removed.
    /// <para>The trim is what makes the <c>keep</c> comparison sound.
    /// <see cref="Path.GetFullPath(string)"/> PRESERVES a trailing separator, while
    /// <see cref="Directory.EnumerateDirectories(string)"/> never produces one — so an
    /// exact string comparison silently failed to match a <c>keep</c> written as
    /// <c>...\staging\abc\</c> and offered that very directory to the delete rules.
    /// Not reachable from today's only caller (which passes a path it composed itself),
    /// but this is an internal API and the cost of getting it wrong is a deleted
    /// unsaved import.</para></summary>
    /// <param name="path">The path to canonicalize, or null.</param>
    /// <returns>The canonical form, or null when there is nothing to compare (null,
    /// blank, or a path the OS refuses to resolve).</returns>
    private static string? NormalizeDirectoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            var full = Path.GetFullPath(path);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Both sides of every comparison come through here, so even a degenerate
            // input (a drive root trimming to "C:") stays self-consistent; the guard
            // only avoids handing back an empty string.
            return trimmed.Length == 0 ? full : trimmed;
        }
        catch
        {
            // An unresolvable path matches nothing — the safe answer for `keep` is to
            // let the ownership and age rules decide on their own.
            return null;
        }
    }

    /// <summary>True when the path is a reparse point (junction/symlink), so the
    /// cleanup sweep never follows a repointed staging root. An unreadable attribute
    /// set counts as a reparse point: the safe answer for a best-effort sweep is to
    /// skip rather than risk following a link.</summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Applies the cleanup rules documented on
    /// <see cref="CleanUpStaleStagingDirectories"/> to a single candidate directory.</summary>
    private static void TryDeleteUnownedStagingDirectory(string directory)
    {
        var markerPath = Path.Combine(directory, OwnerMarkerName);
        var ancient = IsOlderThan(directory, StagingStaleAfter);
        if (File.Exists(markerPath))
        {
            if (IsOwnedByALiveImport(markerPath) && !ancient)
            {
                return;
            }
            try
            {
                File.Delete(markerPath);
            }
            catch
            {
                // Still owned (or otherwise undeletable): the staged images below it
                // have not been touched, and the next sweep tries again.
                return;
            }
        }
        else if (!ancient)
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // A locked or in-use staging dir just stays for the next sweep.
        }
    }

    /// <summary>Probes whether a staging directory's marker is still held by a live
    /// import — in this process or another one — by opening it for reading: an owner
    /// holds the marker with <see cref="FileShare.None"/>, which denies that open, so a
    /// failure means owned. A failure for any other reason (ACLs, AV) counts as owned
    /// too; never deleting is the safe side, and the age rule is what keeps such a
    /// directory from surviving forever.
    /// <para>The probe itself shares as widely as it can
    /// (<see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>) because
    /// nothing is gained by denying: only WSGM writes this file, and only ever with
    /// deny-all sharing, so a permissive probe detects exactly the same owners while no
    /// longer colliding with a concurrent sweep in another process (which would
    /// otherwise read "owned" for a directory nobody owns and leave it for the age
    /// rule) nor with the <see cref="File.Delete(string)"/> that follows it.</para>
    /// <para>KNOWN, DELIBERATE RESIDUAL RACE — the one collision the share mode cannot
    /// remove: while this probe's handle is open, a
    /// <see cref="ClaimStagingDirectory"/> running in ANOTHER process against the SAME
    /// marker fails, because a claim requests <see cref="FileShare.None"/> and Windows
    /// refuses a deny-all request while any handle is open, whatever THAT handle
    /// permits. Probing without a handle at all is not possible for a share-mode
    /// liveness signal, so the window is inherent, not an oversight. It lasts the
    /// microseconds of one open/close, needs two processes sweeping and importing at
    /// the same instant, and its whole consequence is that the fresh import writes no
    /// marker and is protected by the 24-hour age rule
    /// (<see cref="StagingStaleAfter"/>) instead of by ownership: it is deleted early
    /// only if that same Settings window is still holding the unsaved import a day
    /// later.</para></summary>
    private static bool IsOwnedByALiveImport(string markerPath)
    {
        try
        {
            using var probe = new FileStream(
                markerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>True when a staging directory has been untouched for longer than
    /// <paramref name="age"/>. The NEWER of creation and last-write time is used:
    /// extracting the staged images bumps the directory's write time, so an import
    /// that is still running can never look ancient. An unreadable timestamp counts as
    /// young — the age rule may only ever delay a deletion, never cause one.</summary>
    private static bool IsOlderThan(string directory, TimeSpan age)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            var created = info.CreationTimeUtc;
            var written = info.LastWriteTimeUtc;
            var touched = written > created ? written : created;
            return DateTime.UtcNow - touched >= age;
        }
        catch
        {
            return false;
        }
    }

    private static ZipArchiveEntry? FindConfigEntry(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName, ConfigEntryName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }
}
