# WSGM release build: self-contained publish + Inno Setup installer.
# Output: publish\WSGM-Setup-<version>.exe (the one-file installer — the only
# shipped artifact; the logon service requires a real install)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# The csproj <Version> is the single source of truth; the installer gets it via /D.
$csproj = Get-Content "$root\src\WSGM\WSGM.csproj" -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw "No <Version> found in WSGM.csproj" }
$version = $Matches[1]

# This check rebuilds the asset from its TypeScript source and compares, so stale generated Steam
# UI code fails immediately. Install exactly the dependency graph in package-lock.json first: a
# release build must work from a clean checkout and must not reuse an unreviewed node_modules tree.
Write-Host "== Restoring locked Node.js tools ==" -ForegroundColor Cyan
Push-Location $root
try {
    npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }

    Write-Host "== Validating release inputs ==" -ForegroundColor Cyan
    npm run steam-assets:check
    if ($LASTEXITCODE -ne 0) { throw "Steam UI asset drift check failed" }
}
finally {
    Pop-Location
}

# The Steam Input gate is built from the source in native\SteamInput on every
# release build, so a shipped installer can never carry a gate older than the
# code beside it. This must precede the publish, which copies the staged output.
Write-Host "== Building Steam Input Lease (Rust) ==" -ForegroundColor Cyan
# -Validate for the export check: build.rs now drives exports from one authoritative
# .def, and the dumpbin ordinal comparison is the ONLY thing that catches link.exe
# putting an unrelated symbol at XInput's ordinal 104/109 - the stack-corruption case
# that .def exists to prevent. Without this the shipped DLL is the one artifact never
# export-checked, since eng\verify.ps1 only validates a separately built copy.
& "$root\eng\build-steam-input-lease.ps1" -Validate

# The virtual controller library is built from its pinned external revision. Controller management
# is a shipped feature, so a release without the library is an incomplete release, not a valid
# feature-local fallback artifact.
Write-Host "== Building virtual controller library (Go) ==" -ForegroundColor Cyan
& "$root\eng\build-viiper.ps1" -Validate

Write-Host "== Publishing WSGM $version (self-contained JIT) ==" -ForegroundColor Cyan
# Clean first: dotnet publish overlays onto the previous output, so a DLL removed by
# a dependency bump (or an old setup exe) would otherwise leak into the release.
# Test-Path covers the only tolerable failure (no previous output); a clean that
# fails for any other reason must stop the build, not leak a stale tree.
if (Test-Path "$root\publish") { Remove-Item -Recurse -Force "$root\publish" }
$appPublish = "$root\publish\App"
New-Item -ItemType Directory -Path $appPublish | Out-Null

# One RID-aware restore feeds every --no-restore publish below.
dotnet restore "$root\WSGM.slnx" --runtime win-x64 -m:1
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

dotnet publish "$root\src\WSGM\WSGM.csproj" -c Release -r win-x64 `
    -o $appPublish --no-restore -m:1
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# The user-facing Steam launch wrapper. Steam inherits WSGM's elevation, so this
# hands the real command to a medium-integrity scheduled-task child and/or holds a
# Steam Input block lease for the game's lifetime. Publish it beside WSGM so both
# portable and installed layouts use the same stable command path.
dotnet publish "$root\src\WSGM.Launch\WSGM.Launch.csproj" -c Release -r win-x64 `
    -o $appPublish --no-restore "/p:Version=$version" -m:1
if ($LASTEXITCODE -ne 0) { throw "WSGM.Launch publish failed" }

# The SYSTEM logon service that launches WSGM's boot cover at sign-in. Published
# beside the rest; the installer ships it to Program Files (never user-writable).
dotnet publish "$root\src\WSGM.LogonService\WSGM.LogonService.csproj" -c Release -r win-x64 `
    -o $appPublish --no-restore "/p:Version=$version" -m:1
if ($LASTEXITCODE -ne 0) { throw "WSGM.LogonService publish failed" }

if (-not (Test-Path "$appPublish\WSGM.Launch.exe")) { throw "Launch wrapper was not produced" }
if (-not (Test-Path "$appPublish\WSGM.LogonService.exe")) { throw "Logon service was not produced" }
if (-not (Test-Path "$appPublish\libviiper.dll")) { throw "VIIPER controller library was not published" }

# The USB/IP driver installer the virtual controller attaches through. It is a third-party asset
# fetched from its pinned release and verified here — on the release machine — against the reviewed
# digest and signer, so the copy the installer ships has already been checked by the time a user's
# setup re-checks it. Both usbip-win2 and HidHide are required payloads of the optional controller
# installer component; the release build fails rather than publishing a component that cannot work.
Write-Host "== Staging controller driver installers ==" -ForegroundColor Cyan
& "$root\eng\acquire-controller-dependencies.ps1" -Destination $appPublish

Write-Host "== Publishing device tools and package ==" -ForegroundColor Cyan
& "$root\eng\stage-device-components.ps1" `
    -OutputRoot "$root\publish" `
    -Configuration Release `
    -RuntimeIdentifier win-x64 `
    -NoRestore
& "$root\eng\assert-component-staging.ps1" -OutputRoot "$root\publish"

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }

Write-Host "== Compiling installer ==" -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" "$root\installer\WSGM.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem "$root\publish\WSGM-Setup-*.exe" |
    Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}
