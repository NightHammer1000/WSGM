// Probe: the protobuf message contract behind the Performance tab — the state message
// (cI), the settings update request (TR), and the limits/global/per-app field sets.
// Read-only: instantiates messages in JS, sends nothing.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_proto_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  const shape = (ctor) => {
    try {
      const proto = ctor.prototype;
      return Object.getOwnPropertyNames(proto).filter(
        (n) => n !== "constructor" && !n.startsWith("_"),
      );
    } catch (e) {
      return "ERR " + e;
    }
  };
  try {
    const m = runtime("28013");
    out.exports28013 = Object.keys(m);
    for (const k of Object.keys(m)) {
      const v = m[k];
      if (typeof v === "function" && v.prototype) {
        const names = shape(v);
        if (Array.isArray(names) && names.length > 3) out["cls_" + k] = names;
      } else if (typeof v === "object" && v) {
        out["enum_" + k] = Object.keys(v).slice(0, 40);
      }
    }
  } catch (e) {
    out.err28013 = String(e);
  }
  return JSON.stringify(out);
})();
