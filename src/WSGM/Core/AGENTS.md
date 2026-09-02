# Core

Core contains cross-cutting, non-visual application primitives: configuration, Steam protocol/CEF,
Explorer control, elevation/de-elevation, splash assets, process mode selection, logging, and Win32
utilities.

- Keep public production APIs meaningfully XML-documented. Prefer direct managed APIs and keep OS
  interop behind `Interop\`; retain source-generated JSON where it makes durable wire/config shapes
  explicit.
- `ConfigStore` owns the cross-process lock and atomic merge/save flow. Do not bypass it or write the
  real `%LOCALAPPDATA%\WSGM` configuration from tests.
- Read-modify-write operations must use the strict mutation load: an existing unreadable config is
  an aborted mutation, never permission to replace recovery snapshots with defaults.
- `ExplorerControl.ExitExplorerAndWait` is device-settled: use Explorer's exit command and fail open;
  never replace it with `Process.Kill` or Restart Manager shutdown.
- Steam interactions use protocol URLs or the CEF front-end bridge. Never call Steam internals from
  the injected gate; JS values embedded in CEF expressions must be JSON-encoded.
- `SteamInputBlocker` balances named surface claims independently of native acquire success. The
  Settings handoff may use claim-only `ClaimFor`, but any cold injection remains off the UI thread.
- CEF debugger sockets must remain loopback-only; artwork downloads accept bounded static PNG/JPEG
  data over HTTPS, and protocol/JavaScript errors remain distinct from an unreachable Steam client.
- Keep recovery paths (`--restore-shell`, legacy shell migration, de-elevation) usable before normal
  logging or Avalonia initialization.
- Display HDR is DisplayConfig advanced color on a target, not on its GDI source: query current
  support before showing or applying a saved HDR flag, and keep the interop packets blittable.

## Steam Input shim deployment (`SteamInputShim.cs`)

`SteamInputShim` owns the only file WSGM writes outside its own install: the Steam Input payload,
copied into **Steam's install directory** under a search-order name so Steam loads it itself and
WSGM never injects. `Steam.InstallDirectory` is the one accessor for that directory — do not
re-derive it with `Path.GetDirectoryName(Steam.ExePath!)`.

Three rules are load-bearing, and each one is a real failure if undone:

- **Ownership is proven from the file's own bytes**, by scanning for the payload's
  `WsgmSteamInputGateProxy` export name — never from the sidecar stamp. A stamp can be orphaned
  (the user installs ValvePlug over our copy, Steam's updater replaces it), and trusting it would
  let WSGM overwrite a file it does not own. Unreadable counts as NOT ours: fail closed.
- **Never `File.Move(..., overwrite: true)` when parking or restoring.** `MOVEFILE_REPLACE_EXISTING`
  fails against a mapped image, which defeats the entire reason "off" renames to `.dlld` instead of
  deleting — a rename inside one directory succeeds even while Steam has the DLL mapped.
- **A stale copy is replaced only at a Steam cold start** (`Steam.LaunchBigPicture`), the one moment
  in a session when Steam is provably not running. An `IOException` anywhere else means Steam has it
  mapped and must report `UpdatePending`, never retry.

`Reconcile` never throws and is lock-guarded: the config watcher and a Settings save both reach it.
Deployment follows persisted intent and never precedes it, and runs OUTSIDE `ConfigStore.AcquireLock`
— that lock is sized for one small JSON write, not file copies into Program Files. Steam usually
lives under Program Files, so an access-denied result from an unelevated Settings process re-runs
through `SelfElevation.RunElevatedAction("--apply-steam-input-shim", …)`; without that the toggle
appears to do nothing on most machines.
