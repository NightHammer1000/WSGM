# Standing decisions and accepted trade-offs

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

## Gotchas

- The installer (`installer\WSGM.iss`) is now **`PrivilegesRequired=admin`** (the machine service
  demands it) while the app stays per-user — deliberate single-user-device design: `{localappdata}`
  and HKCU belong to the elevating account, documented in the .iss header. Update/uninstall ordering
  is load-bearing: `[Code]` stops the **logon service first** (`sc stop` — a live watchdog would see
  the killed WSGM and start explorer mid-update, flipping the restart into desktop mode; also frees
  the Program Files binary, including an abandoned preview's — same service name), then signals
  `Local\WSGM.ExitForUpdate` (one SetEvent releases every instance, including elevated ones), waits
  bounded on the shell mutex, then taskkills only primary `WSGM.exe` images in the installer's
  Terminal Services session. Restart Manager excludes the separately named `WSGM.ShellAnchor.exe`
  companion; it gets its bounded owner-loss recovery window and is retired only after publishing
  `Local\WSGM.ShellAnchor.RecoverySettled`, through the same current-session process filter, while
  setup holds that session-local event open so a new anchor cannot enter the image-name kill. If
  that acknowledgement is unavailable, setup defers replacement instead of killing the recovery
  owner; a silent update skips the locked companion rather than scheduling the automatic reboot that
  `restartreplace` would otherwise cause. Setup then restarts WSGM in its previous mode (shell-mutex
  check taken _before_ killing → `--shell`, else settings). `[Run]` order: `--setup` (per-user
  files, migrate off any legacy shell registration, Xbox-FSE guard, boot manifest) then
  `WSGM.LogonService.exe --install` (create-or-reconfigure + failure actions + start).
  `[UninstallRun]` order: service `--uninstall` (stop+delete) → `--unregister-shell` (legacy no-op
  on service installs) → `--uninstall-restore`, all before files are deleted; `[UninstallDelete]`
  also removes `{autopf}\WSGM` and `{commonappdata}\WSGM`. An update asks current-session Steam to
  exit normally and refuses replacement while Steam or a WSGM launch wrapper remains; setup never
  terminates either process tree. `NeedRestart` follows the USB/IP install outcome (and stays
  conservative when that bounded status is missing), rather than marking every ordinary upgrade for
  reboot. Silent setup always returns `False`, because `/VERYSILENT` could otherwise reboot
  automatically.
- The direct HKCU Winlogon shell replacement was **retired** (2026-08): running the session without
  Explorer ever initializing broke touch features, and the Explorer-first service boot is the
  device-verified fix. 2.0 deleted the registration path entirely (no install code, no auto mode);
  `ShellRegistration.Uninstall`, the snapshot fields in config.json, and `--unregister-shell` remain
  as a 2.0 install's own recovery and the uninstaller's restore. Do not re-register WSGM as the
  shell from any new code path.
- Elevated processes started by WSGM inherit elevation — that inheritance is the point of
  self-elevation: an elevated WSGM yields an elevated **Steam**, which is what lets Steam Input
  reach elevated windows and the Steam Overlay inject into elevated games (UIPI blocks both
  otherwise); WSGM's own overlay/edge swipes over elevated windows ride the same chain. The flip
  side: an **elevated explorer breaks UWP** (touch keyboard, store apps) — that's what invariant 5
  protects.
- **WSGM's per-user inputs remain per-user.** The boot manifest, live configuration, HKCU Steam
  registration, and install-to-run handoff intentionally remain in the user's profile even when WSGM
  elevates. Keep validation that improves correctness without changing that model: absolute
  system-tool paths, no-follow/no-overwrite file operations, correctly scoped kernel objects, and
  bounded external-data parsers. Do not add publisher tiers or per-action prompts to compensate for
  inputs the same user already controls.
- **`Local\WSGM.ExitForUpdate` is a cross-version coordination contract.** Its name, user plus
  Administrators `EVENT_MODIFY_STATE | SYNCHRONIZE` grant (`0x00100002`), medium label, and startup
  reset must remain compatible with older running builds. One signal releases every WSGM instance;
  the update path requests Steam's normal exit, runs bounded application cleanup, and verifies
  Explorer recovery; a client or launch wrapper that remains is a setup refusal, not a process kill.
  The unelevated Settings instance also needs the event grant, so narrowing it breaks ordinary
  update shutdown.
- **Uninstall uses `Local\WSGM.ExitForUninstall`.** It keeps the same event-access and stale-signal
  behavior, selects the fixed uninstall budget, and does not stop Steam. Removal of older builds
  falls back to the update event before the force-stop path.
- **Never manage Windows device posture or automatic touch-keyboard policy:** game/desktop mode must
  not capture or write `ConvertibleSlateMode` or `TouchKeyboardTapInvoke`. Windows owns both.
- **The volume OSD must never interrupt an exclusive game:** the physical volume command is always
  applied in game mode. The indicator is non-activating and click-through, and is suppressed only
  for `SHQueryUserNotificationState`'s confirmed `QUNS_RUNNING_D3D_FULL_SCREEN` (and absent/locked
  session). `QUNS_BUSY` must stay allowed: Steam Big Picture and borderless fullscreen report it.
- Config lives at `%LOCALAPPDATA%\WSGM\config.json` (`Core\ConfigStore`, System.Text.Json source-gen
  — new scalar props need no context changes). Registry snapshots inside it (previous
  shell/UAC/lock-screen values) belong to the install lifecycle; never clobber them from feature
  code. `ConfigStore.AcquireLock()` is the cross-process scope; the Settings save transaction holds
  it across the config write AND the splash-asset promotion, but the multi-megabyte image copies
  happen OUTSIDE it (sidecars are per-transaction unique, so staging cannot collide). Nested
  acquisition on the same thread is free — do not reintroduce stacked 2 s timeouts.
- The app targets .NET 10 and Avalonia 12.1.1. `LoadingIndicators.Avalonia` is vendored under
  `third_party\LoadingIndicators.Avalonia` and built from source because its published Avalonia 11
  package has precompiled XAML that fails on Avalonia 12; its Unlicense text ships from
  `src\WSGM\Licenses\`. `FluentAvaloniaUI` 3.0.2 and an explicit `Avalonia.Controls.ColorPicker`
  12.1.1 pin keep the controls on the same Avalonia line.
