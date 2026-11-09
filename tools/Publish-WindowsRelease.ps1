using module ./OpsshRelease.psm1

#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$TagObject,
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$ManifestSha256,
    [Parameter(Mandatory)][string]$NotesPath,
    [Parameter(Mandatory)][string]$NotesSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    [Console]::Error.WriteLine('GH_TOKEN is required to publish a release.')
    exit 1
}

try {
    Invoke-OpsshRelease @PSBoundParameters -Token $env:GH_TOKEN | ConvertTo-Json -Depth 5
} catch [OpsshReleaseFailure] {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} catch {
    [Console]::Error.WriteLine('The release publisher could not run.')
    exit 1
}
