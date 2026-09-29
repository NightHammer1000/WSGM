using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>How a frame limit relates to the panel's refresh rate.</summary>
/// <remarks>
/// A user setting rather than a fixed policy, because the right answer differs per device and per
/// tolerance for mode changes. A mode change is not free: an exclusive-fullscreen title can hitch,
/// minimize, or drop out across one.
/// </remarks>
public enum FrameLimitStrategy
{
    /// <summary>
    /// Cap frames and never touch the refresh rate. The default, and the right answer wherever
    /// variable refresh covers the range, because it changes no display state at all.
    /// </summary>
    FrameLimitOnly,

    /// <summary>
    /// Cap frames, and switch refresh only among the panel's own advertised modes.
    /// </summary>
    NativeModes,

    /// <summary>
    /// Cap frames, and pick the lowest driver-accepted mode that shows every frame at least twice —
    /// including modes synthesized beyond what the panel advertises. A doubled cadence is what lets
    /// adaptive sync's low-framerate compensation smooth the presentation; holding a 30 FPS cap at
    /// 30 Hz keeps that machinery out of reach. When no doubled multiple exists the lowest exact
    /// multiple still wins, and failing that the lowest mode that can present the cap.
    /// </summary>
    FrameDoubling,
}

/// <summary>
/// Chooses the refresh rate that goes with a frame cap, and the caps worth offering.
/// </summary>
/// <remarks>
/// In SteamOS the compositor resolves this pairing and the UI only displays the result. WSGM is the
/// backend on Windows, so the pairing is decided here and the refresh row shows what was chosen.
/// <para>
/// Every rate handed in must already have been discovered at runtime and accepted by the driver.
/// Nothing here may be hardcoded: the reference Claw accepts 30/48/60/75/100/120 while advertising
/// only 60 and 120, and a panel without variable refresh will likely accept only what it advertises.
/// </para>
/// </remarks>
public static class FrameLimitPairing
{
    /// <summary>Lowest cap the slider offers, under every strategy.</summary>
    /// <remarks>
    /// Higher than <see cref="MinimumCap"/> on purpose. The cap is a free number rather than a
    /// cadence stop, so the only question left is what is worth playing at: below 30 FPS is not.
    /// </remarks>
    private const int UncoupledFloor = 30;

    /// <summary>Lowest cap worth offering at all.</summary>
    private const int MinimumCap = 15;

    /// <summary>
    /// The refresh rate to apply alongside a frame cap.
    /// </summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    /// <param name="capFps">The frame cap, or zero for uncapped.</param>
    /// <param name="nativeHz">Refresh rates the panel itself advertises.</param>
    /// <param name="acceptedHz">Every rate the driver accepted, including synthesized ones.</param>
    /// <returns>
    /// The rate to set, or <see langword="null"/> when the refresh rate must be left alone — which
    /// is always the answer under <see cref="FrameLimitStrategy.FrameLimitOnly"/>, and the answer
    /// anywhere else when no available mode is an exact multiple of the cap.
    /// </returns>
    public static int? SelectRefreshHz(
        FrameLimitStrategy strategy,
        int capFps,
        IReadOnlyList<int> nativeHz,
        IReadOnlyList<int> acceptedHz
    )
    {
        if (strategy is FrameLimitStrategy.FrameLimitOnly || capFps < MinimumCap)
        {
            return null;
        }

        IReadOnlyList<int> candidates = strategy switch
        {
            FrameLimitStrategy.NativeModes => nativeHz,
            FrameLimitStrategy.FrameDoubling => acceptedHz,
            _ => [],
        };

        // FrameDoubling wants each frame shown at least twice: a doubled cadence is the one LFC and
        // frame-hold smoothing can work with, where an exact 1:1 mode (30 FPS at 30 Hz) presents a
        // low-refresh flickery image the panel is honest about but the user asked to avoid
        // (maintainer-directed 2026-09-02). Still the LOWEST such mode, because refresh rate is a
        // power cost: 60 Hz carries a 30 FPS cap as smoothly as 120 Hz and costs less.
        if (strategy is FrameLimitStrategy.FrameDoubling)
        {
            int? doubled = candidates
                .Where(hz => hz % capFps == 0 && hz >= capFps * 2)
                .OrderBy(hz => hz)
                .Select(hz => (int?)hz)
                .FirstOrDefault();
            if (doubled is not null)
            {
                return doubled;
            }
        }

        // The lowest exact multiple, because refresh rate is a power cost: a 30 FPS cap held at
        // 30 Hz costs meaningfully less than the same cap held at 120 Hz. Under NativeModes this is
        // the whole policy; under FrameDoubling it is the fallback when no doubled mode exists.
        int? exact = candidates
            .Where(hz => hz >= capFps && hz % capFps == 0)
            .OrderBy(hz => hz)
            .Select(hz => (int?)hz)
            .FirstOrDefault();
        if (exact is not null)
        {
            return exact;
        }

        // No exact cadence, and the cap is still a number the user chose. SteamOS's unified slider
        // names a refresh rate for EVERY cap — it does not go blank between the clean multiples —
        // so the fallback is the lowest mode that can still present the cap without dropping
        // frames. Judder against a non-integer cadence is the honest cost of an arbitrary cap; a
        // panel left at 120 Hz for a 47 FPS cap costs power for nothing.
        return candidates
            .Where(hz => hz >= capFps)
            .OrderBy(hz => hz)
            .Select(hz => (int?)hz)
            .FirstOrDefault();
    }

    /// <summary>The lowest and highest frame cap the panel can be asked for.</summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    /// <param name="nativeHz">Refresh rates the panel itself advertises.</param>
    /// <param name="acceptedHz">Every rate the driver accepted, including synthesized ones.</param>
    /// <returns>The inclusive range, or null when the panel cannot hold a cap worth offering.</returns>
    /// <remarks>
    /// A RANGE, not a set of stops, under every strategy. SteamOS's own Frame Limit row is one
    /// continuous slider bookended by the panel's limits — verified against a Steam Deck showing
    /// "60 FPS (60 Hz)" between bookends 10 and 60 — and the pairing is what snaps, not the cap:
    /// the user picks any number and <see cref="SelectRefreshHz"/> answers with the mode that
    /// presents it. Offering only cadence-exact stops made the coupled strategies feel like a
    /// different control from the uncoupled one, which is precisely what Valve merged away.
    /// </remarks>
    public static (int Minimum, int Maximum)? FrameLimitRange(
        FrameLimitStrategy strategy,
        IReadOnlyList<int> nativeHz,
        IReadOnlyList<int> acceptedHz
    )
    {
        IReadOnlyList<int> available = strategy switch
        {
            FrameLimitStrategy.NativeModes => nativeHz,
            _ => acceptedHz,
        };

        int ceiling = available.Count is 0 ? 0 : available.Max();
        return ceiling < UncoupledFloor ? null : (UncoupledFloor, ceiling);
    }

    /// <summary>
    /// The frame caps worth offering under a strategy.
    /// </summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    /// <param name="nativeHz">Refresh rates the panel itself advertises.</param>
    /// <param name="acceptedHz">Every rate the driver accepted, including synthesized ones.</param>
    /// <returns>
    /// The caps, ascending, with zero first for "off". Under a coupled strategy only caps that have
    /// an exact-cadence mode behind them appear, so every stop on the slider is one the backend can
    /// honour exactly.
    /// </returns>
    public static IReadOnlyList<int> FrameLimitOptions(
        FrameLimitStrategy strategy,
        IReadOnlyList<int> nativeHz,
        IReadOnlyList<int> acceptedHz
    )
    {
        if (FrameLimitRange(strategy, nativeHz, acceptedHz) is not { } range)
        {
            return [0];
        }

        // Every integer in the range, under EVERY strategy, with zero first for off. There are no
        // cadence stops any more: the cap is free and SelectRefreshHz answers it with a mode, which
        // is how SteamOS's own unified Frame Limit row behaves. Callers that want the two ends
        // should ask FrameLimitRange rather than reading them back off this list.
        List<int> caps = new(range.Maximum - range.Minimum + 2) { 0 };
        for (int cap = range.Minimum; cap <= range.Maximum; cap++)
        {
            caps.Add(cap);
        }

        return caps;
    }

    /// <summary>
    /// Whether the refresh-rate control should be offered to the user.
    /// </summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    /// <returns><see langword="true"/> when the user owns the refresh rate.</returns>
    /// <remarks>
    /// Only under <see cref="FrameLimitStrategy.FrameLimitOnly"/>. Under the coupled strategies the
    /// pairing policy owns the refresh rate, and a second control would fight it — the user would
    /// set a rate and watch the next cap change overwrite it.
    /// </remarks>
    public static bool RefreshRateIsUserOwned(FrameLimitStrategy strategy) =>
        strategy is FrameLimitStrategy.FrameLimitOnly;
}
