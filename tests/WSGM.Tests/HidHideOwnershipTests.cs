using WSGM.Device.Sdk.Input;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class HidHideOwnershipTests
{
    [Fact]
    public async Task ApplyAndCleanupPreserveEveryExternalEntryAndItsOrdering()
    {
        DeterministicFakeHidHideAdapter adapter = new(
            applications: ["HC.exe", "external.exe"],
            devices: ["HID\\PRE-A", "HID\\PRE-B"]);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);

        HidHideActivationResult activation = await manager.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None);
        Assert.True(activation.Activated);

        adapter.ExternalReplace(
            applications: ["external-new.exe", "HC.exe", "external.exe", "WSGM.exe"],
            devices: ["HID\\PRE-B", "HID\\NEW", "HID\\PRE-A", "HID\\OWN"]);

        HidHideCleanupResult cleanup = await manager.CleanupAsync(
            CancellationToken.None);
        HidHideExactSnapshot final = await adapter.ReadAsync(CancellationToken.None);

        Assert.True(cleanup.Verified);
        Assert.Equal(["external-new.exe", "HC.exe", "external.exe"], final.Applications);
        Assert.Equal(["HID\\PRE-B", "HID\\NEW", "HID\\PRE-A"], final.Devices);
        Assert.True(final.Active);
        Assert.Null(store.Ledger);
    }

    [Fact]
    public async Task PreexistingEquivalentEntriesAreNeverClaimedOrRemoved()
    {
        DeterministicFakeHidHideAdapter adapter = new(
            applications: ["wsgm.EXE"],
            devices: ["hid\\own"]);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);

        HidHideActivationResult activation = await manager.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None);
        HidHideCleanupResult cleanup = await manager.CleanupAsync(
            CancellationToken.None);
        HidHideExactSnapshot final = await adapter.ReadAsync(CancellationToken.None);

        Assert.True(activation.Activated);
        Assert.True(cleanup.Verified);
        Assert.Equal(["wsgm.EXE"], final.Applications);
        Assert.Equal(["hid\\own"], final.Devices);
        Assert.Equal(0, adapter.MutationCount);
    }

    [Fact]
    public async Task AmbiguousDuplicateOwnedValueIsPreservedForExplicitRecovery()
    {
        DeterministicFakeHidHideAdapter adapter = new();
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);
        await manager.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None);
        adapter.ExternalReplace(
            applications: ["WSGM.exe", "WSGM.exe"],
            devices: ["HID\\OWN"]);

        HidHideCleanupResult cleanup = await manager.CleanupAsync(
            CancellationToken.None);
        HidHideExactSnapshot final = await adapter.ReadAsync(CancellationToken.None);

        Assert.False(cleanup.Verified);
        Assert.Equal(["WSGM.exe", "WSGM.exe"], final.Applications);
        Assert.Empty(final.Devices);
        Assert.NotNull(store.Ledger);
        Assert.Contains("Application:WSGM.exe", cleanup.Detail);
    }

    [Fact]
    public async Task PartialActivationFailureRollsBackOnlyAppliedOwnedDeltas()
    {
        DeterministicFakeHidHideAdapter adapter = new(
            applications: ["external.exe"],
            devices: ["HID\\EXTERNAL"]);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);
        adapter.FailMutationAttempt = 2;

        HidHideActivationResult activation = await manager.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None);

        HidHideExactSnapshot final = await adapter.ReadAsync(CancellationToken.None);
        Assert.False(activation.Activated);
        Assert.Equal(["external.exe"], final.Applications);
        Assert.Equal(["HID\\EXTERNAL"], final.Devices);
        Assert.Null(store.Ledger);
    }

    [Fact]
    public async Task InactiveGlobalStateFailsWithoutChangingIt()
    {
        DeterministicFakeHidHideAdapter adapter = new(active: false);
        InMemoryHidHideOwnershipStore store = new();
        HidHideOwnedDeltaManager manager = new(adapter, store);

        HidHideActivationResult activation = await manager.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None);

        Assert.False(activation.Activated);
        Assert.Equal(0, adapter.MutationCount);
        HidHideExactSnapshot final = await adapter.ReadAsync(CancellationToken.None);
        Assert.False(final.Active);
    }

    [Fact]
    public async Task AnOrphanedLedgerIsRecoveredRatherThanBlockingForever()
    {
        // The ledger exists precisely for "WSGM died holding HidHide entries", so finding one from
        // a previous run is the case it was written for. Refusing it instead would cost controller
        // management for good after one crash.
        DeterministicFakeHidHideAdapter adapter = new(
            applications: ["HC.exe"],
            devices: ["HID\\PRE"]);
        InMemoryHidHideOwnershipStore store = new();

        // A first session hides a device and then vanishes, leaving its ledger behind.
        HidHideOwnedDeltaManager crashed = new(adapter, store);
        Assert.True((await crashed.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None)).Activated);
        Assert.NotNull(store.Ledger);

        // A new session finds it.
        HidHideOwnedDeltaManager restarted = new(adapter, store);
        HidHideActivationResult result = await restarted.StartAsync(
            "WSGM.exe",
            [Physical("HID\\OWN")],
            CancellationToken.None);

        Assert.True(result.Activated);

        // And the recovery actually restored the previous run's entry rather than stacking on it:
        // the external device is still hidden exactly once, alongside this session's own.
        HidHideExactSnapshot snapshot = await adapter.ReadAsync(CancellationToken.None);
        Assert.Equal(["HID\\PRE", "HID\\OWN"], snapshot.Devices);
    }

    [Fact]
    public async Task WsgmAllowsItselfBeforeItNeedsToReadDevicesSomethingElseHid()
    {
        // The ordering that mattered on real hardware: another tool had already hidden the pad, so
        // the plugin could not see the device it was being asked to discover, and the allowlisting
        // that would have fixed it only ran later as part of WSGM's own hiding transaction.
        DeterministicFakeHidHideAdapter adapter = new(
            applications: ["HC.exe"],
            devices: ["HID\\SOMEONE-ELSES-PAD"]);
        HidHideOwnedDeltaManager manager = new(adapter, new InMemoryHidHideOwnershipStore());

        string detail = await manager.EnsureReadableAsync(
            controllerManagementEnabled: true,
            "WSGM.exe",
            CancellationToken.None);

        HidHideExactSnapshot snapshot = await adapter.ReadAsync(CancellationToken.None);
        Assert.Contains("WSGM.exe", snapshot.Applications);
        Assert.Contains("allowlist", detail, StringComparison.Ordinal);

        // It grants WSGM sight; it must never hide anything or disturb another owner's entries.
        Assert.Equal(["HID\\SOMEONE-ELSES-PAD"], snapshot.Devices);
        Assert.Contains("HC.exe", snapshot.Applications);
    }

    [Fact]
    public async Task NothingHiddenMeansNothingToAllow()
    {
        // The normal machine. WSGM must not add itself to an allowlist that is guarding nothing.
        DeterministicFakeHidHideAdapter adapter = new();
        HidHideOwnedDeltaManager manager = new(adapter, new InMemoryHidHideOwnershipStore());

        await manager.EnsureReadableAsync(true, "WSGM.exe", CancellationToken.None);

        Assert.Equal(0, adapter.MutationCount);
    }

    [Fact]
    public async Task ManagementOffNeverConsultsHidHideForReadability()
    {
        DeterministicFakeHidHideAdapter adapter = new(devices: ["HID\\PRE"]);
        HidHideOwnedDeltaManager manager = new(adapter, new InMemoryHidHideOwnershipStore());

        await manager.EnsureReadableAsync(false, "WSGM.exe", CancellationToken.None);

        Assert.Equal(0, adapter.ReadCount);
        Assert.Equal(0, adapter.MutationCount);
    }

    private static PhysicalDeviceIdentity Physical(string path) => new()
    {
        InstancePath = path,
        RequiresHiding = true,
    };
}

internal sealed class DeterministicFakeHidHideAdapter : IHidHideAdapter
{
    private readonly object _gate = new();
    private List<string> _applications;
    private List<string> _devices;

    internal DeterministicFakeHidHideAdapter(
        IEnumerable<string>? applications = null,
        IEnumerable<string>? devices = null,
        bool active = true)
    {
        _applications = applications?.ToList() ?? [];
        _devices = devices?.ToList() ?? [];
        Active = active;
        Health = active ? HidHideHealthState.Ready : HidHideHealthState.Inactive;
    }

    internal HidHideHealthState Health { get; set; }

    internal bool Active { get; set; }

    internal Exception? NextReadFailure { get; set; }

    internal Exception? NextMutationFailure { get; set; }

    internal int? FailMutationAttempt { get; set; }

    internal Action<DeterministicFakeHidHideAdapter>? BeforeNextMutation { get; set; }

    internal int ReadCount { get; private set; }

    internal int MutationCount { get; private set; }

    internal int MutationAttemptCount { get; private set; }

    public Task<HidHideExactSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ReadCount++;
            if (NextReadFailure is { } failure)
            {
                NextReadFailure = null;
                throw failure;
            }

            return Task.FromResult(SnapshotUnderGate());
        }
    }

    public Task<HidHideMutationResult> TryMutateAsync(
        HidHideExactSnapshot expected,
        HidHideEntryMutation mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            MutationAttemptCount++;
            BeforeNextMutation?.Invoke(this);
            BeforeNextMutation = null;
            if (FailMutationAttempt == MutationAttemptCount)
            {
                FailMutationAttempt = null;
                throw new IOException("Injected HidHide mutation failure.");
            }

            if (NextMutationFailure is { } failure)
            {
                NextMutationFailure = null;
                throw failure;
            }

            HidHideExactSnapshot current = SnapshotUnderGate();
            if (!current.ExactStateEquals(expected))
            {
                return Task.FromResult(new HidHideMutationResult(
                    false,
                    current,
                    "HidHide changed before the conditional mutation."));
            }

            List<string> entries = mutation.EntryKind is HidHideEntryKind.Application
                ? _applications
                : _devices;
            if (mutation.Mutation is HidHideMutationKind.Add)
            {
                entries.Add(mutation.Value);
            }
            else
            {
                int index = entries.FindIndex(value =>
                    string.Equals(value, mutation.Value, StringComparison.Ordinal));
                if (index < 0)
                {
                    return Task.FromResult(new HidHideMutationResult(
                        false,
                        current,
                        "The exact entry is absent."));
                }

                entries.RemoveAt(index);
            }

            MutationCount++;
            return Task.FromResult(new HidHideMutationResult(
                true,
                SnapshotUnderGate(),
                "Applied."));
        }
    }

    internal void ExternalReplace(
        IEnumerable<string>? applications = null,
        IEnumerable<string>? devices = null,
        bool? active = null)
    {
        lock (_gate)
        {
            if (applications is not null)
            {
                _applications = applications.ToList();
            }

            if (devices is not null)
            {
                _devices = devices.ToList();
            }

            if (active is { } activeValue)
            {
                Active = activeValue;
                Health = activeValue ? HidHideHealthState.Ready : HidHideHealthState.Inactive;
            }
        }
    }

    private HidHideExactSnapshot SnapshotUnderGate() => new(
        Health,
        Active,
        _applications,
        _devices,
        Health.ToString());
}

internal sealed class InMemoryHidHideOwnershipStore : IHidHideOwnershipStore
{
    internal HidHideOwnershipLedger? Ledger { get; private set; }

    internal int SaveCount { get; private set; }

    public Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Ledger);
    }

    public Task SaveAsync(HidHideOwnershipLedger ledger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ledger = ledger;
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ledger = null;
        return Task.CompletedTask;
    }
}
