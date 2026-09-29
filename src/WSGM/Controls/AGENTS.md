# Controls

Controls contains reusable Avalonia controls: the shared tab strip, card buttons, icons,
radio icon, and on-screen keyboard.

- Keep controls presentation-oriented; session, Steam, and device policy belongs in Shell/Core
  managers, not click handlers or control code-behind.
- `Icons` are stroke-style `StreamGeometry`: render them with `Fill={x:Null}` or interior detail
  collapses.
- When using `Path` with `Stretch=Uniform`, size it by its dominant dimension; Avalonia aligns the
  scaled geometry at top-left inside an oversized box.
- Keyboard layer rebuilds must explicitly restore focus to the modifier that initiated the rebuild.
