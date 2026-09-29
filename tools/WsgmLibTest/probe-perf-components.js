// Probe: which performance-control localization tokens module 83571 actually renders, so the
// components available to mount are read from the client rather than guessed.
//
// Reads ONE named module's factory as a string. No module is resolved by loop, nothing is
// constructed — see the rule in docs\steam-cef.md.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_perf_components_probe_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);

  const out = {};
  try {
    const source = String(runtime.m["83571"]);
    out.sourceLength = source.length;

    // Every localization token in the module, deduplicated and counted. Guessing token names told
    // us only which guesses were wrong; this reports what is actually there.
    const counts = {};
    const pattern = /#[A-Za-z0-9_]+/g;
    let match;
    while ((match = pattern.exec(source)) !== null) {
      counts[match[0]] = (counts[match[0]] ?? 0) + 1;
    }

    out.tokens = Object.entries(counts)
      .sort((left, right) => left[0].localeCompare(right[0]))
      .map(([token, count]) => `${token}:${count}`);
  } catch (error) {
    out.error = String(error);
  }

  return JSON.stringify(out);
})();
