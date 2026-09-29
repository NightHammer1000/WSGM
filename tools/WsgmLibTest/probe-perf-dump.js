// Probe: dump the small candidate performance modules so their real shape is visible.
// Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_dump_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const want = ["83571"];
  const out = {};
  for (const id of want) {
    try {
      out[id] = String(runtime.m[id]);
    } catch (e) {
      out[id] = "ERR " + e;
    }
  }
  return JSON.stringify(out);
})();
