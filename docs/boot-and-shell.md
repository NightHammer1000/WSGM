# Boot, shell takeover and session transitions

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

**Process modes** (`Program.DecideMode`): `--shell` / `--boot` (service-launched takeover) /
`--settings` / `--overlay-test`; WSGM never registers as the Windows shell, so no-args = settings.
Shell mode: single-instance mutex `Local\WSGM.Shell` (held only in shell mode — the installer keys
off it), crash-loop breaker (3 shell starts in 2 min → **disarm the service boot**: boot.json
`GameModeBoot=false` + config flag off + shell-snapshot restore + explorer if none), `Panic()` =
shell-snapshot restore (self-guarding no-op), destroy tray host, delegate recovery to the verified
shell anchor when one exists, otherwise start explorer if none is running. The logon service's
watchdog is the robust outer recovery layer; Panic is in-process best effort.

**Logon service + boot flow** (`src\WSGM.LogonService`, `Core\BootManifest.cs`,
`Core\BootManifestWriter.cs`, `Shell\ExplorerReadiness.cs`): the SYSTEM service (raw SCM +
`SERVICE_ACCEPT_SESSIONCHANGE`, no ServiceBase) reacts to `WTS_SESSION_LOGON` only (not console
connect — fast-user-switch keeps what runs), reads the per-user `%LOCALAPPDATA%\WSGM\boot.json`
**boot manifest** (projected from config.json by WSGM on `--setup`, settings saves, and every shell
start; the service treats it as untrusted and only ever launches the named exe AS THAT USER), and
launches `WSGM.exe --boot` via `CreateProcessAsUserW` — with the user's elevated **linked token**
when the manifest says so (legal under the service's SeTcbPrivilege; no UAC prompt at logon). A
startup catch-up sweep covers autologons that beat the auto-start service (fresh = logged on < 60
s). The service watchdog holds the launched pid: dirty exit + active session + no explorer → allow
the session-owned shell anchor five seconds to restore its normal medium/jobless Explorer, then
start explorer with the UNLINKED token only if no shell appeared (explorer must stay unelevated),
once per logon, never relaunching WSGM. This bounded grace prevents the anchor and SYSTEM watchdog
from creating competing shells; the watchdog remains the outer fallback when the anchor is absent or
broken. Service log: `%ProgramData%\WSGM\wsgm-service.log` (SYSTEM must not write user dirs); WSGM's
own `Run mode: Shell (service boot, elevated=…, session N)` line keeps wsgm.log the primary surface.

**The service fires BEFORE Winlogon starts explorer** (device-verified 2026-08-07), so `--boot` runs
the takeover unconditionally — never gate it on `IsRunningInSession()` at start (that exact gate
once left explorer alive behind Big Picture next to WSGM's tray host); the readiness poll is what
waits for explorer to appear at all. The `--boot` takeover (`ShellSession.StartBootTakeover`):
splash FIRST (covers the booting desktop; re-covers itself on display change because posture applies
later) → **input-desktop barrier** (`Core\InputDesktop` polls `OpenInputDesktop` for winsta0\Default
— WTS_SESSION_LOGON fires while LogonUI still owns the screen, and WTS_SESSION_DESKTOP_READY is
never delivered on the Claw; without this gate Steam audio leaks behind the Welcome screen) →
`ExplorerReadiness` — `GetShellWindow()` + explorer's `Shell_TrayWnd`, then `ExplorerLogonSettleMs`
settle (default 5000 ms), 60 s hard cap, and **invariant-7 acceleration** (BP window appears under
the opaque cover → take over immediately) → `ExplorerControl.ExitExplorerAndWait(30 s)` → posture →
TrayHost → startup apps (skipping ones explorer's autostart already launched) → Steam, strictly
AFTER explorer is gone. The splash's **Switch to desktop** is a recovery/quickswitch owned by
`ShellSession`: while the service takeover is active it cancels the input-desktop/readiness waits
before Explorer shutdown; if Explorer's irreversible orderly-exit request already began, it skips
every game-mode side effect and completes the ordinary desktop transition, which starts Explorer
again. It must never compete through `SessionModes`' already-held transition gate or allow Big
Picture to start afterward.

**How Explorer is ended — device-settled, do not change the mechanism:** `ExitExplorerAndWait` posts
`0x5B4` (WM_USER+436, explorer's own Ctrl+Shift-taskbar "Exit Explorer" command) to explorer's
pid-verified `Shell_TrayWnd`. That intentional shutdown is the ONLY way Winlogon's AutoRestartShell
does not respawn the shell. PID-snapshot semantics: any explorer pid not in the initial snapshot is
a Winlogon replacement → cancel and **fail open** (preserve desktop mode, warn
`Couldn't exit Windows Explorer safely`); a replacement is NEVER killed (fighting AutoRestartShell
loops) — instead the orderly exit is retried ONCE against the respawned shell, which is a freshly
started explorer that honors it within seconds, and both attempts share ONE deadline (a fresh full
budget for the retry let a caller asking for 15 s sit in the transition for more than twice that).
Lingering snapshotted pids are terminated only after explorer destroyed its taskbar (a shell
extension can hold the process open — device-observed) **and only after a `LingerGrace` (8 s) window
in which the remnant is given the chance to leave on its own** — killing it mid-shutdown is itself
what Winlogon respawns (device-observed 2026-08-08 as "game mode needs two tries"; a clean run had
the remnant exit ~830 ms after the taskbar went). That grace is never shortened to fit the remaining
budget: a remnant that did not get the full window is left alone and the exit fails open. Success
requires 500 ms of stable absence. Two mechanisms are device-DISPROVEN (2026-08-07): plain
`Process.Kill` (Winlogon respawns) and Restart Manager `RmShutdown` (wedged a freshly logged-on
explorer ~30 s, error 351, then respawn). The full working-era implementation is preserved in the
Codex transcript `~\.codex\sessions\2026\08\06\rollout-2026-08-06T23-57-41-*.jsonl` (L567/L1167).

**How Explorer is restored for a normal desktop transition:** immediately before each orderly exit,
WSGM resolves the actual `Shell_TrayWnd` owner and accepts it only when `GetShellWindow` has the
same owner, its image is the canonical `%WINDIR%\explorer.exe`, it belongs to the current session,
runs at medium integrity, and is not associated with a job. WSGM retains that process as the
designated `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` and starts one fixed-purpose medium/jobless WSGM
anchor under it before the old shell exits. The anchor accepts only an authenticated per-session
`start` command for the fixed Windows Explorer path. WSGM owns the exact child handle, bounds every
pipe operation, stops only that owned process on failed setup, and disposes/replaces the anchor with
the shell session. Capture, restore, replacement, and disposal are serialized inside that session
owner; disposal closes admission before waiting for an already-running operation. A named
per-session stop event lets a new run identify and retire only a stale WSGM anchor. Command-pipe EOF
alone is not owner loss: the anchor keeps the recovery role until the retained owner process
actually exits or that explicit stop event is signalled. A faulted asynchronous owner wait is
likewise not a recovery settlement; the anchor keeps serving its authenticated pipe, retries an
exact-process liveness observation, and otherwise waits for explicit stop instead of abandoning the
session or starting Explorer beside an owner it could not classify.

**Does the retained handle stay a valid designated parent after that process exits?** The API
documentation does not say, so it was measured. On Windows 11 25H2 build 26200.9168 (2026-08-29),
with a throwaway `cmd.exe` standing in for Explorer so nothing about the real shell was disturbed:
with the designated parent already exited and only its handle retained, the handle stays signalled,
`GetExitCodeProcess` still answers, `CreateProcessW` with `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`
succeeds, and the child's **recorded parent is the dead process**, not the caller. Reproduced across
three runs, each with a live-parent control proving the harness itself works.

That settles creation and reparenting. It does **not** settle the half the mechanism depends on:
whether a dead parent still supplies the medium token and the job association, or only the recorded
parent pid. Discriminating those needs a parent at a different integrity level from the caller,
which needs an elevated run. Until that is answered the anchor stays the normal path — so a
`CreateProcessW` that merely succeeds is not evidence the token came from where it was supposed to.

The transition completes only after `GetShellWindow` and `Shell_TrayWnd` have the same resulting
owner for a stable 500 ms and that owner again passes image/session/integrity/job inspection. The
PID returned by process creation is diagnostic only; it is never the success condition. An
already-valid shell is adopted (the early splash-cancellation case). A canonical current-session
medium Explorer with unknown or positive job membership is usable only as a degraded desktop. A
wrong-image, wrong-session, elevated, uninspectable, owner-mismatched, or unsettled taskbar is
failure, not degraded success. If an anchor request was dispatched or may have crossed the pipe,
WSGM never dispatches the scheduler as a second creator and never recreates `TrayHost` while that
late shell may still publish `Shell_TrayWnd`.

The scheduled-task de-elevation route is retained solely as last-resort fail-open recovery when no
anchor request was dispatched. Its result is always reported as degraded even if the observed
Explorer happens to be jobless. Its task XML write and `schtasks` create/run/delete commands consume
the same absolute desktop-restoration deadline as readiness observation; cancellation or a process
wait fault stops the active tool, and cleanup is skipped once that shared budget is closed instead
of acquiring a fresh timeout. An uncertain `/Create` is still cleanup-eligible while budget remains.
A timed-out or faulted `/Run` command is an unknown dispatch, never proof that no launch occurred,
so WSGM keeps its tray retired while a late Explorer may still appear. An older-build job-bound
taskbar is never ended without a verified repair owner: takeover stays in desktop mode and the UI
gives the explicit sign-out/reboot-once instruction. On abnormal WSGM loss the anchor waits briefly
for another recovery actor, preserves any existing shell surface, checks that the session is still
active, and only then restores Explorer. The installed anchor is the same application payload under
the distinct `WSGM.ShellAnchor.exe` image name. Restart Manager excludes that image, so installer
force fallback can stop the primary `WSGM.exe` first, wait for the companion's bounded
recovery-settled acknowledgement, and retire only the companion image in the installer's Terminal
Services session after its preserve/restore decision while holding that session-local event name
against a new anchor. A missing acknowledgement defers companion replacement rather than killing the
only remaining desktop-recovery owner; silent setup keeps the old companion for a later maintenance
pass instead of converting that deferral into an automatic reboot. Before explicitly retiring the
anchor, normal disposal verifies or restores a usable desktop; logoff retires it without launching.
Application shutdown rejects new mode and Steam-launch commands and waits for the one in-flight
transition and boot worker under the process's single outer deadline. Device cleanup runs before
that wait, and the anchor remains alive if the deadline or desktop verification fails so owner-loss
recovery still has a jobless Explorer launch path. Logs record source and result PID, both
shell-surface owners, route, session, integrity, job state, readiness, elapsed time,
fallback/dispatched state, and independent Win32 query errors.

**Residual device acceptance:** the anchor path and refusal/fallback classifications are covered by
isolated policy tests and build verification, but the affected-device matrix remains attended:
before/after-exit splash cancellation, repeated transitions, abnormal-loss recovery, Process
Explorer job inspection, and the Mod Organizer 2 breakaway launch must still be exercised on the
reference Claw. Unattended tests must not start or stop the live shell.

**Shell session** (`Shell\ShellSession`): launches startup apps (optional `StartupDelayMs` wait
before the first one — the "First app delay" setting — then staggered, optionally elevated), then
Steam Big Picture. WSGM self-elevates when a startup app or Steam requires matching integrity, and
watches `config.json` (FileSystemWatcher, 500 ms debounce → `OverlayController.ApplyConfig`; runtime
state must live on controllers, not in `_config`, because reloads replace it wholesale).
`Shell\SteamMonitor` polls `steam;steamwebhelper` every 5 s; its `Paused` flag is how desktop mode
and "Close Steam" suppress auto-relaunch/overlay-pop reactions.

The resident shell also owns a shared `WTSRegisterSessionNotification` lease. `WTS_SESSION_LOGOFF`
requests the five-second session-end shutdown path before Avalonia exits; display-mute owns a
separate lease for unlock recovery, so toggling that optional feature cannot deregister the shell's
logoff signal. Update and uninstall use distinct cross-version events and deadlines. Update asks
Steam and launch wrappers to exit under one bounded ten-second pre-stop before the separate
ten-second WSGM cleanup because the mapped input payload must be replaceable. The installer reserves
both windows plus handoff margin before force-stop; a failed Steam pre-stop still starts WSGM
cleanup. Setup records the initial shell/settings classification once; a post-shutdown refusal,
retry, or cancellation before file mutation releases its device reservations and restores the old
service through its installer-tagged start plus that exact runtime mode instead of classifying the
temporary stopped state. Uninstall allows twenty seconds for WSGM cleanup and does not force-close
Steam. It holds the same global package/owner reservations through `[UninstallDelete]`; cancellation
before uninstall mutation restores the service and prior runtime.

**Steam integration** (`Core\Steam.cs`, `Core\SteamInputBlocker.cs`, `Shell\SessionModes.cs`,
`Overlay\OverlayController.cs`): everything is protocol URLs — start/focus =
`steam://open/bigpicture` (boots Steam if needed, UIPI-proof), leave BP =
`steam://close/bigpicture`, quit = `steam://exit`. Desktop mode = pause monitor + close BP + start
Explorer through the verified session anchor above; `Core\UnelevatedLauncher.cs` via scheduled task
is recovery-only and is surfaced as degraded. A runtime desktop-to-game switch requests Big Picture
FIRST while keeping the monitor paused, then runs `ExplorerControl.ExitExplorerAndWait` off the UI
thread. That overlaps Steam's UI startup with Explorer's bounded linger/retry instead of presenting
the safety wait before Big Picture appears. Only after Explorer is verifiably gone does the UI
thread apply game posture, recreate game-mode services/the tray host, and resume monitoring. If
Explorer refuses to exit, the transition sends `steam://close/bigpicture` and preserves desktop
mode. Conversely, if desktop restoration fails before any Explorer launch was dispatched, rollback
reopens Big Picture before recreating game-mode services; a dispatched/late shell suppresses that
recreation to prevent dual taskbars. The direct logon boot remains stricter: Steam starts only after
Explorer is gone. `SessionModes.TransitionInProgress` serializes transitions (the overlay ignores
mode clicks while one runs). The game/desktop mode transitions and the shared Steam start+warning
flow live in `Shell\SessionModes` (session coordinator, used by both `ShellSession` boot and the
overlay's buttons); `OverlayController` stays the UI owner (lease lifecycle, overlay window) and
surfaces `SessionModes.SteamStartFailed` warnings.

**The strongest current evidence for the recurring Steam startup hang is boot-context CEF mutation,
not the resident input shim** (device-observed repeatedly 2026-08-22). WSGM's direct-boot Steam
start could wedge while a manual start with the same deployed shim succeeded. Failed boot PID
12064's native trace shows the proxy forwarding table ready and rediscovery complete in 2 ms, the
control pipe listening, and zero bootstrap fallback calls — the same shape as successful starts.
WSGM then drove the still-headless CEF session and began replacing the card library before
`WindowFinder` ever observed Big Picture; that window never appeared. Automatic boot CEF mutations
now require the process-owned Big Picture window, while card detection starts immediately and defers
only the live Steam change. Device re-verification of that boundary is still required. Keep the
per-process trace (`%LOCALAPPDATA%\WSGM\steam-input-gate-<pid>.log`) as the control for future
reports; do not resume proxy-timing changes unless a failing trace differs.

**The 2.0 patch host reproduced the same hang from the other side (device-observed 2026-09-01).**
The persistent transport reconnects to Steam's port on a 1/4/16/30 s backoff and
`SteamUiSessionHost` synchronizes every registered patch on the first `GenerationChanged`, so that
path never went through `SteamUiReadiness`; `7ddda25` then folded the last explicit boot gate (the
download sort) into it. On a desktop-to-game transition that cold-started Steam (PID 6500,
19:14:22.028) the log shows `wsgm.download-sort v1: Applied` and a running-application probe at
19:14:24.979, then the native-QAM bootstrap and eighteen patches Applied/Verified by 19:14:26 — and
no Big Picture window, ever; `steam://exit` did nothing and Task Manager was needed. The only
successful cold boot in that log (2026-08-31 15:53) connected 80 ms AFTER
`Big Picture window detected`. The fix sits at the choke point rather than per consumer:
`ShellSession` owns a one-second loop that keeps the transport's enabled flag equal to
`SteamUiReadiness.TransportShouldBeOpen(cefMaster, inGameMode, bigPictureVisible)`, re-checked on
every mode change and Steam lifecycle edge and always under the master-switch gate, so no discovery,
connection or evaluation can reach a cold-starting Steam. Desktop mode stays open on the master
switch alone. What a healthy game-mode cold start looks like in `wsgm.log`:
`Steam UI transport closed: game mode without a Big Picture window …`, then
`Big Picture window detected`, then `Steam UI transport open: Big Picture window is up.`, and only
after that the first `Steam UI patch … Applying`. A patch line before the window line is the
regression. Attended re-verification of this gate on the Claw is still outstanding.

**The gate alone was not enough: state already injected hangs the rebuild too (device-diagnosed over
CDP, 2026-09-01).** A desktop-to-game transition fires `steam://open/bigpicture` against a
SharedJSContext that desktop mode had already patched — the transport there opens on the master
switch alone. Steam then rebuilds its front-end in place (same `CLIENT_SESSION`): the gamepad UI
bootstrap found WSGM's `SteamClient.System.*` namespaces, took the Deck code path, and its calls
went unanswered when the mode flip closed the transport two seconds after the request. Live state of
the hang: `GetDesiredSteamUIWindows()` records the wanted gamepad window native-side, `uiMode` stays
7, `g_PopupManager` holds no popups, `Audio.GetDevices()` pends and then rejects on the bridge's 5 s
timeout — and no window is ever created. The transitions therefore retract first: `SessionModes`
awaits `ShellSession.PrepareSteamUiForBigPictureAsync` (bounded to 5 s) before ANY
`RequestBigPictureWhilePausedAsync`, which retracts the injected UI through the still-open transport
and closes the choke point (`TransportShouldBeOpen` carries the pending flag), so Steam's rebuild
sees stock Windows client state — the one bootstrap Valve ships here. The hold lifts when the
transition settles on any path; the window then reopens the gate and every patch re-applies through
it. `Steam UI transport closed: Big Picture was requested …` before
`Started protocol: steam://open/bigpicture` is the healthy transition shape in `wsgm.log`.

7. **Big Picture's UI (steamwebhelper/CEF) suspends rendering while fully occluded** — a BP intro
   video that initializes under an opaque fullscreen cover stays black even after the cover leaves
   (same behavior BP shows under a game). The boot splash therefore begins its fade **immediately**
   on BP-window detection (the first fade tick drops the layered alpha below 255, which lifts the
   occlusion) with a tight 250 ms detection poll; never hold an opaque cover over a live BP window.
   Additionally, a `steam://open/bigpicture` re-activation while the intro plays kills the video
   (the removed splash→BP "focus handoff") — after the splash closes, do not touch Steam; it takes
   the foreground itself. A no-activate splash was tried and did not affect the symptom. **The
   detection path is boot-critical and must never throw** (regressed 2026-08-12, caught on the
   device across two reboots): the splash's 250 ms poll calls `WindowFinder.FindWindow` →
   `FindProcessIds`, which reads `Process.SessionId` per candidate. That read sits behind a
   deliberately BLANKET `catch` — an audit "fix" narrowed it to
   `InvalidOperationException`/`Win32Exception`, so any other type propagated out of the poll, BP
   was never detected, the splash never faded, and its opaque cover sat over a live BP window: black
   intro video, every boot. Do not narrow it, and do not add an unthrottled `Log` call inside it
   either — at 4 Hz across Steam's several helper processes that alone fills the capped log. The
   general rule: on any poll that feeds splash dismissal or takeover progress, a swallowed exception
   is the lesser failure. Prefer a throttled one-shot warning over a narrower catch.

**Open apps strip + tray host** (`Overlay\OverlayWindow/AppSwitcherViewModel`,
`Core\TrayProtocol.cs`, `Shell\TrayHost.cs`, `Shell\SystemStatus.cs`): the former bottom taskbar
lives inside the quick access sheet — the switchable windows (`WindowFinder.ListSwitchableWindows`)
as a horizontally scrolling chip strip along the sheet's bottom, the tray icons (bounded/scrolling,
budgeted by `OverlayWindow.ComputeTrayMaxWidth` so they can never push the fixed pills off a
1280-wide screen) plus the Wi-Fi/Bluetooth/audio/eject pills, battery and clock from `SystemStatus`
in the sheet's header. Chips and pills keep FIXED sizes at every count. The pills open the
`RadioManager`-backed radio panel; they must never invoke `ms-settings:` (the immersive shell cannot
activate it without Explorer in the session). Chip refreshes reconcile IN PLACE — a wholesale
rebuild would destroy the focused button under the gamepad cursor. `TrayHost` registers a window
class literally named `Shell_TrayWnd` (that's how `Shell_NotifyIcon` finds a tray; game mode has no
explorer, so without it closed-to-tray apps lose their icons) and parses the WM_COPYDATA wire format
in the pure, unit-tested `TrayProtocol` (32-bit handle fields on every architecture). Two hard
rules: (a) **never coexist with explorer's taskbar** — the host is destroyed on
`SessionModes.DesktopModeStarting` (before `StartExplorer`) and recreated on `GameModeEntered`; (b)
the **UIPI gate**: WSGM is usually elevated, and unelevated apps' `Shell_NotifyIcon` WM_COPYDATA is
silently dropped by UIPI unless `ChangeWindowMessageFilterEx(WM_COPYDATA, MSGFLT_ALLOW)` is applied
to the tray window — no shipped replacement shell runs elevated, so this gate is WSGM-specific and
its device verification status must be tracked via the `Tray host created (… WM_COPYDATA filter …)`
/ `Tray icon Added/Rejected` log lines.

A third rule guards the outbound side: (c) **relay only application-defined callback messages in
`WM_USER..0xFFFF`**. `TrayProtocol.IsRelayableCallback` applies that range in `TrayHost.SendClick`;
system messages such as `WM_CLOSE` are never forwarded. The check is on the message rather than the
target integrity level because supported elevated tray applications still need clicks. Registration
remains successful for an out-of-range callback so shell32 does not enter an add/reject loop; only
activation is dropped and logged once per tray-host lifetime. `WM_USER` is the lower bound because
WinForms uses `WM_USER + 1024`, while Qt uses an even higher `WM_APP` value.
