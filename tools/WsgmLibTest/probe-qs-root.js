// Probe: all `function De(` occurrences, matched to the tab-tap capture by its head
// (GetControllers + ~2 KB), and dump that one's full source.
(() => {
  let req;
  window.webpackChunksteamui.push([
    ["wsgm_de2_" + Date.now()],
    {},
    (r) => {
      req = r;
    },
  ]);
  const results = [];
  for (const [id, f] of Object.entries(req.m)) {
    const s = String(f);
    let at = -1;
    while ((at = s.indexOf("function De(", at + 1)) >= 0) {
      let depth = 0,
        end = at;
      for (let i = s.indexOf("{", at); i < s.length; i++) {
        if (s[i] === "{") depth++;
        else if (s[i] === "}" && --depth === 0) {
          end = i + 1;
          break;
        }
      }
      const body = s.slice(at, end);
      if (body.includes("GetControllers")) results.push({ module: id, len: body.length, body });
    }
  }
  return JSON.stringify(results.slice(0, 2));
})();
