# Settings

Settings is the safe local configuration UI. Its pages are kept alive and switched by visibility so
scroll position and short-lived editing state survive tab changes.

- Persist through `SettingsViewModel`'s save transaction and the `ConfigStore` mutation lock; never
  save a stale window snapshot directly or promote image sidecars before the config save succeeds.
- Tests must use the internal view-model constructor with an explicit `AppConfig` and temporary asset
  directories. Never invoke parameterless `SettingsViewModel` or real `ConfigStore.Load/Save`.
- Maintain the layout floor: Settings minimum 1024×640; a page that needs scrolling earns another tab.
- Per-monitor display profiles own the dedicated Display tab; automatic snapshots are runtime-owned,
  so an already-open Settings window must never merge its stale profile rows over a newer transition capture.
- Shortcut recording owns its hook only while recording and must dispose it on every close/cancel path.
- A game-mode Settings window owns a named Steam Input claim, not merely the native lease result:
  `AcquireFor` registers that claim before attempting injection, so deactivation/close must run
  `ReleaseFor` even when Steam was unavailable and the native acquire failed. The overlay handoff
  uses claim-only `ClaimFor` synchronously, then performs any cold acquire off the UI thread; its
  transient 150 ms overlap deactivation does not end ownership until the overlay acknowledges close.
- Any required text credential must have a controller-accessible `OnScreenKeyboard` path; gamepad
  navigation intentionally skips ordinary `TextBox` controls.

## First-run Quick Setup

`SettingsWindow` raises a modal Quick Setup panel over itself when
`QuickSetup.ShouldShow(config)` is true. The gate is `AppConfig.QuickSetupRevision`, an **int, not a
bool**: a later build that adds a setting needing an explicit decision raises
`QuickSetup.CurrentRevision` and the panel returns exactly once. Do not turn it back into a
"seen it" flag.

Two properties of the panel are deliberate. It **disables `SettingsRoot` while it is up**, because
gamepad focus would otherwise wander into the pages behind it and answer nothing. And it applies
**nothing** until Continue: both integrations arrive pre-selected as the recommended setup, but Skip
means off, so a dismissed panel never leaves a file in Steam's directory the user did not agree to.
The revision is stamped in `ApplyTo` only when `QuickSetupAnswered` is set, so a save that fails
leaves the panel due to appear again rather than silently swallowing the answer.

## Steam Input Management

The toggle lives on the Steam page, not Integration — Integration is scoped to what WSGM drives over
Steam's debug port, and this is an install-level fact about Steam. It is save-scoped rather than
instant-apply, so the Tools tab's apply-time read of the setting can never see a value that was
applied but not persisted. Deployment happens in `ApplySteamInputManagementAfterSave`, after the
config lock is released.
