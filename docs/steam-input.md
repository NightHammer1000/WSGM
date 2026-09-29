# The Steam Input lease

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

1. **Steam Input's desktop profile swallows the controller from every API** (XInput/DInput/HID,
   system-wide) the moment it activates. The **only** reason the overlay may take focus
   (Game-Bar-style, which mutes the game while the panel is open) is the **Steam Input Lease**: its
   gate blocks controller access inside `steam.exe`, leaving SDL direct access for WSGM without
   changing Steam's active layout. The lease is **scoped to the overlay/taskbar lifetime** —
   acquired before each focused surface opens and released after the last one closes. It is an open
   named-pipe connection, so Windows releases it after a WSGM crash; normal release requests Steam
   controller rediscovery. **Delivery is a proxy DLL, not injection (since the Steam Input
   Management work).** `WSGM.exe` NEVER injects — the C ABI's `allow_injection` defaults to false
   and `SteamInputBlocker` sets it explicitly, so this is a property of the code, not a promise. The
   payload is deployed by `Core\SteamInputShim.cs` into **Steam's own install directory** under a
   name Steam resolves through the default DLL search order (`XInput1_4.dll` first — ValvePlug
   proves that vector loads — then `dinput8.dll`), and Steam loads it itself. Verified on a live
   client: nothing in `steam.exe` hardens the search order (no `SetDefaultDllDirectories` /
   `AddDllDirectory` anywhere, statically or dynamically; the lone `SetDllDirectoryA` in
   `SteamUI.dll` cannot displace the application directory), neither name is a KnownDLL, and
   **nothing in Steam's directory statically imports XInput or DirectInput** — so a missing export
   degrades a `GetProcAddress` to NULL instead of failing a load. Three rules are load-bearing:
   never overwrite a file the ownership signature does not prove is ours (ValvePlug and Special K
   claim the same names); never `File.Move(..., overwrite: true)` on the park/restore path
   (`REPLACE_EXISTING` fails against a mapped image, which is why disabling parks to `.dlld` instead
   of deleting); and resolve the real system module by FULL System32 path inside the gate, because
   the loader keys loaded modules by BASE NAME and a bare-name load would hand the gate its own
   image to detour. **Hooks are installed on the FIRST LEASE, never at load (device-verified
   2026-08-19).** MinHook's `MH_ApplyQueued` suspends every thread in the process to patch safely.
   Under injection that ran against a fully started, quiescent Steam. As a proxy the DLL is mapped
   during Steam's OWN startup, and suspending threads while the loader lock is being taken
   constantly hung Steam on the first cold boot after an install — completely, unkillable by
   `steam://exit` or a process-tree kill, Task Manager required, with a second (warm) start working.
   `ensure_hooks_installed()` therefore defers `install_hooks()` and the recovery warm-up to the
   first `AcquireLease`. Never move hook installation back into `DllMain`/`server_thread`.

   **The proxy forwarders start BLOCKED and the worker releases them only after complete
   initialization (implemented 2026-08-22; DEVICE-DISPROVEN as a complete cure for the startup hang
   later that day).** This is deliberately separate from `LEASE_COUNT`: a bootstrap block has no
   surface owner and must not install the HID hooks or appear as a client lease. `DllMain` first
   records its `HINSTANCE`, pins the image before its worker can race SDL's `FreeLibrary`, and
   starts the worker. Until that worker finishes, every XInput or DirectInput proxy export returns
   its disconnected fallback without allocating, resolving an export, or entering the Windows
   loader. The worker identifies the deployed vector, loads the real module by full System32 path
   exactly once, caches the complete forwarding table, then publishes one release store and posts
   the ordinary `WM_DEVICECHANGE` rediscovery notification. A failed initialization is cached and
   remains blocked; no Steam call can retry it. A rejected self-load is balanced with `FreeLibrary`
   rather than leaking a module reference. This matches the startup property that made ValvePlug the
   useful control: it begins blocked, pins itself during process attach, and resolves the real
   module on its own initialization thread.

   The earlier identity fix was necessary but not sufficient. `DllMain` began recording its module
   before anything else after the 1.5.0 proxy hung Steam on every Claw cold boot; that build then
   passed 10 consecutive boots (device-verified 2026-08-20), but another XInput startup hang was
   observed on 2026-08-22. Before the identity store, `proxy::is_self` failed closed until the
   server thread ran, so every XInput call loaded the real DLL, rejected it as possibly-us, cached
   nothing, and repeated while SDL probed four user indices. The resulting loader-transaction storm
   starved the server thread that would end the window. Warm starts and holding the stick UP both
   broke the loop from outside, which identifies a livelock rather than a fixed lock cycle.
   Recording the handle closed that particular window; bootstrap blocking removes real-module
   loading from Steam's startup threads altogether, but the same hang subsequently recurred. The
   strongest discriminator was the launch context: it repeated when WSGM started Steam during direct
   boot, while starting Steam by hand with the same deployed shim succeeded. The next failed boot
   supplied decisive trace evidence: PID 12064 completed forwarding and rediscovery in 2 ms, reached
   `control pipe listening`, and served zero bootstrap fallbacks — equivalent to successful traces.
   The failed path instead mutated Steam's card library over CEF before any Big Picture window
   existed (see `docs\steam-cef.md`). Do not label proxy initialization timing as the root cause
   again without a failing trace that differs.

   Inspection after the recurrence also found that rustc's automatic cdylib export ordinals had
   silently placed `DirectInput8Create` at XInput ordinal 104 and `DllRegisterServer` at 109. A
   dynamic lookup of either undocumented XInput ordinal would therefore call an incompatible
   function signature. `build.rs` now supplies one authoritative `.def`: named XInput exports match
   the System32 ordinals, ordinal-only 100/101/102/103/108 remain NONAME, 104/109 remain empty, and
   the **retained** name-resolved `dinput8.dll` fallback lives at non-conflicting ordinals 200-205.
   `eng\build-steam-input-lease.ps1 -Validate` inspects the finished PE and fails if that contract
   drifts. The observable check with no lease is ours plus the real vector module loaded by the
   worker; `xinput1_3/1_2/1_1/9_1_0` appearing means the first lease installed hooks. Keep the
   native `steam_input_gate.dll` and `steam_input_lease_ffi.dll` beside WSGM.exe, and preserve the
   `Steam Input lease acquired/released` logs for device diagnosis.

   **Every mapped gate writes a per-process startup trace** to
   `%LOCALAPPDATA%\WSGM\steam-input-gate-<steam-pid>.log`; the cold-start launcher writes the exact
   expected path into `wsgm.log`. Each line is emitted only by the worker after loader-lock release.
   `DllMain` and the proxy exports record atomics only, so tracing cannot add file I/O or locks to
   the path being diagnosed. The trace distinguishes loader attach/self-record/pin/worker request,
   vector detection, forwarding begin/end, the number of startup calls that received the bootstrap
   fallback, device rediscovery, and control-pipe readiness. Per-pid names deliberately preserve the
   failed direct-boot trace after a later manual Steam start supplies the control comparison. A
   missing expected file means the gate worker never reached its first post-loader phase (or the
   profile directory was unavailable); a last line at `forwarding initialization started` localizes
   the stall inside that operation; `control pipe listening` proves gate initialization finished.

   **The gate's control pipe carries an explicit DACL.** `server_thread` builds
   `D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;<token owner>)` once before accepting clients. System,
   administrators and the token owner retain the full access required by the duplex client, while
   read-only opens cannot consume a pipe instance and worker. The owner comes from
   `GetTokenInformation(TokenOwner)` because `CREATOR OWNER` is not expanded in a directly applied
   DACL. If token lookup or SDDL conversion fails, pipe creation uses the Windows default descriptor
   so controller blocking remains available. The startup trace records whether the scoped or
   fallback descriptor was used.

   **Owner claims outlive a failed native acquire by design:** `AcquireFor` registers the focused
   surface before it attempts injection, so every deactivate/close path must call `ReleaseFor` even
   when Steam was unavailable and `IsApplied` stayed false. During the overlay-to-Settings handoff,
   Settings registers first and the deferred overlay close removes the overlay owner; abandoning
   either name leaves the controller blocked after the visible surface is gone (device-observed
   2026-08-15). Settings ignores the transient deactivation caused by that still-closing 150 ms
   overlay, then resumes normal focus-based ownership after the overlay acknowledges the handoff;
   releasing during the overlap drops and re-revokes the controller (device-observed 2026-08-12).
   **Live-verified end to end (2026-08-12, dev box, `steam-input-lease.exe`, real Steam Controller
   connected):** acquire took the pad from Steam (`tracked HID handles` 1 → 0,
   `handles revoked by last transition` = 1), Steam had rediscovered it within 700 ms of release (0
   → 1), and an explicit `--rescan` moved Steam's scan counter 14 → 16. **Measured cost:** cold
   inject + acquire
   - release **492 ms** (one-off; the injection dominates), warm acquire + release **41-42 ms** with
     and without a pad, and a single pipe reply (`--status`) **12-16 ms across ten consecutive
     calls**. Recovery layout discovery runs once during gate warm-up and is cached; pipe replies do
     not repeat the cross-process scan. The first acquire must still report internal-recovery
     capability, and the pinned gate remains mapped until Steam restarts.
