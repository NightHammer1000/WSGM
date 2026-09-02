// Probe: does a Bluetooth store exist with live state on Windows, and what backend do its
// pair/connect operations call? Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_bt_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  const safe = (label, f) => {
    try {
      out[label] = f();
    } catch (e) {
      out[label] = "ERR " + String(e).slice(0, 200);
    }
  };

  // Which modules mention a bluetooth backend call at all?
  safe("modules_calling_bluetooth", () => {
    const hits = [];
    for (const id of Object.keys(runtime.m)) {
      let src;
      try {
        src = String(runtime.m[id]);
      } catch {
        continue;
      }
      if (/Bluetooth/.test(src) && /SteamClient|\.Get\(\)/.test(src)) {
        const calls = [...new Set(src.match(/SteamClient\.[A-Za-z.]+/g) || [])].filter((c) =>
          /Bluetooth|System/.test(c),
        );
        if (calls.length) hits.push({ id, len: src.length, calls: calls.slice(0, 12) });
      }
    }
    return hits.sort((a, b) => a.len - b.len).slice(0, 8);
  });

  // The store the QAM row uses: 66943 imported u = the bluetooth store module.
  for (const id of ["66943", "18931", "25467"]) {
    safe("exports_" + id, () => {
      const m = runtime(id);
      const r = {};
      for (const k of Object.keys(m)) {
        const v = m[k];
        r[k] = typeof v === "function" ? String(v).slice(0, 150) : typeof v;
      }
      return r;
    });
  }
  return JSON.stringify(out);
})();
