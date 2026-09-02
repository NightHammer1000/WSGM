(async () => {
  const b = window.__wsgmSteamUi_v1_28d7c54a;
  const out = { gate: null };
  try {
    out.gate = b.steamOsManager.status();
  } catch (e) {
    out.gateErr = String(e);
  }
  // Feed a TDP state and read the merged Manager answer plus the query invalidation.
  b.deliver({
    version: 1,
    contextGeneration: 1,
    documentGeneration: 1,
    type: "state",
    patchId: "wsgm.native-qam.tdp",
    payload: {
      available: true,
      minimumWatts: 8,
      maximumWatts: 30,
      stepWatts: 1,
      desiredWatts: 20,
      observedWatts: 20,
      progress: "idle",
      statusText: "",
    },
  });
  let req;
  window.webpackChunksteamui.push([
    ["wsgm_rpc_" + Date.now()],
    {},
    (r) => {
      req = r;
    },
  ]);
  const manager = Object.values(req("90389")).find(
    (v) =>
      v &&
      typeof v === "object" &&
      typeof v.GetState === "function" &&
      typeof v.RefreshScreenReaderAutoLocale === "function",
  );
  const r = await manager.GetState({});
  out.merged = r.Body().toObject().state;
  out.settingsApi = typeof window.SteamClient?.Settings?.RegisterForSettingsChanges;
  out.after = b.steamOsManager.status();
  return JSON.stringify(out);
})();
