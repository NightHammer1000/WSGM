using System;
using System.Globalization;
using System.IO;

namespace WSGM.Core;

/// <summary>Which file name the Steam Input shim is deployed under.</summary>
public enum SteamInputShimVector
{
    /// <summary>Not deployed under any name.</summary>
    None,

    /// <summary>Deployed as <c>XInput1_4.dll</c>. The primary vector: Steam's SDL
    /// backend loads XInput by bare name, and it is the name ValvePlug has shipped
    /// against a live client for years.</summary>
    XInput14,

    /// <summary>Deployed as <c>dinput8.dll</c>. The fallback, used when something
    /// else already owns the primary name.</summary>
    DInput8,
}

/// <summary>What the shim deployment looks like on disk.</summary>
public enum SteamInputShimState
{
    /// <summary>Steam is not installed, so there is nowhere to deploy.</summary>
    SteamNotInstalled,

    /// <summary>Steam Input Management is off; any deployed copy is parked aside.</summary>
    Disabled,

    /// <summary>A WSGM-owned shim is in place and matches this build.</summary>
    Deployed,

    /// <summary>A WSGM-owned shim is in place but stale, and Steam has it mapped so
    /// it could not be replaced. The next cold start replaces it.</summary>
    UpdatePending,

    /// <summary>Every candidate name in Steam's directory belongs to another program.</summary>
    Blocked,

    /// <summary>The deployment could not be carried out (access denied, I/O error).</summary>
    Failed,
}

/// <summary>A snapshot of the shim deployment.</summary>
/// <param name="State">What the deployment looks like on disk.</param>
/// <param name="Vector">Which name the shim occupies, if any.</param>
/// <param name="DeployedPath">Full path of the deployed file, when there is one.</param>
/// <param name="Detail">Extra context for the Settings status line and the log.</param>
public readonly record struct SteamInputShimStatus(
    SteamInputShimState State,
    SteamInputShimVector Vector,
    string? DeployedPath,
    string? Detail);

/// <summary>Owns the Steam Input shim that lives in Steam's own install directory.
/// </summary>
/// <remarks>
/// The payload is deployed as a search-order proxy DLL so Steam loads it itself and
/// WSGM never injects. The load-bearing deployment rules — byte-proven ownership,
/// no move-onto-existing while mapped, cold-start-only replacement — are documented
/// in Core's <c>AGENTS.md</c> under "Steam Input shim deployment".
/// </remarks>
public static class SteamInputShim
{
    /// <summary>Export name the payload carries, used as proof of ownership.</summary>
    /// <remarks>
    /// Scanned for as raw bytes rather than parsed out of the PE export table: the
    /// answer is the same, it avoids a full PE parser for one ownership marker, and no
    /// foreign controller DLL contains this string.
    /// </remarks>
    private const string OwnershipSignature = "WsgmSteamInputGateProxy";

    /// <summary>File name of the payload staged beside WSGM's own executable.</summary>
    private const string PayloadFileName = "steam_input_gate.dll";

    /// <summary>Extension the deployed file is renamed to while disabled.</summary>
    private const string ParkedExtension = ".dlld";

    /// <summary>Extension of the sidecar carrying the version stamp.</summary>
    private const string MarkerExtension = ".wsgm-shim";

    /// <summary>Marker format this build writes and accepts.</summary>
    private const int MarkerFormatVersion = 1;

    /// <summary>Candidate names in preference order.</summary>
    private static readonly SteamInputShimVector[] Vectors =
    [
        SteamInputShimVector.XInput14,
        SteamInputShimVector.DInput8,
    ];

    /// <summary>Serializes reconciles: the config watcher and a Settings save can
    /// both reach this at once, and every operation here is short.</summary>
    private static readonly object Sync = new();

    private static volatile bool _enabled = true;
    private static SteamInputShimStatus _lastStatus =
        new(SteamInputShimState.SteamNotInstalled, SteamInputShimVector.None, null, null);
    private static SteamInputShimVector? _loadedVector;

    /// <summary>Mirrors the persisted Steam Input Management setting so this static
    /// owner can be consulted from code that has no configuration of its own.</summary>
    /// <param name="enabled">Whether the shim should be deployed.</param>
    public static void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>Gets whether Steam Input Management is on.</summary>
    public static bool Enabled => _enabled;

    /// <summary>Returns the durable startup trace path written by the resident shim
    /// in one Steam process. A per-process name preserves a failed boot trace when
    /// the user subsequently starts Steam by hand.</summary>
    /// <param name="processId">The Steam process identifier.</param>
    /// <returns>The full per-user trace path.</returns>
    internal static string StartupTracePath(int processId)
        => Path.Combine(Log.Directory, $"steam-input-gate-{processId}.log");

    /// <summary>Gets the most recent deployment snapshot.</summary>
    public static SteamInputShimStatus LastStatus
    {
        get
        {
            lock (Sync)
            {
                return _lastStatus;
            }
        }
    }

    /// <summary>Gets the vector a running Steam was observed to have loaded, or
    /// <see langword="null"/> when that has never been seen.</summary>
    public static SteamInputShimVector? LoadedVector
    {
        get
        {
            lock (Sync)
            {
                return _loadedVector;
            }
        }
    }

    /// <summary>Records that a resident shim answered, so the UI can distinguish
    /// "deployed" from "deployed and actually loaded".</summary>
    /// <param name="vector">The vector that answered.</param>
    public static void RecordLoad(SteamInputShimVector vector)
    {
        lock (Sync)
        {
            if (_loadedVector == vector)
            {
                return;
            }
            _loadedVector = vector;
        }
        Log.Info($"Steam Input shim loaded in Steam via {FileNameFor(vector)}.");
    }

    /// <summary>Brings Steam's directory in line with the current setting.</summary>
    /// <param name="reason">Why the reconcile ran; appears in the log.</param>
    /// <returns>The resulting deployment snapshot. Never throws.</returns>
    public static SteamInputShimStatus Reconcile(string reason)
    {
        lock (Sync)
        {
            var status = ReconcileIn(Steam.InstallDirectory, SourcePath(), _enabled, reason);
            _lastStatus = status;
            return status;
        }
    }

    /// <summary>Inspects Steam's directory without writing anything.</summary>
    /// <returns>The current deployment snapshot. Never throws.</returns>
    public static SteamInputShimStatus Probe()
    {
        lock (Sync)
        {
            var status = ProbeIn(Steam.InstallDirectory, SourcePath(), _enabled);
            _lastStatus = status;
            return status;
        }
    }

    /// <summary>Removes every shim file this class can prove is its own.</summary>
    /// <param name="reason">Why removal ran; appears in the log.</param>
    public static void Remove(string reason)
    {
        lock (Sync)
        {
            RemoveIn(Steam.InstallDirectory, reason);
            _lastStatus = new SteamInputShimStatus(
                SteamInputShimState.Disabled, SteamInputShimVector.None, null, null);
        }
    }

    /// <summary>The deployed file name for a vector.</summary>
    /// <param name="vector">The vector to name.</param>
    /// <returns>The file name, or an empty string for <see cref="SteamInputShimVector.None"/>.</returns>
    internal static string FileNameFor(SteamInputShimVector vector) => vector switch
    {
        SteamInputShimVector.XInput14 => "XInput1_4.dll",
        SteamInputShimVector.DInput8 => "dinput8.dll",
        _ => "",
    };

    /// <summary>Path of the payload staged beside the running executable.</summary>
    private static string SourcePath() =>
        Path.Combine(AppContext.BaseDirectory, PayloadFileName);

    /// <summary>Reconciles a specific directory. The whole algorithm lives here so it
    /// can be exercised against a temporary directory instead of a real Steam.</summary>
    /// <param name="steamDirectory">Steam's install directory, or <see langword="null"/>.</param>
    /// <param name="sourcePath">Path of the payload to deploy.</param>
    /// <param name="enabled">Whether the shim should be deployed.</param>
    /// <param name="reason">Why the reconcile ran; appears in the log.</param>
    /// <returns>The resulting deployment snapshot. Never throws.</returns>
    internal static SteamInputShimStatus ReconcileIn(
        string? steamDirectory, string sourcePath, bool enabled, string reason)
    {
        try
        {
            if (steamDirectory is null || !Directory.Exists(steamDirectory))
            {
                Log.Info("Steam Input shim: Steam is not installed - nothing deployed.");
                return new SteamInputShimStatus(
                    SteamInputShimState.SteamNotInstalled, SteamInputShimVector.None, null, null);
            }
            if (IsReparsePoint(steamDirectory))
            {
                Log.Warn(
                    $"Steam Input shim directory {steamDirectory} is a reparse point - refusing to write.");
                return new SteamInputShimStatus(
                    SteamInputShimState.Failed, SteamInputShimVector.None, null, "reparse point");
            }

            return enabled
                ? Deploy(steamDirectory, sourcePath, reason)
                : Park(steamDirectory, reason);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn($"Steam Input shim write refused in {steamDirectory} ({ex.Message}).");
            return new SteamInputShimStatus(
                SteamInputShimState.Failed, SteamInputShimVector.None, null, "access denied");
        }
        catch (Exception ex)
        {
            Log.Error($"Steam Input shim reconcile failed ({reason}).", ex);
            return new SteamInputShimStatus(
                SteamInputShimState.Failed, SteamInputShimVector.None, null, ex.Message);
        }
    }

    /// <summary>Classifies a directory without writing to it.</summary>
    /// <param name="steamDirectory">Steam's install directory, or <see langword="null"/>.</param>
    /// <param name="sourcePath">Path of the payload that would be deployed.</param>
    /// <param name="enabled">Whether the shim should be deployed.</param>
    /// <returns>The current deployment snapshot. Never throws.</returns>
    internal static SteamInputShimStatus ProbeIn(
        string? steamDirectory, string sourcePath, bool enabled)
    {
        try
        {
            if (steamDirectory is null || !Directory.Exists(steamDirectory))
            {
                return new SteamInputShimStatus(
                    SteamInputShimState.SteamNotInstalled, SteamInputShimVector.None, null, null);
            }
            foreach (var vector in Vectors)
            {
                var deployed = Path.Combine(steamDirectory, FileNameFor(vector));
                if (IsOurs(deployed))
                {
                    var state = enabled
                        ? (IsStale(sourcePath, deployed, MarkerPath(steamDirectory, vector))
                            ? SteamInputShimState.UpdatePending
                            : SteamInputShimState.Deployed)
                        : SteamInputShimState.Deployed;
                    return new SteamInputShimStatus(state, vector, deployed, null);
                }
            }
            foreach (var vector in Vectors)
            {
                if (IsOurs(ParkedPath(steamDirectory, vector)))
                {
                    return new SteamInputShimStatus(
                        SteamInputShimState.Disabled, vector, null, "parked");
                }
            }
            return new SteamInputShimStatus(
                enabled ? SteamInputShimState.Blocked : SteamInputShimState.Disabled,
                SteamInputShimVector.None,
                null,
                null);
        }
        catch (Exception ex)
        {
            return new SteamInputShimStatus(
                SteamInputShimState.Failed, SteamInputShimVector.None, null, ex.Message);
        }
    }

    /// <summary>Deletes every shim file in a directory that can be proven WSGM's.</summary>
    /// <param name="steamDirectory">Steam's install directory, or <see langword="null"/>.</param>
    /// <param name="reason">Why removal ran; appears in the log.</param>
    internal static void RemoveIn(string? steamDirectory, string reason)
    {
        if (steamDirectory is null || !Directory.Exists(steamDirectory))
        {
            return;
        }
        foreach (var vector in Vectors)
        {
            foreach (var path in new[]
                     {
                         Path.Combine(steamDirectory, FileNameFor(vector)),
                         ParkedPath(steamDirectory, vector),
                     })
            {
                if (!IsOurs(path))
                {
                    continue;
                }
                try
                {
                    File.Delete(path);
                    Log.Info($"Steam Input shim removed from {steamDirectory} ({reason}).");
                }
                catch (IOException)
                {
                    // Steam still has it mapped. Park it instead: it is inert
                    // without a lease, and the name is freed for the next install.
                    TryPark(path, ParkedPath(steamDirectory, vector));
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.Warn($"Steam Input shim could not be deleted ({ex.Message}).");
                }
            }
            TryDelete(MarkerPath(steamDirectory, vector));
        }
    }

    /// <summary>Places the shim on the first candidate name that is free or already
    /// ours, and tidies up any other name we still own.</summary>
    private static SteamInputShimStatus Deploy(
        string steamDirectory, string sourcePath, string reason)
    {
        if (!File.Exists(sourcePath))
        {
            Log.Warn($"Steam Input shim payload is missing at {sourcePath} - nothing deployed.");
            return new SteamInputShimStatus(
                SteamInputShimState.Failed, SteamInputShimVector.None, null, "payload missing");
        }

        foreach (var vector in Vectors)
        {
            var deployed = Path.Combine(steamDirectory, FileNameFor(vector));
            var parked = ParkedPath(steamDirectory, vector);
            var marker = MarkerPath(steamDirectory, vector);

            if (File.Exists(deployed) && !IsOurs(deployed))
            {
                Log.Warn(
                    $"Steam Input shim vector {FileNameFor(vector)} belongs to another program - trying the next vector.");
                continue;
            }

            if (!File.Exists(deployed) && IsOurs(parked))
            {
                File.Move(parked, deployed);
                Log.Info($"Steam Input shim restored from {Path.GetFileName(parked)}.");
            }

            if (!File.Exists(deployed))
            {
                File.Copy(sourcePath, deployed);
                WriteMarker(marker, sourcePath, deployed, vector);
                Log.Info(
                    $"Steam Input shim deployed as {FileNameFor(vector)} in {steamDirectory} ({reason}).");
                CleanOtherVectors(steamDirectory, vector);
                return new SteamInputShimStatus(
                    SteamInputShimState.Deployed, vector, deployed, null);
            }

            if (!IsStale(sourcePath, deployed, marker))
            {
                Log.Info(
                    $"Steam Input shim already current ({FileNameFor(vector)}) - no copy needed.");
                CleanOtherVectors(steamDirectory, vector);
                return new SteamInputShimStatus(
                    SteamInputShimState.Deployed, vector, deployed, null);
            }

            try
            {
                File.Copy(sourcePath, deployed, overwrite: true);
                WriteMarker(marker, sourcePath, deployed, vector);
                Log.Info(
                    $"Steam Input shim updated as {FileNameFor(vector)} in {steamDirectory} ({reason}).");
                CleanOtherVectors(steamDirectory, vector);
                return new SteamInputShimStatus(
                    SteamInputShimState.Deployed, vector, deployed, null);
            }
            catch (IOException)
            {
                // A running Steam has the image mapped. Deliberately no retry: the
                // replacement belongs at the next cold start, which is the only
                // moment Steam is provably gone.
                Log.Info(
                    $"Steam Input shim update deferred - {FileNameFor(vector)} is mapped by a running Steam; it will be replaced the next time WSGM starts Steam.");
                return new SteamInputShimStatus(
                    SteamInputShimState.UpdatePending, vector, deployed, "mapped by Steam");
            }
        }

        Log.Warn(
            $"Steam Input shim has no free vector in {steamDirectory} (XInput1_4.dll, dinput8.dll both belong to another program) - Steam Input Management is inactive.");
        return new SteamInputShimStatus(
            SteamInputShimState.Blocked, SteamInputShimVector.None, null, "all vectors occupied");
    }

    /// <summary>Renames every deployed shim aside so Steam stops loading it.</summary>
    private static SteamInputShimStatus Park(string steamDirectory, string reason)
    {
        var parkedVector = SteamInputShimVector.None;
        foreach (var vector in Vectors)
        {
            var deployed = Path.Combine(steamDirectory, FileNameFor(vector));
            if (!IsOurs(deployed))
            {
                if (IsOurs(ParkedPath(steamDirectory, vector)))
                {
                    parkedVector = vector;
                }
                continue;
            }
            if (TryPark(deployed, ParkedPath(steamDirectory, vector)))
            {
                parkedVector = vector;
                Log.Info(
                    $"Steam Input shim parked as {Path.GetFileName(ParkedPath(steamDirectory, vector))} (Steam Input Management turned off, {reason}).");
            }
        }
        return new SteamInputShimStatus(
            SteamInputShimState.Disabled, parkedVector, null, null);
    }

    /// <summary>Renames a deployed file aside, clearing a previous parked copy of our
    /// own first because a move can never replace an existing destination.</summary>
    private static bool TryPark(string deployed, string parked)
    {
        try
        {
            if (IsOurs(parked))
            {
                TryDelete(parked);
            }
            if (File.Exists(parked))
            {
                // Something we do not own sits on the parked name. Leave both alone.
                return false;
            }
            File.Move(deployed, parked);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Steam Input shim could not be parked ({ex.Message}).");
            return false;
        }
    }

    /// <summary>Drops shim files owned by vectors other than the active one.</summary>
    private static void CleanOtherVectors(string steamDirectory, SteamInputShimVector keep)
    {
        foreach (var vector in Vectors)
        {
            if (vector == keep)
            {
                continue;
            }
            var deployed = Path.Combine(steamDirectory, FileNameFor(vector));
            if (IsOurs(deployed))
            {
                TryDelete(deployed);
            }
            var parked = ParkedPath(steamDirectory, vector);
            if (IsOurs(parked))
            {
                TryDelete(parked);
            }
            TryDelete(MarkerPath(steamDirectory, vector));
        }
    }

    /// <summary>Whether the file at <paramref name="path"/> is a WSGM payload.</summary>
    /// <remarks>
    /// Ownership is proven from the file's own bytes, never from the sidecar marker.
    /// A marker can be orphaned - the user installs ValvePlug over our copy, or Steam's
    /// updater replaces it - and trusting it would let WSGM overwrite a file it does
    /// not own, which is the one outcome this class must never produce.
    /// </remarks>
    private static bool IsOurs(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            var signature = System.Text.Encoding.ASCII.GetBytes(OwnershipSignature);
            var content = File.ReadAllBytes(path);
            return content.AsSpan().IndexOf(signature) >= 0;
        }
        catch
        {
            // Unreadable counts as NOT ours: the fail-closed answer is to leave a
            // file alone rather than risk deleting somebody else's.
            return false;
        }
    }

    /// <summary>Whether the deployed copy no longer matches the staged payload.</summary>
    /// <remarks>
    /// Answered from two <see cref="FileInfo"/> reads and one short text read rather
    /// than a hash, because this runs on every Steam cold start. The recorded identity
    /// of the deployed file is what catches a copy that was replaced underneath us.
    /// </remarks>
    private static bool IsStale(string sourcePath, string deployedPath, string markerPath)
    {
        if (!TryReadMarker(markerPath, out var marker))
        {
            return true;
        }
        var source = new FileInfo(sourcePath);
        var deployed = new FileInfo(deployedPath);
        if (!source.Exists || !deployed.Exists)
        {
            return true;
        }
        return marker.SourceLength != source.Length
            || marker.SourceTicks != source.LastWriteTimeUtc.Ticks
            || marker.DeployedLength != deployed.Length
            || marker.DeployedTicks != deployed.LastWriteTimeUtc.Ticks;
    }

    /// <summary>The version stamp recorded beside a deployed shim.</summary>
    /// <param name="SourceLength">Byte length of the staged payload when it was copied.</param>
    /// <param name="SourceTicks">Last-write ticks of the staged payload when it was copied.</param>
    /// <param name="DeployedLength">Byte length of the copy WSGM wrote.</param>
    /// <param name="DeployedTicks">Last-write ticks of the copy WSGM wrote.</param>
    /// <param name="Vector">Which name the copy was written under.</param>
    internal readonly record struct Marker(
        long SourceLength,
        long SourceTicks,
        long DeployedLength,
        long DeployedTicks,
        SteamInputShimVector Vector)
    {
        /// <summary>Renders the stamp as the single line stored in the sidecar.</summary>
        /// <returns>The serialized stamp.</returns>
        public string Format() => string.Join(
            ' ',
            $"WSGM-SIM/{MarkerFormatVersion}",
            SourceLength.ToString(CultureInfo.InvariantCulture),
            SourceTicks.ToString(CultureInfo.InvariantCulture),
            DeployedLength.ToString(CultureInfo.InvariantCulture),
            DeployedTicks.ToString(CultureInfo.InvariantCulture),
            Vector.ToString());

        /// <summary>Parses a stamp written by <see cref="Format"/>.</summary>
        /// <param name="line">The serialized stamp.</param>
        /// <param name="marker">The parsed stamp when parsing succeeded.</param>
        /// <returns><see langword="true"/> when the line is a stamp this build accepts.</returns>
        public static bool TryParse(string? line, out Marker marker)
        {
            marker = default;
            if (line is null)
            {
                return false;
            }
            var parts = line.Trim().Split(' ');
            if (parts.Length != 6 || parts[0] != $"WSGM-SIM/{MarkerFormatVersion}")
            {
                return false;
            }
            if (!long.TryParse(parts[1], CultureInfo.InvariantCulture, out var sourceLength)
                || !long.TryParse(parts[2], CultureInfo.InvariantCulture, out var sourceTicks)
                || !long.TryParse(parts[3], CultureInfo.InvariantCulture, out var deployedLength)
                || !long.TryParse(parts[4], CultureInfo.InvariantCulture, out var deployedTicks)
                || !Enum.TryParse<SteamInputShimVector>(parts[5], out var vector))
            {
                return false;
            }
            marker = new Marker(sourceLength, sourceTicks, deployedLength, deployedTicks, vector);
            return true;
        }
    }

    private static bool TryReadMarker(string markerPath, out Marker marker)
    {
        marker = default;
        try
        {
            return File.Exists(markerPath)
                && Marker.TryParse(File.ReadAllText(markerPath), out marker);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteMarker(
        string markerPath, string sourcePath, string deployedPath, SteamInputShimVector vector)
    {
        try
        {
            var source = new FileInfo(sourcePath);
            var deployed = new FileInfo(deployedPath);
            var marker = new Marker(
                source.Length,
                source.LastWriteTimeUtc.Ticks,
                deployed.Length,
                deployed.LastWriteTimeUtc.Ticks,
                vector);
            File.WriteAllText(markerPath, marker.Format());
        }
        catch (Exception ex)
        {
            // A missing stamp only costs one redundant copy next time.
            Log.Warn($"Steam Input shim stamp could not be written ({ex.Message}).");
        }
    }

    private static string ParkedPath(string steamDirectory, SteamInputShimVector vector) =>
        Path.Combine(
            steamDirectory,
            Path.GetFileNameWithoutExtension(FileNameFor(vector)) + ParkedExtension);

    private static string MarkerPath(string steamDirectory, SteamInputShimVector vector) =>
        Path.Combine(
            steamDirectory,
            Path.GetFileNameWithoutExtension(FileNameFor(vector)) + MarkerExtension);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort: a leftover file is inert without a lease.
        }
    }

    /// <summary>True when the path is a reparse point, so an elevated write never
    /// follows a junction planted in a user-writable location. An unreadable
    /// attribute set counts as one - the safe answer is to refuse.</summary>
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
}
