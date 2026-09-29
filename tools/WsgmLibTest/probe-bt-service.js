// Probe: the full BluetoothManagerService method list and the device message shape, so the
// cost of supplying it is exact. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_btsvc_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  const safe = (label, f) => {
    try {
      out[label] = f();
    } catch (e) {
      out[label] = "ERR " + String(e).slice(0, 200);
    }
  };
  safe("mod60517_exports", () => Object.keys(runtime("60517")));
  safe("RF_methods", () => {
    const RF = runtime("60517").RF;
    if (!RF) return "no RF";
    const names = new Set();
    for (const n of Object.getOwnPropertyNames(RF)) names.add(n);
    const proto = Object.getPrototypeOf(RF);
    if (proto) for (const n of Object.getOwnPropertyNames(proto)) names.add(n);
    return [...names];
  });
  safe("RF_type", () => typeof runtime("60517").RF);
  // Live call: is the service present on Windows at all?
  safe("live_state", async () => {
    const RF = runtime("60517").RF;
    const r = await RF.GetState({});
    return { success: r.BSuccess ? r.BSuccess() : "?", body: r.Body ? r.Body().toObject() : null };
  });
  return Promise.resolve(out.live_state)
    .then((v) => {
      out.live_state = v;
      return JSON.stringify(out);
    })
    .catch((e) => {
      out.live_state = "ERR " + String(e).slice(0, 200);
      return JSON.stringify(out);
    });
})();
