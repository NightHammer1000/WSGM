# Dev-only deploy: publish WSGM and swap it into the local install WITHOUT the installer.
#
# The installer round-trip costs minutes and a UAC prompt for what is, on the dev box, a file
# copy. Steam must restart anyway so the injected bootstrap and any WSGM-defined
# SteamClient.System.* namespaces are rebuilt from scratch — a bridge left over from the previous
# build keeps running the OLD injected script until Steam restarts, and a fix then appears to do
# nothing (see docs\steam-cef.md).
#
# Order matters on restart: WSGM first, then Steam, so WSGM's patch synchronization is already
# watching when Steam's SharedJSContext appears.
#
# This script is for the attended dev loop only. It is not part of any release path, CI never
# calls it, and it deliberately does not touch WSGM.LogonService.exe (Program Files, elevation,
# and it changes rarely) or the plugin slot (administrator-owned, replaced only by the
# installer).
[CmdletBinding()]
param(
    # Skip the publish and swap whatever publish\App already holds — for iterating on the swap
    # itself or re-deploying a build that was just made.
    [switch]$SkipBuild,

    # Arguments WSGM is restarted with. On this machine the running mode is the shell; plain
    # WSGM.exe would open Settings instead.
    [ValidateNotNull()]
    [string[]]$WsgmArguments = @('--shell'),

    # Leave Steam and WSGM stopped after the swap instead of restarting them.
    [switch]$NoRestart,

    # Skip refreshing the installed device plugin. The plugin rebuild + one elevation prompt only
    # matter when the SDK or the built-in package changed; a pure WSGM code loop can skip both.
    [switch]$SkipPlugin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The shell may only run on the reference Claw. The maintainer develops on other machines where
# WSGM is not installed, and this script restarts Steam and the live shell — on the wrong machine
# that is a takeover of a desktop nobody offered.
# The board product is the same one-command identity check the root AGENTS.md mandates before any
# hardware work.
$board = (Get-CimInstance -ClassName Win32_BaseBoard).Product
if ($board -ne 'MS-1T52') {
    throw "dev-deploy refused: this machine reports board '$board', not the reference Claw (MS-1T52)."
}

$root = Split-Path -Parent $PSScriptRoot
$appPublish = Join-Path $root 'publish\App'
$binDirectory = Join-Path $env:LOCALAPPDATA 'WSGM\bin'
$steamExe = 'C:\Program Files (x86)\Steam\steam.exe'

if (-not (Test-Path -LiteralPath $binDirectory)) {
    throw "No installed WSGM at $binDirectory - run the real installer once first."
}

if (-not $SkipBuild) {
    Write-Host '== Publishing WSGM (self-contained JIT) ==' -ForegroundColor Cyan
    # Preserve the release build environment that build.ps1 uses for native dependencies.
    $env:Path += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
    npm run steam-assets:check
    if ($LASTEXITCODE -ne 0) { throw 'Steam UI asset drift check failed' }
    dotnet publish (Join-Path $root 'src\WSGM\WSGM.csproj') -c Release -r win-x64 `
        -o $appPublish -m:1
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}

$newExe = Join-Path $appPublish 'WSGM.exe'
if (-not (Test-Path -LiteralPath $newExe)) {
    throw "No published WSGM.exe at $newExe - build first or drop -SkipBuild."
}

$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
$wrappers = @(Get-Process -Name 'WSGM.Launch' -ErrorAction SilentlyContinue |
    Where-Object SessionId -eq $sessionId)
if ($wrappers.Count -ne 0) {
    $ids = ($wrappers.Id | Sort-Object) -join ', '
    throw "dev-deploy refused: a game launch wrapper is active in this session (PID $ids). Close the game normally and retry."
}

Write-Host '== Asking Steam to exit, then stopping WSGM ==' -ForegroundColor Cyan
$steamProcesses = @(Get-Process -Name 'steam' -ErrorAction SilentlyContinue |
    Where-Object SessionId -eq $sessionId)
if ($steamProcesses.Count -ne 0) {
    Start-Process 'steam://exit'
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 250
        $steamProcesses = @(Get-Process -Name 'steam' -ErrorAction SilentlyContinue |
            Where-Object SessionId -eq $sessionId)
    } while ($steamProcesses.Count -ne 0 -and $deadline.Elapsed -lt [TimeSpan]::FromSeconds(20))

    if ($steamProcesses.Count -ne 0) {
        throw 'dev-deploy refused: Steam did not exit normally within 20 seconds. Close it manually and retry.'
    }
}

# Stop until quiet, not once: the logon-service watchdog respawns WSGM right after a kill, and
# that respawn held WSGM.exe through the copy on three consecutive deploys (2026-09-01). A process
# may also exit between enumeration and Stop-Process, which is success, not an error.
$stopDeadline = [Diagnostics.Stopwatch]::StartNew()
do {
    $wsgmProcesses = @(Get-Process -Name 'WSGM' -ErrorAction SilentlyContinue |
        Where-Object SessionId -eq $sessionId)
    foreach ($process in $wsgmProcesses) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
        } catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
            # Already gone — the watchdog's respawn can die on its own between the
            # enumeration and the stop.
        }
    }
    if ($wsgmProcesses.Count -eq 0) { break }
    Start-Sleep -Milliseconds 250
} while ($stopDeadline.Elapsed -lt [TimeSpan]::FromSeconds(10))

Write-Host "== Swapping files into $binDirectory ==" -ForegroundColor Cyan
# WSGM.exe plus everything the publish stages beside it that the installer would also place in
# {app}: the launch wrapper and the native helper DLLs. The ShellAnchor is the same binary under
# the shell-registration name; leaving it stale would run two different builds in one session.
# The exe copy retries briefly: a killed process releases its image lock a beat after the process
# object dies, and the watchdog respawn can hold it for a moment more.
$copied = $false
for ($attempt = 1; $attempt -le 10 -and -not $copied; $attempt++) {
    try {
        Copy-Item -LiteralPath $newExe -Destination (Join-Path $binDirectory 'WSGM.exe') -Force -ErrorAction Stop
        $copied = $true
    } catch [System.IO.IOException] {
        Get-Process -Name 'WSGM' -ErrorAction SilentlyContinue |
            Where-Object SessionId -eq $sessionId |
            Stop-Process -Force -Confirm:$false -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
}
if (-not $copied) {
    throw "WSGM.exe stayed locked through 10 copy attempts - is something else holding $binDirectory\WSGM.exe?"
}
$anchor = Join-Path $binDirectory 'WSGM.ShellAnchor.exe'
if (Test-Path -LiteralPath $anchor) {
    # A desktop session keeps a live anchor process (Explorer's launch parent) that holds this
    # image. It is inert after Explorer is up, so stop it rather than shipping a stale anchor.
    $anchorCopied = $false
    for ($attempt = 1; $attempt -le 10 -and -not $anchorCopied; $attempt++) {
        try {
            Copy-Item -LiteralPath $newExe -Destination $anchor -Force -ErrorAction Stop
            $anchorCopied = $true
        } catch [System.IO.IOException] {
            Get-Process -Name 'WSGM.ShellAnchor' -ErrorAction SilentlyContinue |
                Where-Object SessionId -eq $sessionId |
                Stop-Process -Force -Confirm:$false -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $anchorCopied) {
        throw "WSGM.ShellAnchor.exe stayed locked through 10 copy attempts."
    }
}
foreach ($pattern in 'WSGM.Launch.exe', '*.dll') {
    Get-ChildItem -LiteralPath $appPublish -Filter $pattern -ErrorAction SilentlyContinue |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $binDirectory $_.Name) -Force
        }
}

if (-not $SkipPlugin) {
    # The installed device plugin is a separate package under Program Files that the WSGM bin swap
    # never touches, so a dev loop that changes the SDK leaves a stale plugin the running host
    # rejects as api-incompatible (device features silently gone). Rebuild it from the pinned
    # submodules exactly as the installer does, then swap the validated tree into the protected
    # slot. Only this step needs elevation, so it is the one UAC prompt of a dev deploy.
    Write-Host '== Staging device plugin from submodules ==' -ForegroundColor Cyan
    $pluginStage = Join-Path $root 'publish\DevDeviceComponents'
    Remove-Item -LiteralPath $pluginStage -Recurse -Force -ErrorAction SilentlyContinue
    & "$root\eng\stage-device-components.ps1" -OutputRoot $pluginStage
    if ($LASTEXITCODE -ne 0) { throw 'Device plugin staging failed.' }

    $packagesRoot = Join-Path $pluginStage 'Packages'
    $stagedPackage = @(Get-ChildItem -LiteralPath $packagesRoot -Directory)
    if ($stagedPackage.Count -ne 1) {
        throw "Expected exactly one staged package under $packagesRoot; found $($stagedPackage.Count)."
    }
    $packageId = $stagedPackage[0].Name
    $stagedTree = $stagedPackage[0].FullName
    $installedRoot = Join-Path $env:ProgramFiles 'WSGM\DevicePlugins\installed'
    $installedTree = Join-Path $installedRoot $packageId

    Write-Host "== Installing device plugin $packageId (elevation required) ==" -ForegroundColor Cyan
    # A single elevated child does the protected-slot swap: replace the package directory atomically
    # (stage beside, then swap) so a failed copy never leaves a half-written plugin the host loads.
    $swap = @"
`$ErrorActionPreference = 'Stop'
`$installedRoot = '$installedRoot'
`$installedTree = '$installedTree'
`$stagedTree = '$stagedTree'
New-Item -ItemType Directory -Path `$installedRoot -Force | Out-Null
`$incoming = "`$installedTree.incoming"
`$old = "`$installedTree.old"
if (Test-Path -LiteralPath `$incoming) { Remove-Item -LiteralPath `$incoming -Recurse -Force }
Copy-Item -LiteralPath `$stagedTree -Destination `$incoming -Recurse -Force
if (Test-Path -LiteralPath `$old) { Remove-Item -LiteralPath `$old -Recurse -Force }
if (Test-Path -LiteralPath `$installedTree) { Rename-Item -LiteralPath `$installedTree -NewName (Split-Path -Leaf `$old) }
Rename-Item -LiteralPath `$incoming -NewName (Split-Path -Leaf `$installedTree)
if (Test-Path -LiteralPath `$old) { Remove-Item -LiteralPath `$old -Recurse -Force }
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($swap))
    $elevated = Start-Process -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) `
        -Verb RunAs -Wait -PassThru
    if ($elevated.ExitCode -ne 0) {
        throw "Elevated device plugin install failed (exit $($elevated.ExitCode))."
    }
    Write-Host "Device plugin $packageId installed." -ForegroundColor Green
}

if ($NoRestart) {
    Write-Host 'Swap done; Steam and WSGM left stopped (-NoRestart).' -ForegroundColor Yellow
    return
}

Write-Host "== Starting WSGM $WsgmArguments, then Steam ==" -ForegroundColor Cyan
Start-Process -FilePath (Join-Path $binDirectory 'WSGM.exe') -ArgumentList $WsgmArguments
Start-Sleep -Seconds 6
if (-not (Get-Process WSGM -ErrorAction SilentlyContinue)) {
    throw 'WSGM did not stay running after the swap - check %LOCALAPPDATA%\WSGM\wsgm.log.'
}
Start-Process -FilePath $steamExe
Write-Host 'Deployed.' -ForegroundColor Green
