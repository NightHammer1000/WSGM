# WSGM.LogonService

This self-contained SYSTEM service reacts to user logon, reads the per-user boot manifest as untrusted
input, launches WSGM as that user, and provides one-shot Explorer recovery after a dirty boot exit.

- Do not use `ServiceBase`, COM, Avalonia, or user-directory logging. Service logs belong in
  `%ProgramData%\WSGM\wsgm-service.log`.
- Launch only the manifest-named WSGM executable as the manifest's user. Use the linked token only
  when the manifest requests elevation; Explorer recovery always uses the unlinked token.
- Act on `WTS_SESSION_LOGON`, not console connect. The boot path must start before Explorer and let
  WSGM's readiness poll wait for it.
- The watchdog starts Explorer at most once per logon after a dirty exit with no Explorer; it never
  relaunches WSGM.
