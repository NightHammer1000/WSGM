(() => {
  let req;
  window.webpackChunksteamui.push([
    ["wsgm_gate_" + Date.now()],
    {},
    (r) => {
      req = r;
    },
  ]);
  const platform = req("72476");
  const store = req("1409").F5;
  const out = {
    ON_FRAME: platform?.TS?.ON_FRAME,
    IS_STEAMOS: platform?.TS?.IS_STEAMOS,
    IN_GAMESCOPE: platform?.TS?.IN_GAMESCOPE,
    bAvailableBefore: store?.bAvailable,
  };
  const dev = (id, name, o, i) => ({
    id,
    sName: name,
    bHasOutput: o,
    bHasInput: i,
    currentConfig: {},
    availableConfigs: [],
    eConnectorType: 0,
    eBus: 0,
    bSupportsHdmiCec: false,
    bHdmiCecEnabled: false,
    bHdmiCecActive: false,
  });
  try {
    store.m_bAvailable = true;
    store.RegisterOrUpdateDevice(dev(9101, "WSGM Gate Probe", true, false));
    out.bAvailableAfter = store.bAvailable;
    // The Quick Settings audio section renders when !IN_VR && bAvailable (non-VR desktop client).
    out.audioSectionGateWouldOpen = out.bAvailableAfter === true && platform?.TS?.ON_FRAME !== true;
  } catch (e) {
    out.error = String(e).slice(0, 200);
  }
  try {
    store.m_mapAudioDevices.delete(9101);
    store.m_bAvailable = false;
    out.restored = store.bAvailable === false && store.m_mapAudioDevices.size === 0;
  } catch (e) {
    out.restoreError = String(e).slice(0, 200);
  }
  return JSON.stringify(out);
})();
