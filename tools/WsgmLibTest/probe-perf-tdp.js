// Probe: locate any reusable TDP slider component and see which backend it binds to.
// Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_tdp_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  for (const id of ["90389", "85857"]) {
    let src;
    try {
      src = String(runtime.m[id]);
    } catch (e) {
      continue;
    }
    const hits = [];
    const re = /TDPLimit|tdp_limit|TDP_Limit|Tab_Perf_TDP/g;
    let m;
    while ((m = re.exec(src)) !== null && hits.length < 5) {
      hits.push(src.slice(Math.max(0, m.index - 650), m.index + 400));
    }
    out[id] = hits;
  }
  return JSON.stringify(out);
})();
