# Build and verification scripts

`eng` holds the supported build/validation entry points for native dependency staging and repository
verification.

- `verify.ps1` is the validation gate: formatting, staged Rust code, Release build, tests, and
  coverage. Keep vendored source exclusions intact.
- The Steam Input Rust workspace is source-built; update its staging/validation script with any ABI,
  artifact, or workspace change.
- Build scripts must fail fast, stage generated artifacts rather than checking them in, and remain
  safe to run from the repository root.
- Cross-compilation honors `CARGO_BUILD_TARGET` when resolving release artifacts, while normal
  Windows builds continue to use `target\release`.
