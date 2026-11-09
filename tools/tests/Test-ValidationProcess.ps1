[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'ValidationProcess.psm1'
if (!(Test-Path -LiteralPath $modulePath -PathType Leaf)) { throw 'Validation process module is missing.' }
Import-Module $modulePath -Force

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('opssh-job-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $driver = Join-Path $temporary 'driver.ps1'
    $output = Join-Path $temporary 'output'
    $report = Join-Path $temporary 'report.txt'
    @'
param([string]$OutputDirectory, [string]$ReportPath)
if ($env:GIT_INDEX_FILE) { exit 30 }
[IO.File]::WriteAllText($ReportPath, $OutputDirectory)
exit 0
'@ | Set-Content -Encoding utf8 -LiteralPath $driver

    $previous = $env:GIT_INDEX_FILE
    try {
        $env:GIT_INDEX_FILE = 'must-not-cross'
        $passed = Invoke-OpsshValidationProcess -DriverPath $driver -OutputDirectory $output -ReportPath $report -TimeoutMilliseconds 10000
    } finally {
        $env:GIT_INDEX_FILE = $previous
    }
    if ($passed.ExitCode -ne 0 -or $passed.TimedOut -or !$passed.CleanedUp -or
        [IO.File]::ReadAllText($report) -cne $output) {
        throw 'Owned validation did not complete with the requested arguments and clean environment.'
    }

    @'
param([string]$OutputDirectory, [string]$ReportPath)
$child = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList '-NoLogo -NoProfile -NonInteractive -Command "Start-Sleep -Seconds 60"' -PassThru -WindowStyle Hidden
[IO.File]::WriteAllText($ReportPath, [string]$child.Id)
exit 0
'@ | Set-Content -Encoding utf8 -LiteralPath $driver
    $timeout = Invoke-OpsshValidationProcess -DriverPath $driver -OutputDirectory $output -ReportPath $report -TimeoutMilliseconds 4000
    if (!$timeout.TimedOut -or !$timeout.CleanedUp -or $timeout.ExitCode -eq 0) {
        throw 'A descendant outlived the launcher without a failed deadline and owned cleanup.'
    }
    $childId = [int][IO.File]::ReadAllText($report)
    if (Get-Process -Id $childId -ErrorAction SilentlyContinue) { throw 'Timed-out descendant survived validation.' }

    Remove-Item -LiteralPath $report
    $ownerScript = Join-Path $temporary 'owner.ps1'
    @'
param([string]$ReportPath)
$clock = [Diagnostics.Stopwatch]::StartNew()
while (!(Test-Path -LiteralPath $ReportPath) -and $clock.Elapsed.TotalSeconds -lt 15) {
    Start-Sleep -Milliseconds 50
}
'@ | Set-Content -LiteralPath $ownerScript -Encoding utf8
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in '-NoLogo', '-NoProfile', '-NonInteractive', '-File', $ownerScript, $report) {
        $start.ArgumentList.Add($argument)
    }
    $owner = [Diagnostics.Process]::Start($start)
    try {
        $key = "$($owner.Id)-$($owner.StartTime.ToUniversalTime().Ticks)"
        $canceled = Invoke-OpsshValidationProcess -DriverPath $driver -OutputDirectory $output -ReportPath $report -TimeoutMilliseconds 20000 -GitInvocation $key
        if (!$canceled.Canceled -or $canceled.TimedOut -or !$canceled.CleanedUp -or $canceled.ExitCode -eq 0) {
            throw 'Validation did not reap its descendants after the owner exited.'
        }
        $childId = [int][IO.File]::ReadAllText($report)
        if (Get-Process -Id $childId -ErrorAction SilentlyContinue) { throw 'Canceled descendant survived validation.' }
    } finally {
        if (!$owner.HasExited) { $owner.Kill(); $null = $owner.WaitForExit(5000) }
        $owner.Dispose()
    }
    Write-Host 'Owned validation process acceptance passed.'
} finally {
    $absolute = [IO.Path]::GetFullPath($temporary)
    if ((Split-Path -Parent $absolute).TrimEnd('\') -ne ([IO.Path]::GetTempPath()).TrimEnd('\') -or
        !(Split-Path -Leaf $absolute).StartsWith('opssh-job-test-', [StringComparison]::Ordinal)) {
        throw 'Unexpected fixture directory.'
    }
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
