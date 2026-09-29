// Probe: which service carries SetSpeakerConfiguration / HDMI CEC, and what config values
// it accepts. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_spk_" + Date.now()],
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

  // The import that provides SetSpeakerConfiguration, and the enum of configurations.
  safe("import_header", () => src.slice(0, 900));
  safe("speaker_sites", () => {
    const hits = [];
    const re = /SetSpeakerConfiguration|SpeakerConfig|eConfig|speaker/gi;
    let m;
    const seen = new Set();
    while ((m = re.exec(src)) !== null && hits.length < 8) {
      const key = Math.floor(m.index / 400);
      if (seen.has(key)) continue;
      seen.add(key);
      hits.push(src.slice(Math.max(0, m.index - 300), m.index + 400));
    }
    return hits;
  });

  // Any module owning an AudioManagerService-style RPC stub.
  safe("audio_services", () => {
    const found = [];
    for (const id of Object.keys(runtime.m)) {
      let s;
      try {
        s = String(runtime.m[id]);
      } catch {
        continue;
      }
      if (/SetSpeakerConfiguration|AudioManager\./.test(s)) {
        const names = [...new Set(s.match(/"[A-Za-z]+Audio[A-Za-z]*\.[A-Za-z]+#\d"/g) || [])];
        found.push({ id, len: s.length, msgs: names.slice(0, 20) });
      }
    }
    return found.sort((a, b) => a.len - b.len).slice(0, 6);
  });
  return JSON.stringify(out);
})();
