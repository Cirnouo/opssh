$ErrorActionPreference = 'Stop'

if (!('Opssh.Verification.ValidationProcess' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs')
}

function Invoke-OpsshValidationProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$DriverPath,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$ReportPath,
        [ValidateRange(1, 1500000)][int]$TimeoutMilliseconds = 1500000,
        [ValidatePattern('^[1-9][0-9]*-[0-9]+$')][string]$GitInvocation
    )

    $driver = (Resolve-Path -LiteralPath $DriverPath).ProviderPath
    $shell = (Get-Process -Id $PID).Path
    $shim = Join-Path $PSScriptRoot 'Start-ValidationProcess.ps1'
    $ownerId, $ownerStarted = if ($GitInvocation) { $GitInvocation -split '-', 2 } else { 0, 0 }
    [Opssh.Verification.ValidationProcess]::Run($shell, $shim, $driver, $OutputDirectory,
        $ReportPath, $TimeoutMilliseconds, [int]$ownerId, [long]$ownerStarted)
}

Export-ModuleMember -Function Invoke-OpsshValidationProcess
