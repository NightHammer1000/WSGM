(() => {
  let req;
  window.webpackChunksteamui.push([
    ["wsgm_l5_" + Date.now()],
    {},
    (r) => {
      req = r;
    },
  ]);
  const s = String(req.m["74514"]);
  const hImp = s.match(/\bh=r\((\d+)\)/);
  const out = { hModule: hImp ? hImp[1] : null };
  if (hImp) {
    const mod = req(hImp[1]);
    if (mod && typeof mod.l5 === "function") {
      out.l5Source = String(mod.l5).slice(0, 300);
      try {
        out.l5Value = mod.l5();
      } catch (e) {
        out.l5CallErr = String(e);
      }
    } else out.exports = Object.keys(mod).slice(0, 20);
  }
  const holder = Object.values(req("74514")).find((v) => v && typeof v.Get === "function");
  const st = holder.Get();
  out.hasExternalOptions = !!st.msgLimits?.fps_limit_options_external;
  out.hasInternalOptions = !!st.msgLimits?.fps_limit_options;
  return JSON.stringify(out);
})();
