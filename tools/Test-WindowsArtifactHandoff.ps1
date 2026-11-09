[CmdletBinding(DefaultParameterSetName = 'Verify')]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory, ParameterSetName = 'Create')][switch]$Create,
    [Parameter(Mandatory, ParameterSetName = 'Verify')][switch]$Verify,
    [Parameter(Mandatory, ParameterSetName = 'Verify')][ValidatePattern('\A[0-9a-fA-F]{64}\z')][string]$ExpectedSha256
)

$ErrorActionPreference = 'Stop'
$Directory = (Get-Item -LiteralPath $Directory).FullName
$release = Join-Path $Directory 'release'
$interop = Join-Path $Directory 'interop'
$manifest = Join-Path $interop 'artifact-provenance.json'
$drivers = @('opssh-core.dll', 'opssh-core.deps.json', 'opssh-core.runtimeconfig.json')

function Get-InputEvidence([string]$Root, [IO.FileInfo]$File) {
    if ($File.Length -le 0 -or ($File.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Expected a regular, nonempty Windows build input.'
    }
    [pscustomobject]@{
        path = [IO.Path]::GetRelativePath($Root, $File.FullName).Replace('\', '/')
        size = $File.Length
        sha256 = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

if ($Create) {
    $report = Get-Content -Raw -LiteralPath (Join-Path $Directory 'release-report.json') | ConvertFrom-Json
    if ($report.schema -ne 1 -or $report.outcome -cne 'passed' -or !$report.release.Count -or
        (Test-Path -LiteralPath $manifest)) {
        throw 'A fresh successful Windows validation report is required.'
    }
    $interopEvidence = foreach ($name in $drivers) {
        Get-InputEvidence $interop (Get-Item -LiteralPath (Join-Path $interop $name))
    }
    [ordered]@{ schema = 1; release = $report.release; interop = @($interopEvidence) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifest -Encoding utf8
    return
}

if ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -ine $ExpectedSha256) {
    throw 'The downloaded provenance file differs from the validated build receipt.'
}
$expected = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
if ($expected.schema -ne 1 -or !$expected.release.Count -or $expected.interop.Count -ne $drivers.Count) {
    throw 'Invalid Windows artifact provenance.'
}
$actualRelease = @(Get-ChildItem -LiteralPath $release -File -Force -Recurse | ForEach-Object { Get-InputEvidence $release $_ })
$actualInterop = @(Get-ChildItem -LiteralPath $interop -File -Force -Recurse |
    Where-Object FullName -cne $manifest | ForEach-Object { Get-InputEvidence $interop $_ })
if ((Compare-Object $expected.release $actualRelease -Property path,size,sha256 -CaseSensitive) -or
    (Compare-Object $expected.interop $actualInterop -Property path,size,sha256 -CaseSensitive)) {
    throw 'The downloaded Release or handshake files differ from the validated build.'
}
Write-Host "Verified $($actualRelease.Count) Release files and $($actualInterop.Count) handshake files against the offline build."
