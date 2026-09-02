(() => {
  let req;
  window.webpackChunksteamui.push([
    ["wsgm_bright2_" + Date.now()],
    {},
    (r) => {
      req = r;
    },
  ]);
  const store = req("59547").mG.Get();
  const s = store.m_msgSettings || {};
  return JSON.stringify({
    is_display_brightness_available: s.is_display_brightness_available,
    display_brightness_overdrive_hdr_split: s.display_brightness_overdrive_hdr_split,
    m_flDisplayBrightness: store.m_flDisplayBrightness,
    settingsKeyCount: Object.keys(s).length,
    hasSetBrightness: typeof window.SteamClient?.System?.Display?.SetBrightness === "function",
    hasRegisterBrightness:
      typeof window.SteamClient?.System?.Display?.RegisterForBrightnessChanges === "function",
  });
})();
