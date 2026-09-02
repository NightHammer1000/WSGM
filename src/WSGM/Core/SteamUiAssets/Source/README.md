# Steam UI bootstrap source

WSGM's half of the injected asset. `eng/build-steam-assets.mjs` takes the prelude from the
`steam-ui-toolkit` submodule, adds the fragments here, type-checks the combined program, strips the
TypeScript annotations and formats one reviewable injected asset. It is compiled as a single unit
because it is evaluated in a single CDP call, and the fragments deliberately share one lexical
scope: the gates close over the bridge's private functions and must not publish a second runtime API
merely to cross a source-file boundary.

- `gates/` contains the independently reversible Steam service/store integrations. One file per
  gate, each registering itself.
- `components.ts` owns the React component registry, control rows, placement table, and teardown.

The prelude — the bridge, the ownership primitives, the RPC helpers and the compile-time-only
declarations — lives in the toolkit, not here. Change it in that repository and move the pin.

**Adding a gate is a new file in `gates/` and nothing else.** The builder discovers fragments by
directory rather than holding a list, and orders them so the emitted asset is byte-stable. The
`--check` mode rebuilds the same combined program and rejects a stale generated file, a stale hash
in `SteamUiAssetCatalog`, an asset that is not exactly one bounded UTF-8 file, or a second `.js`
appearing beside it.
