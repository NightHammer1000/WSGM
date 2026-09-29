[CmdletBinding()]
param(
    [switch]$Fix,
    [switch]$SkipPrettier
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
# Push/Pop rather than Set-Location: the gate must be runnable from anywhere
# without relocating the caller's shell, including when a step below throws.
Push-Location $root
try {
    # Asset generation uses TypeScript even when formatting is skipped. Provision once for both
    # paths; -SkipPrettier skips only the formatting command it names.
    if (-not (Test-Path "node_modules") -or
        (Get-Item "package-lock.json").LastWriteTimeUtc -gt (Get-Item "node_modules").LastWriteTimeUtc) {
        npm ci --ignore-scripts --prefer-offline --no-audit --no-fund `
            --fetch-retries=2 --fetch-timeout=30000
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    }

    if (-not $SkipPrettier) {
        if ($Fix) {
            npm run format
        }
        else {
            npm run format:check
        }
        if ($LASTEXITCODE -ne 0) { throw "Prettier check failed" }
    }

    # The shipped asset is generated from its TypeScript source. This rebuilds it
    # into memory and compares, so neither a source edit that was never compiled
    # nor a hand edit of the generated file can ship. It needs node_modules, so it
    # is separate from the built-ins-only check above.
    npm run steam-assets:check
    if ($LASTEXITCODE -ne 0) { throw "Steam UI asset is not current with its TypeScript source" }

    # The toolkit's own check, run against WSGM's composed asset: the ownership claims, exercised
    # on the bytes this build injects. It covers the scenarios that cost device sessions —
    # reclaiming a previous bridge's work rather than tearing it down, and restoring exactly what
    # was displaced. Nothing else in this gate can observe that: the C# tests never execute the
    # injected JavaScript, and the drift check proves only that the asset is current, not correct.
    npm run steam-assets:claims
    if ($LASTEXITCODE -ne 0) { throw "Steam UI ownership claim check failed" }

    & "$PSScriptRoot\check-agent-guidance.ps1"

    # Parse every retained PowerShell entry point in this repository and its recursive submodules.
    # This is syntax-only: deployment, shell, installer, and hardware scripts must never be invoked
    # by the unattended verification gate merely to prove that they still parse.
    $powerShellFiles = @(
        git ls-files --recurse-submodules -- "*.ps1" "*.psm1" |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
    )
    if ($LASTEXITCODE -ne 0 -or $powerShellFiles.Count -eq 0) {
        throw "Enumerating tracked PowerShell scripts failed"
    }
    foreach ($powerShellFile in $powerShellFiles) {
        $tokens = $null
        $parseErrors = $null
        [void][Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $root $powerShellFile),
            [ref]$tokens,
            [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            $details = $parseErrors | ForEach-Object {
                "$powerShellFile`:$($_.Extent.StartLineNumber): $($_.Message)"
            }
            throw "PowerShell syntax validation failed:`n$($details -join [Environment]::NewLine)"
        }
    }
    Write-Host "Parsed $($powerShellFiles.Count) tracked PowerShell scripts."

    # Cheap source scan, before anything is built: a test or probe that can resolve the real
    # %LOCALAPPDATA%\WSGM directory is a defect regardless of whether it compiles.
    & "$PSScriptRoot\check-no-live-data-paths.ps1"

    # The setup step that installs the USB/IP driver carries its own copy of the pinned identity,
    # because it runs where the lock file does not exist. This is what stops the two drifting into
    # a setup that installs a version nobody reviewed.
    & "$PSScriptRoot\assert-controller-pin.ps1"

    # The vendored Rust library is validated and built before the .NET build,
    # which needs its staged output present. -Validate adds the library's own
    # gates (clippy as errors, unit tests) so a change there fails here rather than
    # in a release build.
    & "$PSScriptRoot\build-steam-input-lease.ps1" -Validate

    # This small solution does not benefit from one MSBuild node per logical CPU; on the
    # high-core reference handheld that left dozens of idle child processes after test runs.
    dotnet restore WSGM.slnx -m:1
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

    # Neither is ours to restyle, and both are reachable through a project reference.
    # Reformatting vendored upstream source would destroy the diff against upstream, which is what
    # makes it re-syncable; reformatting a submodule dirties a working tree this repository only
    # pins, and the result could never be committed from here anyway. Each has its own gates.
    $notOurs = @("third_party/", "external/")

    $formatArgs = @("format", "WSGM.slnx", "whitespace", "--no-restore", "--verbosity", "minimal")
    foreach ($path in $notOurs) { $formatArgs += @("--exclude", $path) }
    if (-not $Fix) { $formatArgs += "--verify-no-changes" }
    & dotnet @formatArgs
    if ($LASTEXITCODE -ne 0) { throw "C# whitespace format check failed" }

    $styleArgs = @("format", "WSGM.slnx", "style", "--no-restore", "--severity", "warn", "--verbosity", "minimal")
    foreach ($path in $notOurs) { $styleArgs += @("--exclude", $path) }
    if (-not $Fix) { $styleArgs += "--verify-no-changes" }
    & dotnet @styleArgs
    if ($LASTEXITCODE -ne 0) { throw "C# style check failed" }

    $analyzerArgs = @("format", "WSGM.slnx", "analyzers", "--no-restore", "--severity", "warn", "--verbosity", "minimal")
    foreach ($path in $notOurs) { $analyzerArgs += @("--exclude", $path) }
    if (-not $Fix) { $analyzerArgs += "--verify-no-changes" }
    & dotnet @analyzerArgs
    if ($LASTEXITCODE -ne 0) { throw "C# analyzer check failed" }

    dotnet build WSGM.slnx --configuration Release --no-restore --warnaserror -m:1
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

    dotnet test WSGM.slnx --configuration Release --no-build `
        --logger "console;verbosity=normal" -m:1
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed" }

    # Only WSGM.Tests carries the coverage collector. The submodule suites run above without a
    # collector request, avoiding false "collector not found" diagnostics while still keeping the
    # application's existing coverage artifact.
    dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --configuration Release --no-build `
        --settings coverlet.runsettings --collect:"XPlat Code Coverage" `
        --results-directory TestResults --logger "console;verbosity=normal" -m:1
    if ($LASTEXITCODE -ne 0) { throw "WSGM coverage test run failed" }
}
finally {
    Pop-Location
}
