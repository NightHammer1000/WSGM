// Probe: what the rf() platform predicate tests (it gates networkManagementAvailable), and
// whether the Bluetooth store carries live state on Windows the way the network one does.
// Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_rfbt_" + Date.now()],
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

  // 72476 is the platform module referenced as E/h in the QAM and network modules.
  safe("platform_exports", () => Object.keys(runtime("72476")));
  safe("rf_source", () => String(runtime("72476").rf).slice(0, 400));
  safe("rf_value", () => runtime("72476").rf());
  safe("TS_flags", () => {
    const ts = runtime("72476").TS;
    const r = {};
    for (const k of Object.keys(ts || {})) r[k] = ts[k];
    return r;
  });
  safe("Xk_source", () => String(runtime("72476").Xk).slice(0, 300));

  // Bluetooth: 18931 held the pairing UI tokens; find its store.
  safe("bt_18931_exports", () => {
    const m = runtime("18931");
    const r = {};
    for (const k of Object.keys(m)) {
      const v = m[k];
      r[k] = typeof v === "function" ? String(v).slice(0, 140) : typeof v;
    }
    return r;
  });
  safe("SteamClient_keys_system", () => Object.keys(window.SteamClient?.System || {}));
  return JSON.stringify(out);
})();
