using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Shell;

internal enum HidHideHealthState
{
    Unavailable,
    Inactive,
    Incompatible,
    Ready,
    Faulted,
}

internal sealed class HidHideExactSnapshot
{
    internal HidHideExactSnapshot(
        HidHideHealthState health,
        bool active,
        IEnumerable<string> applications,
        IEnumerable<string> devices,
        string detail = "")
    {
        Health = health;
        Active = active;
        Applications = applications.ToArray();
        Devices = devices.ToArray();
        Detail = detail;
    }

    internal HidHideHealthState Health { get; }

    internal bool Active { get; }

    internal IReadOnlyList<string> Applications { get; }

    internal IReadOnlyList<string> Devices { get; }

    internal string Detail { get; }

    // Inverse mode needs no field of its own here: it is encoded as Health.Incompatible, so a flip
    // still fails this comparison.
    internal bool ExactStateEquals(HidHideExactSnapshot other) =>
        Health == other.Health
        && Active == other.Active
        && Applications.SequenceEqual(other.Applications, StringComparer.Ordinal)
        && Devices.SequenceEqual(other.Devices, StringComparer.Ordinal);
}

internal enum HidHideEntryKind
{
    Application,
    Device,
}

internal enum HidHideMutationKind
{
    Add,
    Remove,
}

internal sealed record HidHideEntryMutation(
    HidHideMutationKind Mutation,
    HidHideEntryKind EntryKind,
    string Value);

internal sealed record HidHideMutationResult(
    bool Applied,
    HidHideExactSnapshot Current,
    string Detail);

internal interface IHidHideAdapter
{
    Task<HidHideExactSnapshot> ReadAsync(CancellationToken cancellationToken);

    Task<HidHideMutationResult> TryMutateAsync(
        HidHideExactSnapshot expected,
        HidHideEntryMutation mutation,
        CancellationToken cancellationToken);
}

internal enum HidHideOwnedDeltaState
{
    Pending,
    Applied,
    Cleaned,
    CleanupIndeterminate,
}

internal sealed class HidHideOwnedDelta
{
    public HidHideEntryKind EntryKind { get; init; }

    public string Value { get; init; } = string.Empty;

    public HidHideOwnedDeltaState State { get; set; }
}

internal sealed class HidHideOwnershipLedger
{
    public List<HidHideOwnedDelta> Deltas { get; init; } = [];

    public string? RecoveryDetail { get; set; }
}

internal interface IHidHideOwnershipStore
{
    Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(HidHideOwnershipLedger ledger, CancellationToken cancellationToken);

    Task DeleteAsync(CancellationToken cancellationToken);
}

internal sealed class FileHidHideOwnershipStore : IHidHideOwnershipStore
{
    private readonly string _path;

    internal FileHidHideOwnershipStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public async Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using FileStream stream = new(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(
            stream,
            HidHideOwnershipJsonContext.Default.HidHideOwnershipLedger,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        HidHideOwnershipLedger ledger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The HidHide ledger path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporary = _path + ".new";
        await using (FileStream stream = new(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                ledger,
                HidHideOwnershipJsonContext.Default.HidHideOwnershipLedger,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(_path);
        return Task.CompletedTask;
    }
}

internal sealed record HidHideActivationResult(
    bool Activated,
    string Detail,
    HidHideOwnershipLedger? Ledger);

internal sealed record HidHideCleanupResult(
    bool Verified,
    string Detail,
    HidHideOwnershipLedger? RemainingLedger);

internal sealed class HidHideOwnedDeltaManager
{
    private const int MaximumCompareRetries = 3;
    private readonly IHidHideAdapter _adapter;
    private readonly IHidHideOwnershipStore _store;
    private readonly SemaphoreSlim _transition = new(1, 1);

    internal HidHideOwnedDeltaManager(
        IHidHideAdapter adapter,
        IHidHideOwnershipStore store)
    {
        _adapter = adapter;
        _store = store;
    }

    /// <summary>Makes WSGM able to read devices HidHide is hiding, before it needs to.</summary>
    /// <param name="controllerManagementEnabled">Whether controller management may run at all.</param>
    /// <param name="controllerReaderApplication">The WSGM image path to allow.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A description of what was found, for the log.</returns>
    /// <remarks>
    /// <see cref="StartAsync"/> allowlists WSGM too, but only as the first step of WSGM's own
    /// hiding transaction — which is to say only once WSGM already knows which devices to hide. That
    /// ordering assumes WSGM is the only thing using HidHide. When something else hid the controller
    /// first, the plugin cannot see the device it is being asked to discover, discovery finds
    /// nothing, and the allowlisting that would have fixed it never runs because it comes later
    /// (device evidence in <c>docs\device-security.md</c>).
    /// <para>
    /// This adds nothing to the hidden set and takes nothing away from another owner: it only grants
    /// WSGM's own process the ability to read. It is therefore safe before a transaction exists, and
    /// it is idempotent, so the later transaction finds it present and records no delta.
    /// </para>
    /// </remarks>
    internal async Task<string> EnsureReadableAsync(
        bool controllerManagementEnabled,
        string controllerReaderApplication,
        CancellationToken cancellationToken)
    {
        if (!controllerManagementEnabled || string.IsNullOrWhiteSpace(controllerReaderApplication))
        {
            return "Controller management is off; HidHide was not consulted.";
        }

        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HidHideExactSnapshot snapshot = await _adapter.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.Health is not HidHideHealthState.Ready)
            {
                return $"HidHide is not available ({snapshot.Health}); nothing to allow.";
            }

            if (!snapshot.Active || snapshot.Devices.Count == 0)
            {
                return "HidHide is hiding nothing; no allowance needed.";
            }

            if (Contains(snapshot.Applications, controllerReaderApplication))
            {
                return $"HidHide hides {snapshot.Devices.Count} device(s); WSGM is already allowed.";
            }

            HidHideMutationResult mutation = await _adapter.TryMutateAsync(
                snapshot,
                new HidHideEntryMutation(
                    HidHideMutationKind.Add,
                    HidHideEntryKind.Application,
                    controllerReaderApplication),
                cancellationToken).ConfigureAwait(false);
            if (!mutation.Applied)
            {
                return "HidHide is hiding devices and WSGM could not add itself to its allowlist: "
                    + mutation.Detail;
            }

            return $"HidHide hides {snapshot.Devices.Count} device(s) that WSGM does not own; "
                + "added WSGM to its allowlist so the plugin can read them.";
        }
        finally
        {
            _transition.Release();
        }
    }

    internal async Task<HidHideActivationResult> StartAsync(
        string controllerReaderApplication,
        IReadOnlyList<PhysicalDeviceIdentity> physicalDevices,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerReaderApplication);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _store.LoadAsync(cancellationToken).ConfigureAwait(false) is { } existing)
            {
                // A ledger loaded before this run writes anything records an interrupted ownership
                // transaction. Recover it before admitting a new transaction.
                HidHideCleanupResult recovery = await CleanupUnderGateAsync(existing, cancellationToken)
                    .ConfigureAwait(false);
                if (!recovery.Verified)
                {
                    // Recovery could not put HidHide back, which is a real reason to keep hands off.
                    return new(
                        false,
                        $"A previous HidHide ownership ledger could not be recovered: {recovery.Detail}",
                        existing);
                }

                Log.Info("Recovered an orphaned HidHide ownership ledger from a previous session.");
            }

            HidHideExactSnapshot snapshot = await _adapter.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.Health is not HidHideHealthState.Ready || !snapshot.Active)
            {
                return new(false,
                    $"HidHide prerequisite unavailable: {snapshot.Health} ({snapshot.Detail}).",
                    null);
            }

            HidHideOwnershipLedger ledger = new();

            try
            {
                snapshot = await AddIfAbsentAsync(
                    snapshot,
                    ledger,
                    HidHideEntryKind.Application,
                    controllerReaderApplication,
                    cancellationToken).ConfigureAwait(false);

                foreach (string instancePath in physicalDevices
                    .Where(device => device.RequiresHiding)
                    .Select(device => device.InstancePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    snapshot = await AddIfAbsentAsync(
                        snapshot,
                        ledger,
                        HidHideEntryKind.Device,
                        instancePath,
                        cancellationToken).ConfigureAwait(false);
                }

                if (!Contains(snapshot.Applications, controllerReaderApplication)
                    || physicalDevices.Where(device => device.RequiresHiding)
                        .Any(device => !Contains(snapshot.Devices, device.InstancePath)))
                {
                    throw new InvalidOperationException("HidHide readback did not contain every required entry.");
                }

                return new(true, "WSGM-owned HidHide deltas applied and verified.", ledger);
            }
            catch (Exception ex)
            {
                ledger.RecoveryDetail = $"Activation failed: {ex.Message}";
                await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
                HidHideCleanupResult cleanup = await CleanupUnderGateAsync(ledger, cancellationToken)
                    .ConfigureAwait(false);
                return new(false,
                    cleanup.Verified
                        ? $"HidHide activation rolled back: {ex.Message}"
                        : $"HidHide activation cleanup is unverified: {ex.Message}",
                    cleanup.RemainingLedger);
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    internal async Task<HidHideCleanupResult> CleanupAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HidHideOwnershipLedger? ledger = await _store.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            return ledger is null
                ? new(true, "No WSGM-owned HidHide state exists.", null)
                : await CleanupUnderGateAsync(ledger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<HidHideExactSnapshot> AddIfAbsentAsync(
        HidHideExactSnapshot snapshot,
        HidHideOwnershipLedger ledger,
        HidHideEntryKind entryKind,
        string value,
        CancellationToken cancellationToken)
    {
        if (Contains(Entries(snapshot, entryKind), value))
        {
            return snapshot;
        }

        HidHideOwnedDelta delta = new()
        {
            EntryKind = entryKind,
            Value = value,
            State = HidHideOwnedDeltaState.Pending,
        };
        ledger.Deltas.Add(delta);
        await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);

        for (int attempt = 0; attempt < MaximumCompareRetries; attempt++)
        {
            HidHideMutationResult result = await _adapter.TryMutateAsync(
                snapshot,
                new(HidHideMutationKind.Add, entryKind, value),
                cancellationToken).ConfigureAwait(false);
            snapshot = result.Current;
            if (result.Applied)
            {
                delta.State = HidHideOwnedDeltaState.Applied;
                await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
                return snapshot;
            }

            if (Contains(Entries(snapshot, entryKind), value))
            {
                ledger.Deltas.Remove(delta);
                await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
                return snapshot;
            }
        }

        throw new IOException($"HidHide {entryKind} entry kept changing during activation.");
    }

    private async Task<HidHideCleanupResult> CleanupUnderGateAsync(
        HidHideOwnershipLedger ledger,
        CancellationToken cancellationToken)
    {
        List<string> problems = [];
        foreach (HidHideOwnedDelta delta in ledger.Deltas.AsEnumerable().Reverse())
        {
            if (delta.State is HidHideOwnedDeltaState.Cleaned)
            {
                continue;
            }

            bool cleaned = await RemoveOwnedDeltaAsync(delta, cancellationToken)
                .ConfigureAwait(false);
            delta.State = cleaned
                ? HidHideOwnedDeltaState.Cleaned
                : HidHideOwnedDeltaState.CleanupIndeterminate;
            if (!cleaned)
            {
                problems.Add($"{delta.EntryKind}:{delta.Value}");
            }

            await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
        }

        if (problems.Count == 0)
        {
            await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
            return new(true, "Only WSGM-owned HidHide deltas were removed.", null);
        }

        ledger.RecoveryDetail = "Cleanup refused ambiguous entries: " + string.Join(", ", problems);
        await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
        return new(false, ledger.RecoveryDetail, ledger);
    }

    private async Task<bool> RemoveOwnedDeltaAsync(
        HidHideOwnedDelta delta,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < MaximumCompareRetries; attempt++)
        {
            HidHideExactSnapshot snapshot = await _adapter.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.Health is not HidHideHealthState.Ready)
            {
                return false;
            }

            IReadOnlyList<string> entries = Entries(snapshot, delta.EntryKind);
            int semanticCount = entries.Count(entry =>
                string.Equals(entry, delta.Value, StringComparison.OrdinalIgnoreCase));
            int exactCount = entries.Count(entry =>
                string.Equals(entry, delta.Value, StringComparison.Ordinal));

            if (semanticCount == 0)
            {
                return true;
            }

            if (semanticCount != 1 || exactCount != 1)
            {
                return false;
            }

            HidHideMutationResult result = await _adapter.TryMutateAsync(
                snapshot,
                new(HidHideMutationKind.Remove, delta.EntryKind, delta.Value),
                cancellationToken).ConfigureAwait(false);
            if (result.Applied)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> Entries(
        HidHideExactSnapshot snapshot,
        HidHideEntryKind entryKind) => entryKind is HidHideEntryKind.Application
            ? snapshot.Applications
            : snapshot.Devices;

    /// <summary>Whether HidHide already lists this entry, in whichever notation it stored it.</summary>
    /// <param name="entries">Entries exactly as HidHide returned them.</param>
    /// <param name="value">The entry WSGM is looking for.</param>
    /// <returns>Whether it is present.</returns>
    /// <remarks>
    /// A plain string compare is not enough for applications: HidHide stores them as NT device
    /// paths — <c>\Device\HarddiskVolume3\Program Files\…</c> — while WSGM knows its own executables
    /// by drive letter. Without normalization the allowlist grows on every activation and cleanup
    /// leaves the other notation's duplicate behind (device evidence in
    /// <c>docs\device-security.md</c>).
    /// </remarks>
    internal static bool Contains(IEnumerable<string> entries, string value)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string normalized = NormalizePath(value);
        return entries.Any(entry =>
            string.Equals(entry, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizePath(entry), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reduces an entry to a form both notations agree on.</summary>
    /// <param name="value">A DOS path, an NT device path, or a device instance path.</param>
    /// <returns>The comparable form.</returns>
    /// <remarks>
    /// Only the volume prefix differs between the two notations, so stripping it leaves the part
    /// that identifies the file. Device instance paths carry no such prefix and pass through, which
    /// is why the device list never had this problem.
    /// </remarks>
    internal static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string path = value.Trim().Replace('/', '\\');

        // \Device\HarddiskVolumeN\rest  ->  \rest
        const string devicePrefix = @"\device\harddiskvolume";
        if (path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            int separator = path.IndexOf('\\', devicePrefix.Length);
            return separator < 0 ? string.Empty : path[separator..];
        }

        // C:\rest  ->  \rest. Deliberately only a drive letter: a UNC path has no volume to strip
        // and must keep its server and share, which are part of what identifies it.
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            return path.Length == 2 ? string.Empty : path[2..];
        }

        return path;
    }
}

[JsonSerializable(typeof(HidHideOwnershipLedger))]
internal sealed partial class HidHideOwnershipJsonContext : JsonSerializerContext;
