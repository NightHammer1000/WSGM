# Interop

Interop is the narrow managed boundary to Win32, WinRT, COM, and WSGM's remaining native DLLs.

- Keep P/Invoke declarations `LibraryImport`-based where supported and explicit about ownership.
  Keep COM contracts private to the boundary and release every acquired interface deterministically.
- Native buffers must have a matching free call on every success path; do not expose native pointers
  or COM objects beyond the interop/manager boundary.
- Add logging and graceful unavailable behavior for device-specific APIs. The application must remain
  usable when a native DLL, radio, audio endpoint, or shell service is unavailable.
