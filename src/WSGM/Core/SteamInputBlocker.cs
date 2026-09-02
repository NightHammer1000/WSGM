using System;
using System.Collections.Generic;
using SteamInterop;

namespace WSGM.Core;

/// <summary>Process-wide owner of WSGM's Steam Input block lease.
/// The injected gate runs only in Steam and prevents Steam Input from opening
/// controllers while a focus-taking WSGM surface needs SDL to read them. The
/// pipe-backed lease is released automatically if WSGM crashes.</summary>
public static class SteamInputBlocker
{
    /// <summary>Displayed when the authoritative host-side probe cannot safely
    /// resolve the current Steam build for controller recovery.</summary>
    public const string DynamicRecoveryWarning = "Steam Input could not dynamically locate Steam's controller-release code. Please report this on GitHub — the Steam Input hook may need updating.";

    private static readonly object Sync = new();

    // The lease itself is process-wide, but several WSGM surfaces can need it at
    // the same time (the quick-access panel/taskbar and the settings window opened
    // from them). Each names itself here, so one surface closing cannot take the
    // controller away from another that is still on screen — invariant 1.
    private static readonly HashSet<string> Owners = new(StringComparer.Ordinal);

    private static SteamInputClient? _client;
    private static SteamInputBlockLease? _lease;

    /// <summary>Raised when the authoritative dynamic Steam recovery probe or
    /// its guarded controller-rescan operation fails.</summary>
    public static event Action<string>? RecoveryWarningRaised;

    /// <summary>True while this process owns an active Steam Input block lease.</summary>
    public static bool IsApplied
    {
        get
        {
            lock (Sync)
            {
                return _lease is not null;
            }
        }
    }

    /// <summary>Acquires the shared lease when a resident shim is available.
    /// Failures are logged and leave the UI alive so the device report can identify
    /// what was missing.</summary>
    /// <remarks>
    /// WSGM.exe never injects. The block is delivered by the shim Steam loads from
    /// its own directory, so with Steam Input Management off - or before Steam has
    /// restarted since the shim was deployed - there is simply nothing to connect
    /// to, and this fails open exactly like the Steam-unavailable path always did.
    /// </remarks>
    public static void Acquire()
    {
        lock (Sync)
        {
            if (_lease is not null)
            {
                return;
            }

            var shim = SteamInputShim.Probe();
            if (shim.State is not (SteamInputShimState.Deployed or SteamInputShimState.UpdatePending))
            {
                // UpdatePending is connectable on purpose: an older-but-ours shim is
                // the one Steam has mapped, and the protocol handshake is the
                // authority on whether it is compatible.
                Log.Warn(
                    $"Steam Input lease unavailable - no resident shim ({shim.State}" +
                    $"{(shim.Detail is null ? "" : $": {shim.Detail}")}). Surface opens unblocked.");
                return;
            }

            try
            {
                // AllowInjection stays false: this is what makes "WSGM never writes
                // into the Steam process" a property of the code rather than a promise.
                _client ??= new SteamInputClient(new SteamInputClientOptions { AllowInjection = false });
                _lease = _client.Acquire();
                if (shim.Vector != SteamInputShimVector.None)
                {
                    SteamInputShim.RecordLoad(shim.Vector);
                }
                Log.Info($"Steam Input lease acquired via {SteamInputShim.FileNameFor(shim.Vector)} (revoked {_lease.InitialStatus.LastRevokedHandleCount} HID handles).");
                if (!_lease.InitialStatus.SupportsInternalRecovery)
                {
                    CheckHostRecoveryBestEffort();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Steam Input lease acquisition failed.", ex);
            }
        }
    }

    /// <summary>Acquires the shared lease for a named surface owner and records that
    /// owner's claim. Acquiring is a no-op when the lease is already live, so a
    /// surface opening over another one inherits it without release/re-inject churn
    /// while still becoming an owner of it.</summary>
    /// <param name="owner">A stable identifier for the claiming surface owner; it
    /// appears in the lease log lines the device workflow reads.</param>
    public static void AcquireFor(string owner)
    {
        lock (Sync)
        {
            ClaimForCore(owner);
            Acquire();
        }
    }

    // The Settings handoff must register its name synchronously before the overlay's
    // deferred close drops the old one, but it must not perform a cold injection on
    // the UI thread. Its reconciler follows this quick claim with AcquireFor on a
    // worker when no native lease was available to inherit.
    internal static void ClaimFor(string owner)
    {
        lock (Sync)
        {
            ClaimForCore(owner);
        }
    }

    private static void ClaimForCore(string owner)
    {
        if (Owners.Add(owner))
        {
            Log.Info($"Steam Input lease claimed by {owner} ({Owners.Count} owner(s)).");
        }
    }

    /// <summary>Ends <paramref name="owner"/>'s claim and releases the shared lease
    /// only once no other owner still holds one. A surface closing must never drop
    /// the controller block out from under a surface that is still on screen
    /// (invariant 1).</summary>
    /// <param name="owner">The owner whose claim ends.</param>
    /// <param name="reason">Why the claim ends; logged for device diagnosis.</param>
    public static void ReleaseFor(string owner, string reason)
    {
        lock (Sync)
        {
            Owners.Remove(owner);
            if (Owners.Count > 0)
            {
                Log.Info($"Steam Input lease kept ({reason}; {owner} let go, still owned by " +
                         $"{string.Join(", ", Owners)}).");
                return;
            }
            ReleaseBestEffort(reason);
        }
    }

    /// <summary>Releases the shared lease and asks the gate to resume Steam's
    /// controller discovery. Never throws because it runs during shutdown.
    /// Unconditional: this is the recovery/shutdown form, so it drops every
    /// recorded owner claim as well. Surface owners use <see cref="ReleaseFor"/>.</summary>
    /// <param name="reason">Why the lease is released; logged for device diagnosis.</param>
    public static void ReleaseBestEffort(string reason)
    {
        lock (Sync)
        {
            Owners.Clear();
            if (_lease is null)
            {
                return;
            }

            var lease = _lease;
            _lease = null;
            try
            {
                // Release already performs recovery; repeating it here costs a
                // second multi-second scan of Steam's address space that the next
                // overlay open then waits on.
                var outcome = lease.Release();
                Log.Info($"Steam Input lease released ({reason}; {outcome.Status.LeaseCount} active " +
                         $"leases remain; recovery {DescribeRecovery(outcome)}).");
                if (!outcome.RecoveryRequested)
                {
                    // Blocking is lifted — Steam keeps working, it just has not
                    // been told to look for controllers again, so a pad can stay
                    // missing in Steam until it notices by itself.
                    Log.Warn($"Steam Input controller recovery did not run ({reason}): {outcome.RecoveryMessage}");
                    RaiseRecoveryWarning();
                }
            }
            catch (Exception ex)
            {
                // The SafeHandle/pipe lifetime makes a failed handshake crash-safe.
                lease.Dispose();
                Log.Error($"Steam Input lease release failed ({reason}).", ex);
            }
        }
    }

    private static string DescribeRecovery(SteamInputReleaseOutcome outcome) => outcome.Recovery switch
    {
        SteamControllerRecovery.Scheduled => "scheduled by the payload",
        SteamControllerRecovery.Completed => outcome.Rescan is { } rescan
            ? $"run by the host (scans {rescan.ScanCountBefore}→{rescan.ScanCountAfter})"
            : "run by the host",
        SteamControllerRecovery.NotRequired => "not required",
        _ => "UNAVAILABLE",
    };

    /// <summary>Probes host-side recovery at acquire time. Runs while Steam is
    /// mid-teardown of its HID thread, so a failure is only a heads-up, not proof
    /// recovery will fail: the release path performs the real recovery and is the
    /// user-facing authority. Log it; never raise the panel warning off this probe.</summary>
    private static void CheckHostRecoveryBestEffort()
    {
        try
        {
            _client?.CheckRecovery();
            Log.Info("Steam Input host recovery probe succeeded.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam Input acquire-time host recovery probe failed (release-path recovery remains authoritative): {ex.Message}");
        }
    }

    private static void RaiseRecoveryWarning()
    {
        Log.Warn("Steam Input dynamic controller-recovery resolver is unavailable; GitHub report requested.");
        RecoveryWarningRaised?.Invoke(DynamicRecoveryWarning);
    }
}
