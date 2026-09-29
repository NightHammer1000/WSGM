using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Threading;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Packaging;

namespace WSGM.Core;

/// <summary>The startup-safe cardinality of the one protected plugin slot.</summary>
internal enum DevicePackageCardinality
{
    /// <summary>No package is installed; core WSGM remains available without Device Integration.</summary>
    Empty,

    /// <summary>Exactly one package root exists and may be validated.</summary>
    Single,

    /// <summary>More than one package root exists; normal startup must refuse all of them.</summary>
    Multiple,
}

/// <summary>A manifest-free inventory of the protected plugin slot.</summary>
internal sealed record DevicePackageInventory
{
    /// <summary>Every immediate package root, sorted by absolute path.</summary>
    public required IReadOnlyList<string> PackageRoots { get; init; }

    /// <summary>The hard startup cardinality derived solely from <see cref="PackageRoots"/>.</summary>
    public DevicePackageCardinality Cardinality => PackageRoots.Count switch
    {
        0 => DevicePackageCardinality.Empty,
        1 => DevicePackageCardinality.Single,
        _ => DevicePackageCardinality.Multiple,
    };
}

/// <summary>The sole installed package after structural, API, and architecture validation.</summary>
internal sealed record InstalledDevicePackage
{
    /// <summary>Canonical protected package directory.</summary>
    public required string PackagePath { get; init; }

    /// <summary>Parsed manifest when structural validation succeeded.</summary>
    public PluginManifest? Manifest { get; init; }

    /// <summary>Whether this sole package may be activated.</summary>
    public required bool Valid { get; init; }

    /// <summary>Stable rejection code, or null when eligible.</summary>
    public string? RejectionCode { get; init; }

    /// <summary>Sanitized diagnostic detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>The complete result of reading the one protected plugin slot.</summary>
internal sealed record DevicePackageDiscovery
{
    /// <summary>The manifest-free slot inventory.</summary>
    public required DevicePackageInventory Inventory { get; init; }

    /// <summary>The sole installed package, including its validation failure when invalid.</summary>
    public InstalledDevicePackage? InstalledPackage { get; init; }

    /// <summary>The slot-level failure code used when multiple package roots were found.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Sanitized slot-level failure detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>Manifest-free cardinality plus sole-package validation for the protected slot.</summary>
internal static class DevicePackagePolicy
{
    private const string ManifestName = "plugin.wsgm.json";
    internal const int MaxMetadataBytes = 1024 * 1024;
    internal const int MaxPackageEntries = 1024;
    internal const int MaxPackageFiles = 512;
    internal const long MaxPackageFileBytes = 128L * 1024 * 1024;
    internal const long MaxPackageBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Counts immediate package directories without reading a manifest or opening plugin files.
    /// </summary>
    /// <param name="packageRoot">The protected directory containing zero or one package directory.</param>
    /// <param name="attributeReader">Attribute reader; defaults to the filesystem.</param>
    /// <returns>The absolute package paths and their hard cardinality.</returns>
    public static DevicePackageInventory Inventory(
        string packageRoot,
        Func<string, FileAttributes?>? attributeReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        string root = NormalizeDirectoryPath(packageRoot);
        Func<string, FileAttributes?> readAttributes = attributeReader ?? ReadPathAttributes;
        FileAttributes? rootAttributes = readAttributes(root);
        if (rootAttributes is null)
        {
            return new DevicePackageInventory { PackageRoots = [] };
        }

        if ((rootAttributes.Value & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException("The protected package slot must be a directory.");
        }
        if ((rootAttributes.Value & FileAttributes.ReparsePoint) != 0)
        {
            return new DevicePackageInventory { PackageRoots = [root] };
        }

        List<string> packages = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            FileAttributes? attributes = readAttributes(entry)
                ?? throw new IOException("A package-slot entry disappeared during inspection.");
            if ((attributes.Value & FileAttributes.Directory) != 0)
            {
                packages.Add(NormalizeDirectoryPath(entry));
            }
        }

        string[] sortedPackages = packages
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new DevicePackageInventory { PackageRoots = sortedPackages };
    }

    /// <summary>
    /// Validates the sole installed package. Multiple roots are all rejected without reading any
    /// manifest, and an empty slot returns no package.
    /// </summary>
    /// <param name="packageRoot">Protected directory whose immediate children are package roots.</param>
    /// <param name="attributeReader">Attribute reader; defaults to the filesystem.</param>
    /// <returns>The inventory and, only for a single root, its validated package.</returns>
    public static DevicePackageDiscovery Discover(
        string packageRoot,
        Func<string, FileAttributes?>? attributeReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        Func<string, FileAttributes?> readAttributes = attributeReader ?? ReadPathAttributes;
        DevicePackageInventory inventory = Inventory(packageRoot, readAttributes);
        if (inventory.Cardinality is DevicePackageCardinality.Empty)
        {
            return new DevicePackageDiscovery { Inventory = inventory };
        }

        if (inventory.Cardinality is DevicePackageCardinality.Multiple)
        {
            return new DevicePackageDiscovery
            {
                Inventory = inventory,
                ErrorCode = "multiple-package-roots",
                Detail = "Normal startup refuses every package when the protected slot contains more than one root.",
            };
        }

        return new DevicePackageDiscovery
        {
            Inventory = inventory,
            InstalledPackage = ValidateInstalledPackage(
                inventory.PackageRoots[0],
                readAttributes),
        };
    }

    private static InstalledDevicePackage ValidateInstalledPackage(
        string packagePath,
        Func<string, FileAttributes?> readAttributes)
    {
        try
        {
            string root = NormalizeDirectoryPath(packagePath);
            FileAttributes? rootAttributes = readAttributes(root)
                ?? throw new IOException("The installed package disappeared during validation.");
            if ((rootAttributes.Value & FileAttributes.Directory) == 0)
            {
                return Reject(root, "package-invalid", "The installed package is not a directory.");
            }
            if ((rootAttributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                return Reject(root, "package-link", "Package directories may not be links or reparse points.");
            }

            ValidateBoundedPackage(root, readAttributes);
            byte[] manifestBytes = ReadAllBytesBounded(
                Constrain(root, ManifestName, readAttributes),
                MaxMetadataBytes,
                "Plugin manifest");
            PluginManifestReadResult manifestRead = PluginManifestReader.Read(manifestBytes);
            if (!manifestRead.IsValid || manifestRead.Manifest is null)
            {
                string rejectionCode = manifestRead.Errors.Any(error =>
                    error.Code is ManifestValidationCode.InvalidApiVersion)
                    ? "api-incompatible"
                    : "manifest-invalid";
                return Reject(
                    root,
                    rejectionCode,
                    string.Join("; ", manifestRead.Errors.Select(error => error.Message)));
            }

            PluginManifest manifest = manifestRead.Manifest;
            if (DeviceApi.Version != manifest.ApiVersion)
            {
                return Reject(
                    root,
                    "api-incompatible",
                    "Package API version does not equal this runtime.",
                    manifest);
            }

            string entryPath = Constrain(root, manifest.EntryAssembly, readAttributes);
            if (!IsX64ManagedAssembly(entryPath))
            {
                return Reject(
                    root,
                    "architecture-unsupported",
                    "Plugin entry point is not an x64 managed assembly.",
                    manifest);
            }

            return new InstalledDevicePackage
            {
                PackagePath = root,
                Manifest = manifest,
                Valid = true,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException)
        {
            return Reject(packagePath, "package-invalid", ex.Message);
        }
    }

    private static void ValidateBoundedPackage(
        string root,
        Func<string, FileAttributes?> readAttributes)
    {
        int entryCount = 0;
        int fileCount = 0;
        long totalBytes = 0;
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            IReadOnlyList<string> entries = EnumerateBoundedDirectory(
                directory,
                MaxPackageEntries - entryCount);
            entryCount += entries.Count;
            foreach (string entry in entries)
            {
                FileAttributes? attributes = readAttributes(entry)
                    ?? throw new IOException("A package entry disappeared during validation.");
                if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Package paths may not traverse links.");
                }

                if ((attributes.Value & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                FileInfo file = new(entry);
                fileCount++;
                if (fileCount > MaxPackageFiles
                    || file.Length > MaxPackageFileBytes
                    || file.Length > MaxPackageBytes - totalBytes)
                {
                    throw new InvalidDataException("Package exceeds the bounded file or size limit.");
                }
                totalBytes += file.Length;
            }
        }
    }

    internal static IReadOnlyList<string> EnumerateBoundedDirectory(
        string directory,
        int remainingEntries,
        CancellationToken cancellationToken = default)
    {
        List<string> entries = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(entry);
            if (entries.Count > remainingEntries)
            {
                throw new InvalidDataException(
                    "Package exceeds the bounded filesystem-entry limit.");
            }
        }

        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return entries;
    }

    private static byte[] ReadAllBytesBounded(string path, int maxBytes, string description)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        return ReadAllBytesBounded(stream, maxBytes, description);
    }

    /// <summary>Reads a whole already-open stream, refusing one over the given bound.</summary>
    /// <remarks>Stream-based so a caller holding a no-follow handle keeps its reparse safety.</remarks>
    internal static byte[] ReadAllBytesBounded(FileStream stream, int maxBytes, string description)
    {
        if (stream.Length > maxBytes)
        {
            throw new InvalidDataException($"{description} exceeds {maxBytes} bytes.");
        }

        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Resolves a manifest-relative path while refusing escapes and reparse traversal.</summary>
    internal static string Constrain(
        string root,
        string relativePath,
        Func<string, FileAttributes?>? attributeReader = null)
    {
        Func<string, FileAttributes?> readAttributes = attributeReader ?? ReadPathAttributes;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Package paths must be relative.");
        }

        string normalizedRoot = NormalizeDirectoryPath(root);
        string prefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A package path escaped its protected directory.");
        }

        string current = normalizedRoot;
        foreach (string segment in Path.GetRelativePath(normalizedRoot, candidate).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes? attributes = readAttributes(current);
            if (attributes is not null
                && (attributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Package paths may not traverse links.");
            }
        }

        return candidate;
    }

    private static bool IsX64ManagedAssembly(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader pe = new(stream, PEStreamOptions.LeaveOpen);
            return pe.PEHeaders.CoffHeader.Machine is Machine.Amd64
                && pe.PEHeaders.CorHeader is not null
                && pe.HasMetadata
                && pe.GetMetadataReader().IsAssembly;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static FileAttributes? ReadPathAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static InstalledDevicePackage Reject(
        string path,
        string code,
        string detail,
        PluginManifest? manifest = null) => new()
        {
            PackagePath = Path.GetFullPath(path),
            Manifest = manifest,
            Valid = false,
            RejectionCode = code,
            Detail = detail,
        };
}
