#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LauncherPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$launcher = (Get-Item -LiteralPath $LauncherPath).FullName
$output = [IO.Path]::GetFullPath($OutputDirectory, $PWD.ProviderPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh portable launch verification directory.' }
$runtime = Join-Path $output 'extracted folder 测试/opssh-runtime'
$working = Join-Path $output 'unrelated working directory'
New-Item -ItemType Directory -Path $runtime,$working | Out-Null
$entry = Join-Path $output 'extracted folder 测试/opssh-tray.exe'
Copy-Item -LiteralPath $launcher -Destination $entry
$fixture = Join-Path $PSScriptRoot 'tests/testdata/portable-launcher/tray.go'
Push-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
try {
    & go build -mod=readonly -trimpath '-ldflags=-H windowsgui' -o (Join-Path $runtime 'opssh-tray.exe') $fixture
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the portable launcher test child.' }
} finally {
    Pop-Location
}

$start = [Diagnostics.ProcessStartInfo]::new($entry)
$start.WorkingDirectory = $working
$start.UseShellExecute = $false
foreach ($argument in @('a path with spaces', 'embedded "quotes"', 'trailing\')) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)
try {
    if (!$process.WaitForExit(10000)) {
        if (!$process.HasExited) { $process.Kill($true) }
        throw 'The portable launcher did not exit promptly.'
    }
    if ($process.ExitCode -ne 0) { throw 'The portable launcher did not start its child.' }
} finally {
    $process.Dispose()
}
$receipt = Join-Path $runtime 'portable-launch.receipt'
$clock = [Diagnostics.Stopwatch]::StartNew()
while (!(Test-Path -LiteralPath $receipt) -and $clock.ElapsedMilliseconds -lt 5000) {
    Start-Sleep -Milliseconds 25
}
if (!(Test-Path -LiteralPath $receipt) -or [IO.File]::ReadAllText($receipt) -cne 'launched') {
    throw 'The portable launcher did not pass the working directory and arguments to its sibling tray.'
}
Write-Host 'Verified the portable launcher with an offline child, paths with spaces and an unrelated working directory.'
