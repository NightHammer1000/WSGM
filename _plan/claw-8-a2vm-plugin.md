# MSI Claw 8 AI+ A2VM Device Plugin

Status: lean implementation and hardware record, rewritten 2026-08-28

Target: MSI Claw 8 AI+ A2VM, baseboard `MS-1T52`

Reference unit: this development machine

Hardware observations: 2026-08-27 unless stated otherwise

## Purpose

This is the first complete WSGM Device Plugin and the reference implementation for the public SDK.
It owns the Claw's identity, power, fans, telemetry, controller modes, physical input, rumble,
motion, OEM controls, firmware shortcut suppression, RGB, profiles, diagnostics, and restoration.

The document records facts and behavior that must survive architectural simplification. It does not
require a general evidence database, promotion state machine, implementation-module catalog, or
multi-plugin policy.

## Required product behavior

- Match only the `MS-1T52` Claw 8 AI+ A2VM definition and its verified firmware gates.
- Start asynchronously with WSGM and remain active across Desktop Mode, Game Mode, games, and Steam
  restarts.
- Stop only when WSGM exits or Device Integration is turned off.
- Leave zero hooks, watchers, handles, writes, virtual targets, or WSGM HidHide state when disabled.
- Keep controller management optional beneath device integration.
- Publish all handheld controls through the WSGM overlay in Desktop and Game Mode.
- Project the frequent subset through Steam's native QAM.
- Support Steam Deck Composite, Xbox 360, and DualShock 4 through WSGM's controller layer.
- Preserve global/per-application device profiles, controller-target overrides, and AutoTDP.
- Restore temporary hardware/controller ownership before handing control to Handheld Companion or
  another manager.
- Keep healthy capabilities usable when one resource is absent or fails.

The repository-wide plugin rule also applies: this Claw package can be the sole installed Device
Plugin. If another plugin package is present, normal WSGM startup refuses before matching either.

## Explicit non-goals

- No A1M or 7-inch A2VM limits, offsets, or policy.
- No general face/stick/trigger/D-pad remapping.
- No gyro-to-mouse/stick or touch synthesis.
- No arbitrary scripts, executables, macros, or key sequences for OEM controls.
- No invented accelerometer, touchpad, or stick-touch data.
- No per-LED UI; expose the three verified logical RGB zones.
- No disabling `i8042prt`, the ACPI keyboard, or volume keys.
- No unsigned keyboard filter.
- No killing or reconfiguring MSI Center M or Handheld Companion.
- No blind EC probing, firmware flashing, EEPROM/UEFI experiments, or routine profile-memory repair.
- No repeated persistent writes during startup, preview, polling, or animation.

## Exact identity and prerequisites

| Signal | Required/observed value | Use |
| --- | --- | --- |
| Manufacturer | `Micro-Star International Co., Ltd.` normalized case-insensitively | Required |
| Baseboard product | `MS-1T52` | Required exact model gate |
| System product | `Claw 8 AI+ A2VM` | Display/supporting evidence |
| System SKU | `1T52.1` | Required supporting identity |
| System family | `Claw` | Coarse supporting identity |
| BIOS | `E1T52IMS.112` on reference unit | Diagnostics/compatibility |
| EC firmware | `1T52EMS1.109` from `Get_EC` | Firmware compatibility |
| Controller VID | `0x0DB0` | Controller family |
| Controller PID | `0x1901` XInput, `0x1902` DirectInput | Supported modes |
| Additional PIDs | `0x1903`, `0x1904` | Diagnostic only until understood |
| Controller `bcdDevice` | `0x0229` | MCU/RGB firmware descriptor |
| MSI WMI namespace | `root\WMI` | Provider discovery |
| MSI provider interface | 8.0 | Diagnostics/compatibility |

The baseboard and system product are different SMBIOS fields. A matcher that treats `MS-1T52` as
the Type 1 system product will fail. `MS-1T42` and `MS-1T41` require different device definitions.

SMBIOS EC major/minor values are both `0xFF` and unusable. `Get_EC` returns status, an `0x81`
marker, and ASCII data such as:

```text
01 81 "1T52EMS1.109" "12042025" "09:10:47"
```

The `MSI_ACPI` instance must be discovered rather than hardcoded. The reference instance was
`ACPI\PNP0C14\0_0`. Its schema and all 38 method signatures are readable without elevation, but
instance access returns `WBEM_E_ACCESS_DENIED` at medium integrity. The installed Claw plugin
therefore runs inside elevated WSGM. `MSI_Event` remains readable unelevated.

The OEM/chipset-driver environment provides MSI WMI. WSGM does not copy `msiapcfg.dll`, rewrite
`MofImagePath`, restart ACPI devices, or require MSI Center M to be running.

Unknown board identity prevents activation. Unknown controller firmware may still permit ordinary
input and diagnostics, but no firmware-addressed RGB/profile write.

## Direct runtime structure

The plugin may organize its implementation however is clearest, but the useful device-owned
services are:

- One serialized MSI WMI transport.
- One serialized MCU/vendor HID transport.
- Power/scenario capability.
- Dual-fan capability and telemetry.
- Controller source and mode switcher.
- Rumble sink.
- Gyroscope source and calibration.
- OEM WMI event source and firmware-chord suppressor.
- Three-zone RGB capability.
- Compact recovery record for temporary state that remains unresolved.

WSGM owns the UI, desired profiles, controller targets, HidHide, input arbitration, RTSS, AutoTDP,
Steam CEF, QAM, and OEM action mapping.

## Lifecycle

### Start

1. Confirm exact board/SKU and read firmware/provider information without writing.
2. Discover WMI, MCU, controller, motion, and OEM event resources independently.
3. Read and retain the original value for every temporary state the plugin may change.
4. Reconcile any prior unresolved recovery record.
5. Start read-only telemetry, WMI OEM events, and motion as each becomes available.
6. If controller management is enabled, capture the original controller mode, switch to DirectInput
   when required, rediscover endpoints, and publish canonical input.
7. Start firmware-chord suppression only after the WMI OEM2 source is healthy.
8. Apply the selected profile once through semantic commands, with readback.
9. Publish each capability independently; never block WSGM or a mode transition.

Desktop/Game Mode transitions may change the active application/profile and visible projection.
They do not recreate the plugin, reset fans/RGB, switch controller mode, or rebuild the target.

### Controller-management disable

1. WSGM establishes SDL/Steam-lease fallback for open WSGM surfaces.
2. WSGM neutralizes the virtual target while retaining its HidHide entries.
3. The plugin stops physical input/output, closes handles, restores the captured controller mode,
   and waits for the stable endpoint.
4. WSGM removes the target and only its HidHide entries.
5. Power, fans, telemetry, RGB, motion where useful, and OEM handling remain in the same plugin
   process.

### Full stop and external-manager handoff

1. Reject new commands and cancel pending hardware work.
2. Unhook shortcut suppression and release only plugin-tracked injected key states.
3. Send zero rumble; stop input and motion publication; close controller handles.
4. Restore the captured controller mode and rediscover the expected endpoint.
5. Restore temporary fan tables/flags, scenario, and other temporary state from exact snapshots.
6. Leave deliberate persistent RGB state intact.
7. Close WMI/HID/event resources and report clean, unverified, or failed restoration.
8. WSGM removes its target/HidHide state and unloads the plugin cycle.

If a snapshot was unavailable or restoration cannot be verified, retain that exact item for next
start. Never substitute a guessed factory value.

The recovery record captures a resource immediately before its first mutation. Acquisition reads
are observations for UI and admission, not restoration snapshots: another manager can legitimately
change a value between acquisition and WSGM's command. Shutdown therefore restores the journal's
pre-mutation value. Controller-source shutdown and Arc Sync restoration are also part of the stop
result; either failing makes the handoff unverified or failed while independent cleanup continues.

### Suspend, resume, lock, and session change

Before suspend or desktop/session loss, cancel calls, send zero rumble, quiesce input/motion,
unhook/reset chord state, and close volatile handles. Do not begin long hardware transactions.

On resume/unlock, rediscover endpoints, repeat identity/firmware/provider gates, read fresh state,
and reapply the active semantic desired state once. Wait for concrete arrival/ACK events with bounded
cancellation; do not use fixed sleeps as ownership logic.

### Conflicts

Detect Handheld Companion, MSI Center M, other writers, and exclusive-handle failures for diagnosis.
Do not kill or reconfigure them. Process presence alone is not proof of ownership. Conflicts are
resource-specific: controller conflict does not automatically disable WMI power/fans, RGB, motion,
or OEM events.

## MSI WMI transport

All relevant methods use a 32-byte `Package_32`. Response byte 0 is status; `0x01` means success.
Treat the provider as non-thread-safe: one FIFO serializer covers reads and writes, with bounded
timeouts, cancellation, length/status validation, and contextual logs.

### Confirmed data addresses

A stock read is what the firmware ships at, not what the register accepts. PL1 was recorded here as
"8–30 W" when 30 W was only its stock value, and that range reached the capability descriptor and
the write validator, so the QAM's TDP slider stopped at 30 W on a device whose ceiling is 37 W.
Record the two separately, and where the accepted range has not been measured, say so rather than
copying the stock value into it.

| Address | Meaning | Reference/allowed value |
| ---: | --- | --- |
| `0x50` | PL1 / sustained power | 8–37 W; stock read 30 W |
| `0x51` | PL2 / short power | 8–37 W; stock read 37 W |
| `0x52` | PL3 / peak field | Stock 0; do not write on A2VM |
| `0x98` | Full-speed flag | Bit 7; preserve lower bits |
| `0xD2` | Scenario | Bitfield below |
| `0xD4` | Custom-fan flag | Read/modify with fan ownership |
| `0xD7` | Charge limit | Factory read 80%; write remains conditional on validation |

### Power transaction

- Accept integer watts only.
- Enforce the current plugin-advertised ranges and `PL2 >= PL1`.
- Capture both values before the first write.
- Order writes so the intermediate pair remains valid.
- Read back both values.
- Restore the captured pair after partial failure.
- Do not write PL3 on this device.

AutoTDP controls the primary/sustained limit through the same semantic command path. It never calls
WMI directly and does not bypass the plugin's range or readback checks.

### MSI scenario

Scenario is a bitfield: bit 7 supported, bit 6 active, low six bits select the mode.

| Mode | Value |
| --- | ---: |
| Comfort | `0xC0` |
| Green | `0xC1` |
| Eco | `0xC2` |
| User | `0xC3` |
| Sport | `0xC4` |

The clean stock capture was active Green. Earlier Sport state came from prior user/application
configuration. Validate scenario-imposed power ceilings on AC and battery before relying on them.

### Temperature

`Get_Temperature` subfeature 0 is the live device temperature source. It tracked approximately
51→82→52 °C during load and cooldown. Do not add a second telemetry provider for this value.

## Fans

The product exposes two fans:

- Subfeature 1: left fan.
- Subfeature 2: right fan.

`Get_Fan` subfeature 0 returns two 16-bit big-endian divisors:

```text
RPM = 480000 / divisor
```

A zero divisor means stopped.

### Curve layout

Each fan's confirmed six points are:

- Temperature bytes 1 and 4–8: `0, 50, 60, 70, 80, 88 °C`.
- Duty bytes 2–7: `0, 40, 49, 58, 67, 75%` in direct percent.

Preserve unidentified duty bytes 1 and 8 and temperature bytes 2–3 through full-buffer
read-modify-write. Handheld Companion's 1.5× duty scaling is wrong and must not be copied.

### Modes and verification

- Automatic: firmware owns fan behavior.
- Custom: firmware follows the two plugin-supplied curves.
- Full Speed: temporary firmware full-speed flag.

There is no software PWM loop. Fans take roughly six seconds to begin responding and tens of
seconds to converge. Verify commands using curve/flag readback; RPM is telemetry, not an immediate
write ACK.

## MCU and controller topology

### MCU framing

MCU configuration messages are 64 bytes:

- Output prefix: `0F 00 00 3C`.
- Input prefix: `10 00 00 3C`.

Confirmed commands:

| Operation | Command/ACK |
| --- | --- |
| Read profile | `0x04` / `0x05` |
| Generic ACK | `0x06` |
| Write profile frame | `0x21` |
| Sync | `0x22` |
| Switch controller mode | `0x24` |
| Read controller mode | `0x26` / `0x27` |
| Reset | `0x28` |

The stale `WriteProfile = 0x03` name from a reference is wrong; observed profile writes use `0x21`.
Serialize MCU requests, match responses, validate lengths/prefixes, and invalidate the active handle
when topology changes.

### XInput mode

- PID `0x1901`.
- MCU: `MI_01`, usage `FFA0/0001`, 64-byte input/output, no feature report.
- Windows denies raw opening of the ordinary XInput gamepad HID.

### DirectInput mode

- PID `0x1902`.
- Gamepad: `MI_00&COL01`, 64-byte input, 32-byte output, 48-byte feature.
- MCU: `MI_00&COL02`, usage `FFF0/0040`, 64-byte input/output/feature.

Select the current MCU using the mode-specific PID/usage tuple or a vendor-defined usage with
64-byte output. Never bind by interface index or product string.

### Mode switching and continuation

XInput→DirectInput→XInput switching is hardware verified. The switch uses a 64-byte ordinary
`WriteFile`, forces re-enumeration, and invalidates cached paths/handles.

Container ID is the null GUID and unusable. The USB serial exists only in XInput and cannot bridge
modes. Continue by the physical USB location prefix, such as `...#USB(2)`, obtained by walking PnP
parents with `cfgmgr32`. HID children lack `LocationPaths`; strip unstable `#USBMI(n)` suffixes.

## Physical input

DirectInput is preferred under WSGM controller management because it exposes raw input and M1/M2.
Restore the captured original mode on handoff.

Neutral report prefix:

```text
01 80 80 80 80 0F 00 00 00 00
```

| Location | Meaning |
| --- | --- |
| Bytes 1–4 | Left X/Y, right X/Y; center `0x80` |
| Byte 5 low nibble | 8-way hat: 0 up, 2 right, 4 down, 6 left, F neutral |
| Byte 5 high nibble | X/A/B/Y |
| Byte 6 | LB, RB, LT digital, RT digital, View, Menu, L3, R3 |
| Byte 7 bit 3 | M2/right paddle, DirectInput index 15 |
| Byte 7 bit 4 | M1/left paddle, DirectInput index 16 |
| Bytes 8/9 | LT/RT analog |

Handheld Companion has M1 and M2 reversed; do not copy it. Left Y and right-stick Y use the verified
reversed-range normalization. Retain the corrupt-first-state guard when all three rotations read
`32767`.

Profile memory confirms factory M1 at `0x00BA` and M2 at `0x0163`. Do not repair or rewrite profile
memory at startup.

## Rumble

DirectInput output is:

```text
05 01 00 00 <small> <large> 00 00 00 00 00
```

Byte 4 controls the small/weak/right motor; byte 5 controls the large/strong/left motor. Values are
genuine 0–255 amplitudes. Do not copy the A1M 100 ms binary workaround.

Always send zero on target removal, game exit, suspend, disconnect, Device Integration/controller
disable, output-router fault, and plugin stop. Coalesce high-rate output if measurement shows it is
needed.

## Motion

The verified source is Intel Integrated Sensor Solution `VID_8087&PID_0AC2` through WinRT
`Gyrometer`:

- Three-axis angular velocity only.
- Units: degrees/second.
- Minimum report interval: 10 ms, therefore maximum 100 Hz.
- No usable accelerometer, inclinometer, or orientation sensor was exposed in either controller
  mode, including after the candidate MCU motion command.
- `SetMotionStatus(0x2F)` produced no reports/ACK and is not another source.

Steam Deck and DS4 accelerometer fields remain neutral; Xbox drops motion. Do not synthesize gravity
or orientation. The candidate physical transform is `+X,+Y,-Z`, pending final physical-axis
validation and calibration.

## OEM controls and firmware chord suppression

Confirmed WMI events:

| Control | Code |
| --- | ---: |
| OEM1 / left Claw button | `0x29` |
| OEM2 short / right Quick Settings | `0x58` |
| OEM2 long | `0x2A` (undocumented) |

M1/M2 remain ordinary logical OEM controls from the physical controller source. WSGM maps OEM
controls to its fixed allowlist, including overlay, native QAM, and supported target buttons.

OEM1 and OEM2 are latched into the controller stream with independent expiries. A later event for
one button must not lengthen the other button's virtual press, which would synthesize a chord the
user never held.

### Firmware keyboard side effect

The right button also emits malformed keyboard bursts through `ACPI\MSNB1001`:

- Short: `LWin DOWN`, orphan `G UP`, `LWin UP`, approximately 5 ms.
- Long: `LWin DOWN`, orphan `Tab UP`, `LWin UP`, approximately 68 ms.

The same device carries normal volume keys. `MSI_Event` is the action source; the keyboard hook is
suppression-only.

The suppressor:

- Recognizes the verified orphan-up sequence rather than blocking all Win+G or Win+Tab.
- Runs on the interactive desktop from the elevated plugin process.
- Uses a dedicated bounded callback thread.
- Keeps tagged `SendInput`, accepted-prefix accounting, and precise unmatched-down cleanup.
- Never performs WMI, HID, logging, or allocation-heavy work in the hook callback.
- Fails open on unknown or well-formed keyboard sequences.
- Resets/unhooks on disable, lock, suspend, desktop/session change, handoff, and host failure.
- Never strands Win, G, Tab, Ctrl, Alt, or Shift and never filters the full ACPI device.

Future BIOS versions that produce a different or well-formed chord must fail open until observed.

## RGB lighting

Firmware `0x0229` uses the verified live RGB base `0x024A`. The older populated `0x01FA` block is
inert.

Write frame:

```text
0F 00 00 3C 21 01 <addr> 20 00 01 09 03 <brightness> <RGB x 9>
```

Brightness is direct 0–100. Physical indices are:

- 0–3: right ring — bottom-left, bottom-right, top-right, top-left.
- 4–7: left ring — top-right, top-left, bottom-left, bottom-right.
- 8: ABXY/button group.

The product exposes exactly three logical zones: **Right Ring**, **Left Ring**, and **Buttons**.
Replicate each logical color across the corresponding physical indices.

RGB writes persist across reboot without `SyncToROM`; no volatile preview path exists. Therefore:

- Coalesce pointer/preview changes.
- Write only a settled explicit apply.
- Never stream animation frames from WSGM.
- Do not reapply at every startup.
- Read back every commit with `ReadProfile`.
- Treat deliberate user lighting as persistent desired state and do not restore an old snapshot on
  ordinary shutdown/handoff.
- Restore only changes made by a bounded diagnostic trial.

Solid/off, three colors, and brightness are grounded. Breathe, chroma, rainbow, frostfire, and speed
remain visible only after their exact generated frame sequences are validated.

## Profiles, AutoTDP, QAM, and glyphs

- Profiles store semantic desired values and optional per-application overrides in WSGM, not raw
  WMI/HID buffers in the plugin.
- AutoTDP drives the primary power capability through the same range/readback path and restores the
  underlying manual/profile value when disabled.
- Native QAM exposes TDP, frame limit, overlay level, controller target, supported performance
  state, and AutoTDP controls through shared WSGM services.
- The overlay exposes complete power/fans/controller/motion/OEM/RGB/profile/diagnostic controls.
- The plugin ships the Claw physical glyph package. Verify OEM1/OEM2 sides and M1-left/M2-right; use
  a distinct `msi.claw-a2vm` profile if the upstream `msi.claw` art is inaccurate.

## Safety and recovery invariants

- Unknown board, firmware, provider response, endpoint, mode, or prerequisite means no guessed
  write.
- Never choose firmware addresses by proximity; `0x0229` maps explicitly to RGB base `0x024A`.
- No WMI/MCU write if the exact initial read/snapshot failed.
- One serializer per vendor transport; bounded timeout, cancellation, and contextual failure.
- Read-modify-write complete firmware buffers and preserve unidentified bytes.
- Power and fan multi-write changes use readback and restore captured values after partial failure.
- Restore controller mode, temporary fan state, scenario, and other temporary ownership; persistent
  user RGB is the explicit exception.
- Do not infer conflict from a process name or race an active external writer.
- Hardware failure removes only the affected capability and never blocks WSGM startup or a
  Desktop/Game transition.
- HidHide belongs to WSGM and preserves every external entry.
- Managed UI capture neutralizes gameplay output until held controls are released.
- The Steam Input lease remains available for unmanaged/degraded input and per-game launch leases.

## Remaining hardware and acceptance work

- Find a controller/MCU firmware version beyond `bcdDevice` if one exists.
- Determine exact meaning of PIDs `0x1903` and `0x1904`.
- Validate PL1/PL2 equality and write ordering on hardware.
- Measure scenario power ceilings on AC and battery.
- Complete physical gyro axis/sign and suspend/resume validation.
- Validate OEM short/long suppression across supported BIOS versions, cold boot, resume, repetition,
  elevated applications, remappers, RDP, and on-screen keyboard input.
- Validate DirectInput rollover/report loss and Guide behavior under load.
- Confirm exact rumble send length/padding and any needed output coalescing.
- Decode and validate non-solid RGB effects and speed.
- Validate charge-limit writes/restoration if that optional capability is enabled.
- Visually accept the A2VM glyph package.
- Complete crash, suspend/hibernate, repeated mode-switch, target, HidHide, HC handoff, CPU/latency,
  power/fan, AutoTDP, and long-run restoration testing.

## Definition of done

The plugin is done when every listed feature either works on the exact supported hardware/firmware
or reports a clear unavailable state; all temporary ownership restores after disable, exit,
suspend, and fault; persistent lighting behaves deliberately; controller input remains recoverable;
and another SDK developer can understand the reference implementation without adopting WSGM's old
catalog, evidence, trust-tier, or generated-scaffold architecture.
