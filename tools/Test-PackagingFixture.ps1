[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WixPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new fixture directory.' }
$release = Join-Path $OutputDirectory 'release'
$legal = Join-Path $OutputDirectory 'legal'
$packages = Join-Path $OutputDirectory 'packages'
New-Item -ItemType Directory -Path $release,$legal,(Join-Path $release 'zh-Hans'),(Join-Path $legal 'licenses/fixture') | Out-Null

$fixtureFiles = @(
    'opssh-core.exe', 'opssh-tray.exe', 'opssh-tray.pri', 'Microsoft.UI.Xaml.dll',
    'opssh-tray.deps.json', 'opssh-tray.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll',
    'opssh-tray.pdb', 'zh-Hans\Microsoft.UI.Xaml.dll.mui'
)
foreach ($path in $fixtureFiles) {
    Set-Content -LiteralPath (Join-Path $release $path) -Value "synthetic bytes for $path" -NoNewline
}
$trayAssembly = @'
using System.Reflection;
[assembly: AssemblyVersion("255.255.65534.0")]
public sealed class TrayFixture {}
'@
Add-Type -TypeDefinition $trayAssembly -OutputAssembly (Join-Path $release 'opssh-tray.dll')
Set-Content -LiteralPath (Join-Path $legal 'LICENSE') -Value 'synthetic license'
Set-Content -LiteralPath (Join-Path $legal 'THIRD-PARTY-NOTICES.md') -Value 'synthetic notices'
Set-Content -LiteralPath (Join-Path $legal 'licenses/fixture/LICENSE') -Value 'synthetic third-party license'

$builder = Join-Path $PSScriptRoot 'Build-WindowsPackages.ps1'
if (!(Test-Path -LiteralPath $builder -PathType Leaf)) { throw 'The Windows packaging entry point is missing.' }

function Assert-Rejected([string]$Version, [string]$Message) {
    $invalidOutput = Join-Path $OutputDirectory "invalid-$Version"
    try {
        & $builder -Version $Version -ReleaseDirectory $release -OutputDirectory $invalidOutput -LegalDirectory $legal -WixPath $WixPath
    } catch {
        if (Test-Path -LiteralPath $invalidOutput) { throw "A rejected package exposed output for $Version." }
        if ($_.Exception.Message -cne $Message) { throw }
        return
    }
    throw "The packaging command accepted an invalid or mismatched version $Version."
}

$versionError = 'Use a canonical major.minor.patch version in the range 0.0.0 through 255.255.65534.'
foreach ($invalid in @('255.255.65535', '256.255.65534', '255.256.65534', '0255.255.65534', '255.255.65534.0', 'v255.255.65534')) {
    Assert-Rejected $invalid $versionError
}
Assert-Rejected '1.2.3' 'The Release tray assembly version differs from the package version.'

& $builder -Version '255.255.65534' -ReleaseDirectory $release -OutputDirectory $packages -LegalDirectory $legal -WixPath $WixPath
$manifest = Get-Content -Raw -LiteralPath (Join-Path $packages 'payload-manifest.json') | ConvertFrom-Json
$expectedPaths = @(
    'coreclr.dll', 'hostfxr.dll', 'LICENSE', 'Microsoft.UI.Xaml.dll', 'opssh-core.exe',
    'opssh-tray.deps.json', 'opssh-tray.dll', 'opssh-tray.exe', 'opssh-tray.pri',
    'opssh-tray.runtimeconfig.json', 'THIRD-PARTY-NOTICES.md', 'zh-Hans/Microsoft.UI.Xaml.dll.mui',
    'licenses/fixture/LICENSE'
)
if ((Compare-Object $expectedPaths @($manifest.files.path) -CaseSensitive) -or $manifest.version -cne '255.255.65534') {
    throw 'The payload manifest differs from the expected curated Release files.'
}

$stem = 'opssh-255.255.65534-windows-x64'
$expectedArtifacts = @('payload-manifest.json', "$stem.msi", "$stem.exe", "$stem.zip")
if (Compare-Object $expectedArtifacts @(Get-ChildItem -Force -LiteralPath $packages -Name) -CaseSensitive) {
    throw 'The output differs from the expected package filenames.'
}

& (Join-Path $PSScriptRoot 'Test-WindowsPackages.ps1') -PackageDirectory $packages -ReleaseDirectory $release `
    -LegalDirectory $legal -VerificationDirectory (Join-Path $OutputDirectory 'verify') -Version '255.255.65534' -WixPath $WixPath
Write-Host 'Synthetic packaging acceptance passed.'
