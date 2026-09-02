<#
.SYNOPSIS
    Fails the build when test, fixture, probe, or tooling sources can reach the developer's real
    WSGM data directory.

.DESCRIPTION
    A throwaway probe once destroyed the developer's real config.json. The repository rule that
    followed - no test or probe may touch %LOCALAPPDATA%\WSGM - has been convention ever since; this
    script makes it mechanical.

    It scans the sources that are allowed to open files on a developer's machine for the two ways
    that directory gets reached: a literal path containing "LOCALAPPDATA\WSGM", or a call to
    Environment.GetFolderPath/SpecialFolder.LocalApplicationData (or the LOCALAPPDATA environment
    variable) inside a project that has no business resolving it.

    One legitimate case exists: code whose purpose is to REFUSE that path has to resolve it first.
    Such a line carries the marker below with a reason. The marker is line-scoped rather than
    file-scoped on purpose - exempting a whole file would let an unrelated write slip in beside the
    guard that justified the exemption.

    Production WSGM processes resolve that directory legitimately - ConfigStore and Log own it - so
    their source is not scanned. Everything that runs
    as a test, plugin, or developer tool is.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot

# Scanned: anything that may run on a developer's machine outside the shipped application.
# Deliberately excluded: the shipped WSGM processes, which own the real directory; third_party\,
# which is vendored upstream source; and external\, whose submodules enforce this in their own
# repositories. Runtime tests use explicit-root seams.
$scanned = @(
    "tests"
)

# A literal WSGM data path, or resolving the local-app-data root at all. The second pattern is the
# one that matters: a probe that composes the path from SpecialFolder is exactly as destructive as
# one that hardcodes it, and only the literal form is obvious in review.
$patterns = @(
    @{ Name = "literal %LOCALAPPDATA%\WSGM path"; Regex = 'LOCALAPPDATA[\\/]+WSGM' },
    @{ Name = "SpecialFolder.LocalApplicationData"; Regex = 'SpecialFolder\.LocalApplicationData' },
    @{ Name = "LOCALAPPDATA environment variable"; Regex = 'GetEnvironmentVariable\(\s*"LOCALAPPDATA"' }
)

# A line may opt out only by stating why, in a form that greps cleanly during review.
$allowMarker = 'wsgm-allow-live-data-path:'

$findings = [System.Collections.Generic.List[object]]::new()

foreach ($relative in $scanned) {
    $directory = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $directory)) { continue }

    $files = Get-ChildItem -LiteralPath $directory -Recurse -File -Include *.cs, *.ps1, *.json |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

    foreach ($pattern in $patterns) {
        foreach ($match in ($files | Select-String -Pattern $pattern.Regex -CaseSensitive:$false -Context 3, 0)) {
            # Exemption for code that resolves the path in order to reject it. Accepted on the
            # matching line or within the three lines above, because the reason belongs in a comment
            # and a reason worth writing rarely fits on one line.
            $window = @($match.Line)
            if ($match.Context -and $match.Context.PreContext) { $window += $match.Context.PreContext }
            if ($window -match $allowMarker) { continue }
            $findings.Add([pscustomobject]@{
                    File    = $match.Path.Substring($root.Length + 1)
                    Line    = $match.LineNumber
                    Pattern = $pattern.Name
                    Text    = $match.Line.Trim()
                })
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Host "Sources that can reach the real WSGM data directory:" -ForegroundColor Red
    foreach ($finding in $findings) {
        Write-Host ("  {0}:{1}  [{2}]" -f $finding.File, $finding.Line, $finding.Pattern)
        Write-Host ("      {0}" -f $finding.Text) -ForegroundColor DarkGray
    }
    throw "Test, probe, or tooling code must never resolve %LOCALAPPDATA%\WSGM. Use an explicit temporary directory and the existing seams."
}

Write-Host "No test, probe, or tooling path resolves the live WSGM data directory."
