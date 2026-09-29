<#[
.SYNOPSIS
    Fails when isolated release staging contains a boundary or package-safety violation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outputFull = [IO.Path]::GetFullPath($OutputRoot)

function Require-File([string]$RelativePath) {
    $path = Join-Path $outputFull $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required staged artifact is missing: $RelativePath"
    }
}

function Assert-NoLinks([string]$Directory) {
    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Force -Recurse) {
        if ($entry.LinkType -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Release staging may not contain links or reparse points: $($entry.FullName)"
        }
    }
}

Require-File "App\WSGM.exe"
Require-File "App\WSGM.Launch.exe"
Require-File "App\WSGM.LogonService.exe"
Require-File "App\LICENSE.txt"
Require-File "Tools\DeviceLab\wsgm-device.exe"
Require-File "Tools\DeviceLab\THIRD_PARTY_NOTICES.md"
Require-File "Tools\DeviceLab\DotNetRuntime-LICENSE.txt"
Require-File "Tools\DeviceLab\DotNetRuntime-THIRD-PARTY-NOTICES.txt"

foreach ($directory in @("App", "Tools", "Packages")) {
    Assert-NoLinks (Join-Path $outputFull $directory)
}

$packageRoots = @(
    Get-ChildItem -LiteralPath (Join-Path $outputFull "Packages") -Directory |
    Sort-Object FullName
)
if ($packageRoots.Count -ne 1) {
    throw "Exactly one plugin package must be staged; found $($packageRoots.Count)."
}

$forbiddenExtensions = @(
    ".pdb", ".cs", ".csx", ".ps1", ".psm1", ".pfx", ".p12", ".snk", ".key",
    ".pem", ".pvk", ".jks", ".keystore", ".etl", ".evtx", ".pcap", ".pcapng",
    ".dmp", ".dump", ".wsgmcap", ".zip", ".7z"
)
$textExtensions = @(".json", ".xml", ".config", ".txt", ".md", ".js", ".css", ".svg")
$localPathPattern = '(?i)(?:[A-Z]:[\\/](?:Users|Coding|Repos?|Source|Worktrees?)[\\/]|\\\\\?\\[A-Z]:\\)'
$secretPattern = '(?i)(?:password|passwd|api[_-]?key|access[_-]?token|client[_-]?secret)\s*[=:]\s*["'']?[^\s"'']{8,}'

foreach ($packageRoot in $packageRoots) {
    Require-File ([IO.Path]::GetRelativePath($outputFull, (Join-Path $packageRoot.FullName "plugin.wsgm.json")))
    foreach ($noticeName in @("LICENSE.txt", "PROVENANCE.md", "THIRD_PARTY_NOTICES.md")) {
        Require-File ([IO.Path]::GetRelativePath(
            $outputFull,
            (Join-Path $packageRoot.FullName $noticeName)))
    }

    $manifest = Get-Content -LiteralPath (Join-Path $packageRoot.FullName "plugin.wsgm.json") -Raw |
        ConvertFrom-Json -Depth 32
    if ($packageRoot.Name -cne [string]$manifest.id) {
        throw "Plugin package path does not match its manifest identity: $($packageRoot.FullName)"
    }
    Require-File ([IO.Path]::GetRelativePath(
        $outputFull,
        (Join-Path $packageRoot.FullName ([string]$manifest.entryAssembly))))

    $files = @(Get-ChildItem -LiteralPath $packageRoot.FullName -File -Recurse | Sort-Object FullName)
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($packageRoot.FullName, $file.FullName).Replace("\", "/")
        if ($file.Extension -in $forbiddenExtensions) {
            throw "Plugin package contains source/debug/capture/key material: $relative"
        }
        if ($file.Name -in @(".env", "secrets.json", "appsettings.Development.json", "NuGet.Config")) {
            throw "Plugin package contains a credential-bearing developer file: $relative"
        }
        if ($relative -match '(?i)(?:^|/)(?:captures?|raw[-_]?evidence|fixtures?|recipes?)(?:/|$)') {
            throw "Plugin package contains a source-capture or evidence directory: $relative"
        }
        if ($file.Name -in @("WSGM.exe", "WSGM.Launch.exe", "WSGM.LogonService.exe",
            "WSGM.DeviceHost.exe", "wsgm-device.exe")) {
            throw "Plugin package contains an unrelated WSGM executable: $relative"
        }
        if ($file.Extension -in $textExtensions -and $file.Length -le 4MB) {
            $text = Get-Content -LiteralPath $file.FullName -Raw
            if ($text -match $localPathPattern) {
                throw "Plugin package leaks a local developer path: $relative"
            }
            if ($text -match $secretPattern) {
                throw "Plugin package contains a secret-shaped assignment: $relative"
            }
        }
    }

}

Write-Host "Component isolation and package staging assertions passed."
