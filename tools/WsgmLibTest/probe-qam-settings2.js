// Probe: the Quick Settings tab composition (module 79476) and where Wi-Fi / network /
// brightness / audio rows live. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_qamset2_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  try {
    out.tab = String(runtime.m["79476"]);
  } catch (e) {
    out.tabErr = String(e);
  }
  // Where do wifi / network / brightness / volume rows live?
  const probes = ["Wifi", "WiFi", "wifi", "Network_", "Brightness", "Volume", "AirplaneMode"];
  out.hits = {};
  for (const id of Object.keys(runtime.m)) {
    let src;
    try {
      src = String(runtime.m[id]);
    } catch {
      continue;
    }
    for (const p of probes) {
      if (!src.includes(p)) continue;
      (out.hits[p] = out.hits[p] || []).push({ id, len: src.length });
    }
  }
  for (const p of Object.keys(out.hits)) {
    out.hits[p] = out.hits[p].sort((a, b) => a.len - b.len).slice(0, 6);
  }
  return JSON.stringify(out);
})();
