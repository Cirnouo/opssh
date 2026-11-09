[CmdletBinding()]
param([string]$OutputDirectory, [string]$ReportPath, [string]$ReleaseVersion)

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

$priorDotnetNoLogo = $env:DOTNET_NOLOGO
$priorDotnetCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$priorCgo = $env:CGO_ENABLED
$priorFixture = $env:OPSSH_FIXTURE_DIRECTORY
$priorUnconfiguredFixture = $env:OPSSH_FIXTURE_UNCONFIGURED
Push-Location -LiteralPath $repo
try {
    Write-ValidationReport 'incomplete'
    $env:DOTNET_NOLOGO = 'true'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
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
    $evaluatedVersion = @(dotnet msbuild $tray -nologo -property:Configuration=Release -getProperty:Version)
    $version = $null
    if ($LASTEXITCODE -ne 0 -or $evaluatedVersion.Count -ne 1 -or
        ![Version]::TryParse($evaluatedVersion[0], [ref]$version) -or $version.Build -lt 0 -or $version.Revision -ge 0 -or
        $version.ToString(3) -cne $evaluatedVersion[0] -or $version.Major -gt 255 -or
        $version.Minor -gt 255 -or $version.Build -gt 65534) {
        throw 'The tray project Version must be a canonical triplet within 255.255.65534.'
    }
    if ($PSBoundParameters.ContainsKey('ReleaseVersion') -and $ReleaseVersion -cne $evaluatedVersion[0]) {
        throw 'The expected release version differs from the tray project Version.'
    }
    $linkerFlags = "-ldflags=-H windowsgui -X main.version=$version"
    Invoke-Checked 'tray-test-restore' dotnet @('restore',$tests,'--locked-mode')
    Invoke-Checked 'tray-test-build' dotnet @('build',$tests,'--no-restore','-c','Release','-o',$interopDir)
    $driver = Join-Path $interopDir 'opssh-core.dll'
    Invoke-Checked 'tray-tests' dotnet @($driver)

    $env:OPSSH_FIXTURE_DIRECTORY = $fixtureDir
    $env:OPSSH_FIXTURE_UNCONFIGURED = '1'
    Invoke-Checked 'fixture-build' go @('build','-mod=readonly','-trimpath','-tags','opssh_fixture',$linkerFlags,'-o',(Join-Path $interopDir 'opssh-core.exe'),'./cmd/opssh-core')
    Invoke-Checked 'go-tray-interop' dotnet @($driver,'--go-interop')

    Invoke-Checked 'tray-restore' dotnet @('restore',$tray,'--locked-mode')
    Invoke-Checked 'tray-publish' dotnet @('publish',$tray,'--no-restore','-c','Release','-p:DebugType=none','-p:DebugSymbols=false','-o',$releaseDir)
    Invoke-Checked 'core-build' go @('build','-mod=readonly','-trimpath',$linkerFlags,'-o',(Join-Path $releaseDir 'opssh-core.exe'),'./cmd/opssh-core')

    foreach ($name in @('opssh-core.exe','opssh-tray.exe','opssh-tray.dll','opssh-tray.pri','Microsoft.UI.Xaml.dll')) {
        $item = Get-Item -LiteralPath (Join-Path $releaseDir $name)
        if ($item.Length -eq 0) { throw "Empty release component: $name" }
    }
    $trayAssembly = Join-Path $releaseDir 'opssh-tray.dll'
    if ([Reflection.AssemblyName]::GetAssemblyName($trayAssembly).Version -ne [Version]"$version.0") {
        throw 'The published tray assembly version differs from the project Version.'
    }
    $stream = [IO.File]::OpenRead($trayAssembly)
    try {
        $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if ($reader.ReadDebugDirectory().Type -contains 'CodeView') {
                throw 'The published tray assembly contains a CodeView debug path.'
            }
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
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
    $env:DOTNET_NOLOGO = $priorDotnetNoLogo
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $priorDotnetCertificate
    $env:CGO_ENABLED = $priorCgo
    $env:OPSSH_FIXTURE_DIRECTORY = $priorFixture
    $env:OPSSH_FIXTURE_UNCONFIGURED = $priorUnconfiguredFixture
}
