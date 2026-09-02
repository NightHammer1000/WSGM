(() => {
  const b = window.__wsgmSteamUi_v1_28d7c54a;
  if (!b) return JSON.stringify({ bridge: false });
  return JSON.stringify({
    bridge: true,
    asset: b.assetHash && b.assetHash.slice(0, 8),
    perfOwned: !!window.SteamClient?.System?.Perf?.__wsgmOwnedNamespace,
  });
})();
