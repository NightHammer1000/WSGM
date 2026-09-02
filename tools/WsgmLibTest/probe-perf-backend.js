// Probe: which backend the shipped TDP and frame-limit controls actually read and write —
// the CMsgSystemPerf store, or the steamos_*/gamescope_* client settings. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_backend_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = { owners: {} };
  const tokens = [
    "steamos_tdp_limit",
    "steamos_manual_gpu_clock",
    "gamescope_app_target_framerate",
    "gamescope_enable_app_target_framerate",
    "gamescope_disable_framelimit",
    "steamos_platform_performance_profile",
  ];
  for (const id of Object.keys(runtime.m)) {
    let src;
    try {
      src = String(runtime.m[id]);
    } catch {
      continue;
    }
    for (const t of tokens) {
      if (!src.includes(t)) continue;
      (out.owners[t] = out.owners[t] || []).push({ id, len: src.length });
    }
  }
  // Dump the smallest owner of each token, with the surrounding function text.
  out.snippets = {};
  for (const t of tokens) {
    const list = (out.owners[t] || []).slice().sort((a, b) => a.len - b.len);
    if (!list.length) continue;
    const src = String(runtime.m[list[0].id]);
    const i = src.indexOf(t);
    out.snippets[t] = { id: list[0].id, text: src.slice(Math.max(0, i - 700), i + 500) };
  }
  return JSON.stringify(out);
})();
