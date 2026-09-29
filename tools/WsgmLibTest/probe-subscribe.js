// Does the call that crashed the Performance tab now succeed? subscribe() throws
// "subscription not allowlisted" for a patch id missing from config.allowed, and it throws during
// render, which is why the whole tab went blank rather than one row disappearing.
(() => {
  const b = window.__wsgmSteamUi_v1_28d7c54a;
  if (!b) return JSON.stringify({ error: "bridge absent" });
  const out = {};
  for (const id of [
    "wsgm.native-qam.resolution",
    "wsgm.native-qam.vrr",
    "wsgm.native-qam.frame-limit",
  ]) {
    try {
      const off = b.subscribe(id, () => {});
      off();
      out[id] = "ok";
    } catch (e) {
      out[id] = String(e && e.message ? e.message : e);
    }
  }
  return JSON.stringify(out);
})();
