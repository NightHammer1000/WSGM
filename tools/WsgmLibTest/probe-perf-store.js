// Probe: the perf-settings store (74514) that every Performance tab control reads and
// writes, and the settings hook module (33867) that owns force_deck_perf_tab.
// Read-only: dumps sources and inspects the live store, writes nothing.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_store_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  for (const id of ["74514", "33867"]) {
    try {
      out["src_" + id] = String(runtime.m[id]);
    } catch (e) {
      out["src_" + id] = "ERR " + e;
    }
  }
  // Live store instance: 74514 export Hn is the singleton in the minified source.
  try {
    const mod = runtime("74514");
    out.exports74514 = Object.keys(mod);
    const store = mod.Hn && mod.Hn.Get ? mod.Hn.Get() : null;
    if (store) {
      out.storeCtor = store.constructor && store.constructor.name;
      const proto = Object.getPrototypeOf(store);
      out.storeMethods = Object.getOwnPropertyNames(proto).slice(0, 200);
      out.storeFields = Object.keys(store).slice(0, 120);
    } else {
      out.storeMissing = true;
    }
  } catch (e) {
    out.storeError = String(e);
  }
  return JSON.stringify(out);
})();
