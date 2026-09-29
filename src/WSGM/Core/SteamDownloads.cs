using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>A point-in-time view of Steam's download activity, read from the running
/// client's own downloads store over the CEF bridge.</summary>
/// <param name="Active">Whether a transfer is actively running (state is not
/// <c>None</c> and the queue is not paused).</param>
/// <param name="State">Steam's raw <c>update_state</c> string (<c>None</c>,
/// <c>Downloading</c>, …) for the log surface.</param>
/// <param name="Paused">Whether the download queue is paused.</param>
/// <param name="AppId">The app currently transferring, 0 when idle.</param>
/// <param name="NetworkBytesPerSecond">Current network rate reported by Steam.</param>
public readonly record struct DownloadOverview(
    bool Active, string State, bool Paused, int AppId, long NetworkBytesPerSecond);

/// <summary>Reads the running Steam client's download overview through
/// <see cref="SteamCef"/>. <c>SteamClient.Downloads.RegisterForDownloadOverview</c>
/// fires immediately with a full snapshot (live-verified 2026-08-12, Windows client:
/// active state string is <c>Downloading</c>, idle is <c>None</c>), so a one-shot
/// subscribe/unsubscribe is a clean synchronous read with no resident script to heal
/// across Steam restarts.</summary>
public static class SteamDownloads
{
    private static readonly TimeSpan EvalTimeout = TimeSpan.FromSeconds(10);

    private const string OverviewExpression =
        """
        (() => new Promise((resolve) => {
          let reg = null, done = false;
          const finish = (v) => {
            if (done) return;
            done = true;
            try { reg && reg.unregister(); } catch (e) {}
            resolve(v);
          };
          setTimeout(() => finish(JSON.stringify({ err: 'timeout' })), 4000);
          try {
            reg = SteamClient.Downloads.RegisterForDownloadOverview((o) => finish(JSON.stringify({
              state: String(o.update_state ?? ''),
              paused: !!o.paused,
              appid: o.update_appid | 0,
              bps: Math.max(0, Math.round(o.update_network_bytes_per_second || 0)),
            })));
          } catch (e) { finish(JSON.stringify({ err: String(e) })); }
        }))()
        """;

    /// <summary>Queries the current download overview. Null means no usable answer —
    /// Steam unreachable, CEF disabled, or an unexpected payload — which callers must
    /// never read as an ACTIVE download, and must debounce before it releases a hold
    /// (see <c>KeepAwakeService.NextDownloadHold</c>): counting an unusable answer as inactive is what
    /// stops a closed or dead Steam pinning the device awake for the whole session.</summary>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public static async Task<DownloadOverview?> QueryAsync(CancellationToken cancellationToken = default)
    {
        var result = await SteamUiTransportSession.EvaluateAsync(OverviewExpression, EvalTimeout, cancellationToken)
            .ConfigureAwait(false);
        return result.Reachable ? Parse(result.Value) : null;
    }

    /// <summary>Parses the JS payload into an overview; null for error payloads and
    /// malformed JSON. Kept pure for unit tests.</summary>
    internal static DownloadOverview? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("err", out _))
            {
                return null;
            }
            var state = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? "" : "";
            var paused = root.TryGetProperty("paused", out var p) && p.GetBoolean();
            var appId = root.TryGetProperty("appid", out var a) && a.ValueKind == JsonValueKind.Number
                ? a.GetInt32() : 0;
            var bps = root.TryGetProperty("bps", out var b) && b.ValueKind == JsonValueKind.Number
                ? b.GetInt64() : 0;
            return new DownloadOverview(IsActive(state, paused), state, paused, appId, bps);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The active-transfer rule (live-verified): any state other than
    /// <c>None</c> counts while the queue is not paused, so transitional states keep
    /// the hold rather than flapping it.</summary>
    internal static bool IsActive(string state, bool paused)
        => state.Length > 0 && state != "None" && !paused;

    /// <summary>Updates the last known download activity without turning a transient
    /// CEF failure into a false completion. A confirmed dead Steam process is idle;
    /// a live but temporarily unreachable client leaves the prior answer intact.</summary>
    /// <param name="currentActive">The last usable activity answer.</param>
    /// <param name="steamAlive">Whether the shared lifecycle monitor sees Steam.</param>
    /// <param name="overview">The latest usable CEF snapshot, or null when unavailable.</param>
    /// <returns>The activity state consumers should publish.</returns>
    internal static bool ResolveActivity(
        bool currentActive,
        bool steamAlive,
        DownloadOverview? overview)
    {
        if (!steamAlive)
        {
            return false;
        }
        return overview?.Active ?? currentActive;
    }
}
