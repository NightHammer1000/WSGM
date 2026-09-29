<#
.SYNOPSIS
    Builds Device Lab and the built-in device plugin from their pinned submodules.

.DESCRIPTION
    Device Lab is published self-contained for the optional tools component. The plugin submodule
    assembles, validates, and packs its framework-dependent package with that exact Device Lab
    build; WSGM then expands and validates the exact package tree handed to the installer.

    All inputs are source repositories pinned by Git links. This script performs no downloads and
    no hardware access.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64",

    [string]$BuiltInPackageId = "wsgm.device.msi.claw-8-a2vm",

    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outputFull = [IO.Path]::GetFullPath($OutputRoot)
$repositoryFull = [IO.Path]::GetFullPath($root).TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not ($outputFull + [IO.Path]::DirectorySeparatorChar).StartsWith(
        $repositoryFull,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Device component staging must stay inside the repository workspace."
}

$deviceLabRoot = Join-Path $root "external\WSGM.DeviceLab"
$deviceLabProject = Join-Path $deviceLabRoot "src\WSGM.DeviceLab\WSGM.DeviceLab.csproj"
$pluginRoot = Join-Path $root "external\WSGM.Device.Msi.Claw8A2Vm"
$pluginPack = Join-Path $pluginRoot "eng\pack.ps1"
$pluginSource = Join-Path $pluginRoot "src\WSGM.Device.Msi.Claw8A2Vm"
$manifestFile = Join-Path $pluginSource "plugin.wsgm.json"

foreach ($requiredSource in @($deviceLabProject, $pluginPack, $manifestFile)) {
    if (-not (Test-Path -LiteralPath $requiredSource -PathType Leaf)) {
        throw "A required submodule source is missing: $requiredSource. Run git submodule update --init --recursive."
    }
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "WSGM-DeviceComponents-{0}-{1}" -f $PID, [Guid]::NewGuid().ToString("N"))
$temporaryMarker = Join-Path $temporaryRoot ".wsgm-device-component-stage"
$temporaryMarkerValue = "WSGM device component stage v1"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
Set-Content -LiteralPath $temporaryMarker -Value $temporaryMarkerValue -NoNewline

function Invoke-ComponentPublish(
    [string]$Project,
    [string]$Destination
) {
    $arguments = @(
        "publish",
        $Project,
        "--configuration", $Configuration,
        "--runtime", $RuntimeIdentifier,
        "--self-contained", "true",
        "--output", $Destination,
        "/p:PublishSingleFile=false",
        "/p:TreatWarningsAsErrors=true",
        "-m:1"
    )
    if ($NoRestore) {
        $arguments += "--no-restore"
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing $Project failed."
    }
}

function Assert-RegularSourceFile([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if ($item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Component metadata may not be copied through a link or reparse point: $Path"
    }
}

function Copy-DotNetRuntimeNotices(
    [string]$AssetsPath,
    [string]$Destination
) {
    if (-not (Test-Path -LiteralPath $AssetsPath -PathType Leaf)) {
        throw "Component restore assets are missing: $AssetsPath"
    }

    $assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -Depth 100
    $runtimePackName = "Microsoft.NETCore.App.Runtime.$RuntimeIdentifier"
    $frameworks = @($assets.project.frameworks.psobject.Properties | ForEach-Object { $_.Value })
    $runtimeDependencies = @(
        $frameworks |
            ForEach-Object { $_.downloadDependencies } |
            Where-Object { [string]$_.name -ieq $runtimePackName }
    )
    if ($runtimeDependencies.Count -ne 1) {
        throw "Component restore must resolve exactly one $runtimePackName pack."
    }

    $versionRange = ([string]$runtimeDependencies[0].version -replace '^\[|\]$', '')
    $bounds = @($versionRange.Split(',') | ForEach-Object { $_.Trim() })
    if ($bounds.Count -ne 2 -or $bounds[0] -cne $bounds[1] -or
        [string]::IsNullOrWhiteSpace($bounds[0])) {
        throw "Component runtime pack version is not exact: $($runtimeDependencies[0].version)"
    }

    $runtimePack = $null
    foreach ($packageFolder in $assets.packageFolders.psobject.Properties.Name) {
        $candidate = Join-Path (
            Join-Path $packageFolder $runtimePackName.ToLowerInvariant()) $bounds[0]
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $runtimePack = $candidate
            break
        }
    }
    if ($null -eq $runtimePack) {
        throw "Resolved component runtime pack was not found in the restored package folders."
    }

    foreach ($notice in @(
        @{ Source = "LICENSE.TXT"; Destination = "DotNetRuntime-LICENSE.txt" },
        @{ Source = "THIRD-PARTY-NOTICES.TXT"; Destination = "DotNetRuntime-THIRD-PARTY-NOTICES.txt" }
    )) {
        $source = Join-Path $runtimePack $notice.Source
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Required .NET runtime notice is missing: $source"
        }
        Assert-RegularSourceFile $source
        Copy-Item -LiteralPath $source -Destination (Join-Path $Destination $notice.Destination) -Force
    }
}

try {
    $deviceLabDestination = Join-Path $temporaryRoot "Tools\DeviceLab"
    Invoke-ComponentPublish -Project $deviceLabProject -Destination $deviceLabDestination
    Copy-DotNetRuntimeNotices -AssetsPath (
        Join-Path $deviceLabRoot "src\WSGM.DeviceLab\obj\project.assets.json") -Destination $deviceLabDestination

    $deviceLabLicense = Join-Path $deviceLabRoot "LICENSE"
    Assert-RegularSourceFile $deviceLabLicense
    Copy-Item -LiteralPath $deviceLabLicense -Destination (
        Join-Path $deviceLabDestination "LICENSE.txt") -Force

    $validator = Join-Path $deviceLabDestination "wsgm-device.exe"
    foreach ($requiredToolFile in @(
        $validator,
        (Join-Path $deviceLabDestination "THIRD_PARTY_NOTICES.md"),
        (Join-Path $deviceLabDestination "DotNetRuntime-LICENSE.txt"),
        (Join-Path $deviceLabDestination "DotNetRuntime-THIRD-PARTY-NOTICES.txt")
    )) {
        if (-not (Test-Path -LiteralPath $requiredToolFile -PathType Leaf)) {
            throw "Device Lab publish is missing required content: $requiredToolFile"
        }
    }

    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json -Depth 32
    $packageId = [string]$manifest.id
    $packageVersion = [string]$manifest.version
    $entryAssembly = [string]$manifest.entryAssembly
    if ($packageId -cne $BuiltInPackageId) {
        throw "The built-in package declares id '$packageId', not the expected '$BuiltInPackageId'."
    }
    if ($packageId -notmatch '^[A-Za-z0-9._-]+$' -or
        $packageVersion -notmatch '^[0-9]+(?:\.[0-9]+){1,3}$') {
        throw "$manifestFile has an unsafe package id or version."
    }
    if ([IO.Path]::IsPathRooted($entryAssembly) -or
        [IO.Path]::GetFileName($entryAssembly) -cne $entryAssembly -or
        [IO.Path]::GetExtension($entryAssembly) -cne ".dll") {
        throw "$manifestFile must name a package-root entry assembly."
    }

    $packageBuildRoot = Join-Path $temporaryRoot "Packed"
    $packArguments = @{
        OutputRoot = $packageBuildRoot
        Configuration = $Configuration
        RuntimeIdentifier = $RuntimeIdentifier
        DeviceLabExecutable = $validator
    }
    if ($NoRestore) {
        $packArguments.NoRestore = $true
    }
    & $pluginPack @packArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Building the commit-pinned device package failed."
    }

    $archive = Join-Path $packageBuildRoot "$packageId-$packageVersion.wsgmpkg"
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw "The plugin packer did not produce the expected archive: $archive"
    }

    $archiveEntries = @(& tar -tf $archive)
    if ($LASTEXITCODE -ne 0) {
        throw "Reading the built-in package archive failed."
    }
    foreach ($archiveEntry in $archiveEntries) {
        $normalized = ([string]$archiveEntry).Replace('\', '/').TrimEnd('/')
        if ($normalized.Length -eq 0) {
            continue
        }
        $segments = @($normalized.Split('/', [StringSplitOptions]::RemoveEmptyEntries))
        if ([IO.Path]::IsPathRooted($normalized) -or
            $normalized -match '^[A-Za-z]:' -or
            $segments -contains '..') {
            throw "The built-in package archive contains an unsafe path: $archiveEntry"
        }
    }

    $packageDestination = Join-Path $temporaryRoot "Packages\$packageId"
    New-Item -ItemType Directory -Path $packageDestination -Force | Out-Null
    & tar -xf $archive -C $packageDestination
    if ($LASTEXITCODE -ne 0) {
        throw "Extracting the built-in package failed."
    }

    foreach ($required in @("PROVENANCE.md", "THIRD_PARTY_NOTICES.md", "LICENSE.txt", $entryAssembly)) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageDestination $required) -PathType Leaf)) {
            throw "The built-in package is missing required content: $required"
        }
    }

    $sourceGlyphs = @(Get-ChildItem -LiteralPath (Join-Path $pluginSource "glyphs") -File -Recurse)
    $stagedGlyphs = @(Get-ChildItem -LiteralPath (Join-Path $packageDestination "glyphs") -File -Recurse)
    if ($sourceGlyphs.Count -eq 0 -or $stagedGlyphs.Count -ne $sourceGlyphs.Count) {
        throw "The built-in package staged $($stagedGlyphs.Count) of $($sourceGlyphs.Count) source glyph files."
    }
    Write-Host "  glyph assets staged: $($stagedGlyphs.Count) file(s)"

    $validationOutput = @(& $validator validate $packageDestination 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw ("Offline package validation failed for {0}: {1}" -f
            $packageId, ($validationOutput -join [Environment]::NewLine))
    }

    foreach ($component in @("Tools", "Packages")) {
        $destination = Join-Path $outputFull $component
        if (Test-Path -LiteralPath $destination) {
            throw "Refusing to overwrite existing component staging: $destination"
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Move-Item -LiteralPath (Join-Path $temporaryRoot $component) -Destination $destination
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        $systemTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
            [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $markerIsValid = (Test-Path -LiteralPath $temporaryMarker -PathType Leaf) -and
            (Get-Content -LiteralPath $temporaryMarker -Raw).Trim() -cne "" -and
            (Get-Content -LiteralPath $temporaryMarker -Raw).Trim() -ceq $temporaryMarkerValue
        if ($resolvedTemporaryRoot.StartsWith(
                $systemTemporaryRoot,
                [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedTemporaryRoot).StartsWith(
                "WSGM-DeviceComponents-$PID-",
                [StringComparison]::Ordinal) -and
            $markerIsValid) {
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
        }
        else {
            Write-Warning "Refusing to remove an unrecognized component staging directory: $resolvedTemporaryRoot"
        }
    }
}

Write-Host "Device tools and package staged under $outputFull."
