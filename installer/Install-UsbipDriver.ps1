<#
.SYNOPSIS
    Installs the usbip-win2 USB/IP client driver that WSGM's virtual controller requires.

.DESCRIPTION
    WSGM's virtual controller is created by VIIPER, which emulates a USB device entirely in
    userspace and hands it to the operating system over the USB/IP protocol. The kernel half of that
    protocol is neither WSGM's nor VIIPER's: on Windows it is usbip-win2, whose UDE client driver is
    WHLK-certified or attestation-signed through the Open Source Codesigning Initiative. Without it
    `viiper_device_attach` has nothing to attach to and controller management stays unavailable.

    This script is invoked once, from WSGM's setup, only when the user ticked the virtual-controller
    driver task. It is deliberately not reachable from the running shell (INV-020): installing the
    driver restarts every USB 3.0 hub in the machine, which on a handheld means the built-in
    controller, touch digitiser and keyboard all drop and re-enumerate. Doing that underneath a
    running game mode would take the user's input away with no way to get it back.

    The installer normally ships inside WSGM's setup, already verified on the release machine by
    `eng/acquire-controller-dependencies.ps1`. It is re-verified here anyway — the release machine's
    verification says nothing about the copy sitting on this disk — and, when the release build had
    no network and could not stage it, this falls back to downloading the same pinned asset. That
    fallback matters: a freshly imaged handheld often runs WSGM's setup before its Wi-Fi is
    configured, which is exactly when a download-only design would fail.

    Failure is never fatal to WSGM's setup. The script reports what happened and exits 0 for
    "installed", "already present" and "failed" alike; WSGM without the driver simply reports
    controller management as unavailable, which is a supported state. Because that deliberately
    makes the process exit code non-diagnostic, every run also atomically replaces a small bounded
    INI status marker that setup can read after the process finishes.

.PARAMETER InstallerPath
    The staged, already-verified installer. Defaults to the copy setup placed beside this script.

.PARAMETER LogPath
    Where to append a transcript of what this run decided and did. Defaults to WSGM's machine-wide
    diagnostic directory, so a failed driver install can be diagnosed from a pasted log like every
    other WSGM subsystem.

.PARAMETER StatusPath
    The durable bounded INI result consumed by setup after this script exits. A final result is one
    of installed, already-present, blocked-newer-version, report-only or failed. The marker is
    written atomically and defaults to WSGM's machine-wide diagnostic directory.

.PARAMETER ReportOnly
    Reports the detection result and verifies whatever installer is present without installing
    anything. This is the safe mode for a development machine, where installing a kernel driver is
    off-limits.

.NOTES
    Verified against the upstream project on 2026-08-29:

    - The release asset is an INNO SETUP installer, not NSIS. VIIPER's own `scripts/install.ps1`
      passes `/S`, which Inno Setup does not recognise, so that script pops the full interactive
      installer instead of installing silently. The switches used below are the correct ones.
    - `USBip-0.9.7.7-x64.exe` carries a valid GlobalSign EV code-signing signature issued to
      Cloudyne Systems (Scheibling Consulting AB) — the operator of the Open Source Codesigning
      Initiative. Its drivers land in the driver store signed by the Microsoft Windows Hardware
      Compatibility Publisher, marked Universal and Attested. Current releases therefore need no
      Windows test-signing mode, and the warning in VIIPER's documentation about a test-signing CA
      being added as a trusted root is stale.
    - The pin is 0.9.7.7 and not the newer 0.9.7.8 deliberately. See `versionPinReason` in the lock
      file: 0.9.7.8 has two open kernel-pool-corruption reports against the Windows build the
      reference handheld runs.
    - The package installs `usbip2_ude.sys` and its companion filter `usbip2_filter.sys`, registers
      the root device `ROOT\USBIP_WIN2\UDE`, and places `usbip.exe` in `%ProgramFiles%\USBip`.
      VIIPER attaches through the driver's device interface by IOCTL and falls back to that
      executable, so installing this package satisfies both of its paths.

    The pinned identity below is a copy of the reviewed entry in
    `third_party/controller/controller-components.lock.json`. It is duplicated here, and only here,
    because this script runs on the user's machine where the repository does not exist;
    `eng/assert-controller-pin.ps1` fails the build if the two ever disagree.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string]$InstallerPath = (Join-Path $PSScriptRoot 'USBip-0.9.7.7-x64.exe'),

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$LogPath = (Join-Path $env:ProgramData 'WSGM\usbip-install.log'),

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$StatusPath = (Join-Path $env:ProgramData 'WSGM\usbip-install-status.ini'),

    [Parameter()]
    [switch]$ReportOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RequiredVersion = [Version]'0.9.7.7'
$InstallerUrl = 'https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.7/USBip-0.9.7.7-x64.exe'
$InstallerSha256 = '51620FA5F9F8BE5932BC9D786DEEE557CE06D5407A99CAB490DCFAC71F185FEA'
$SignerThumbprint = '9AC56B6C76141395D74FFF6652818376E80B9C95'
$SilentArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCANCEL', '/SP-')

function ConvertTo-BoundedIniValue {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowNull()]
        [object]$Value,

        [ValidateRange(1, 1024)]
        [int]$MaximumLength = 512
    )

    if ($null -eq $Value) {
        return ''
    }

    $text = ([string]$Value) -replace '[\u0000-\u001F\u007F]+', ' '
    $text = $text.Trim()
    if ($text.Length -gt $MaximumLength) {
        return $text.Substring(0, $MaximumLength)
    }

    return $text
}

function Write-OutcomeStatus {
    <#
    .SYNOPSIS
        Atomically publishes the bounded result that the installer can show to the user.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet(
            'running',
            'installed',
            'already-present',
            'blocked-newer-version',
            'report-only',
            'failed')]
        [string]$Outcome,

        [AllowNull()]
        [object]$ObservedVersion,

        [AllowNull()]
        [Nullable[bool]]$DriverRegistered,

        [Parameter(Mandatory)]
        [bool]$RebootRequired,

        [AllowNull()]
        [object]$Message
    )

    $fullStatusPath = $null
    $temporaryPath = $null
    $backupPath = $null
    try {
        $fullStatusPath = [IO.Path]::GetFullPath($StatusPath)
        $directory = [IO.Path]::GetDirectoryName($fullStatusPath)
        if ([string]::IsNullOrWhiteSpace($directory)) {
            throw "Status path has no parent directory: $StatusPath"
        }
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }

        $isFinal = $Outcome -cne 'running'
        $driverValue = if ($null -eq $DriverRegistered) {
            'unknown'
        }
        elseif ($DriverRegistered.Value) {
            'true'
        }
        else {
            'false'
        }
        $completedAt = if ($isFinal) { [DateTime]::UtcNow.ToString('O') } else { '' }
        $lines = @(
            '[usbip]',
            'schemaVersion=1',
            "outcome=$(ConvertTo-BoundedIniValue $Outcome 64)",
            "requiredVersion=$(ConvertTo-BoundedIniValue $RequiredVersion 64)",
            "observedVersion=$(ConvertTo-BoundedIniValue $ObservedVersion 64)",
            "driverRegistered=$driverValue",
            "rebootRequired=$($RebootRequired.ToString().ToLowerInvariant())",
            "completedAtUtc=$completedAt",
            "updatedAtUtc=$([DateTime]::UtcNow.ToString('O'))",
            "message=$(ConvertTo-BoundedIniValue $Message 512)"
        )
        $content = ($lines -join "`r`n") + "`r`n"
        # UTF-16LE with a BOM is deliberate: Inno Setup's GetIniString then reads non-ASCII
        # diagnostic text correctly through the Windows profile API.
        $encoding = New-Object Text.UnicodeEncoding($false, $true)
        $encodedBytes = $encoding.GetPreamble().Length + $encoding.GetByteCount($content)
        if ($encodedBytes -gt 4096) {
            throw 'Generated USB/IP outcome marker exceeded its 4 KiB limit.'
        }

        $leafName = [IO.Path]::GetFileName($fullStatusPath)
        $nonce = "{0}-{1}" -f $PID, [Guid]::NewGuid().ToString('N')
        $temporaryPath = Join-Path $directory ".$leafName.$nonce.tmp"
        $backupPath = Join-Path $directory ".$leafName.$nonce.bak"
        [IO.File]::WriteAllText($temporaryPath, $content, $encoding)

        if ([IO.File]::Exists($fullStatusPath)) {
            # File.Replace keeps the previous complete marker in a same-volume backup until the
            # new complete marker is durable. The unique backup is removed only after replacement.
            [IO.File]::Replace($temporaryPath, $fullStatusPath, $backupPath, $true)
            $temporaryPath = $null
            $completedBackup = $backupPath
            $backupPath = $null
            try {
                [IO.File]::Delete($completedBackup)
            }
            catch {
                Write-Warning "usbip: previous status remains at '$completedBackup': $($_.Exception.Message)"
            }
        }
        else {
            try {
                [IO.File]::Move($temporaryPath, $fullStatusPath)
                $temporaryPath = $null
            }
            catch [IO.IOException] {
                # Another invocation may have created the marker between Exists and Move. Replace
                # that complete marker instead of deleting it first.
                if (-not [IO.File]::Exists($fullStatusPath)) {
                    throw
                }
                [IO.File]::Replace($temporaryPath, $fullStatusPath, $backupPath, $true)
                $temporaryPath = $null
                $completedBackup = $backupPath
                $backupPath = $null
                try {
                    [IO.File]::Delete($completedBackup)
                }
                catch {
                    Write-Warning "usbip: previous status remains at '$completedBackup': $($_.Exception.Message)"
                }
            }
        }
    }
    catch {
        # Outcome reporting must never turn this optional driver into an installer failure. The
        # existing complete marker is preserved whenever atomic replacement itself fails.
        Write-Warning "usbip: could not publish status to '$StatusPath': $($_.Exception.Message)"
    }
    finally {
        try {
            if ($null -ne $temporaryPath -and [IO.File]::Exists($temporaryPath)) {
                [IO.File]::Delete($temporaryPath)
            }
        }
        catch {
            Write-Warning "usbip: could not remove temporary status '$temporaryPath': $($_.Exception.Message)"
        }
        try {
            if ($null -ne $backupPath -and [IO.File]::Exists($backupPath)) {
                if ($null -ne $fullStatusPath -and -not [IO.File]::Exists($fullStatusPath)) {
                    # A failed replacement must not discard the last complete outcome.
                    [IO.File]::Move($backupPath, $fullStatusPath)
                }
                else {
                    [IO.File]::Delete($backupPath)
                }
            }
        }
        catch {
            Write-Warning "usbip: previous status remains at '$backupPath': $($_.Exception.Message)"
        }
    }
}

function Write-Step {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Message
    )

    $line = '{0:yyyy-MM-dd HH:mm:ss} usbip: {1}' -f (Get-Date), $Message
    Write-Information $line -InformationAction Continue
    try {
        $directory = Split-Path -Path $LogPath -Parent
        if (-not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }

        Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    }
    catch {
        # The log is a diagnostic, not the job. Losing it must not stop a driver install, but it is
        # still reported so a silent setup does not look like it wrote a log it did not.
        Write-Warning "usbip: could not append to '$LogPath': $($_.Exception.Message)"
    }
}

function Get-UsbipState {
    <#
    .SYNOPSIS
        Reports whether usbip-win2's client driver is registered, and at which version.
    .DESCRIPTION
        Two independent sources, because they answer different questions and fail differently.

        The uninstall entry is the package's own record and carries the version, which is the only
        place a version can be read: the shipped `usbip2_ude.sys` has no version resource at all.

        The driver's own service key answers whether the kernel half is actually registered, which
        is what an attach needs. It is deliberately not a file test — this is a universal driver
        that lives in the driver store, so `System32\drivers\usbip2_ude.sys` does not exist even on
        a fully working install (device-verified on the reference Claw, 2026-08-29, where the driver
        sits under `DriverStore\FileRepository\usbip2_ude.inf_amd64_…`). It is also not a `pnputil`
        parse: that tool's output is localised, and this machine prints it in German.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param()

    $version = $null
    $roots = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
    foreach ($root in $roots) {
        $entry = Get-ItemProperty -Path $root -ErrorAction SilentlyContinue |
            Where-Object { $_.PSObject.Properties.Name -contains 'DisplayName' } |
            Where-Object { $_.DisplayName -like 'USBip version*' } |
            Select-Object -First 1
        if ($null -ne $entry -and $entry.PSObject.Properties.Name -contains 'DisplayVersion') {
            $parsed = [Version]'0.0'
            if ([Version]::TryParse($entry.DisplayVersion, [ref]$parsed)) {
                $version = $parsed
                break
            }
        }
    }

    $registered = Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\usbip2_ude'
    return @{ Version = $version; DriverRegistered = $registered }
}

function Assert-PinnedInstaller {
    <#
    .SYNOPSIS
        Fails unless the file is byte-for-byte the reviewed asset and validly signed by its signer.
    .PARAMETER Path
        The installer to verify.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Path
    )

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -cne $InstallerSha256) {
        # Refuse rather than degrade. Everything this file does happens in the kernel, so an
        # unexpected payload is the one case where doing nothing is strictly better.
        throw "Installer SHA-256 $actual does not match the pinned $InstallerSha256."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Installer is not validly signed (status: $($signature.Status))."
    }

    $thumbprint = $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
    if ($thumbprint -cne $SignerThumbprint) {
        throw "Installer signer thumbprint $thumbprint does not match the pinned $SignerThumbprint."
    }

    Write-Step "verified $([System.IO.Path]::GetFileName($Path)): SHA-256 $actual, signer $thumbprint"
}

$temporaryDirectory = $null
$outcome = 'running'
$outcomeMessage = 'USB/IP driver evaluation started.'
$observedVersion = $null
$driverRegistered = $null
$rebootRequired = $false
Write-OutcomeStatus -Outcome $outcome -ObservedVersion $observedVersion `
    -DriverRegistered $driverRegistered -RebootRequired $rebootRequired -Message $outcomeMessage
try {
    $state = Get-UsbipState
    $installed = $state.Version
    $observedVersion = $installed
    $driverRegistered = [bool]$state.DriverRegistered
    if ($null -ne $installed -and $installed -gt $RequiredVersion) {
        # A newer build than the reviewed pin. Not silently accepted, and not silently replaced
        # either: 0.9.7.8 is excluded here for open kernel-pool-corruption reports (see the header),
        # and forcing a downgrade under another product's installed driver is its own hazard. Say
        # what is installed, leave it alone, and leave controller management unavailable until a
        # human decides.
        $outcome = 'blocked-newer-version'
        $outcomeMessage = "Installed $installed is newer than reviewed $RequiredVersion and was not replaced."
        Write-Step "installed $installed is newer than the reviewed $RequiredVersion; not replaced"
        Write-Warning ("usbip-win2 $installed is installed, which is newer than the reviewed " +
            "$RequiredVersion that WSGM pins. It was neither used as a match nor overwritten: " +
            "0.9.7.8 has open kernel-pool-corruption reports, and forcing a downgrade under " +
            "another product's driver is its own hazard. Uninstall it and re-run this step to get " +
            "the reviewed build. See '$LogPath'.")
        # Exit 0 like every other outcome here: a driver state WSGM will not touch is still a
        # supported one, and this step must never strand a WSGM install.
        exit 0
    }

    if ($null -ne $installed -and $installed -eq $RequiredVersion -and $state.DriverRegistered) {
        $outcome = 'already-present'
        $outcomeMessage = "USB/IP $installed is already present and its driver is registered."
        Write-Step "already present (installed $installed, required $RequiredVersion); nothing to do"
        exit 0
    }

    if ($null -ne $installed -and $installed -eq $RequiredVersion) {
        # The package's own record says it is here but the kernel half is not registered — a
        # half-removed install, or one whose driver was deleted underneath it. Reinstalling is the
        # repair, so this is deliberately not treated as "already present".
        Write-Step "version $installed is recorded but the usbip2_ude driver is not registered; repairing"
    }
    elseif ($null -eq $installed -and $state.DriverRegistered) {
        # Registered out of band, with no uninstall entry to read a version from. The version cannot
        # be established, so the upstream installer — which is an upgrade-in-place installer — gets
        # to decide. That is what the user ticked the task for.
        Write-Step "the usbip2_ude driver is registered but its version cannot be established; installing $RequiredVersion over it"
    }
    elseif ($null -eq $installed) {
        Write-Step "not installed; required $RequiredVersion"
    }
    else {
        Write-Step "outdated (installed $installed, required $RequiredVersion); upgrading"
    }

    $source = $InstallerPath
    if (-not [string]::IsNullOrWhiteSpace($source) -and (Test-Path -LiteralPath $source)) {
        Write-Step "using the staged installer at $source"
    }
    else {
        # The release build could not stage it — no network on the release machine — so fetch the
        # same pinned asset. Verification below is identical either way.
        Write-Step "no staged installer; downloading $InstallerUrl"
        $temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('wsgm-usbip-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
        $source = Join-Path $temporaryDirectory 'USBip-setup.exe'
        $previousProgress = $ProgressPreference
        try {
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri $InstallerUrl -OutFile $source -UseBasicParsing
        }
        finally {
            $ProgressPreference = $previousProgress
        }
    }

    Assert-PinnedInstaller -Path $source

    if ($ReportOnly) {
        $outcome = 'report-only'
        $outcomeMessage = 'The pinned installer was verified; ReportOnly prevented installation.'
        Write-Step 'ReportOnly was requested; the installer was verified but not run'
        exit 0
    }

    # /NORESTART matters: setup decides when to offer a reboot, and a driver install must never
    # restart the machine out from under a setup that has not finished.
    Write-Step 'installing (all USB 3.0 hubs restart during this step)'
    $process = Start-Process -FilePath $source -ArgumentList $SilentArguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "The usbip-win2 installer exited with code $($process.ExitCode)."
    }

    # Trust the result, not the exit code: confirm the kernel half is actually registered now.
    $result = Get-UsbipState
    $observedVersion = $result.Version
    $driverRegistered = [bool]$result.DriverRegistered
    if (-not $result.DriverRegistered) {
        throw 'The usbip-win2 installer reported success but the usbip2_ude driver is not registered.'
    }

    $reported = if ($null -eq $result.Version) { 'an unreported version' } else { $result.Version }
    $outcome = 'installed'
    $outcomeMessage = "USB/IP $reported was installed and its driver is registered."
    $rebootRequired = $true
    Write-Step "installed $reported; a reboot is required before the virtual controller can attach"
    exit 0
}
catch {
    # A missing driver is a supported state, so this failure is reported and not propagated: WSGM
    # installs fine and reports controller management as unavailable until the driver is present.
    $outcome = 'failed'
    $outcomeMessage = ConvertTo-BoundedIniValue $_.Exception.Message 512
    Write-Step "failed: $outcomeMessage"
    Write-Step 'controller management stays unavailable; install usbip-win2 and re-run WSGM setup'
    Write-Warning "usbip: driver installation failed. See '$LogPath'."
    exit 0
}
finally {
    if ($null -ne $temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-OutcomeStatus -Outcome $outcome -ObservedVersion $observedVersion `
        -DriverRegistered $driverRegistered -RebootRequired $rebootRequired -Message $outcomeMessage
}
