# Radio, Bluetooth and audio integration

The Windows mechanisms — radio power, WLAN scanning and profiles, Bluetooth discovery and pairing
ceremonies, Core Audio endpoints, panel brightness, the volume cue — live in
`external\windows-device-control`, a submodule of `KillerPixelCrew/windows-device-control`. Its
`docs\radios.md` is the authoritative record of the platform constraints and of the approaches that
were tried and disproven (32feet.NET, the legacy Win32 Bluetooth API, `WiFiAdapter`, the consent
store as a precondition). Read that before changing anything about how Windows is called.

Nothing in that library is specific to WSGM or to gaming, which is why it was extracted. This file
records only what stays WSGM's decision.

## What WSGM owns

`Shell\RadioManager.cs` is the single owner of radio state for the game-mode UI. It holds the
observable collections, the refresh timer, the scan and pairing lifecycles, and the wording. The
library returns outcomes; the wording that reaches the user is WSGM's:

- Only `WifiFailureKind.KeyRejected` and `SecurityMismatch` re-prompt for a password. An unreachable
  network says so instead, because re-prompting makes the user retype a password that was never
  tried.
- An unusable radio says why — off, blocked by Windows, no adapter, state unavailable — rather than
  collapsing to "Off" and leaving the user pressing a switch that cannot do anything.
- A scan refused with Win32 5 names the 24H2 location-consent gate. It reads as a generic failure
  otherwise, and neither elevating nor retrying fixes it.
- `PairingKind.Unknown` is presented as confirm-only rather than declined. An accept is what Windows
  most often wants, and the log records the raw kind so a device that really needs another ceremony
  is still diagnosable.

The wording rules above are covered by tests in `tests\WSGM.Tests\RadioManagerTests.cs`; the library
contracts they sit on are tested in the library's own repository.

`Shell\AudioManager.cs` similarly owns the session's default render and capture endpoint state. The
library's direction-aware `CoreAudio.GetVolume`/`SetVolume` calls are the only Windows edge; WSGM
polls the two directions independently and coalesces each slider's writes separately. The Steam QAM
projection must never copy speaker volume into the microphone field: a missing capture endpoint
publishes a null input volume so Steam leaves that direction unavailable, while a present one
receives its own 0-100 value and routes writes back with `AudioDirection.Capture`.

## Touch keyboard boundary

The radio panel's credential and PIN entry uses WSGM's own `Controls\OnScreenKeyboard`; it never
depends on `TabTip.exe`. The Windows touch keyboard still depends on Explorer completing its normal
unelevated per-session initialization before game-mode takeover. That shell invariant is documented
in `boot-and-shell.md` and must not be weakened as part of radio work.

## Diagnostics and verification

`WSGM.exe --radio-probe` is the read-only diagnostic. It records process elevation, Explorer
presence, radio power and access, consent state, WLAN scan/list/status, and Bluetooth enumeration in
the normal WSGM log. `--radio-pair <name>` is deliberately separate because it changes pairing state
and requires attended use.

Compile and isolated tests prove the managed contracts. Power, discovery, pairing ceremonies, audio
reconnection, location consent, and shell-less or elevated behavior still need device verification
on the reference handheld.
