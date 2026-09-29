// Probe: the live contents of the perf store on this Windows client — does the backend
// populate limits/state/global/per-app, and what does the availability gate resolve to?
// Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_state_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  const plain = (msg) => {
    if (!msg) return null;
    try {
      if (typeof msg.toObject === "function") return msg.toObject();
    } catch {}
    try {
      return JSON.parse(JSON.stringify(msg));
    } catch (e) {
      return "unserializable " + String(e);
    }
  };
  try {
    const mod = runtime("74514");
    const store = mod.Hn.Get();
    out.nCurrentGameID = String(store.nCurrentGameID);
    out.nActiveProfileGameID = String(store.nActiveProfileGameID);
    out.nBatteryTemperatureC = store.nBatteryTemperatureC;
    out.msgState = plain(store.msgState);
    out.msgLimits = plain(store.msgLimits);
    out.msgSettingsGlobal = plain(store.msgSettingsGlobal);
    out.msgSettingsPerApp = plain(store.msgSettingsPerApp);
    out.msgDiagnosticInfo = plain(store.msgDiagnosticInfo);
  } catch (e) {
    out.storeError = String(e);
  }
  // The developer-settings hook that owns force_deck_perf_tab.
  try {
    const dev = runtime("33867");
    out.exports33867 = Object.keys(dev);
    for (const k of Object.keys(dev)) {
      const v = dev[k];
      out["fn_" + k] = typeof v === "function" ? String(v).slice(0, 400) : typeof v;
    }
  } catch (e) {
    out.devError = String(e);
  }
  return JSON.stringify(out);
})();
