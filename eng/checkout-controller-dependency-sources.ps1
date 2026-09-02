[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null

$lockPath = Join-Path $PSScriptRoot "..\third_party\controller\controller-components.lock.json"
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$sources = @($lock.components | ForEach-Object {
    @{
        Id = $_.id
        Repository = $_.repository
        Commit = $_.commit
    }
})

foreach ($source in $sources) {
    $sourceDirectory = Join-Path $destinationRoot $source.Id
    if (Test-Path -LiteralPath $sourceDirectory) {
        throw "Refusing to replace existing source directory: $sourceDirectory"
    }

    & git clone --filter=blob:none --no-checkout $source.Repository $sourceDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "git clone failed for $($source.Id)."
    }

    & git -C $sourceDirectory checkout --detach $source.Commit
    if ($LASTEXITCODE -ne 0) {
        throw "git checkout failed for $($source.Id) at $($source.Commit)."
    }

    $actualCommit = (& git -C $sourceDirectory rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -cne $source.Commit) {
        throw "Commit verification failed for $($source.Id): got $actualCommit."
    }
}

Write-Host "Exact reviewed sources checked out under $destinationRoot."
Write-Host 'No build, driver operation, executable launch, or install was performed.'
