using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>
/// Applies the refresh rate that goes with the frame cap in force, under the user's strategy.
/// </summary>
/// <remarks>
/// The pairing decision itself is <see cref="FrameLimitPairing"/> and stays pure; this owns the
/// parts that touch the machine — discovering what the display accepts, caching that, applying a
/// rate, and putting the original back.
/// <para>
/// Discovery is cached for the session because it is not free: every candidate rate costs a
/// `CDS_TEST` round trip through the driver, and a cap change is a user-facing action that should
/// not stall behind a dozen of them. There is no display-change invalidation: the internal panel's
/// modes do not change within a session, and a dock/undock already goes through the
/// display-profile path.
/// </para>
/// </remarks>
internal sealed class RefreshRatePairingService
{
    private readonly Func<IReadOnlyList<int>> _readAcceptedRates;
    private readonly Func<IReadOnlyList<int>> _readAdvertisedRates;
    private readonly Func<int, bool> _applyRate;
    private readonly Func<int?> _readCurrentRate;
    private readonly object _gate = new();

    private IReadOnlyList<int>? _accepted;
    private IReadOnlyList<int>? _advertised;
    private int? _originalRate;
    private FrameLimitStrategy _strategy = FrameLimitStrategy.FrameLimitOnly;

    /// <summary>Creates the service over the real display.</summary>
    internal RefreshRatePairingService()
        : this(
            DisplayProfiles.EnumerateAcceptedRefreshRates,
            DisplayProfiles.ReadAdvertisedRefreshRates,
            DisplayProfiles.TryApplyTransientRefreshRate,
            DisplayProfiles.ReadCurrentRefreshRate)
    {
    }

    /// <summary>Creates the service over supplied display operations, for tests.</summary>
    /// <param name="readAcceptedRates">Every rate the driver accepts.</param>
    /// <param name="readAdvertisedRates">Rates the panel itself advertises.</param>
    /// <param name="applyRate">Applies a rate, returning whether it took.</param>
    /// <param name="readCurrentRate">Reads the rate in force.</param>
    internal RefreshRatePairingService(
        Func<IReadOnlyList<int>> readAcceptedRates,
        Func<IReadOnlyList<int>> readAdvertisedRates,
        Func<int, bool> applyRate,
        Func<int?> readCurrentRate
    )
    {
        _readAcceptedRates = readAcceptedRates;
        _readAdvertisedRates = readAdvertisedRates;
        _applyRate = applyRate;
        _readCurrentRate = readCurrentRate;
    }

    /// <summary>
    /// Adopts a strategy, restoring the display first when the new one no longer owns it.
    /// </summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    internal void SetStrategy(FrameLimitStrategy strategy)
    {
        bool restore;
        lock (_gate)
        {
            if (_strategy == strategy)
            {
                return;
            }

            // Switching to cap-only hands the refresh rate back to the user, so anything this
            // service moved has to go back before it stops being responsible for it.
            restore = strategy is FrameLimitStrategy.FrameLimitOnly;
            _strategy = strategy;
        }

        Log.Info($"Frame limit strategy: {strategy}.");
        if (restore)
        {
            Restore();
        }
    }

    /// <summary>The rates the driver accepts, discovered once and shared by every consumer.</summary>
    /// <returns>Accepted rates, ascending. Empty when the display cannot be read.</returns>
    internal IReadOnlyList<int> AcceptedRates()
    {
        IReadOnlyList<int>? accepted;
        lock (_gate)
        {
            accepted = _accepted;
        }

        // Discovery runs outside the lock because each candidate rate costs a driver round trip,
        // and holding the gate across that would block every caller behind it.
        accepted ??= _readAcceptedRates();
        lock (_gate)
        {
            _accepted ??= accepted;
        }

        return accepted;
    }

    /// <summary>Applies a refresh rate the user chose by hand.</summary>
    /// <param name="refreshHz">The chosen rate.</param>
    /// <param name="capFps">The frame cap in force; zero or negative means uncapped.</param>
    /// <returns>Whether the display is now at that rate.</returns>
    /// <remarks>
    /// Checked against the rates discovery accepted, not passed straight to the driver: the value
    /// arrives from injected JavaScript, and a rate the panel cannot show is a black screen. A
    /// manual write is user-owned, so no original is captured and nothing restores it later.
    /// </remarks>
    internal bool TryApplyManual(int refreshHz, int capFps)
    {
        FrameLimitStrategy strategy;
        lock (_gate)
        {
            strategy = _strategy;
        }

        // A pairing strategy owns the refresh rate only while there is a CAP for it to own one
        // against. With the frame limit off there is no cadence to pair to and the unified row's
        // slider becomes the rate itself, so refusing there rejected the very writes that row
        // exists to make — "Manual refresh rate 72 Hz refused" against a strategy that was, at that
        // moment, pairing nothing.
        if (capFps > 0 && !FrameLimitPairing.RefreshRateIsUserOwned(strategy))
        {
            Log.Warn(
                $"Manual refresh rate {refreshHz} Hz refused: the "
                + $"{strategy} strategy owns the refresh rate while a "
                + $"{capFps} FPS cap is set.");
            return false;
        }

        IReadOnlyList<int> accepted = AcceptedRates();
        if (!accepted.Contains(refreshHz))
        {
            Log.Warn(
                $"Manual refresh rate {refreshHz} Hz refused: accepted rates are "
                + $"[{string.Join(",", accepted)}].");
            return false;
        }

        return _applyRate(refreshHz);
    }

    /// <summary>The frame caps worth offering under the current strategy.</summary>
    /// <returns>Caps, ascending, with zero first for uncapped.</returns>
    internal IReadOnlyList<int> FrameLimitOptions()
    {
        (FrameLimitStrategy strategy, IReadOnlyList<int> advertised, IReadOnlyList<int> accepted) =
            Snapshot();
        return FrameLimitPairing.FrameLimitOptions(strategy, advertised, accepted);
    }

    /// <summary>The refresh rate a cap would be presented at, without applying anything.</summary>
    /// <param name="capFps">The frame cap being considered.</param>
    /// <returns>The paired rate, or null when the refresh rate would be left alone.</returns>
    /// <remarks>
    /// The read-only half of <see cref="ApplyForCap"/>, for labelling a cap the user is still
    /// dragging through. Same policy, same snapshot, no display call.
    /// </remarks>
    internal int? SelectRefreshHz(int capFps)
    {
        (FrameLimitStrategy strategy, IReadOnlyList<int> advertised, IReadOnlyList<int> accepted) =
            Snapshot();
        return FrameLimitPairing.SelectRefreshHz(strategy, capFps, advertised, accepted);
    }

    /// <summary>
    /// Applies the refresh rate paired with a frame cap.
    /// </summary>
    /// <param name="capFps">The frame cap in force; zero or negative means uncapped.</param>
    /// <returns>The rate applied, or null when the refresh rate was left alone.</returns>
    internal int? ApplyForCap(int capFps)
    {
        (FrameLimitStrategy strategy, IReadOnlyList<int> advertised, IReadOnlyList<int> accepted) =
            Snapshot();
        if (strategy is FrameLimitStrategy.FrameLimitOnly)
        {
            return null;
        }

        int? target = FrameLimitPairing.SelectRefreshHz(strategy, capFps, advertised, accepted);
        if (target is not { } rate)
        {
            Log.Info(
                $"Frame limit {capFps}: no exact-cadence mode among [{string.Join(",", accepted)}]; "
                + "refresh left alone.");
            return null;
        }

        CaptureOriginal();
        return _applyRate(rate) ? rate : null;
    }

    /// <summary>
    /// Puts back the refresh rate found before this service moved it.
    /// </summary>
    /// <returns><see langword="true"/> when nothing was left changed.</returns>
    /// <remarks>
    /// Applying is transient rather than persisted, so an abrupt exit already self-heals. This
    /// exists for the ordinary case, where leaving the desktop at 48 Hz after a game closes would
    /// be a change the user never asked for and would have to hunt for.
    /// </remarks>
    internal bool Restore()
    {
        int? original;
        lock (_gate)
        {
            original = _originalRate;
        }

        if (original is not { } rate)
        {
            return true;
        }

        Log.Info($"Frame limit strategy released the display; restoring {rate} Hz.");
        bool restored = _applyRate(rate);
        if (restored)
        {
            lock (_gate)
            {
                if (_originalRate == rate)
                {
                    _originalRate = null;
                }
            }
        }
        else
        {
            Log.Warn($"Frame limit strategy could not restore {rate} Hz; the snapshot was retained.");
        }
        return restored;
    }

    private void CaptureOriginal()
    {
        lock (_gate)
        {
            if (_originalRate is not null)
            {
                return;
            }
        }

        // Read outside the lock: it crosses into the display driver, and the only cost of a race
        // here is capturing the same rate twice.
        int? current = _readCurrentRate();
        lock (_gate)
        {
            _originalRate ??= current;
        }
    }

    private (FrameLimitStrategy, IReadOnlyList<int>, IReadOnlyList<int>) Snapshot()
    {
        FrameLimitStrategy strategy;
        IReadOnlyList<int>? advertised;
        lock (_gate)
        {
            strategy = _strategy;
            advertised = _advertised;
        }

        IReadOnlyList<int> accepted = AcceptedRates();
        advertised ??= _readAdvertisedRates();
        lock (_gate)
        {
            _advertised ??= advertised;
        }

        return (strategy, advertised, accepted);
    }
}
