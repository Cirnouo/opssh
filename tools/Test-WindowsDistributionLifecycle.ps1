#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$InteropDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$LegalDirectory,
    [Parameter(Mandatory)][string]$WixPath
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
    $env:RUNNER_OS -cne 'Windows' -or $env:RUNNER_ARCH -cne 'X64') {
    throw 'Run the installer lifecycle only on a disposable GitHub-hosted Windows x64 runner.'
}
$production = $null
if (![version]::TryParse($Version, [ref]$production) -or $production.Build -lt 0 -or $production.Revision -ge 0 -or
    $production.ToString(3) -cne $Version -or $production.Major -gt 255 -or $production.Minor -gt 255 -or
    $production.Build -gt 65534) {
    throw 'Use the verified Windows package version.'
}
$alternateVersion = if ($production.Build -gt 0) {
    [version]::new($production.Major, $production.Minor, $production.Build - 1)
} elseif ($production.Minor -gt 0) {
    [version]::new($production.Major, $production.Minor - 1, 0)
} elseif ($production.Major -gt 0) {
    [version]::new($production.Major - 1, 0, 0)
} else {
    [version]'0.0.1'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $PWD.ProviderPath)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh lifecycle output directory.' }
$PackageDirectory = (Get-Item -LiteralPath $PackageDirectory).FullName
$InteropDirectory = (Get-Item -LiteralPath $InteropDirectory).FullName
$WixPath = (Get-Item -LiteralPath $WixPath).FullName
$LegalDirectory = (Get-Item -LiteralPath $LegalDirectory).FullName
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$repo = Split-Path -Parent $PSScriptRoot
$archive = Join-Path $OutputDirectory 'alternate-source.zip'
$source = Join-Path $OutputDirectory 'alternate-source'
$alternateOutput = Join-Path $OutputDirectory 'alternate-output'
$alternateRelease = Join-Path $alternateOutput 'release'
$alternatePackages = Join-Path $OutputDirectory 'alternate-packages'

# Produce the upgrade fixture from the same checked-out sources with one isolated project edit.
git -C $repo archive --format=zip "--output=$archive" HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not archive the lifecycle fixture source.' }
Expand-Archive -LiteralPath $archive -DestinationPath $source
$project = Join-Path $source 'tray/Opssh.Tray/Opssh.Tray.csproj'
$xml = [Xml.XmlDocument]::new()
$xml.PreserveWhitespace = $true
$xml.Load($project)
$versionNodes = $xml.SelectNodes('/Project/PropertyGroup/Version')
if ($versionNodes.Count -ne 1 -or $versionNodes[0].InnerText -cne $Version) {
    throw 'The checked-out tray project differs from the verified package version.'
}
$versionNodes[0].InnerText = $alternateVersion.ToString(3)
$xml.Save($project)
& (Join-Path $source 'tools/Test-Windows.ps1') -OutputDirectory $alternateOutput -ReleaseVersion $alternateVersion.ToString(3)
& (Join-Path $PSScriptRoot 'Test-DistributionNotices.ps1') -ReleaseDirectory $alternateRelease
& (Join-Path $PSScriptRoot 'Build-WindowsPackages.ps1') -ReleaseDirectory $alternateRelease `
    -Version $alternateVersion.ToString(3) -OutputDirectory $alternatePackages -WixPath $WixPath -LegalDirectory $LegalDirectory
& (Join-Path $PSScriptRoot 'Test-WindowsPackages.ps1') -ReleaseDirectory $alternateRelease `
    -Version $alternateVersion.ToString(3) -PackageDirectory $alternatePackages -WixPath $WixPath -LegalDirectory $LegalDirectory `
    -VerificationDirectory (Join-Path $OutputDirectory 'alternate-verification')

$arguments = @{
    PreviousPackageDirectory = $alternatePackages
    PreviousVersion = $alternateVersion.ToString(3)
    PackageDirectory = $PackageDirectory
    Version = $Version
    InteropDirectory = $InteropDirectory
    ProductionVersion = $Version
    ReportPath = (Join-Path $OutputDirectory 'lifecycle-report.json')
}
if ($alternateVersion -gt $production) {
    $arguments.PreviousPackageDirectory = $PackageDirectory
    $arguments.PreviousVersion = $Version
    $arguments.PackageDirectory = $alternatePackages
    $arguments.Version = $alternateVersion.ToString(3)
}
& (Join-Path $PSScriptRoot 'Test-WindowsPackageLifecycle.ps1') @arguments
