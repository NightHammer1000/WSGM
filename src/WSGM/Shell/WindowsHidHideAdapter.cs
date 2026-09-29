using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using WSGM.Interop;

namespace WSGM.Shell;

internal sealed record HidHideControlState(
    bool Succeeded,
    int Error,
    bool Active,
    bool Inverse,
    IReadOnlyList<string> Applications,
    IReadOnlyList<string> Devices);

internal interface IHidHideControl
{
    HidHideControlState Read();

    int Write(HidHideEntryKind entryKind, IReadOnlyList<string> entries);
}

internal sealed class NativeHidHideControl : IHidHideControl
{
    public HidHideControlState Read()
    {
        if (!NativeHidHide.TryOpen(out SafeFileHandle handle, out int error))
        {
            return Failure(error);
        }

        using (handle)
        {
            if (!NativeHidHide.TryReadBoolean(
                    handle,
                    NativeHidHide.GetActive,
                    out bool active,
                    out error)
                || !NativeHidHide.TryReadBoolean(
                    handle,
                    NativeHidHide.GetInverse,
                    out bool inverse,
                    out error)
                || !NativeHidHide.TryReadMultiString(
                    handle,
                    NativeHidHide.GetApplications,
                    out IReadOnlyList<string> applications,
                    out error)
                || !NativeHidHide.TryReadMultiString(
                    handle,
                    NativeHidHide.GetDevices,
                    out IReadOnlyList<string> devices,
                    out error))
            {
                return Failure(error);
            }

            return new(true, 0, active, inverse, applications, devices);
        }
    }

    public int Write(HidHideEntryKind entryKind, IReadOnlyList<string> entries)
    {
        if (!NativeHidHide.TryOpen(out SafeFileHandle handle, out int error))
        {
            return error;
        }

        using (handle)
        {
            uint code = entryKind is HidHideEntryKind.Application
                ? NativeHidHide.SetApplications
                : NativeHidHide.SetDevices;
            return NativeHidHide.TryWriteMultiString(handle, code, entries, out error)
                ? 0
                : error;
        }
    }

    private static HidHideControlState Failure(int error) =>
        new(false, error, false, false, [], []);
}

internal sealed class WindowsHidHideAdapter : IHidHideAdapter
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private readonly IHidHideControl _control;
    private readonly SemaphoreSlim _operation = new(1, 1);

    internal WindowsHidHideAdapter()
        : this(new NativeHidHideControl())
    {
    }

    internal WindowsHidHideAdapter(IHidHideControl control)
    {
        _control = control;
    }

    public async Task<HidHideExactSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(ReadSnapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task<HidHideMutationResult> TryMutateAsync(
        HidHideExactSnapshot expected,
        HidHideEntryMutation mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutation.Value);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => TryMutate(expected, mutation),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private HidHideMutationResult TryMutate(
        HidHideExactSnapshot expected,
        HidHideEntryMutation mutation)
    {
        HidHideExactSnapshot current = ReadSnapshot();
        if (!current.ExactStateEquals(expected))
        {
            return new(false, current, "HidHide changed before the conditional mutation.");
        }

        if (current.Health is not HidHideHealthState.Ready)
        {
            return new(false, current, $"HidHide is not writable: {current.Detail}");
        }

        List<string> desired = (mutation.EntryKind is HidHideEntryKind.Application
            ? current.Applications
            : current.Devices).ToList();
        if (mutation.Mutation is HidHideMutationKind.Add)
        {
            if (desired.Contains(mutation.Value, StringComparer.Ordinal))
            {
                return new(false, current, "The exact entry already exists.");
            }

            desired.Add(mutation.Value);
        }
        else
        {
            int index = desired.FindIndex(value =>
                string.Equals(value, mutation.Value, StringComparison.Ordinal));
            if (index < 0)
            {
                return new(false, current, "The exact entry is absent.");
            }

            desired.RemoveAt(index);
        }

        int error = _control.Write(mutation.EntryKind, desired);
        HidHideExactSnapshot readback = ReadSnapshot();
        if (error != 0)
        {
            return new(false, readback, $"HidHide write failed with Win32 error {error}.");
        }

        IReadOnlyList<string> actual = mutation.EntryKind is HidHideEntryKind.Application
            ? readback.Applications
            : readback.Devices;
        bool exact = readback.Health is HidHideHealthState.Ready
            && readback.Active == current.Active
            && actual.SequenceEqual(desired, StringComparer.Ordinal);
        return exact
            ? new(true, readback, "Applied and verified by exact readback.")
            : new(false, readback, "HidHide readback did not match the requested exact state.");
    }

    private HidHideExactSnapshot ReadSnapshot()
    {
        HidHideControlState state = _control.Read();
        if (!state.Succeeded)
        {
            HidHideHealthState health = state.Error is ErrorFileNotFound or ErrorPathNotFound
                ? HidHideHealthState.Unavailable
                : HidHideHealthState.Faulted;
            return new(health, false, [], [],
                $"HidHide control device read failed with Win32 error {state.Error}.");
        }

        HidHideHealthState stateHealth = state.Inverse
            ? HidHideHealthState.Incompatible
            : state.Active
                ? HidHideHealthState.Ready
                : HidHideHealthState.Inactive;
        string detail = state.Inverse
            ? "HidHide inverse mode is incompatible with WSGM-owned allow-list deltas."
            : state.Active
                ? "HidHide is active."
                : "HidHide is installed but inactive.";
        return new(
            stateHealth,
            state.Active,
            state.Applications,
            state.Devices,
            detail);
    }
}
