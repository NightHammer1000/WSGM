using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Packaging;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Stages one expanded plugin outside discovery, then replaces the protected slot.</summary>
internal static class DevicePackageStager
{
    private const string RecoveryDirectoryName = ".previous";
    private const string StagingDirectoryName = ".staging";

    internal static async Task<InstalledDevicePackage> StageAsync(
        string sourceDirectory,
        string installedRoot,
        CancellationToken cancellationToken = default,
        Action? previousSlotMoved = null,
        Func<string, bool>? sourcePathTraversesLink = null,
        Func<string, NativePathIdentity?>? pathIdentityReader = null,
        Action? sourceRootSecured = null,
        Func<string, NativePathIdentity?>? securedSourceIdentityReader = null,
        Func<string, FileAttributes?>? protectedPathAttributeReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedRoot);

        string source = DevicePackagePolicy.NormalizeDirectoryPath(sourceDirectory);
        string destination = DevicePackagePolicy.NormalizeDirectoryPath(installedRoot);
        string parent = Directory.GetParent(destination)?.FullName
            ?? throw new InvalidDataException("The installed package root needs a parent directory.");
        string stagingRoot = ReplacementStagingRoot(destination);
        string backupRoot = ReplacementRecoveryRoot(destination);
        if (SourceOverlapsProtectedNamespace(source, destination, PathsOverlap))
        {
            throw new InvalidDataException(
                "Package source must be separate from the installed slot and every staging or recovery namespace.");
        }

        Func<string, bool> inspectSourcePath = sourcePathTraversesLink ?? PathTraversesLink;
        if (inspectSourcePath(source))
        {
            // A lexical external path can still alias one of the protected siblings through a
            // junction or symlink. Refuse it before reconciliation can delete or move that target.
            throw new InvalidDataException("Package source may not traverse a link or reparse point.");
        }

        Func<string, NativePathIdentity?> readPathIdentity = pathIdentityReader
            ?? NativePathIdentityReader.Read;
        Func<string, NativePathIdentity?> revalidateSecuredSource = securedSourceIdentityReader
            ?? NativePathIdentityReader.Read;
        Func<string, FileAttributes?> readProtectedAttributes = protectedPathAttributeReader
            ?? DevicePackagePolicy.ReadPathAttributes;
        NativePackageSource? packageSource = null;
        FileStream? manifestPin = null;
        try
        {
            ThrowIfSourceOverlapsProtectedNamespace(
                source,
                destination,
                readPathIdentity);
            packageSource = NativePackageSource.TryOpen(source);
            if (packageSource is not null)
            {
                ValidateSecuredSourceIdentity(
                    source,
                    packageSource,
                    revalidateSecuredSource,
                    sourceRootSecured);
                ThrowIfSourceOverlapsProtectedNamespace(
                    source,
                    destination,
                    readPathIdentity);
            }

            // Overlap and every existing source path component are secured before even creating
            // the protected parent or reconciling recovery. An unrelated missing source still
            // reaches reconciliation before the ordinary source-absence error is returned.
            if (!ValidateDirectoryPath(
                parent,
                "Device package slot parent",
                readProtectedAttributes))
            {
                Directory.CreateDirectory(parent);
            }
            ReconcileInstalledPackage(destination, readProtectedAttributes);
            if (packageSource is null)
            {
                if (inspectSourcePath(source))
                {
                    throw new InvalidDataException(
                        "Package source may not traverse a link or reparse point.");
                }

                packageSource = NativePackageSource.TryOpen(source)
                    ?? throw new InvalidDataException("Package source is absent or is not a directory.");
                ValidateSecuredSourceIdentity(
                    source,
                    packageSource,
                    revalidateSecuredSource,
                    sourceRootSecured);
                ThrowIfSourceOverlapsProtectedNamespace(
                    source,
                    destination,
                    readPathIdentity);
            }

            FileStream manifestStream;
            using (NativePackageSourceEntry manifestEntry = packageSource.OpenEntry(
                Path.Combine(source, "plugin.wsgm.json")))
            {
                if (manifestEntry.IsDirectory || manifestEntry.IsReparsePoint)
                {
                    throw new InvalidDataException(
                        "Plugin manifest must be an ordinary package file.");
                }

                manifestStream = manifestEntry.OpenReadStream();
                manifestPin = manifestStream;
            }

            PluginManifest manifest = ReadManifest(manifestStream);
            if (!SafeSegment(manifest.Id))
            {
                throw new InvalidDataException("Package identifier is not a safe directory segment.");
            }

            string stagingPackage = Path.Combine(stagingRoot, manifest.Id);
            bool previousMoved = false;
            bool replacementInstalled = false;

            try
            {
                Directory.CreateDirectory(stagingPackage);
                await CopyPackageAsync(packageSource, stagingPackage, cancellationToken)
                    .ConfigureAwait(false);
                DevicePackageDiscovery discovery = DevicePackagePolicy.Discover(
                    stagingRoot,
                    readProtectedAttributes);
                InstalledDevicePackage package = discovery.InstalledPackage
                    ?? throw new InvalidDataException("Staged package did not occupy exactly one slot.");
                if (!package.Valid || package.Manifest is null)
                {
                    throw new InvalidDataException(
                        $"Staged package validation failed: {package.RejectionCode ?? "unknown"}.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                bool destinationExists = ValidateDirectoryPath(
                    destination,
                    "Installed package root",
                    readProtectedAttributes);
                bool backupExists = ValidateDirectoryPath(
                    backupRoot,
                    "Device package replacement recovery",
                    readProtectedAttributes);
                if (backupExists)
                {
                    throw new InvalidDataException(
                        "Device package replacement recovery unexpectedly reappeared before publication.");
                }
                if (!ValidateDirectoryPath(
                    stagingRoot,
                    "Device package staging root",
                    readProtectedAttributes))
                {
                    throw new InvalidDataException(
                        "Device package staging root disappeared before publication.");
                }

                if (destinationExists)
                {
                    Directory.Move(destination, backupRoot);
                    previousMoved = true;
                }

                try
                {
                    if (previousMoved)
                    {
                        previousSlotMoved?.Invoke();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.Move(stagingRoot, destination);
                    replacementInstalled = true;
                }
                catch
                {
                    if (previousMoved && !ValidateDirectoryPath(
                        destination,
                        "Installed package root",
                        readProtectedAttributes))
                    {
                        Directory.Move(backupRoot, destination);
                        previousMoved = false;
                    }

                    throw;
                }

                string finalPackage = Path.Combine(destination, manifest.Id);
                return package with { PackagePath = finalPackage };
            }
            finally
            {
                if (!replacementInstalled && ValidateDirectoryPath(
                    stagingRoot,
                    "Device package staging root",
                    readProtectedAttributes))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }

                if (replacementInstalled && previousMoved)
                {
                    try
                    {
                        if (ValidateDirectoryPath(
                            backupRoot,
                            "Device package replacement recovery",
                            readProtectedAttributes))
                        {
                            Directory.Delete(backupRoot, recursive: true);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Warn($"Device package replacement recovery cleanup failed: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            manifestPin?.Dispose();
            packageSource?.Dispose();
        }
    }

    /// <summary>Reconciles the fixed replacement sibling left by an interrupted atomic slot swap.
    /// Callers hold <see cref="DevicePackageSlotGate"/>, so an absent destination means the prior
    /// package must be restored, while a present destination proves the replacement was published.</summary>
    internal static void ReconcileInstalledPackage(
        string installedRoot,
        Func<string, FileAttributes?>? attributeReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedRoot);
        string destination = DevicePackagePolicy.NormalizeDirectoryPath(installedRoot);
        string recoveryRoot = ReplacementRecoveryRoot(destination);
        Func<string, FileAttributes?> readAttributes = attributeReader
            ?? DevicePackagePolicy.ReadPathAttributes;
        bool destinationExists = ValidateDirectoryPath(
            destination,
            "Installed package root",
            readAttributes);
        bool recoveryExists = ValidateDirectoryPath(
            recoveryRoot,
            "Device package replacement recovery",
            readAttributes);

        CleanupStagingRoot(destination, readAttributes);
        if (destinationExists)
        {
            DeleteDirectoryIfPresent(recoveryRoot, recoveryExists);
            return;
        }

        if (recoveryExists)
        {
            Directory.Move(recoveryRoot, destination);
        }
    }

    /// <summary>Returns the stable, undiscoverable sibling used to recover an interrupted swap.</summary>
    internal static string ReplacementRecoveryRoot(string installedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedRoot);
        string destination = DevicePackagePolicy.NormalizeDirectoryPath(installedRoot);
        string parent = Directory.GetParent(destination)?.FullName
            ?? throw new InvalidDataException("The installed package root needs a parent directory.");
        return Path.Combine(parent, RecoveryDirectoryName);
    }

    /// <summary>Returns the fixed, undiscoverable sibling used to validate a replacement.</summary>
    internal static string ReplacementStagingRoot(string installedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedRoot);
        string destination = DevicePackagePolicy.NormalizeDirectoryPath(installedRoot);
        string parent = Directory.GetParent(destination)?.FullName
            ?? throw new InvalidDataException("The installed package root needs a parent directory.");
        return Path.Combine(parent, StagingDirectoryName);
    }

    /// <summary>Inventories the slot that would become active after recovery, without moving files.
    /// Startup holds <see cref="DevicePackageSlotGate"/> while calling this method, so an interrupted
    /// swap cannot hide an ambiguous parked package set from the pre-UI cardinality refusal.</summary>
    internal static DevicePackageInventory InventoryEffectiveInstalledPackage(
        string installedRoot,
        Func<string, FileAttributes?>? attributeReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedRoot);
        string destination = DevicePackagePolicy.NormalizeDirectoryPath(installedRoot);
        string recoveryRoot = ReplacementRecoveryRoot(destination);
        Func<string, FileAttributes?> readAttributes = attributeReader
            ?? DevicePackagePolicy.ReadPathAttributes;
        bool destinationExists = ValidateDirectoryPath(
            destination,
            "Installed package root",
            readAttributes);
        bool recoveryExists = ValidateDirectoryPath(
            recoveryRoot,
            "Device package replacement recovery",
            readAttributes);

        if (destinationExists)
        {
            return DevicePackagePolicy.Inventory(destination, readAttributes);
        }

        return recoveryExists
            ? DevicePackagePolicy.Inventory(recoveryRoot, readAttributes)
            : new DevicePackageInventory { PackageRoots = [] };
    }

    internal static void RemoveInstalledPackage(
        string installedRoot,
        Action<string>? beforeDirectoryDelete = null,
        Func<string, FileAttributes?>? attributeReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedRoot);
        string destination = DevicePackagePolicy.NormalizeDirectoryPath(installedRoot);
        string recoveryRoot = ReplacementRecoveryRoot(destination);
        Func<string, FileAttributes?> readAttributes = attributeReader
            ?? DevicePackagePolicy.ReadPathAttributes;
        bool destinationExists = ValidateDirectoryPath(
            destination,
            "Installed package root",
            readAttributes);
        bool recoveryExists = ValidateDirectoryPath(
            recoveryRoot,
            "Device package replacement recovery",
            readAttributes);
        CleanupStagingRoot(destination, readAttributes);
        DeleteDirectoryIfPresent(recoveryRoot, recoveryExists, beforeDirectoryDelete);
        // Delete the live slot last. If any recovery cleanup fails, a failed removal leaves the
        // current package active instead of allowing a surviving backup to resurrect later.
        DeleteDirectoryIfPresent(destination, destinationExists, beforeDirectoryDelete);
    }

    private static async Task CopyPackageAsync(
        NativePackageSource source,
        string destination,
        CancellationToken cancellationToken)
    {
        Stack<(string Source, string Destination)> pending = new();
        pending.Push((source.RootPath, destination));
        byte[] buffer = new byte[64 * 1024];
        int entryCount = 0;
        int fileCount = 0;
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            (string currentSource, string currentDestination) = pending.Pop();
            IReadOnlyList<string> entries = DevicePackagePolicy.EnumerateBoundedDirectory(
                currentSource,
                DevicePackagePolicy.MaxPackageEntries - entryCount,
                cancellationToken);
            entryCount += entries.Count;
            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using NativePackageSourceEntry sourceEntry = source.OpenEntry(entry);
                if (sourceEntry.IsReparsePoint)
                {
                    throw new InvalidDataException("Package staging never follows links or reparse points.");
                }

                string target = Path.Combine(currentDestination, Path.GetFileName(entry));
                if (sourceEntry.IsDirectory)
                {
                    source.RetainDirectory(sourceEntry);
                    Directory.CreateDirectory(target);
                    pending.Push((entry, target));
                    continue;
                }

                fileCount++;
                if (fileCount > DevicePackagePolicy.MaxPackageFiles
                    || sourceEntry.Length > DevicePackagePolicy.MaxPackageFileBytes
                    || sourceEntry.Length > DevicePackagePolicy.MaxPackageBytes - totalBytes)
                {
                    throw new InvalidDataException("Package exceeds staging file or size bounds.");
                }

                await using FileStream input = sourceEntry.OpenReadStream();
                await using FileStream output = new(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                long fileBytes = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    if (read > DevicePackagePolicy.MaxPackageFileBytes - fileBytes
                        || read > DevicePackagePolicy.MaxPackageBytes - totalBytes)
                    {
                        throw new InvalidDataException("Package exceeds staging file or size bounds.");
                    }

                    fileBytes += read;
                    totalBytes += read;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static PluginManifest ReadManifest(FileStream stream)
    {
        byte[] bytes = DevicePackagePolicy.ReadAllBytesBounded(
            stream,
            DevicePackagePolicy.MaxMetadataBytes,
            "Plugin manifest");
        PluginManifestReadResult result = PluginManifestReader.Read(bytes);
        return result.IsValid && result.Manifest is not null
            ? result.Manifest
            : throw new InvalidDataException(
                "Plugin manifest is invalid: "
                    + string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    private static void ValidateSecuredSourceIdentity(
        string source,
        NativePackageSource packageSource,
        Func<string, NativePathIdentity?> readPathIdentity,
        Action? sourceRootSecured)
    {
        sourceRootSecured?.Invoke();
        NativePathIdentity? currentIdentity = readPathIdentity(source);
        if (currentIdentity is null || currentIdentity.Value != packageSource.RootIdentity)
        {
            throw new InvalidDataException(
                "Package source changed while its path was being secured.");
        }
    }

    private static void ThrowIfSourceOverlapsProtectedNamespace(
        string source,
        string destination,
        Func<string, NativePathIdentity?> readPathIdentity)
    {
        if (SourceOverlapsProtectedNamespace(
            source,
            destination,
            (first, second) => PathsOverlapByIdentity(first, second, readPathIdentity)))
        {
            throw new InvalidDataException(
                "Package source must be separate from the installed slot and every staging or recovery namespace.");
        }
    }

    private static bool SafeSegment(string value) =>
        DeviceIdentifier.IsValid(value, 128)
        && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);

    private static bool PathsOverlap(string first, string second) =>
        IsSameOrDescendant(first, second) || IsSameOrDescendant(second, first);

    private static bool PathsOverlapByIdentity(
        string first,
        string second,
        Func<string, NativePathIdentity?> readPathIdentity)
    {
        if (PathsOverlap(first, second))
        {
            return true;
        }

        IReadOnlyList<PathLineageEntry> firstLineage = ReadPathLineage(first, readPathIdentity);
        IReadOnlyList<PathLineageEntry> secondLineage = ReadPathLineage(second, readPathIdentity);
        foreach (PathLineageEntry firstEntry in firstLineage)
        {
            foreach (PathLineageEntry secondEntry in secondLineage)
            {
                if (firstEntry.Identity == secondEntry.Identity
                    && RelativePathsOverlap(
                        firstEntry.RelativeSegments,
                        secondEntry.RelativeSegments))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<PathLineageEntry> ReadPathLineage(
        string path,
        Func<string, NativePathIdentity?> readPathIdentity)
    {
        List<PathLineageEntry> lineage = [];
        List<string> relativeSegments = [];
        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        while (true)
        {
            NativePathIdentity? identity = readPathIdentity(current);
            if (identity is not null)
            {
                lineage.Add(new PathLineageEntry(identity.Value, [.. relativeSegments]));
            }

            string trimmed = current.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            string segment = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(segment))
            {
                break;
            }

            relativeSegments.Insert(0, segment);
            current = parent;
        }

        return lineage;
    }

    private static bool RelativePathsOverlap(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second) =>
        IsSegmentPrefix(first, second) || IsSegmentPrefix(second, first);

    private static bool IsSegmentPrefix(
        IReadOnlyList<string> candidatePrefix,
        IReadOnlyList<string> candidate)
    {
        if (candidatePrefix.Count > candidate.Count)
        {
            return false;
        }

        for (int index = 0; index < candidatePrefix.Count; index++)
        {
            if (!string.Equals(
                candidatePrefix[index],
                candidate[index],
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PathTraversesLink(string path)
    {
        DirectoryInfo? current = new(path);
        while (current is not null)
        {
            try
            {
                // LinkTarget remains meaningful for a dangling link even though Exists is false.
                if (current.LinkTarget is not null
                    || current.Exists
                    && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Missing lexical segments are expected; their existing parents still need review.
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool SourceOverlapsProtectedNamespace(
        string source,
        string destination,
        Func<string, string, bool> pathsOverlap) =>
        pathsOverlap(source, destination)
        || pathsOverlap(source, ReplacementStagingRoot(destination))
        || pathsOverlap(source, ReplacementRecoveryRoot(destination));

    private static void CleanupStagingRoot(
        string destination,
        Func<string, FileAttributes?> readAttributes)
    {
        string parent = Directory.GetParent(destination)?.FullName
            ?? throw new InvalidDataException("The installed package root needs a parent directory.");
        if (!ValidateDirectoryPath(parent, "Device package slot parent", readAttributes))
        {
            return;
        }

        string stagingRoot = ReplacementStagingRoot(destination);
        if (ValidateDirectoryPath(stagingRoot, "Device package staging root", readAttributes))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }
    }

    private static void DeleteDirectoryIfPresent(
        string path,
        bool exists,
        Action<string>? beforeDirectoryDelete = null)
    {
        if (exists)
        {
            beforeDirectoryDelete?.Invoke(path);
            Directory.Delete(path, recursive: true);
        }
    }

    private static bool ValidateDirectoryPath(
        string path,
        string description,
        Func<string, FileAttributes?> readAttributes)
    {
        FileAttributes? attributes = readAttributes(path);
        if (attributes is null)
        {
            return false;
        }
        if ((attributes.Value & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException($"{description} must be a directory.");
        }
        if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{description} may not be a link or reparse point.");
        }

        return true;
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        string normalizedCandidate = DevicePackagePolicy.NormalizeDirectoryPath(candidate);
        string normalizedRoot = DevicePackagePolicy.NormalizeDirectoryPath(root);
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PathLineageEntry(
        NativePathIdentity Identity,
        IReadOnlyList<string> RelativeSegments);
}
