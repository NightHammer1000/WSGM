// Register the component kinds and report what the host says, so "no rows" can be attributed to a
// specific step rather than guessed at.
(() => {
  const b = window.__wsgmSteamUi_v1_28d7c54a;
  if (!b) return JSON.stringify({ error: "bridge absent" });
  const out = { install: {} };
  for (const kind of [
    "tdp",
    "autoTdp",
    "frameLimit",
    "overlayLevel",
    "controllerTarget",
    "resolution",
    "valveVrr",
    "valveProfileHeader",
    "valveReset",
  ]) {
    try {
      out.install[kind] = b.nativeComponents.install(kind);
    } catch (e) {
      out.install[kind] = String(e);
    }
  }
  out.status = b.nativeComponents.status();
  return JSON.stringify(out);
})();
