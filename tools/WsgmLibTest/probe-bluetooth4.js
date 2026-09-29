// Probe: dump the Bluetooth hook/store module (25467) whole — it holds PairDevice and the
// availability gate — plus the device store (46938) head. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_bt4_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  return JSON.stringify({
    m25467: String(runtime.m["25467"]),
    m46938: String(runtime.m["46938"]).slice(0, 4500),
  });
})();
