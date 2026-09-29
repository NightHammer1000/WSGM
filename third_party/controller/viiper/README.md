# VIIPER, and what WSGM needs from it

WSGM's virtual controller targets are created by [VIIPER](https://github.com/Alia5/VIIPER), a
userspace virtual-USB framework that speaks USBIP. Nothing in this directory is a checkout: it holds
the exact upstream revision WSGM builds against and the patches that revision still needs. The
reasoning for choosing VIIPER over HIDMaestro is in the parent `README.md`.

## Pinned revision

- Repository: `corando98/VIIPER`, branch `viiper-controller`
- Commit: `024aef3a5659fb54d9675929d05f155f47049c4c`

That branch is well ahead of `Valkirie/VIIPER` and carries the performance work this integration
depends on: opt-in NAK-idle interrupt-IN endpoints, hardware-paced completions, a type-agnostic clib
fast path, value-typed input state, and a `GOMAXPROCS` cap.

## Patches WSGM applies

`0001-steamdeck-idle-and-stick-fixes.patch` carries two fixes that are merged in `Valkirie/VIIPER`
but not on this branch. A third, the SDL3 `ucLength` fix, is already present here and needs no
patch.

| Source | Fix | Why it matters to WSGM |
| --- | --- | --- |
| Valkirie/VIIPER#3 | Clamp stick Y off `-32768` | SDL3's Deck driver negates stick Y with a plain unary minus, so `-32768` wraps to itself and a fully-down stick reads as fully up. Real Deck sticks are calibrated and never report it. |
| Valkirie/VIIPER#2 | Placeholder mouse and keyboard endpoints stay pending | They carry no data, yet completed a transfer on every poll. That both wakes the system from standby and burns CPU for nothing. |
| WSGM | Stale quaternion assertion | `9de6355` deliberately dropped the forced identity orientation quaternion, because a frozen identity made Steam ignore raw angular velocity and collapse gyro-to-stick to centre. The test still expected `0x4000` and was left failing, so the package had no green baseline to regress from. |

`0002-attach-plugin-hardware-layouts.patch` is WSGM's own, and without it `viiper_device_attach`
cannot succeed against usbip-win2 0.9.7.8. See the next section — it was found by running the call,
not by reading the code.

`0003-device-add-does-not-attach.patch` is WSGM's own and stops `viiper_device_add` attaching. That
matters twice over. Upstream attached in both `add` and `attach`, so the documented pair produced
**two** USB/IP attachments of one device — two ports in `usbip port` pointing at the same bus/dev,
and two identical controllers in Steam's controller list (device-observed 2026-08-29). Attach was
only ever described as a retry there, so following the API literally gives a duplicate rather than a
retry. It also made the intended ordering impossible: a caller cannot present a neutral first frame
before Windows enumerates the device when adding it is what enumerates it. With attach explicit,
WSGM opens the fast handle, submits a neutral frame, and only then lets the host see the controller.

`0004-detach-usbip-port-on-device-remove.patch` backports the detach change from Handheld
Companion's bundled VIIPER commit `679f7e0` without replacing the pinned
`corando98/VIIPER@024aef3a` `viiper-controller` baseline, its newer performance work, or the Steam
Deck patches above. usbip-win2 assigns a client port when a device is attached. Removing only
VIIPER's server-side device closes its stream but does not plug that port out of the Windows driver,
so an immediate replacement can collide with the stale attachment. The patch retains the port
returned by either attach route and issues `IOCTL_PLUGOUT_HARDWARE` before server-side removal, with
the command route as fallback.

`0005-quiesce-feedback-before-client-detach.patch` tightens the removal ordering that patch 0004
introduced. Patch 0004 performed the blocking driver plugout while holding VIIPER's global C-API
mutex and while its reverse feedback callback was still registered; usbip-win2 may deliver a last
output packet as it cancels the endpoints, so a callback could in principle re-enter WSGM from
VIIPER's Go thread while the caller synchronously waited for removal. Removal now deletes the
registration, drains callbacks that already crossed the registration boundary, and releases the
global mutex before asking the driver to plug the port out.

That patch was written against the wrong diagnosis, and the record matters more than the patch. The
"cannot create a new stack guard page" crash on every live target change was **not** native
re-entry: a procdump first-chance `STATUS_STACK_OVERFLOW` dump (2026-09-01) showed 1,598 frames of
`ViiperControllerBackend.SafeNative` calling itself — a managed overload-resolution bug in WSGM
(see the remark on that method). Patch 0005 stays because holding the C-API mutex across a driver
request was wrong on its own terms, but it never fixed, and could not have fixed, that crash.

PR #2 needed adapting rather than applying verbatim: this branch replaced the inline `ctx.Done()`
waits with `device.BlockUntilDeadline`, so the two endpoint cases collapse into one that blocks and
returns no data.

`0006-report-credible-deck-attributes.patch` is WSGM's own. Steam decides controller features from
the `GET_ATTRIBUTES_VALUES` identity block, and with the baseline's answers — board revision 1 and
a BCD-style firmware build time (`0x20260226`, which reads as the year 1987 when taken as the unix
epoch Steam expects) — Steam never sends `ID_TRIGGER_RUMBLE_CMD` (0xEB) to the virtual Deck, while
SDL sends it regardless: rumble worked from SDL applications like RPCS3 but never from Steam Input
(device-observed 2026-09-02, via WSGM's undecoded-feedback log showing Steam probing attributes and
then withholding 0xEB). The patch reports the identity hhd's emulated Deck presents — board
revision `0x2e`, real epoch firmware and bootloader build times, and the trailing attribute set
(`0x0c`..`0x0e`) a current Deck answers — which Steam demonstrably sends rumble to.

## How WSGM builds and binds it

`eng\build-viiper.ps1` checks the pinned revision out, applies the patches, optionally runs the Deck
device tests, builds `libviiper.dll` with `go build -buildmode=c-shared ./clib`, and stages it with
its header and licences into `src\WSGM\Native\Viiper`. `WSGM.csproj` copies that beside the
executable. The staging directory is generated and is not committed.

Two toolchains are required and the script names them rather than failing obscurely: Go, and a C
compiler for cgo. Without a C compiler Go quietly sets `CGO_ENABLED=0` and then reports "build
constraints exclude all Go files", which says nothing about the real cause.

The library exposes a flat C ABI over blittable types, so WSGM binds it directly through
`LibraryImport`. VIIPER owns the virtual USB implementation in-process; no helper process is needed.

## Build baseline

Verified with Go 1.27.0 and WinLibs GCC on the reference Claw, 2026-09-01. `go build ./...` succeeds
for the whole tree, `go test ./device/steamdeck/...` passes with the patch applied, and
`eng\build-viiper.ps1 -Validate` runs the whole sequence end to end.

**The binding is verified end to end against the real library and the real driver.** Every entry
point WSGM uses — `viiper_init`, `viiper_bus_create`, `viiper_device_add("steamdeck")`,
`viiper_device_attach`, `viiper_device_open_fast`, `viiper_device_set_input_fast` with a 64-byte
Neptune frame, `viiper_device_remove`, `viiper_shutdown` — returns success, and the attach is real
rather than nominal: while attached, Windows enumerates `USB\VID_28DE&PID_1205` as a composite
device with the expected three interfaces (MI_00 keyboard, MI_01 mouse, MI_02 the vendor-defined
controller), and after teardown no `VID_28DE` device is present. Verified on the reference Claw,
2026-08-29, unelevated.

## The attach ABI break, and why the pin is not optional

`viiper_device_attach` failed on the first real attempt, and the reason is worth recording because
the pinned version is what fixes it.

VIIPER attaches by issuing usbip-win2's `IOCTL_PLUGIN_HARDWARE` against the driver's device
interface, falling back to running `usbip.exe`. Both halves were broken here:

- **usbip-win2 0.9.7.8 changed the IOCTL's structure.** `usbip::vhci::ioctl::plugin_hardware` gained
  a trailing `char serial[SERIAL_BUFSZ]`, taking it from 1100 to 1116 bytes. The driver validates
  the caller's `size` against its own `sizeof` and rejects a mismatch with
  `ERROR_INSUFFICIENT_BUFFER` before doing anything. VIIPER encodes the 0.9.7.7 shape, so on
  0.9.7.8 every attach is rejected on size alone. Confirmed against the installed driver by issuing
  the IOCTL directly at both sizes: 1100 returned `122 ERROR_INSUFFICIENT_BUFFER`, 1116 got through
  to `1225 ERROR_CONNECTION_REFUSED` — the expected answer when nothing is listening on the port.
- **The `usbip.exe` fallback cannot save it.** The usbip-win2 installer does not put
  `%ProgramFiles%\USBip` on `PATH`, on this machine in neither the user nor the machine variable, so
  the fallback fails with `executable file not found in %PATH%`. It is not a fallback WSGM can rely
  on, and the answer is not to start editing `PATH`.

So the 0.9.7.7 pin has a functional reason on top of the open BSOD reports: it is the version
VIIPER's ABI actually matches. `0002-attach-plugin-hardware-layouts.patch` makes the backend work on
both, by declaring the newer structure and trying the two known sizes newest-first. That retry is
safe rather than a repeated attach: a size rejection happens before the driver acts, is reported
with its own specific error code, and any other failure stops immediately instead of being retried
against a layout the driver has already refused.

**Do not "simplify" that loop to a single size, and do not replace it with a version probe.** The
driver's own rejection is the authority on which layout it wants; a version number read from
somewhere else is a second source that can be wrong.

Three packages fail on this branch **before** any WSGM patch and are the accepted baseline:
`device/xboxelite2`, `device/xboxgip`, and `internal/server/api` (build failure). None is touched by
the patch, and none is on WSGM's path — but a fourth failure appearing is a regression worth
investigating.

## What the installer must provide

VIIPER needs three things on Windows, and none of them may be installed by the running shell —
INV-020 keeps driver, service, and certificate installation in the installer, as an explicit,
user-approved, elevated step that verifies the locked component identity first.

1. **usbip-win2**, which supplies the generic signed kernel-mode USB/IP driver and the client device
   VIIPER attaches to. Pinned and signature-verified in `../controller-components.lock.json`
   (`USBip-0.9.7.7-x64.exe`, publisher thumbprint `9AC56B6C…`). This is the one kernel component,
   it is generic, and it never needs to know about specific device types — which is the whole reason
   this approach avoids shipping a driver per controller.
2. **`libviiper`**, the VIIPER server built as a shared library from `clib/`. It runs in userspace,
   embedded in WSGM's controller component rather than as a separate service, and listens on a local
   USBIP port. It is built from the pinned revision above with the patches applied.
3. **HidHide**, already pinned, and already mandatory only while controller management is active.

Licensing is settled and is not a blocker: WSGM is GPL-3.0 and so is the VIIPER server, so shipping
it is straightforward. Retain the upstream notices as for any other shipped component.

The remaining installer requirement is ordinary failure handling: verify each locked component
identity before installing it, and keep a machine where usbip-win2 is absent or declined installing
and running WSGM normally, with controller management simply unavailable — exactly as today.

### State of the installer work

`WSGM.iss` declares a `controller` component; `libviiper.dll` with its notices and header, and the
verified usbip-win2 installer, ship under it. Every one of those entries is
`skipifsourcedoesntexist`, because they exist only when the release machine has a Go toolchain, a C
compiler, and a network — `build.ps1` skips each loudly rather than failing an otherwise good
release.

The driver step is a separate ticked task, `Install-UsbipDriver.ps1`, run from `[Run]` before setup
restarts anything of WSGM's. It prefers the staged installer and falls back to downloading the same
pinned asset, re-verifies digest and signer on this disk either way, skips an install that is
already present or newer, and confirms `usbip2_ude` is registered afterwards instead of trusting the
exit code. Every failure is non-fatal: a machine without the driver runs WSGM normally with
controller management unavailable.

Two things learned by doing rather than reading, both of which would have produced a broken step:

- The release asset is an **Inno Setup** installer, not NSIS. VIIPER's own `scripts/install.ps1`
  passes `/S`, which Inno Setup does not recognise — that script pops the full interactive installer
  instead of installing silently. The correct switches are `/VERYSILENT /SUPPRESSMSGBOXES
  /NORESTART /NOCANCEL /SP-`.
- **`System32\drivers\usbip2_ude.sys` does not exist even on a working install.** It is a universal
  driver and lives in the driver store; on the reference Claw the real path is
  `DriverStore\FileRepository\usbip2_ude.inf_amd64_…`, reached through the `ImagePath` of the
  `usbip2_ude` service key. A file test — which is what VIIPER's script falls back to — reports "not
  installed" on a machine where it is. `pnputil` is no substitute either: its output is localised,
  and it prints German here.

With the driver present, `viiper_device_attach` is the one entry point the binding has not yet been
driven through. That is now testable on this machine, which already carries a usbip-win2 install.
