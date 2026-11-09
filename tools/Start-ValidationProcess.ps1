param(
    [string]$DriverPath,
    [string]$OutputDirectory,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
# The parent sends this only after assigning this process to its Windows job.
if ([Console]::ReadLine() -cne 'run') { exit 1 }
try {
    & $DriverPath -OutputDirectory $OutputDirectory -ReportPath $ReportPath
} catch {
    exit 1
}
exit 0
