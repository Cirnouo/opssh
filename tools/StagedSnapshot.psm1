#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (!('Opssh.StagedSnapshot' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'StagedSnapshot.cs')
}

function Get-OpsshIndexIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $git = (Get-Command git -CommandType Application | Select-Object -First 1).Source
    [Opssh.StagedSnapshot]::Identify($git, $RepositoryRoot)
}

function New-OpsshStagedSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$ScratchRoot,
        [Parameter(Mandatory)][string]$Destination
    )

    $git = (Get-Command git -CommandType Application | Select-Object -First 1).Source
    [Opssh.StagedSnapshot]::Create($git, $RepositoryRoot, $ScratchRoot, $Destination)
}

Export-ModuleMember -Function Get-OpsshIndexIdentity, New-OpsshStagedSnapshot
