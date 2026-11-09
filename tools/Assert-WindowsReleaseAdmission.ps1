using module ./OpsshRelease.psm1

#Requires -Version 7.4
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-GitObjectId([string]$revision) {
    $sha = & git rev-parse --verify --end-of-options $revision 2>$null
    if ($LASTEXITCODE -ne 0 -or $sha -isnot [string] -or $sha -cnotmatch '\A[a-f0-9]{40}\z') {
        throw 'Git checkout is missing the expected tag or commit object.'
    }
    return $sha
}

try {
    if ($env:GITHUB_EVENT_NAME -cne 'push' -or $env:GITHUB_REF_TYPE -cne 'tag' -or
        $env:GITHUB_REF -cnotmatch '\Arefs/tags/(v[0-9]+\.[0-9]+\.[0-9]+)\z' -or
        [string]::IsNullOrWhiteSpace($env:GH_TOKEN) -or [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        throw [OpsshReleaseFailure]::new('Admission requires a GitHub Actions stable tag push.')
    }
    $null = Get-OpsshReleaseVersion -Tag $Matches[1]
    $noticeManifest = Join-Path (Split-Path -Parent $PSScriptRoot) 'licenses/manifest.json'
    Assert-OpsshReleaseNoticeReadiness -ManifestPath $noticeManifest
    $head = Read-GitObjectId 'HEAD^{commit}'
    $tagObject = Read-GitObjectId $env:GITHUB_REF
    $tagCommit = Read-GitObjectId ($env:GITHUB_REF + '^{commit}')
    $result = Invoke-OpsshReleaseAdmission -Repository $env:GITHUB_REPOSITORY -RepositoryId $env:GITHUB_REPOSITORY_ID `
        -EventName $env:GITHUB_EVENT_NAME -EventRef $env:GITHUB_REF -EventSha $env:GITHUB_SHA -EventPath $env:GITHUB_EVENT_PATH `
        -CheckoutHead $head -LocalTagObject $tagObject -LocalTagCommit $tagCommit -Token $env:GH_TOKEN
    [IO.File]::AppendAllLines($env:GITHUB_OUTPUT, [string[]]@(
        "tag=$($result.tag)",
        "version=$($result.version)",
        "tag-object=$($result.tagObject)",
        "source-commit=$($result.sourceCommit)",
        "main-sha=$($result.mainSha)",
        "main-runs=$(ConvertTo-Json -InputObject $result.mainRuns -Compress)"
    ))
    Write-Host "Admitted $($result.tag), object $($result.tagObject), source $($result.sourceCommit), main $($result.mainSha)."
    foreach ($run in $result.mainRuns) {
        Write-Host "$($run.workflow) main push run $($run.id) attempt $($run.attempt)."
    }
} catch [OpsshReleaseFailure] {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} catch {
    [Console]::Error.WriteLine('Release admission could not verify the checkout or workflow inputs.')
    exit 1
}
