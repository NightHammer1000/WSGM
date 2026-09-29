# Elevation, de-elevation and the launch wrapper

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

5. **De-elevation:** the naive `TokenLinkedToken` → primary-token route fails (error 1346, needs
   `SeTcbPrivilege`); the working mechanism is a one-shot scheduled task (`InteractiveToken`, no
   RunLevel, task XML **must be UTF-16**, never ship `/NoUACCheck` — EDRs flag it). Win11 explorer
   usually de-elevates itself; `ExplorerControl` verifies 5 s after start and repairs once via the
   task on blocking terminal recovery paths. That route is valid for ordinary one-shot processes and
   for a fail-open desktop, but it is not normal transition success: Explorer inherits the Task
   Scheduler launch owner's job and desktop launchers such as Mod Organizer 2 can then fail
   `CREATE_BREAKAWAY_FROM_JOB` with error 5. Normal game-to-desktop transitions instead use the
   medium/jobless fixed-purpose shell anchor captured from the original canonical taskbar owner;
   they verify the resulting taskbar owner is current-session, medium, canonical, jobless, and
   stable. Scheduler fallback is always logged and surfaced as degraded, even if a later observation
   happens to report the resulting Explorer as jobless. That recovery fallback gives task creation,
   dispatch, deletion, and shell-readiness observation one shared absolute deadline; cancellation or
   a process-wait fault stops an active `schtasks`, an uncertain task creation is cleanup-eligible
   while budget remains, and best-effort cleanup never receives a fresh timeout. Once `/Run` begins,
   a timeout or fault is an unknown dispatch rather than a proved failure; shell recovery must keep
   game-mode surfaces retired while a late Explorer may still appear. Modern Settings activation
   uses this same scheduled task to run a narrow WSGM one-shot at medium integrity before opening
   `ms-settings:`. The shell is normally elevated, and relying on `ShellExecute` directly only works
   while unelevated Explorer happens to broker the request; never start Explorer just to open
   Bluetooth or Wi-Fi Settings. `WSGM.Launch.exe` is the user-facing extension of the same mechanism
   for Steam games that reject elevation. It is the **single** launch wrapper: it replaced
   `WSGM.Deelevate.exe` and `steam-input-lease.exe`, which the installer now deletes on update, so
   anyone who had pasted one of the old commands must re-apply the fix (call this out in the release
   notes). Behaviours are selected by flag:
   `"...\WSGM.Launch.exe" [--deelevate] [--input-lease | --input-lease-inject] -- %command%`, at
   least one required, the target command always after `--`. **The two lease flags differ only in
   delivery and are mutually exclusive.** `--input-lease` connects to the resident shim and NEVER
   injects; `--input-lease-inject` injects and is the only route in the shipped product that can.
   The Tools-tab button picks between them from the Steam Input Management setting **at apply time**
   (`LaunchWrapperCommand.ForCurrentInputMode`), so the value a game carries always names the route
   it will take. Two consequences: `ModeFor` must match on TOKEN boundaries, because
   `"--input-lease-inject".Contains("--input-lease")` is true and a plain `Contains` reports both
   behaviours at once; and every launch option written before this split says `--input-lease`, which
   now means shim-only — with Steam Input Management off those games silently stop blocking, so the
   toggle logs the affected appids and the release notes must say to re-apply the fix. The elevated
   wrapper must remain alive for the target lifetime, preserve Steam's arguments/environment/CWD,
   and stop the target tree if Steam terminates the wrapper. Do not replace it with a
   fire-and-forget scheduled task or an Explorer-token shortcut. **The lease is the OUTER
   behaviour**: its gate injects into an elevated `steam.exe`, which a medium-integrity process
   cannot do, so it is acquired by the elevated parent _before_ the de-elevation hand-off and
   released after the medium child reports the target's exit. **Both paths wait on a job object, not
   on the process they started** (`--input-lease` alone in the native wrapper, which starts the
   target suspended and assigns before resume; the de-elevated child in `WSGM.Launch\JobObject.cs`,
   which assigns right after `Process.Start`): a game behind a launcher exits its root process
   seconds in, and waiting on that alone released the lease mid-session and told Steam the game had
   stopped. The job is also what makes the stop-on-parent-exit path reach orphaned descendants.
   Lease failures fail **open** — log, tell the user, launch anyway — and so does an impossible
   de-elevation (UAC switched off leaves no limited token to hand out; the child tags that failure
   and the parent launches the game as-is). That fail-open is gated on the **parent's own** token,
   never on the peer's report: the handshake pipe must grant the user SID (invariant 5b), so any
   same-user process can connect first and send the tag. `Elevation.HasLinkedLimitedToken()` asks
   whether this process has a linked limited token — `TOKEN_ELEVATION_TYPE == Full` means
   de-elevation IS possible here, so the tag is refused and the wrapper returns 1 rather than
   launching the game elevated. Only `Default` (UAC off, built-in Administrator, standard user) and
   an unqueryable token still fail open, which is exactly the device case the fail-open exists for.
   Reading the peer's token instead would race the genuine child, which exits milliseconds after
   writing. **Accepted narrowing:** if the medium child's own token query fails on a UAC-enabled
   machine, it sends the same tag and the game now does not start at all, where it previously
   started elevated. The refusal line names the observed state so a pasted log distinguishes the
   two. An error out of `run_wrapped` means only that the target NEVER STARTED, because that is what
   the caller does about it; a release handshake that fails after the game exited returns the exit
   code instead, or the wrapper would start a finished game a second time. It is still **reported**,
   through `WrappedRun.release` (ABI 3 added the `release` output to `sil_client_run_wrapped`, which
   previously discarded it): blocking is lifted either way, but a failed handshake means Steam was
   never asked to rediscover controllers, so `WSGM.Launch` writes
   `Steam Input lease released, but Steam controller recovery did not run …` to `launch.log` — the
   only surface that failure was ever diagnosable from. **Four device-verified invariants make it
   actually work when Steam is elevated (each was a separate real failure, 2026-08-12):** (a) it
   MUST be a **console** subsystem exe (`<OutputType>Exe</OutputType>`, shows a CLI window) — a
   windowless `WinExe` is treated by Steam as a game and gets Steam Input hooked into it, dying
   before it logs; (b) the elevated parent's IPC pipe MUST grant the **User SID** explicitly
   (`NamedPipeServerStreamAcl`
   - `WindowsIdentity.User`), NOT `PipeOptions.CurrentUserOnly` — an elevated server's
     CurrentUserOnly grants the token OWNER = `BUILTIN\Administrators`, deny-only in the child's
     filtered token, so the medium child's connect fails "Access is denied"; (c) the medium child
     launches the game with `__COMPAT_LAYER=RunAsInvoker` in its environment, or a target with a
     RUNASADMIN flag / admin manifest fails a medium `CreateProcess` with `ERROR_ELEVATION_REQUIRED`
     (740); (d) for a **non-Steam (custom) shortcut** Steam ignores an exe-replacement `%command%`
     launch option and runs the original target anyway — the wrapper goes in the shortcut's
     **Target**, the real program in **Launch Arguments**. Never reintroduce `CurrentUserOnly` on an
     elevated↔medium pipe, and never make the wrapper WinExe. `Core\SteamLaunchConfig.cs` writes (d)
     into the running client so the user never has to; see invariant 11.

## Steam client launch integrity

WSGM starts Steam at its own integrity by default, which means elevated in a normal shell session.
That is deliberate: WSGM drives the running client over CEF and sends it window messages, and a
mismatched pair loses those messages to UIPI. The cost is that every game Steam launches inherits
the elevation.

`AppConfig.SteamLaunchUnelevated` is the user-owned choice between the two. When it is set and WSGM
is elevated, the cold start goes through the same de-elevating scheduled task Explorer uses
(`UnelevatedLauncher`), so the whole client — not an individual game — runs at medium integrity.
From an unelevated WSGM the setting changes nothing, because the ordinary launch already produces a
medium-integrity Steam.

Both the cold start and the auto-relaunch after Steam exits pass through
`SessionModes.StartBigPicture`, so the choice cannot apply to one and not the other. The selected
integrity is logged on every launch (`Steam launch integrity: …`), including when de-elevation was
requested but unavailable, so a pasted log settles which one actually happened. The scheduled-task
route returns no process handle, so the Steam Input shim startup-trace line is only logged for the
integrity-matched path that has one.

`WSGM.Launch` is unaffected and keeps de-elevating individual games independently.
