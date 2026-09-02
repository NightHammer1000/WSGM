<#
.SYNOPSIS
    Fails when the driver installer's pinned identity disagrees with the reviewed lock file.

.DESCRIPTION
    `installer/Install-UsbipDriver.ps1` runs on the user's machine, where the repository does not
    exist, so it cannot read `third_party/controller/controller-components.lock.json` at runtime and
    has to carry the pinned version, URL, digest, signer thumbprint and silent arguments itself.
    That is the only copy, and this check is what keeps it honest: bumping the lock without bumping
    the script — or the reverse — fails verification instead of shipping a setup that installs one
    version while the review covered another.

    Comparison is on the values, not on formatting, so reordering or reformatting either file is
    free. Every mismatch is reported in one run rather than stopping at the first, because a version
    bump normally moves several of them together.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$LockPath = (Join-Path $PSScriptRoot '..\third_party\controller\controller-components.lock.json'),

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$ScriptPath = (Join-Path $PSScriptRoot '..\installer\Install-UsbipDriver.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
$entry = $lock.components | Where-Object { $_.id -eq 'usbip-win2' } | Select-Object -First 1
if ($null -eq $entry) {
    throw "No 'usbip-win2' component in '$LockPath'."
}

$script = Get-Content -LiteralPath $ScriptPath -Raw

function Get-ScriptValue {
    <#
    .SYNOPSIS
        Reads a single-quoted script constant by name.
    .PARAMETER Name
        The variable name, without the sigil.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Name
    )

    $match = [regex]::Match($script, "(?m)^\`$$Name\s*=\s*(?:\[Version\])?'([^']*)'")
    if (-not $match.Success) {
        throw "Could not read `$$Name from '$ScriptPath'."
    }

    return $match.Groups[1].Value
}

$failures = [System.Collections.Generic.List[string]]::new()

function Assert-Equal {
    <#
    .SYNOPSIS
        Records a mismatch instead of throwing, so one run reports every disagreement.
    .PARAMETER Label
        What is being compared.
    .PARAMETER Expected
        The reviewed value from the lock file.
    .PARAMETER Actual
        The value the shipped script carries.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Label,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Expected,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Actual
    )

    if ($Expected -cne $Actual) {
        $failures.Add("$Label`: lock has '$Expected', Install-UsbipDriver.ps1 has '$Actual'.")
    }
}

Assert-Equal -Label 'version' -Expected $entry.version -Actual (Get-ScriptValue -Name 'RequiredVersion')
Assert-Equal -Label 'assetUrl' -Expected $entry.assetUrl -Actual (Get-ScriptValue -Name 'InstallerUrl')
Assert-Equal -Label 'assetSha256' -Expected $entry.assetSha256 -Actual (Get-ScriptValue -Name 'InstallerSha256')
Assert-Equal -Label 'signerThumbprint' -Expected $entry.signerThumbprint -Actual (Get-ScriptValue -Name 'SignerThumbprint')

# The default staged path and the installer's [Files] entry both name the asset, so a version bump
# that missed either would ship a setup that silently falls back to downloading.
$defaultPath = [regex]::Match($script, "(?m)^\s*\[string\]\`$InstallerPath\s*=\s*\(Join-Path \`$PSScriptRoot '([^']*)'\)")
if (-not $defaultPath.Success) {
    $failures.Add("Could not read the default `$InstallerPath from '$ScriptPath'.")
}
else {
    Assert-Equal -Label 'staged asset name' -Expected $entry.asset -Actual $defaultPath.Groups[1].Value
}

$issPath = Join-Path $PSScriptRoot '..\installer\WSGM.iss'
$iss = Get-Content -LiteralPath $issPath -Raw
if ($iss -notmatch [regex]::Escape($entry.asset)) {
    $failures.Add("WSGM.iss does not ship '$($entry.asset)'.")
}

# The silent switches decide whether setup installs quietly or stalls on an interactive installer
# nobody can see, so they are reviewed data too.
$argumentsMatch = [regex]::Match($script, "(?m)^\`$SilentArguments\s*=\s*@\(([^)]*)\)")
if (-not $argumentsMatch.Success) {
    $failures.Add("Could not read `$SilentArguments from '$ScriptPath'.")
}
else {
    $actualArguments = [regex]::Matches($argumentsMatch.Groups[1].Value, "'([^']*)'") |
        ForEach-Object { $_.Groups[1].Value }
    Assert-Equal `
        -Label 'silentArguments' `
        -Expected ($entry.silentArguments -join ' ') `
        -Actual ($actualArguments -join ' ')
}

if ($failures.Count -gt 0) {
    throw ("The pinned usbip-win2 identity disagrees with the reviewed lock file:`n  " +
        ($failures -join "`n  "))
}

Write-Information "usbip-win2 pin matches the lock file ($($entry.version))." -InformationAction Continue
