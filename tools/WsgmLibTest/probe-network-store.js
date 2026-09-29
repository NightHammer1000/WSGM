// Probe: the network management store behind Steam's Internet settings page and the QAM
// Wi-Fi row — its methods, its live state, and what gates networkManagementAvailable.
// Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_net_" + Date.now()],
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
  const mod = runtime("77347");

  // OQ looked like the store singleton in the Wi-Fi row: A.OQ.Get().SetWifiEnabled(e)
  safe("OQ_type", () => typeof mod.OQ);
  safe("store_methods", () => {
    const s = mod.OQ.Get();
    return Object.getOwnPropertyNames(Object.getPrototypeOf(s));
  });
  safe("store_fields", () => Object.keys(mod.OQ.Get()));
  safe("networkManagementAvailable", () => mod.OQ.Get().networkManagementAvailable);

  // Anything on the store that looks like device / access-point state.
  safe("store_snapshot", () => {
    const s = mod.OQ.Get();
    const r = {};
    for (const k of Object.keys(s)) {
      const v = s[k];
      if (v === null || ["boolean", "number", "string"].includes(typeof v)) r[k] = v;
      else if (Array.isArray(v)) r[k] = "array[" + v.length + "]";
      else r[k] = typeof v;
    }
    return r;
  });

  // The exported hooks, so we can see what the settings page reads.
  safe("exports", () => {
    const r = {};
    for (const k of Object.keys(mod)) {
      const v = mod[k];
      r[k] = typeof v === "function" ? String(v).slice(0, 180) : typeof v;
    }
    return r;
  });

  safe("SteamClient_Network", () => Object.keys(window.SteamClient?.System?.Network || {}));
  safe("SteamClient_Network_Device", () =>
    Object.keys(window.SteamClient?.System?.Network?.Device || {}),
  );
  return JSON.stringify(out);
})();
