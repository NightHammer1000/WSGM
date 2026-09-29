(() => {
  const out = {};
  out.bridge = Object.keys(window).filter((k) => k.indexOf("__wsgmSteamUi") === 0);
  const s = window.SteamClient && window.SteamClient.System;
  out.audio = !!(s && s.Audio);
  out.audioOwned = !!(s && s.Audio && s.Audio.__wsgmOwnedNamespace === true);
  out.perf = !!(s && s.Perf);
  out.perfOwned = !!(s && s.Perf && s.Perf.__wsgmOwnedNamespace === true);
  return JSON.stringify(out);
})();
