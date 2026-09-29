// Probe: what the QAM Quick Settings tab is made of — which modules own it, which
// localization tokens it renders, and therefore which rows exist. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_qamset_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = { owners: {}, tokens: {} };
  const ids = Object.keys(runtime.m);
  out.moduleCount = ids.length;

  // Every QuickAccess settings-tab localization token, grouped by owning module.
  const tokenRe = /#QuickAccess_[A-Za-z0-9_]+/g;
  const perModule = {};
  for (const id of ids) {
    let src;
    try {
      src = String(runtime.m[id]);
    } catch {
      continue;
    }
    if (!src.includes("#QuickAccess_")) continue;
    const found = new Set();
    let m;
    while ((m = tokenRe.exec(src)) !== null) found.add(m[0]);
    if (found.size) perModule[id] = { len: src.length, count: found.size, tokens: [...found] };
  }
  out.owners = perModule;
  return JSON.stringify(out);
})();
