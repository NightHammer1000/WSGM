// Probe: is *Handler a registration point WSGM could implement the service through, or
// just a client-side dispatch stub? Decides implement-vs-intercept. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_bthandler_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const RF = runtime("60517").RF;
  const out = {};
  const describe = (name) => {
    const v = RF[name] ?? Object.getPrototypeOf(RF)[name];
    if (typeof v === "function") return "fn: " + String(v).slice(0, 300);
    if (v && typeof v === "object")
      return "obj keys: " + Object.getOwnPropertyNames(v).slice(0, 20).join(",");
    return typeof v;
  };
  for (const n of [
    "GetStateHandler",
    "GetState",
    "SendMsgGetState",
    "PairHandler",
    "Pair",
    "ConnectHandler",
  ]) {
    out[n] = describe(n);
  }
  out.RF_ctor = RF.constructor && RF.constructor.name;
  out.RF_own = Object.getOwnPropertyNames(RF).slice(0, 30);
  return JSON.stringify(out);
})();
