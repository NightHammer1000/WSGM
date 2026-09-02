# WSGM application

This is the self-contained Avalonia executable. It owns Settings, the game-mode shell session, the
quick access sheet, and the per-user configuration and boot manifest.

- Keep OS declarations and managed COM interfaces inside `Interop\`; expose narrow managed results
  to the rest of the application.
- Put behavior with its existing owner and call it directly. The folders keep related code findable;
  they are not a reason to add a facade, message contract, mirror type, or another manager.
- `--settings` and `--overlay-test` are the only safe local UI modes. Never run `--shell` or `--boot`
  directly; the root instructions define the one attended reference-device deploy path.
- Runtime config reload replaces the config object. Keep transient state in its controller/manager,
  not in `AppConfig` references.
- Update this file when this executable's responsibilities or safety boundaries change.
- `SkipNativeArtifacts=true` is a compile/test-only escape hatch for native dependencies that remain;
  release and supported verification builds must never set it.
