// Probe: the Bluetooth store (57421) — its operations and the exact backend surface it
// calls, so we know what supplying it costs. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_bt2_" + Date.now()],
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
  const src = String(runtime.m["57421"]);
  out.len = src.length;

  safe("all_steamclient_calls", () => [
    ...new Set(src.match(/SteamClient\.[A-Za-z0-9_.]+/g) || []),
  ]);
  safe("bluetooth_mentions", () => [...new Set(src.match(/[A-Za-z_]*Bluetooth[A-Za-z_]*/g) || [])]);
  safe("class_methods", () => {
    const m = src.match(/\b(async\s+)?([A-Z][A-Za-z0-9_]*)\s*\(/g) || [];
    return [...new Set(m.map((x) => x.replace(/\s*\($/, "").trim()))].slice(0, 60);
  });
  safe("exports", () => {
    const mod = runtime("57421");
    const r = {};
    for (const k of Object.keys(mod)) {
      const v = mod[k];
      r[k] = typeof v === "function" ? String(v).slice(0, 160) : typeof v;
    }
    return r;
  });
  // Live state, if a singleton is reachable.
  safe("live", () => {
    const mod = runtime("57421");
    for (const k of Object.keys(mod)) {
      const v = mod[k];
      if (typeof v === "function" && typeof v.Get === "function") {
        const s = v.Get();
        const r = { via: k, fields: {} };
        for (const f of Object.keys(s)) {
          const val = s[f];
          r.fields[f] = Array.isArray(val)
            ? "array[" + val.length + "]"
            : val === null || ["boolean", "number", "string"].includes(typeof val)
              ? val
              : typeof val;
        }
        return r;
      }
    }
    return "no singleton";
  });
  return JSON.stringify(out);
})();
