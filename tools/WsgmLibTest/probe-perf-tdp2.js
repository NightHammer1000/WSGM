// Probe: the TDP Limit row itself — which component renders it, what it binds to, and
// how the SteamOS Manager state is fetched. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_tdp2_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  try {
    const src = String(runtime.m["29788"]);
    out.len = src.length;
    const hits = [];
    const re = /steamos_tdp_limit/g;
    let m;
    while ((m = re.exec(src)) !== null && hits.length < 3) {
      hits.push(src.slice(Math.max(0, m.index - 900), m.index + 600));
    }
    out.tdpRows = hits;
  } catch (e) {
    out.err = String(e);
  }
  // How is the SteamOS manager state fetched? Find the transport behind GetState.
  try {
    const src = String(runtime.m["33706"]);
    const i = src.indexOf("GetState");
    out.stateFetch = src.slice(Math.max(0, i - 900), i + 300);
  } catch (e) {
    out.err2 = String(e);
  }
  return JSON.stringify(out);
})();
