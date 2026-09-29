// Probe: resolve the Quick Settings availability gates by reading the exported functions
// directly rather than searching minified text. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_gates2_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  const fn = (label, id, name) => {
    try {
      const mod = runtime(id);
      const v = mod[name];
      out[label] = typeof v === "function" ? String(v).slice(0, 900) : typeof v;
    } catch (e) {
      out[label] = "ERR " + e;
    }
  };
  fn("wifi_available_77347_Ev", "77347", "Ev");
  fn("wifi_row_89600_cV", "89600", "cV");
  fn("bt_available_25467_Iz", "25467", "Iz");
  fn("bt_row_66943_ty", "66943", "ty");
  fn("audio_available_1409_In", "1409", "In");
  fn("brightness_available_59547_zx", "59547", "zx");
  fn("brightness_row_83571_PS", "83571", "PS");
  fn("brightness_row_83571_zt", "83571", "zt");
  fn("section_17386_DP", "17386", "DP");
  fn("section_17386_vB", "17386", "vB");
  fn("nightmode_supported_96555_hb", "96555", "hb");
  return JSON.stringify(out);
})();
