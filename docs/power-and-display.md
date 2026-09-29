# Display profiles, power and wake locks

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

**Display profiles** (`Core\DisplayScale.cs`, `Core\DisplayProfiles.cs`): display management has
four mutually exclusive modes: Off, legacy DPI-only, automatic profiles, and fixed profiles.
Profiles are keyed by stable monitor device identity (with the current GDI source name retained for
Win32 application) and contain resolution, refresh rate, DPI, and — only when the active target
reports advanced-color support — an HDR flag for both Desktop and Game mode. Automatic mode captures
only at a Desktop/Game transition (never continuously, or an exclusive-fullscreen game's temporary
mode would become the saved preference), then restores the last values for the mode being entered.
Fixed mode applies the values edited in Settings. DPI-only retains the crash-safe saved-scale
recovery path. A surviving DPI-only snapshot never authorizes lowering a newly docked display absent
from that snapshot. Panic/uninstall recovery applies the last known Desktop profile without
capturing the possibly half-torn-down current mode, and restores any pending legacy DPI snapshot
even when display management has since been switched Off. Automatic snapshots are runtime-owned;
Settings preserves a newer capture made while its window was open. HDR uses DisplayConfig
advanced-color get/set packets against the path TARGET; never show or apply the flag merely because
it was persisted when the currently active target reports no HDR support.

**Mute during screen-off downloads** (`Shell\DisplayOffMuteService.cs`, `Shell\KeepAwakeService.cs`,
`Interop\MessageWindow.cs`, config `MuteWhileDisplayOff`, default OFF, Settings → System → POWER —
display notification device-verified on the MSI Claw 2026-08-13; download-aware policy implemented
2026-08-22, device re-verification required): the companion to keep-awake, which deliberately lets
the display time out while downloads continue — and Steam plays a sound on every finished download,
into a dark room. The condition is the exact conjunction **setting enabled + this session's display
off + Steam actively downloading**. Screen-off alone never mutes. An active download arriving while
the display is already dark mutes then; display wake restores immediately; the first usable inactive
Steam snapshot starts a 10 s restore grace, and a new active snapshot cancels it. A transient CEF
failure preserves the last usable activity answer rather than inventing a completion, while a
confirmed dead Steam process is inactive. The display signal is
`RegisterPowerSettingNotification(hwnd, GUID_SESSION_DISPLAY_STATUS, DEVICE_NOTIFY_WINDOW_HANDLE)`
on the existing process message-only window → `WM_POWERBROADCAST` / `PBT_POWERSETTINGCHANGE`,
payload a DWORD `MONITOR_DISPLAY_STATE` (0 off, 1 on, 2 dimmed). Microsoft documents

**`GUID_SESSION_DISPLAY_STATUS` as the one interactive user-mode apps must use** —
`GUID_CONSOLE_DISPLAY_STATE` is for services/kernel-mode and `GUID_MONITOR_POWER_ON` is the
superseded legacy setting; do not "simplify" to either. Dimmed is NOT treated as off (the screen is
still lit in front of the user). The open question was whether the notification fires at all when
the Claw's screen times out under Modern Standby; it does (device-verified 2026-08-13). The
`Display state: off/on` and `Mute on display off: …` log lines are the whole remote test surface, so
preserve them. Only a mute WSGM applied itself is undone (a user who muted on purpose stays muted),
and the service restores on `ProcessExit` so a normal exit while the screen is dark cannot strand
the device muted; a hard kill still can, which is why the toggle defaults off. The managed Core
Audio boundary reads the current endpoint before claiming the mute, then applies an absolute
`IAudioEndpointVolume.SetMute` value. The read preserves the user-mute ownership rule; the absolute
write avoids a read/toggle race during recovery.

**The wake side listens on every signal Windows has, because there is no way to ASK.** No user-mode
API reports current display power state (`GetDevicePowerState` explicitly excludes displays), so a
notification is the only mechanism, and WSGM registers all three display power settings plus session
unlock on the same message window: `GUID_SESSION_DISPLAY_STATUS` (primary),
`GUID_CONSOLE_DISPLAY_STATE`, the superseded `GUID_MONITOR_POWER_ON`, and `WM_WTSSESSION_CHANGE` /
`WTS_SESSION_UNLOCK`. **The asymmetry is the safety rule** (`DisplayMuteDecider.MayReportDark`):
only the session setting may report the screen going DARK — console state describes whichever
session owns the console, so acting on its "off" would mute the wrong session after a fast user
switch — while **every** source may report it coming back. The
`Display state: … (via Session | Console | LegacyMonitor)` tag is what makes a missed wake
diagnosable from a pasted log; the extra registrations are not a substitute for the documented one
and must not replace it. Note the blind spot in the `GetLastInputInfo` net below: it does not see
gamepads or the power button, so a user who wakes with the power button and then navigates by
controller (HandheldCompanion blocks controller wake by design) depends entirely on the
notifications.

**Coming back must not hang on any one notification** (reported 2026-08-19: a mute applied during a
screen-off download never came back; `DisplayMuteDecider` in `Shell\DisplayOffMuteService.cs` owns
the pure display mapping and download/display reconciliation). Three rules make the restore path
robust and none of them may be simplified away: the "we muted this" claim is cleared only after a
**confirmed** unmute — the default endpoint is re-enumerated when the display wakes, and the old
code cleared the flag _before_ attempting the read/toggle, so one transient
`GetDefaultAudioEndpoint` failure stranded the mute permanently with nothing left to retry; a failed
attempt is retried on a 2 s timer that runs **only** while the claim is outstanding; and while muted
that timer also watches `GetLastInputInfo` against a baseline taken at mute time (wrap-safe signed
tick compare), because keyboard/mouse/touch input means a lit screen, so the mute is undone even if
the display-on notification never arrives. Restore direction is fail-safe and deliberately
asymmetric with mute: only state 0 establishes the dark half of the mute condition; **every other
value restores** — dimmed and any value Windows may add later — since an unrecognised state must
never be the reason a device stays silent. The added
`Mute on display off: user input while muted, …` line joins the remote test surface.

**Keep-awake wake lock** (`Core\WakeLock.cs`, `Core\SteamDownloads.cs`, `Shell\KeepAwakeService.cs`
— device-verified on the MSI Claw 2026-08-12, including the download hold across screen-off, the
manual cycle, the indicator dot, and the idle-timeout rows): a Windows power request
(`PowerCreateRequest` + `PowerRequestSystemRequired`) that blocks standby entry while held — the
display still times out dark, but Wi-Fi and Steam keep running, which is what makes downloads
survive "screen off" on a Modern-Standby handheld. Research-settled (2026-08-12): downloads during
REAL Modern Standby sleep are impossible for a Win32 app (DAM suspends every desktop process, no
opt-out), so keep-awake is the whole feature — the same model Valve ships as SteamOS "Display-Off
Downloads". Windows-documented limits: indefinite on AC; on battery the OS force-terminates the
request ~5 min after the sleep timeout expires, and the power button always wins. Two independent
holds, each its own request so `powercfg /requests` attributes them: a **manual toggle**
(quick-access Power tab, session-lifetime, survives mode switches) and an **automatic download
hold** — `KeepAwakeService` polls `SteamClient.Downloads.RegisterForDownloadOverview` over the CEF
bridge every 30 s (one-shot subscribe/unsubscribe; fires immediately with a snapshot, live-verified;
active = `update_state != "None" && !paused`, and the Windows client's active state string is
`Downloading`, NOT decky's Linux-documented `Updating`). Release is debounced
(`KeepAwakeService.NextDownloadHold`, 2 consecutive inactive polls) so queue gaps don't flap the
hold; unreachable polls count as inactive for that wake-lock debounce so a dead Steam cannot pin the
device awake. The separate activity answer consumed by display muting is stricter: an unreachable
live client preserves the prior answer, and only a usable idle snapshot or dead process ends
activity. `CefConfig.DownloadKeepAwake` (default on, Settings row on the Integration tab, gated by
the CEF master switch AND off in `--overlay-test`, whose safe-mode contract excludes autonomous
Steam traffic) gates only the automatic hold. The shared poll remains active while either that hold
or download-aware muting consumes it. Hold transitions and the config apply share one gate — a
disable landing mid-poll must not lose to the stale sample, or the hold sticks for the session. The
manual side is a **three-state cycle** (Off → Standby lock → Standby+Display lock → Off), holding a
separate DisplayRequired request for the third state — acquired-before-released so a step never has
a lock gap. Preserve the `Keep awake: … hold acquired/released / manual mode …` log lines — they are
the remote test surface. The row also carries a **WakeWatch-style indicator dot** (the maintainer's
WakeWatch tray tool, deliberately the same color vocabulary): green free / yellow standby-blocked /
red display-pinned / grey unknown, computed from the system-wide power-request list — A **"What's
keeping this awake"** row below it opens the Power tab's own in-place sub-view
(`Overlay\WakeLockHoldersView.cs`, grouped by `Core\WakeLockHolders.cs`) listing every requester —
WakeWatch's right-click detail, reimplemented: dedupe on (label, detail, reason) so thirty identical
Steam requests read as `steam.exe ×30`, sorted by count then name, with the caller kind, pid, path
and reason string on the second line. It is the first sub-view that belongs to the **Power** tab
rather than Tools, so `LeaveWakeLockSubView` restores `PanelPower`, and it appears in `AnySubView`,
`DefaultFocusTarget`, `TryCancelSubView`, the tab-switch teardown and the `Activated` reset like
every other one. Unlike the summary line it deliberately does NOT hide WSGM's own request: the row
above already explains WSGM's holds, but the full list is answering "what is holding this awake" and
must not omit an answer. An unelevated read yields "couldn't read", never an empty all-clear.
`Interop\PowerRequestList.cs` calls the undocumented `NtPowerInformation(GetPowerRequestList=45)`
against ntdll directly (the documented wrapper rejects the class; needs elevation, denied → grey),
decodes the version-dependent layout through bounds-checked readers ported from WakeWatch's
`power.rs` (MIT, same author) — any structural surprise must yield grey "unknown", NEVER a false
all-clear — and `Core\WakeLockStatus.cs` maps entries to state + a collapsed holder summary (WSGM's
own pid colors the state but is excluded from the summary). Polled at 1.5 s only while the panel is
open. The Power tab also hosts four **idle-timeout rows** (screen-off / standby × battery /
plugged-in) that cycle presets via `Core\PowerTimeouts.cs` — the flat powrprof value-index API, NOT
`powercfg /q` parsing (localized output, same trap as netstat); these are a user-facing convenience
over the active scheme, deliberately not snapshotted/restored state.

## Refresh rates: what a panel advertises is not what a driver accepts

**Device-verified on the reference MSI Claw 8 AI+ A2VM, 2026-08-30.** The two lists differ, and
every frame-limit strategy depends on the difference.

`EnumDisplaySettings` reports 30/48/60/75/100/120 Hz at 1920x1200, and
`ChangeDisplaySettingsEx(CDS_TEST)` accepts all six. The panel's EDID advertises **only 60 and 120**
— two detailed timings, 315.50 MHz and 157.75 MHz over a 2080x1264 total. The other four exist
because the panel declares a 30-120 Hz adaptive-sync range in its display-range-limits descriptor
and the driver synthesizes timings inside it. Arc Sync independently reports the same 30-120 band.

The synthesized modes are real, not cosmetic: applying 48 Hz moved DWM's `rateRefresh` from 119.999
to 47.997 and back. **Windows Settings kept showing 120 throughout**, because the change was applied
without `CDS_UPDATEREGISTRY` and Settings reads the persisted configuration — which is exactly the
property that makes a game-scoped refresh change safe. Exit, a crash, or a reboot all restore the
user's own configuration with WSGM doing nothing.

Consequences encoded in `Core\FrameLimitPairing.cs`, `Core\EdidModes.cs` and
`Core\RefreshRatePairingService.cs`:

- Enumeration alone cannot tell an advertised mode from a synthesized one, so the native-modes
  strategy needs the EDID. Without it that strategy would silently equal full frame doubling.
- Rates are enumerated and then tested; a driver may refuse one it enumerated. `CDS_TEST` changes
  nothing and is safe while a game runs.
- Discovery is cached because each candidate costs a driver round trip.
- The frame-doubling strategy prefers the lowest mode at **at least twice the cap** (30 FPS at
  60 Hz, 60 at 120), because a 1:1 cadence keeps adaptive sync's low-framerate compensation out of
  reach and a 30 Hz panel visibly flickers (maintainer-directed 2026-09-02). Where no doubled
  multiple exists, and under native-modes always, pairing takes the **lowest** exact multiple, since
  refresh rate is a power cost.
- A cap with no exact multiple leaves the refresh rate alone. Forcing a near-miss mode adds judder
  rather than removing it.
- A mode change is not free — an exclusive-fullscreen title can hitch, minimize, or drop out across
  one — which is why cap-only is the default wherever variable refresh already covers the range.

## Variable refresh over IGCL

**Device-verified on the same unit and date, unelevated.** `ControlLib.dll` ships with the Intel
driver and is already in `System32`; IGCL initialises at v1.1. The internal panel reports
`IsIntelArcSyncSupported` across 30-120 Hz with the profile at `EXCELLENT`. Writing `OFF` and
restoring the saved parameter struct both succeed, and the read-back confirms each.

The panel belongs to the device, so the transport belongs to the plugin
(`plugins\WSGM.Device.Msi.Claw8A2Vm\ArcSyncTransport.cs`), and WSGM only projects the capability.

Four facts that cost real time to establish:

- Both enumerations are **two-call**: ask for the count with a null buffer, then fetch. Passing a
  buffer straight away returns nothing.
- The panel is chosen by **which output answers**, never by index. The reference unit enumerates
  twelve display outputs of which one is real; the other eleven return `CTL_RESULT_ERROR_KMD_CALL`.
  An external display when docked is a different output.
- IGCL's `bool` is **one byte**. A managed `bool` is four and would shift every float after it.
- Every call passes its own `sizeof` in a `Size` field and the driver refuses a mismatch — and that
  refusal is indistinguishable from "this machine has no variable refresh", so a layout drift would
  remove the feature silently. The sizes are 36 / 24 / 28 and are pinned by a test.

Turning the profile `OFF` collapses the reported range to 120/120, which is a second confirmation
independent of the profile enum. That is why this capability reports a verified read-back rather
than an applied-unverified one.
