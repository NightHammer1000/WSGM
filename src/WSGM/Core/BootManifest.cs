// Shared between WSGM and WSGM.LogonService (linked as a source file into the
// service project): the manifest is the ONLY contract between the per-user app
// and the SYSTEM service, so it must stay free of dependencies on either side —
// no Log, no ConfigStore, explicit usings (WSGM has no ImplicitUsings).
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>What the logon service needs to know about one user's WSGM install,
/// projected from config.json into %LOCALAPPDATA%\WSGM\boot.json by WSGM itself.
/// The service reads it as SYSTEM, treats it as untrusted user data, and does
/// nothing with it beyond launching <see cref="ExePath"/> AS THAT USER (which is
/// why a user-writable manifest is not an escalation).</summary>
public sealed class BootManifest
{
    /// <summary>Manifest format version; readers skip versions they don't know.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Whether sign-in should boot into game mode at all.</summary>
    public bool GameModeBoot { get; set; }

    /// <summary>Whether WSGM should be launched with the user's elevated (linked)
    /// token. Precomputed from the same condition Core\SelfElevation checks —
    /// elevated startup apps or an elevated Steam install — so the service needs
    /// no config parsing and no UAC prompt fires at logon.</summary>
    public bool Elevate { get; set; }

    /// <summary>Full path of the WSGM.exe to launch (the installed copy).</summary>
    public string ExePath { get; set; } = "";
}

/// <summary>Shared source-generated JSON metadata that keeps the app and service on one boot-manifest contract.</summary>
[JsonSerializable(typeof(BootManifest))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class BootManifestJsonContext : JsonSerializerContext
{
}

/// <summary>Load/save helpers for boot.json. Reading is defensive on purpose: the
/// service consumes this from SYSTEM, so garbage, truncation, or an oversized file
/// must degrade to "disabled", never throw.</summary>
public static class BootManifestStore
{
    /// <summary>File name of the manifest inside the per-user WSGM directory.</summary>
    public const string FileName = "boot.json";

    // A legitimate manifest is a few hundred bytes; anything bigger is not ours.
    private const long MaxBytes = 64 * 1024;

    /// <summary>Parses manifest JSON, returning null for anything unusable
    /// (malformed JSON, wrong shape, unknown schema version, missing exe path).</summary>
    public static BootManifest? TryParse(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize(json, BootManifestJsonContext.Default.BootManifest);
            if (manifest is null ||
                manifest.SchemaVersion != 1 ||
                string.IsNullOrWhiteSpace(manifest.ExePath))
            {
                return null;
            }
            return manifest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads and parses the manifest at <paramref name="path"/>; null when
    /// absent, unreadable, oversized, or unparsable.</summary>
    public static BootManifest? TryLoad(string path)
    {
        try
        {
            // One handle for both the bound and the read: a separate FileInfo probe
            // could be raced away by the (user-writable) file growing in between.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes)
            {
                return null;
            }
            using var reader = new StreamReader(stream);
            return TryParse(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Atomically writes the manifest to <paramref name="path"/>
    /// (temp file + replace, same pattern as ConfigStore.Save).</summary>
    public static void Save(string path, BootManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(manifest, BootManifestJsonContext.Default.BootManifest);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}
