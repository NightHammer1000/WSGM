(() => {
  let req;
  window.webpackChunksteamui.push([
    ["wsgm_night2_" + Date.now()],
    {},
    (r) => {
      req = r;
    },
  ]);
  const mod = req("96555");
  const d = Object.getOwnPropertyDescriptor(mod, "hb");
  const out = {
    descriptor: d
      ? { hasGet: typeof d.get === "function", writable: d.writable, configurable: d.configurable }
      : "absent",
  };
  if (d && d.configurable) {
    try {
      Object.defineProperty(mod, "hb", { get: () => () => true, configurable: true });
      out.overrideWorks = mod.hb() === true;
      Object.defineProperty(mod, "hb", d);
      out.restored = mod.hb() === false;
    } catch (e) {
      out.error = String(e).slice(0, 200);
    }
  }
  return JSON.stringify(out);
})();
