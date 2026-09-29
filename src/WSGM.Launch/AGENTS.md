# WSGM.Launch

The single user-facing Steam launch wrapper. It replaces `WSGM.Deelevate.exe` and
`steam-input-lease.exe`: one executable, selected by flags, so a game can have de-elevation, a Steam
Input block, or both. WSGM writes its command into a game's launch options over the CEF bridge
(`Core\SteamLaunchConfig.cs`); the clipboard copy is the fallback when CEF is off.

```
"…\WSGM.Launch.exe" [--deelevate] [--input-lease] -- %command%
```

- **Console subsystem, always** (`<OutputType>Exe</OutputType>`). A windowless `WinExe` is treated by
  Steam as a game, gets Steam Input hooked into it and dies before it can log. The CLI window is the
  price of working at all — never hide it by switching subsystem.
- At least one behaviour flag is required; the target command always follows `--`, and its arguments
  are preserved as individual Windows arguments (Steam expands `%command%` into several).
- Preserve Steam's command, arguments, environment, and working directory exactly.
- The elevated wrapper must remain alive for the target lifetime and stop the target tree if Steam
  terminates the wrapper. Do not replace it with a fire-and-forget scheduled task or Explorer shortcut.
- The scheduled-task XML is UTF-16, uses `InteractiveToken`, and must never use `/NoUACCheck`.
- The de-elevation pipe grants the **User SID** explicitly. Never `PipeOptions.CurrentUserOnly` — on an
  elevated server that grants the token owner (`BUILTIN\Administrators`), which is deny-only in the
  medium child's filtered token.
- `--input-lease` alone uses the native job-object wrapper (whole process tree). Combined with
  `--deelevate` the lease is acquired by the **elevated parent before** the hand-off, because the gate
  injects into an elevated `steam.exe` and a medium process cannot.
- Lease failures fail **open**: log, tell the user, launch the game anyway. A held controller is a
  degraded experience; a game that will not start is a broken one.
- `SteamInterop\*.cs` is linked directly from the canonical
  `native\SteamInput\bindings\SteamInterop.Net` source. Change the ABI there and in Rust together.

## The two lease flags

`--input-lease` and `--input-lease-inject` differ only in how the block is delivered and are
mutually exclusive; `TryParse` rejects both together. `--input-lease` sets
`SteamInputClientOptions.AllowInjection = false` and therefore connects only to the shim Steam
loaded from its own directory — it can never write into the Steam process. `--input-lease-inject`
is the sole route in the shipped product that injects, and WSGM writes it into a game only while
Steam Input Management is off, because then no resident shim exists to connect to.

Both still fail **open**: a lease that cannot be acquired logs and launches the game anyway. When
`--input-lease` was requested and no shim answered, say so specifically in `launch.log` — "turn
Steam Input Management on, or re-apply the launch fix with it off" is the difference between a
diagnosable report and a shrug.
