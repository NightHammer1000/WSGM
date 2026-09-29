// Reads the display store's brightness value, its change registration, and tests what the native
// SetBrightness answers — a returned promise's outcome tells whether a backend is behind it.
(() => {
  const chunk = window.webpackChunksteamui;
  if (!chunk) return "no webpackChunksteamui";
  let runtime = null;
  chunk.push([
    [Symbol("wsgm-brightness2-probe")],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  if (!runtime) return "no runtime";
  const store = runtime("59547")?.mG?.Get?.();
  const out = { flDisplayBrightness: store?.m_flDisplayBrightness };
  // The store class source around brightness: who sets m_flDisplayBrightness.
  const src = String(runtime.m["59547"]);
  const at = src.indexOf("m_flDisplayBrightness");
  out.storeSource = src.slice(Math.max(0, at - 500), at + 700);
  return JSON.stringify(out, null, 1);
})();
