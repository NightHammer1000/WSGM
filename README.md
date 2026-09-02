<p align="center">
  <img src="docs/banner.svg" alt="WSGM — Windows Steam Game Mode" width="810">
</p>

WSGM reconstructs the SteamOS Game Mode experience on Windows 11 — on gaming handhelds, gaming PCs,
and DIY Steam Machines alike. Sign in, land directly in Steam Big Picture, control everything with
the pad and the touchscreen, and only see the desktop when you ask for it. Explorer stays your
Windows shell the whole time.

## Features

- **Boot to Big Picture** — a logon service starts game mode at sign-in behind a splash screen;
  switching to the desktop and back is one press, any time.
- **Quick access sheet** — one controller- and touch-driven surface that slides down from the top
  edge and leaves the game visible below: a home tab of rows you pin yourself, session control,
  Steam and device tools, power actions, your open programs, tray icons, Wi-Fi/Bluetooth state,
  battery, and a clock. Left and right edges stay Steam's own menus, exactly like SteamOS.
- **Wi-Fi & Bluetooth** — join networks and pair controllers/headsets without leaving game mode
  (Windows' own flyouts can't open there).
- **Audio** — volume and output-device switching from the sheet, plus an on-screen indicator for
  hardware volume keys.
- **Safe Eject** — remove SD cards and USB drives cleanly from the sheet.
- **Library tabs** — build custom tabs for Steam's library from filters (installed, tags, playtime,
  size, title patterns, …), reorder the whole tab strip, and hide Steam's built-in tabs.
- **SD card & external drive libraries** — every removable Steam library gets its own tab that
  remembers its games while ejected; rename, hide, or forget cards from a controller-driven manager,
  and an "On: card" badge shows where the game you're viewing lives.
- **Drive formatting** — format a card or drive into a ready-to-use Steam library in one guided
  flow, keeping its exact drive letter; register any folder or network share with the running Steam
  client, no restart.
- **SteamGridDB artwork** — browse and apply capsule/hero/logo art for any game, including non-Steam
  shortcuts, without leaving game mode.
- **A working Wi-Fi icon** — Big Picture's header shows your real network and signal strength on
  Windows (Steam never feeds it there; WSGM does).
- **Steam Input everywhere** — Steam runs elevated so Steam Input keeps working over elevated
  windows and games.
- **Steam Input Lease** — the first tool to take the controller out of the running Steam client's
  hands **dynamically**: Steam is asked to let go of the pad and gets it back the moment it's needed
  again — no restart, no drivers, no config changes, no Steam file touched; Steam just sees a brief
  unplug. It's what lets WSGM's own panels read the controller while they're open.
- **Free the controller for emulators & SDL3 apps** — Steam's desktop layout normally swallows the
  pad from every other program. The lease blocks Steam Input for a single title, so emulators and
  SDL3 applications read the real controller directly — and Steam takes it back the moment the game
  exits. The same wrapper de-elevates titles that refuse to run elevated, and can do both at once.
- **Per-game launch fixes, applied for you** — open the panel on a game and pick the fix; WSGM
  writes it straight into the running Steam client. No pasting, no restart, and it gets the awkward
  non-Steam-shortcut setup right by itself. One button puts everything back.
- **Make it yours** — a fully configurable boot splash (text, spinner, logo, background, shareable
  presets) and an accent colour every surface follows.
- **Fails open** — if anything goes wrong, WSGM keeps or restores the desktop rather than leaving a
  black screen, and a crash-loop breaker disarms game mode by itself.

## Demo

The quick access sidebar, and switching between game mode and the desktop:

https://github.com/user-attachments/assets/4e422b98-cf27-4f17-aa46-b8c956ce7275

The 1.x game-mode taskbar (2.0 merges it into the quick access sheet):

https://github.com/user-attachments/assets/c90e6354-5d05-46c5-9866-d5f8a647cbcb

## ⚠ Recovery — read this FIRST

Game mode ends Explorer while it runs, so if something goes wrong you can end up looking at a screen
with no desktop on it. **You can always recover:**

1. Press **Ctrl+Alt+Del** (this always works — it belongs to Windows, not to WSGM). On a handheld
   without a keyboard, attach a USB/Bluetooth keyboard.
2. Choose **Task Manager** → **Run new task**.
3. Type either:
   - `explorer.exe` — brings the desktop back for this session, or
   - `%LOCALAPPDATA%\WSGM\bin\WSGM.exe --restore-shell` — turns **off** game mode at sign-in and
     starts the desktop, so the next sign-in is an ordinary Windows one.

Safety nets also run on their own: the boot takeover keeps the desktop if it can't end Explorer
cleanly, the service starts Explorer if WSGM crashes without one, and three failed game-mode starts
within two minutes disarm game mode automatically.

## Why not Windows' own fullscreen experience?

Windows 11's Xbox Full Screen Experience doesn't deliver controller input to elevated processes —
and Steam must run elevated if you want Steam Input to keep working while an elevated window has
focus, or in games that require elevation. Under FSE, an elevated Steam additionally refuses input
from virtual controllers (Handheld Companion and friends). WSGM gives you boot-to-Steam without FSE,
so all of it works at once.

## Compatibility

- **Handheld Companion** — works, and is heavily used on WSGM's own development devices. Tested
  against all of its controller types.
- **CSSLoader Desktop** — works, with caution: themes restyle the same Steam UI that WSGM's
  library-tab engine patches, so a theme that touches the library's tab strip can break the injected
  tabs.
- **Custom (non-Steam) shortcuts are set up differently** — WSGM handles this for you, but it is
  worth knowing why the two look different in Steam. A normal Steam title takes the wrapper in its
  **Launch Options** (`"…\WSGM.Launch.exe" --deelevate -- %command%`). A **non-Steam shortcut**
  cannot: Steam quietly ignores an exe-replacement launch option there and runs the original target
  anyway (it even mangles the command line — the wrapper never starts). So for a shortcut the
  wrapper goes in the **Target** field and the real program moves into **Launch Arguments**. With
  the Steam integration turned off, the Tools tab copies the command and you apply it by hand — in
  that case the shortcut layout above is on you.

## How it works

The full technical deep-dive — the logon service, the Explorer takeover, the Steam Input Lease, the
Steam CEF bridge behind the library features, elevation and recovery — lives in the wiki:
**[How it Works](https://github.com/NightHammer1000/WSGM/wiki/How-it-Works)**.

## Install

**Prerequisites:** Steam (installed and signed in once — the setup refuses to run without it) and
**Windows 11 x64**. Everything else is self-contained: no .NET runtime, no redistributables.

1. Download and run **`WSGM-Setup-<version>.exe`** from the
   [latest release](https://github.com/NightHammer1000/WSGM/releases/latest). It asks for
   administrator rights once, to register the logon service.
2. Open WSGM — Steam is detected automatically; add startup apps from the suggestions (Handheld
   Companion and friends are detected too).
3. Leave **Start game mode at sign-in** on, **Save changes**, sign out and back in.

**Upgrading:** run the newer setup. **Uninstall:** Windows Settings → Apps → WSGM — it restores
every machine setting it changed and removes its files.

Building from source: `.\build.ps1` (needs the .NET SDK, Rust with the MSVC toolchain, Go, Git, a
cgo-capable GCC, and Inno Setup 6) → `publish\WSGM-Setup-<version>.exe`.

## Credits

The library features are Windows reimplementations of approaches from Decky Loader plugins on
SteamOS: [TabMaster](https://github.com/Tormak9970/TabMaster) (filter tabs, tab-strip control),
[MicroSDeck](https://github.com/CEbbinghaus/MicroSDeck) (per-card libraries), and
[decky-steamgriddb](https://github.com/SteamGridDB/decky-steamgriddb) (artwork flow). The Steam
Input Lease's blocking model was informed by SpecialK's ValvePlug. Controller button glyphs come
from CC0 prompt packs (see `src/WSGM/Assets/Glyphs/CREDITS.md`).

## AI usage disclaimer

Large parts of WSGM are written with AI assistance, directed and reviewed by a human. Changes are
tested on real handheld hardware before release.

## License

Copyright (C) 2026 NightHammer1000.

WSGM is free software: you can redistribute it and/or modify it under the terms of the **GNU General
Public License as published by the Free Software Foundation, either version 3 of the License, or (at
your option) any later version** ([full text](LICENSE)).

WSGM is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the
implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public
License for more details.

Bundled third-party components keep their own licenses; their notices ship beside the executable and
with the installer.
