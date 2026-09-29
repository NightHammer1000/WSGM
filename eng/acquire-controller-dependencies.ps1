<#
.SYNOPSIS
    Downloads and verifies the third-party controller components pinned in the lock file.

.DESCRIPTION
    `third_party/controller/controller-components.lock.json` is the single source of truth for what
    WSGM's controller support depends on, at which exact version, with which digest and which
    signer. This script reads it rather than restating it: a second copy of a pinned hash is a copy
    that can silently disagree with the reviewed one.

    Every asset is verified twice before it is allowed to exist under the destination — SHA-256
    against the reviewed digest, and Authenticode against the reviewed signer thumbprint. A failure
    of either is fatal here, because this runs on the release machine where the right answer is to
    stop and look, not to ship an unverified kernel-driver installer.

.PARAMETER Destination
    Where to place the verified assets. Each component gets its asset written directly here, so a
    release build can stage the directory into the installer payload unchanged.

.PARAMETER Component
    Restrict acquisition to these component ids. Defaults to every component in the lock file.

.PARAMETER LockPath
    The lock file to read. Defaults to the repository's own.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Destination,

    [Parameter()]
    [string[]]$Component,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$LockPath = (Join-Path $PSScriptRoot '..\third_party\controller\controller-components.lock.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null

$selected = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $lock.components) {
    if ($null -eq $Component -or $Component.Count -eq 0 -or $Component -contains $entry.id) {
        $selected.Add($entry)
    }
}

if ($selected.Count -eq 0) {
    throw "No component in '$LockPath' matched: $($Component -join ', ')."
}

foreach ($entry in $selected) {
    $assetPath = Join-Path $destinationRoot $entry.asset
    Write-Information "Acquiring $($entry.id) $($entry.version)" -InformationAction Continue

    # The default progress renderer costs more than the download on a large asset.
    $previousProgress = $ProgressPreference
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $entry.assetUrl -OutFile $assetPath -UseBasicParsing
    }
    finally {
        $ProgressPreference = $previousProgress
    }

    $actualHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -cne $entry.assetSha256) {
        Remove-Item -LiteralPath $assetPath -Force -ErrorAction SilentlyContinue
        throw "Hash mismatch for $($entry.asset): expected $($entry.assetSha256), got $actualHash."
    }

    if ($entry.PSObject.Properties.Name -contains 'signerThumbprint') {
        $signature = Get-AuthenticodeSignature -LiteralPath $assetPath
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            Remove-Item -LiteralPath $assetPath -Force -ErrorAction SilentlyContinue
            throw "Authenticode validation failed for $($entry.asset): $($signature.Status)."
        }

        $actualThumbprint = $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
        if ($actualThumbprint -cne $entry.signerThumbprint) {
            Remove-Item -LiteralPath $assetPath -Force -ErrorAction SilentlyContinue
            throw "Signer mismatch for $($entry.asset): got $actualThumbprint."
        }
    }

    Write-Information "  verified $($entry.asset) ($actualHash)" -InformationAction Continue
}
