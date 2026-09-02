(() => {
  const b = window.__wsgmSteamUi_v1_28d7c54a;
  const out = {};
  for (const kind of [
    "valveFrameLimit",
    "valveOverlayLevel",
    "valveProfileHeader",
    "resolution",
    "valveRefreshRate",
  ]) {
    try {
      out[kind] = b.nativeComponents.install(kind).ok;
    } catch (e) {
      out[kind] = String(e);
    }
  }
  return JSON.stringify(out);
})();
