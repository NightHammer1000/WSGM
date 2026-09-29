// Probe: does the Windows steamui bundle still carry SteamOS's own per-app performance
// settings panel, and what gates it? Read-only: enumerates module factories and the
// SteamClient surface, mutates nothing.
(() => {
  const out = { ok: true };
  let runtime;
  try {
    window.webpackChunksteamui.push([
      ["wsgm_probe_perf_" + Date.now()],
      {},
      (r) => {
        runtime = r;
      },
    ]);
  } catch (e) {
    return JSON.stringify({ ok: false, error: String(e) });
  }
  if (!runtime || !runtime.m) return JSON.stringify({ ok: false, error: "no runtime" });

  const ids = Object.keys(runtime.m);
  out.moduleCount = ids.length;

  // Tokens that only the real performance-settings panel would carry.
  const tokens = [
    "per_app_profile",
    "PerAppProfile",
    "UsePerAppProfile",
    "use_per_app_profile",
    "half_rate_shading",
    "HalfRateShading",
    "AllowTearing",
    "allow_tearing",
    "SetPerAppFrameLimit",
    "FrameLimit",
    "perf_overlay_level",
    "PerfOverlayLevel",
    "SetPerAppGPUPerformanceLevel",
    "gpu_performance_level",
    "TDPLimit",
    "tdp_limit",
    "Settings_SteamDeck",
    "PerformanceSettings",
    "PerfSettings",
    "scaling_filter",
    "SetPerAppScalingFilter",
    "composite_debug",
    "is_steam_deck",
    "IsSteamDeck",
    "BIsSteamDeck",
    "SteamDeckDevice",
    "device_supports",
  ];

  const hits = {};
  for (const token of tokens) hits[token] = [];
  for (const id of ids) {
    let source;
    try {
      source = String(runtime.m[id]);
    } catch {
      continue;
    }
    for (const token of tokens) {
      if (source.includes(token) && hits[token].length < 6) {
        hits[token].push({ id, len: source.length });
      }
    }
  }
  out.tokenHits = {};
  for (const token of tokens) {
    out.tokenHits[token] = { count: hits[token].length, modules: hits[token] };
  }

  // What the SteamClient side exposes on Windows.
  const describe = (obj, depth) => {
    if (!obj || typeof obj !== "object" || depth > 1) return typeof obj;
    const r = {};
    for (const k of Object.keys(obj)) {
      const v = obj[k];
      r[k] = typeof v === "function" ? "fn" : depth < 1 ? describe(v, depth + 1) : typeof v;
    }
    return r;
  };
  try {
    out.steamClientKeys = Object.keys(window.SteamClient || {});
    out.systemPerf = describe((window.SteamClient || {}).System, 0);
  } catch (e) {
    out.steamClientError = String(e);
  }

  return JSON.stringify(out);
})();
