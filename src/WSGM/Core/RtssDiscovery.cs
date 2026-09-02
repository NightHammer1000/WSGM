using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>One RTSS uninstall-registration snapshot.</summary>
internal sealed record RtssInstallRecord(
    string? DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string? InstallLocation,
    string? DisplayIcon,
    string? UninstallString);

/// <summary>File identity used during discovery without loading external code.</summary>
internal sealed record RtssFileIdentity(
    bool Exists,
    long Length,
    string? ProductName,
    string? FileVersion,
    bool Is64Bit,
    IReadOnlySet<string> Exports,
    bool SignatureValid = true);

/// <summary>Runtime process identity observed without opening an IPC/control path.</summary>
internal sealed record RtssProcessIdentity(
    int ProcessId,
    string ExecutablePath,
    DateTimeOffset StartedAt);

/// <summary>Injectable read-only environment for deterministic RTSS discovery.</summary>
internal interface IRtssDiscoveryEnvironment
{
    IReadOnlyList<RtssInstallRecord> ReadInstallRecords();

    IReadOnlyList<string> ProtectedInstallRoots { get; }

    RtssFileIdentity ReadFileIdentity(string path);

    IReadOnlyList<RtssProcessIdentity> ReadProcesses();
}

/// <summary>Bounded discovery for an external RTSS 7.3-or-newer installation.</summary>
internal sealed class RtssDiscovery
{
    private static readonly Version MinimumVersion = new(7, 3);
    private static readonly string[] RequiredApiExports =
    [
        "LoadProfile",
        "SaveProfile",
        "GetProfileProperty",
        "SetProfileProperty",
        "UpdateProfiles",
    ];

    private readonly IRtssDiscoveryEnvironment _environment;

    internal RtssDiscovery(IRtssDiscoveryEnvironment? environment = null)
    {
        _environment = environment ?? new WindowsRtssDiscoveryEnvironment();
    }

    internal RtssProbe Probe()
    {
        IReadOnlyList<RtssInstallRecord> records;
        try
        {
            records = _environment.ReadInstallRecords();
        }
        catch (Exception ex)
        {
            return Failure(RtssAvailability.Degraded, $"RTSS registration read failed: {ex.Message}");
        }

        List<(RtssInstallRecord Record, string Root, Version Version)> accepted = [];
        foreach (RtssInstallRecord record in records.Take(8))
        {
            if (!TryValidateRegistration(record, out string? root, out Version? version))
            {
                continue;
            }

            accepted.Add((record, root!, version!));
        }

        if (accepted.Count == 0)
        {
            return records.Count == 0
                ? Failure(RtssAvailability.NotInstalled, "RTSS 7.3 or newer is not installed.")
                : Failure(RtssAvailability.Incompatible, "No registered RTSS installation passed identity checks.");
        }

        if (accepted.Count != 1)
        {
            return Failure(RtssAvailability.Incompatible, "Multiple verified RTSS installations are registered.");
        }

        (RtssInstallRecord _, string installRoot, Version registrationVersion) = accepted[0];
        string executable = Path.Combine(installRoot, "RTSS.exe");
        string api = Path.Combine(
            installRoot,
            Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll");
        RtssFileIdentity executableIdentity;
        RtssFileIdentity apiIdentity;
        try
        {
            executableIdentity = _environment.ReadFileIdentity(executable);
            apiIdentity = _environment.ReadFileIdentity(api);
        }
        catch (Exception ex)
        {
            return Failure(
                RtssAvailability.Degraded,
                $"RTSS file identity read failed: {ex.Message}",
                registrationVersion.ToString(),
                executable);
        }
        if (!ValidExecutable(executableIdentity, registrationVersion)
            || !ValidApi(apiIdentity))
        {
            return Failure(
                RtssAvailability.Incompatible,
                "The registered RTSS executable or profile API identity is incompatible.",
                registrationVersion.ToString(),
                executable);
        }

        RtssProcessIdentity[] processes;
        try
        {
            processes = _environment.ReadProcesses()
                .Where(process => SamePath(process.ExecutablePath, executable))
                .Take(2)
                .ToArray();
        }
        catch (Exception ex)
        {
            return Failure(
                RtssAvailability.Degraded,
                $"RTSS process identity read failed: {ex.Message}",
                registrationVersion.ToString(),
                executable);
        }
        if (processes.Length == 0)
        {
            return Failure(
                RtssAvailability.NotRunning,
                "The verified RTSS installation is not running.",
                registrationVersion.ToString(),
                executable);
        }

        if (processes.Length != 1)
        {
            return Failure(
                RtssAvailability.Degraded,
                "Multiple processes match the verified RTSS executable.",
                registrationVersion.ToString(),
                executable);
        }

        RtssProcessIdentity process = processes[0];
        long generation = HashCode.Combine(
            executable.ToUpperInvariant(),
            registrationVersion,
            process.ProcessId,
            process.StartedAt.UtcDateTime.Ticks);
        generation = generation == 0 ? 1 : generation;
        return new RtssProbe(
            RtssAvailability.AdapterUnavailable,
            registrationVersion.ToString(),
            executable,
            generation,
            null,
            "RTSS identity and documented profile API exports are verified.");
    }

    private bool TryValidateRegistration(
        RtssInstallRecord record,
        out string? root,
        out Version? version)
    {
        root = null;
        version = null;
        string expectedVersionedName = $"RivaTuner Statistics Server {record.DisplayVersion}";
        if ((!string.Equals(record.DisplayName, "RivaTuner Statistics Server", StringComparison.Ordinal)
                && !string.Equals(record.DisplayName, expectedVersionedName, StringComparison.Ordinal))
            || !string.Equals(record.Publisher?.Trim(), "Unwinder", StringComparison.Ordinal)
            || !Version.TryParse(record.DisplayVersion, out version)
            || version < MinimumVersion)
        {
            return false;
        }

        string? candidate = record.InstallLocation;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            string? installer = ExtractExecutable(record.UninstallString)
                ?? ExtractExecutable(record.DisplayIcon);
            candidate = installer is null ? null : Path.GetDirectoryName(installer);
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception)
        {
            return false;
        }

        string resolvedRoot = root;
        return _environment.ProtectedInstallRoots.Any(protectedRoot =>
            IsUnder(resolvedRoot, protectedRoot));
    }

    private static bool ValidExecutable(RtssFileIdentity identity, Version registrationVersion)
    {
        if (!identity.Exists
            || identity.Length <= 0
            || !identity.SignatureValid
            || !string.Equals(identity.ProductName, "RTSS", StringComparison.Ordinal))
        {
            return false;
        }

        return Version.TryParse(NormalizeVersion(identity.FileVersion), out Version? fileVersion)
            && fileVersion >= MinimumVersion
            && fileVersion.Major == registrationVersion.Major;
    }

    private static bool ValidApi(RtssFileIdentity identity) => identity.Exists
        && identity.Length > 0
        && identity.SignatureValid
        && identity.Is64Bit == Environment.Is64BitProcess
        && RequiredApiExports.All(identity.Exports.Contains);

    private static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string value = command.Trim();
        if (value[0] == '"')
        {
            int closing = value.IndexOf('"', 1);
            return closing > 1 ? value[1..closing] : null;
        }

        int end = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : value[..(end + 4)];
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Replace(',', '.').Trim();
        int separator = normalized.IndexOf(' ');
        return separator < 0 ? normalized : normalized[..separator];
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static RtssProbe Failure(
        RtssAvailability availability,
        string diagnostic,
        string? version = null,
        string? executable = null) => new(
            availability,
            version,
            executable,
            0,
            null,
            diagnostic);
}

/// <summary>Windows registry, file, PE-export, and process observation for RTSS discovery.</summary>
internal sealed class WindowsRtssDiscoveryEnvironment : IRtssDiscoveryEnvironment
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS";
    private readonly object _identityGate = new();
    private readonly Dictionary<string, CachedFileIdentity> _identities =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ProtectedInstallRoots { get; } =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    ];

    public IReadOnlyList<RtssInstallRecord> ReadInstallRecords()
    {
        List<RtssInstallRecord> records = [];
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = machine.OpenSubKey(UninstallKey, writable: false);
                if (key is null)
                {
                    continue;
                }

                records.Add(new RtssInstallRecord(
                    key.GetValue("DisplayName") as string,
                    key.GetValue("DisplayVersion") as string,
                    key.GetValue("Publisher") as string,
                    key.GetValue("InstallLocation") as string,
                    key.GetValue("DisplayIcon") as string,
                    key.GetValue("UninstallString") as string));
            }
            catch (Exception ex)
            {
                Log.Change(
                    $"rtss.discovery.registration.{view}",
                    $"RTSS discovery could not read the {view} registration: {ex.Message}",
                    "warn ");
            }
        }

        return records.Distinct().ToArray();
    }

    public RtssFileIdentity ReadFileIdentity(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length > 32L * 1024 * 1024)
            {
                return new(false, 0, null, null, false,
                    new HashSet<string>(StringComparer.Ordinal), false);
            }

            DateTime lastWrite = file.LastWriteTimeUtc;
            lock (_identityGate)
            {
                if (_identities.TryGetValue(path, out CachedFileIdentity? cached)
                    && cached.Length == file.Length
                    && cached.LastWriteTimeUtc == lastWrite)
                {
                    return cached.Identity;
                }
            }

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            bool is64Bit = false;
            IReadOnlySet<string> exports = Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                ? PeExportReader.Read(path, out is64Bit)
                : new HashSet<string>(StringComparer.Ordinal);
            bool signatureValid = NativeAuthenticode.VerifyFile(path) == 0;
            RtssFileIdentity identity = new(
                true,
                file.Length,
                version.ProductName,
                version.FileVersion,
                is64Bit,
                exports,
                signatureValid);
            lock (_identityGate)
            {
                _identities[path] = new(file.Length, lastWrite, identity);
            }

            return identity;
        }
        catch (Exception ex)
        {
            Log.Change(
                $"rtss.discovery.file.{Path.GetFileName(path)}",
                $"RTSS discovery could not inspect {Path.GetFileName(path)}: {ex.Message}",
                "warn ");
            return new(false, 0, null, null, false,
                new HashSet<string>(StringComparer.Ordinal), false);
        }
    }

    public IReadOnlyList<RtssProcessIdentity> ReadProcesses()
    {
        List<RtssProcessIdentity> result = [];
        foreach (Process process in Process.GetProcessesByName("RTSS").Take(8))
        {
            using (process)
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        result.Add(new(process.Id, path, process.StartTime.ToUniversalTime()));
                    }
                }
                catch (Exception ex)
                {
                    Log.Change(
                        "rtss.discovery.process",
                        $"RTSS discovery could not inspect process {process.Id}: {ex.Message}",
                        "warn ");
                }
            }
        }

        return result;
    }

    private sealed record CachedFileIdentity(
        long Length,
        DateTime LastWriteTimeUtc,
        RtssFileIdentity Identity);
}

/// <summary>Bounded PE export-table reader; it never maps or executes the inspected DLL.</summary>
internal static class PeExportReader
{
    private const int MaxExportNames = 4096;
    private const int MaxExportNameBytes = 128;

    internal static IReadOnlySet<string> Read(string path, out bool is64Bit)
    {
        is64Bit = false;
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using BinaryReader reader = new(stream);
        if (stream.Length < 256 || reader.ReadUInt16() != 0x5A4D)
        {
            return Empty();
        }

        stream.Position = 0x3c;
        uint peOffset = reader.ReadUInt32();
        if (peOffset > stream.Length - 24)
        {
            return Empty();
        }

        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550)
        {
            return Empty();
        }

        reader.ReadUInt16();
        ushort sectionCount = reader.ReadUInt16();
        stream.Position += 12;
        ushort optionalSize = reader.ReadUInt16();
        stream.Position += 2;
        long optionalOffset = stream.Position;
        ushort magic = reader.ReadUInt16();
        int dataDirectoryOffset = magic switch
        {
            0x10b => 96,
            0x20b => 112,
            _ => -1,
        };
        if (dataDirectoryOffset < 0 || optionalSize < dataDirectoryOffset + 8)
        {
            return Empty();
        }

        is64Bit = magic == 0x20b;

        stream.Position = optionalOffset + dataDirectoryOffset;
        uint exportRva = reader.ReadUInt32();
        if (exportRva == 0 || sectionCount == 0 || sectionCount > 96)
        {
            return Empty();
        }

        stream.Position = optionalOffset + optionalSize;
        List<PeSection> sections = [];
        for (int index = 0; index < sectionCount; index++)
        {
            stream.Position += 8;
            uint virtualSize = reader.ReadUInt32();
            uint virtualAddress = reader.ReadUInt32();
            uint rawSize = reader.ReadUInt32();
            uint rawOffset = reader.ReadUInt32();
            stream.Position += 16;
            sections.Add(new(virtualAddress, Math.Max(virtualSize, rawSize), rawOffset, rawSize));
        }

        if (!TryMap(exportRva, sections, stream.Length, out long exportOffset))
        {
            return Empty();
        }

        stream.Position = exportOffset + 24;
        uint nameCount = reader.ReadUInt32();
        stream.Position += 4;
        uint namesRva = reader.ReadUInt32();
        if (nameCount > MaxExportNames
            || !TryMap(namesRva, sections, stream.Length, out long namesOffset)
            || namesOffset + (nameCount * 4L) > stream.Length)
        {
            return Empty();
        }

        HashSet<string> exports = new(StringComparer.Ordinal);
        for (uint index = 0; index < nameCount; index++)
        {
            stream.Position = namesOffset + (index * 4L);
            uint nameRva = reader.ReadUInt32();
            if (!TryMap(nameRva, sections, stream.Length, out long nameOffset))
            {
                continue;
            }

            stream.Position = nameOffset;
            List<byte> bytes = [];
            for (int byteIndex = 0; byteIndex < MaxExportNameBytes && stream.Position < stream.Length; byteIndex++)
            {
                byte value = reader.ReadByte();
                if (value == 0)
                {
                    break;
                }

                if (value is < 0x20 or > 0x7e)
                {
                    bytes.Clear();
                    break;
                }

                bytes.Add(value);
            }

            if (bytes.Count > 0)
            {
                exports.Add(System.Text.Encoding.ASCII.GetString(bytes.ToArray()));
            }
        }

        return exports;
    }

    private static bool TryMap(
        uint rva,
        IReadOnlyList<PeSection> sections,
        long fileLength,
        out long offset)
    {
        foreach (PeSection section in sections)
        {
            ulong end = (ulong)section.VirtualAddress + section.VirtualSpan;
            if (rva < section.VirtualAddress || rva >= end)
            {
                continue;
            }

            ulong delta = rva - section.VirtualAddress;
            if (delta >= section.RawSize)
            {
                break;
            }

            ulong candidate = section.RawOffset + delta;
            if (candidate < (ulong)fileLength)
            {
                offset = (long)candidate;
                return true;
            }
        }

        offset = 0;
        return false;
    }

    private static IReadOnlySet<string> Empty() => new HashSet<string>(StringComparer.Ordinal);

    private readonly record struct PeSection(
        uint VirtualAddress,
        uint VirtualSpan,
        uint RawOffset,
        uint RawSize);
}
