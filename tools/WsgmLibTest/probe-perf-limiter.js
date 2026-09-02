// Probe: which client settings gate the perf tab (the legacy frame-limit-only slider),
// and the TDP component module. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_limiter_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  // Client settings the perf tab consults.
  try {
    const dev = runtime("33867");
    // rV is the non-function export; the store itself is closed over. Reach it via a hook's
    // observable target instead: SteamClient settings snapshot.
    out.settingsKeys = [];
    const seen = new Set();
    for (const id of Object.keys(runtime.m)) {
      let src;
      try {
        src = String(runtime.m[id]);
      } catch {
        continue;
      }
      const re =
        /["']([a-z0-9_]*(?:perf|frame|fps|deck|tdp|refresh|gpu_clock|legacy)[a-z0-9_]*)["']/g;
      let m;
      while ((m = re.exec(src)) !== null) {
        if (!seen.has(m[1]) && m[1].length > 3) {
          seen.add(m[1]);
          out.settingsKeys.push(m[1]);
        }
      }
      if (out.settingsKeys.length > 400) break;
    }
  } catch (e) {
    out.devErr = String(e);
  }
  try {
    out.tdpModule = String(runtime.m["38747"]);
  } catch (e) {
    out.tdpErr = String(e);
  }
  return JSON.stringify(out);
})();
