// Probe: what Steam's audio store expects from SteamClient.System.Audio, and what the
// Audio settings page renders. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_audio_" + Date.now()],
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
  const src = String(runtime.m["1409"]);
  out.len = src.length;

  // Every SteamClient.System.Audio call site with a little context, so the expected
  // signatures and payload shapes are visible.
  safe("audio_call_sites", () => {
    const hits = [];
    const re = /SteamClient\.System\.Audio\.[A-Za-z]+/g;
    let m;
    while ((m = re.exec(src)) !== null && hits.length < 14) {
      hits.push(src.slice(Math.max(0, m.index - 220), m.index + 260));
    }
    return hits;
  });

  // The store's availability flag and device shape.
  safe("bAvailable_context", () => {
    const i = src.indexOf("bAvailable");
    return src.slice(Math.max(0, i - 700), i + 500);
  });

  safe("exports_1409", () => {
    const mod = runtime("1409");
    const r = {};
    for (const k of Object.keys(mod)) {
      const v = mod[k];
      r[k] = typeof v === "function" ? String(v).slice(0, 130) : typeof v;
    }
    return r;
  });

  safe("SteamClient_System_Audio", () => typeof window.SteamClient?.System?.Audio);
  return JSON.stringify(out);
})();
