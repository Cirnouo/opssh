[CmdletBinding()]
param([string]$OutputDirectory, [string]$ReportPath)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repo ('.artifacts/check-' + [Guid]::NewGuid().ToString('N'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, (Get-Location).ProviderPath)
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -Force -LiteralPath $OutputDirectory).Count) {
    throw 'Choose an empty output directory.'
}
$releaseDir = Join-Path $OutputDirectory 'release'
$interopDir = Join-Path $OutputDirectory 'interop'
$fixtureDir = Join-Path $OutputDirectory 'fixture'
New-Item -ItemType Directory -Force -Path $releaseDir,$interopDir,$fixtureDir | Out-Null

$phases = [Collections.Generic.List[object]]::new()
$releaseFiles = [Collections.Generic.List[object]]::new()
if ($ReportPath) {
    $ReportPath = [IO.Path]::GetFullPath($ReportPath, (Get-Location).ProviderPath)
    if (Test-Path -LiteralPath $ReportPath) { throw 'Choose a fresh report path.' }
}

function Add-Phase([string]$Id, [bool]$Passed, [int]$ExitCode) {
    $phases.Add(@{
        id = $Id
        outcome = if ($Passed) { 'passed' } else { 'failed' }
        exitCode = $ExitCode
    })
    Write-ValidationReport 'incomplete'
}

function Write-ValidationReport([string]$Outcome) {
    if (!$ReportPath) { return }
    $report = @{
        schema = 1
        outcome = $Outcome
        phases = $phases.ToArray()
        release = $releaseFiles.ToArray()
    } | ConvertTo-Json -Depth 5
    $pending = $ReportPath + '.pending-' + [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($pending, $report)
        [IO.File]::Move($pending, $ReportPath, $true)
    } finally {
        if ([IO.File]::Exists($pending)) { [IO.File]::Delete($pending) }
    }
}

function Invoke-Checked([string]$Phase, [string]$Program, [string[]]$CommandArgs) {
    & $Program @CommandArgs
    $code = $LASTEXITCODE
    Add-Phase $Phase ($code -eq 0) $code
    if ($code -ne 0) { throw "$Program failed in $Phase with exit code $code." }
}

$priorCgo = $env:CGO_ENABLED
$priorFixture = $env:OPSSH_FIXTURE_DIRECTORY
$priorUnconfiguredFixture = $env:OPSSH_FIXTURE_UNCONFIGURED
Push-Location -LiteralPath $repo
try {
    Write-ValidationReport 'incomplete'
    $env:CGO_ENABLED = '0'
    $unformatted = & gofmt -l cmd internal
    $formatPassed = $LASTEXITCODE -eq 0 -and !$unformatted
    Add-Phase 'go-format' $formatPassed $LASTEXITCODE
    if (!$formatPassed) { throw "gofmt check failed: $unformatted" }
    Invoke-Checked 'go-modules' go @('mod','verify')
    Invoke-Checked 'go-tests' go @('test','-mod=readonly','-count=1','-timeout=90s','./...')
    Invoke-Checked 'go-vet' go @('vet','-mod=readonly','./...')

    $tray = 'tray/Opssh.Tray/Opssh.Tray.csproj'
    $tests = 'tray/Opssh.Tray.Tests/Opssh.Tray.Tests.csproj'
    Invoke-Checked 'tray-test-restore' dotnet @('restore',$tests,'--locked-mode')
    Invoke-Checked 'tray-test-build' dotnet @('build',$tests,'--no-restore','-c','Release','-o',$interopDir)
    $driver = Join-Path $interopDir 'opssh-core.dll'
    Invoke-Checked 'tray-tests' dotnet @($driver)

    $env:OPSSH_FIXTURE_DIRECTORY = $fixtureDir
    $env:OPSSH_FIXTURE_UNCONFIGURED = '1'
    Invoke-Checked 'fixture-build' go @('build','-mod=readonly','-trimpath','-tags','opssh_fixture','-ldflags=-H windowsgui','-o',(Join-Path $interopDir 'opssh-core.exe'),'./cmd/opssh-core')
    Invoke-Checked 'go-tray-interop' dotnet @($driver,'--go-interop')

    Invoke-Checked 'tray-restore' dotnet @('restore',$tray,'--locked-mode')
    Invoke-Checked 'tray-publish' dotnet @('publish',$tray,'--no-restore','-c','Release','-o',$releaseDir)
    Invoke-Checked 'core-build' go @('build','-mod=readonly','-trimpath','-ldflags=-H windowsgui','-o',(Join-Path $releaseDir 'opssh-core.exe'),'./cmd/opssh-core')

    foreach ($name in @('opssh-core.exe','opssh-tray.exe','opssh-tray.dll','opssh-tray.pri','Microsoft.UI.Xaml.dll')) {
        $item = Get-Item -LiteralPath (Join-Path $releaseDir $name)
        if ($item.Length -eq 0) { throw "Empty release component: $name" }
    }
    if ($ReportPath) {
        foreach ($file in (Get-ChildItem -LiteralPath $releaseDir -File -Force -Recurse | Sort-Object FullName)) {
            $releaseFiles.Add(@{
                path = [IO.Path]::GetRelativePath($releaseDir, $file.FullName).Replace('\', '/')
                size = $file.Length
                sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            })
        }
    }
    Add-Phase 'release-inspection' $true 0
    Write-ValidationReport 'passed'
    Write-Host "Validated Windows release folder: $releaseDir"
} catch {
    try { Write-ValidationReport 'failed' } catch { Write-Warning 'Unable to record Windows validation failure.' }
    throw
} finally {
    Pop-Location
    $env:CGO_ENABLED = $priorCgo
    $env:OPSSH_FIXTURE_DIRECTORY = $priorFixture
    $env:OPSSH_FIXTURE_UNCONFIGURED = $priorUnconfiguredFixture
}
