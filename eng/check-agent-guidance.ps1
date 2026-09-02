<#[
.SYNOPSIS
    Verifies the load-bearing CLAUDE.md symlink convention.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$failures = [Collections.Generic.List[string]]::new()

function Relative-Path([string]$Path) {
    $rootFull = [IO.Path]::GetFullPath($root).TrimEnd("\", "/")
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($rootFull + "\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Agent-guidance path is outside the repository: $pathFull"
    }

    return $pathFull.Substring($rootFull.Length + 1).Replace("\", "/")
}

# A submodule's guidance belongs to its own repository: its CLAUDE.md is tracked there, so this
# repository's index never sees it and every check below would report it missing.
$submodules = @(& git -C $root ls-files -s |
    Where-Object { $_.StartsWith("160000 ", [StringComparison]::Ordinal) } |
    ForEach-Object { ($_ -split "`t", 2)[1] })
if ($LASTEXITCODE -ne 0) {
    throw "Agent-guidance validation could not list submodules."
}

$agentFiles = Get-ChildItem -LiteralPath $root -Filter "AGENTS.md" -File -Recurse |
    Where-Object { $_.FullName -notmatch "[\\/](\.claude|bin|obj|node_modules|publish|TestResults)[\\/]" }

foreach ($agents in $agentFiles) {
    $agentsRelative = Relative-Path $agents.FullName
    if ($submodules | Where-Object { $agentsRelative.StartsWith("$_/", [StringComparison]::OrdinalIgnoreCase) }) {
        continue
    }

    $directory = $agents.DirectoryName
    $claudePath = Join-Path $directory "CLAUDE.md"
    $relative = Relative-Path $claudePath
    if (-not (Test-Path -LiteralPath $claudePath)) {
        $failures.Add("Missing $relative beside $agentsRelative.")
        continue
    }

    $claude = Get-Item -LiteralPath $claudePath -Force
    if ($claude.LinkType -ne "SymbolicLink" -or
        @($claude.Target).Count -ne 1 -or
        [string]$claude.Target -cne "AGENTS.md") {
        $failures.Add("$relative must be a symbolic link whose exact target is AGENTS.md.")
    }

    $index = & git -C $root ls-files -s -- $relative
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($index)) {
        $failures.Add("$relative is not tracked.")
    }
    elseif (-not $index.StartsWith("120000 ", [StringComparison]::Ordinal)) {
        $failures.Add("$relative is tracked with a mode other than 120000.")
    }
}

if ($failures.Count -gt 0) {
    throw "Agent-guidance validation failed:`n - $($failures -join "`n - ")"
}

Write-Host "Every CLAUDE.md is a tracked AGENTS.md symlink."
