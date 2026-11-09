[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repo ('.artifacts/check-' + [Guid]::NewGuid().ToString('N'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -Force -LiteralPath $OutputDirectory).Count) {
    throw 'Choose an empty output directory.'
}
$releaseDir = Join-Path $OutputDirectory 'release'
$interopDir = Join-Path $OutputDirectory 'interop'
$fixtureDir = Join-Path $OutputDirectory 'fixture'
New-Item -ItemType Directory -Force -Path $releaseDir,$interopDir,$fixtureDir | Out-Null

function Invoke-Checked([string]$Program, [string[]]$CommandArgs) {
    & $Program @CommandArgs
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE." }
}

$priorCgo = $env:CGO_ENABLED
$priorFixture = $env:OPSSH_FIXTURE_DIRECTORY
Push-Location -LiteralPath $repo
try {
    $env:CGO_ENABLED = '0'
    $unformatted = & gofmt -l cmd internal
    if ($LASTEXITCODE -ne 0 -or $unformatted) { throw "gofmt check failed: $unformatted" }
    Invoke-Checked go @('mod','verify')
    Invoke-Checked go @('test','-mod=readonly','-count=1','-timeout=90s','./...')
    Invoke-Checked go @('vet','-mod=readonly','./...')

    $tray = 'tray/Opssh.Tray/Opssh.Tray.csproj'
    $tests = 'tray/Opssh.Tray.Tests/Opssh.Tray.Tests.csproj'
    Invoke-Checked dotnet @('restore',$tests,'--locked-mode')
    Invoke-Checked dotnet @('build',$tests,'--no-restore','-c','Release','-o',$interopDir)
    $driver = Join-Path $interopDir 'opssh-core.dll'
    Invoke-Checked dotnet @($driver)

    $env:OPSSH_FIXTURE_DIRECTORY = $fixtureDir
    Invoke-Checked go @('build','-mod=readonly','-trimpath','-tags','opssh_fixture','-ldflags=-H windowsgui','-o',(Join-Path $interopDir 'opssh-core.exe'),'./cmd/opssh-core')
    Invoke-Checked dotnet @($driver,'--go-interop')

    Invoke-Checked dotnet @('restore',$tray,'--locked-mode')
    Invoke-Checked dotnet @('publish',$tray,'--no-restore','-c','Release','-o',$releaseDir)
    Invoke-Checked go @('build','-mod=readonly','-trimpath','-ldflags=-H windowsgui','-o',(Join-Path $releaseDir 'opssh-core.exe'),'./cmd/opssh-core')

    foreach ($name in @('opssh-core.exe','opssh-tray.exe','opssh-tray.dll','opssh-tray.pri','Microsoft.UI.Xaml.dll')) {
        $item = Get-Item -LiteralPath (Join-Path $releaseDir $name)
        if ($item.Length -eq 0) { throw "Empty release component: $name" }
    }
    Write-Host "Validated Windows release folder: $releaseDir"
} finally {
    Pop-Location
    $env:CGO_ENABLED = $priorCgo
    $env:OPSSH_FIXTURE_DIRECTORY = $priorFixture
}
