[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot 'Test-CommitSubjects.ps1'
$repo = 'Cirnouo/opssh'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('opssh-subjects-' + [Guid]::NewGuid().ToString('N'))
$names = @('GITHUB_EVENT_NAME', 'GITHUB_EVENT_PATH', 'GITHUB_REF', 'GITHUB_REPOSITORY', 'GITHUB_SHA')
$prior = @{}
foreach ($name in $names) { $prior[$name] = [Environment]::GetEnvironmentVariable($name) }

function Invoke-Git([string[]]$Arguments) {
    $result = & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Git fixture setup failed.' }
    $result
}

function gh {
    if (($args -join ' ') -cne 'api --method GET repos/Cirnouo/opssh/pulls/42') {
        throw 'Unexpected GitHub API call in fixture.'
    }
    $global:LASTEXITCODE = 0
    $current | ConvertTo-Json -Depth 6 -Compress
}

function Assert-Validation([string]$Name, [hashtable]$Event, [string]$ExpectedError = '') {
    $Event | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $env:GITHUB_EVENT_PATH -Encoding utf8
    try {
        & $validator
    } catch {
        if (!$ExpectedError -or $_.Exception.Message -notlike "*$ExpectedError*") { throw }
        Write-Host "Passed $Name"
        return
    }
    if ($ExpectedError) { throw "Fixture unexpectedly succeeded: $Name" }
    Write-Host "Passed $Name"
}

New-Item -ItemType Directory -Path $directory | Out-Null
Push-Location -LiteralPath $directory
try {
    Invoke-Git @('init', '-q', '-b', 'main')
    Invoke-Git @('config', 'user.name', 'Commit Subject Fixture')
    Invoke-Git @('config', 'user.email', 'fixture@example.invalid')
    Invoke-Git @('config', 'commit.gpgSign', 'false')
    Invoke-Git @('config', 'core.hooksPath', $directory)
    Invoke-Git @('commit', '-q', '--allow-empty', '-m', 'historical main root')
    $base = Invoke-Git @('rev-parse', 'HEAD')
    Invoke-Git @('switch', '-q', '-c', 'topic')
    Invoke-Git @('commit', '-q', '--allow-empty', '-m', 'feat(core)!: support staged changes (#31)')
    $topic = Invoke-Git @('rev-parse', 'HEAD')
    Invoke-Git @('switch', '-q', 'main')
    Invoke-Git @('commit', '-q', '--allow-empty', '-m', 'fix: repair checks (#40)')
    $newBase = Invoke-Git @('rev-parse', 'HEAD')
    Invoke-Git @('merge', '-q', '--no-ff', '--no-edit', 'topic')
    $merge = Invoke-Git @('rev-parse', 'HEAD')

    $env:GITHUB_REPOSITORY = $repo
    $env:GITHUB_EVENT_PATH = Join-Path $directory 'event.json'
    $env:GITHUB_EVENT_NAME = 'pull_request'
    $env:GITHUB_REF = 'refs/pull/42/merge'
    $env:GITHUB_SHA = $merge
    $event = @{
        repository = @{ full_name = $repo }
        number = 42
        pull_request = @{
            title = 'old title from the original event'
            base = @{ ref = 'main'; sha = $newBase; repo = @{ full_name = $repo } }
            head = @{ sha = $topic }
        }
    }
    $current = @{
        number = 42
        state = 'open'
        title = 'fix: preserve $(throw "title executed") ${{ secrets.SOME_SECRET }}'
        base = $event.pull_request.base.Clone()
        head = $event.pull_request.head.Clone()
    }
    Assert-Validation 'current title as inert data with exact divergent base/head' $event
    $current.title = 'feat: edited title (#42)'
    Assert-Validation 'current own suffix rejected' $event 'omit its own'
    $current.title = 'unconventional edited title'
    Assert-Validation 'current invalid edited title rejected' $event 'must be conventional'
    $current.title = 'fix: title with newline' + [char]10 + 'second line'
    Assert-Validation 'control character rejected' $event 'must be conventional'
    $current.title = 'feat: edited title (#24)'
    $current.head.sha = $base
    Assert-Validation 'stale head rejected' $event 'differs from the event target, base or head'
    $current.head.sha = $topic
    $current.base.sha = $base
    Assert-Validation 'stale current base rejected' $event 'differs from the event target, base or head'
    $current.base.sha = $newBase
    $event.pull_request.base.sha = $base
    Assert-Validation 'different event base rejected' $event 'exact event base and head'
    $event.pull_request.base.sha = '--all'
    Assert-Validation 'Git option rejected as SHA' $event 'exact nonzero SHA'
    $event.pull_request.base.sha = $newBase
    $event.pull_request.base.ref = 'other'
    Assert-Validation 'different target rejected' $event 'must target main'

    Invoke-Git @('switch', '-q', 'topic')
    Invoke-Git @('commit', '-q', '--allow-empty', '-m', 'fix: owns the suffix (#42)')
    $event.pull_request.head.sha = $current.head.sha = Invoke-Git @('rev-parse', 'HEAD')
    Invoke-Git @('checkout', '-q', '--detach', $newBase)
    Invoke-Git @('merge', '-q', '--no-ff', '--no-edit', 'topic')
    $env:GITHUB_SHA = Invoke-Git @('rev-parse', 'HEAD')
    $event.pull_request.base.ref = 'main'
    Assert-Validation 'raw submitted own suffix rejected' $event 'repeats its own'

    $env:GITHUB_EVENT_NAME = 'push'
    $env:GITHUB_REF = 'refs/heads/main'
    $event = @{
        repository = @{ full_name = $repo }
        ref = $env:GITHUB_REF
        before = $base
        after = $env:GITHUB_SHA
        created = $false
        deleted = $false
        forced = $false
    }
    Assert-Validation 'introduced merge commit rejected' $event 'must be linear'

    Invoke-Git @('checkout', '-q', '--detach', $newBase)
    Invoke-Git @('commit', '-q', '--allow-empty', '-m', 'docs: explain checks (#41)')
    $env:GITHUB_SHA = $event.after = Invoke-Git @('rev-parse', 'HEAD')
    Assert-Validation 'both exact introduced squash commits checked and historical root excluded' $event
    $event.before = $env:GITHUB_SHA
    Assert-Validation 'empty push range rejected' $event 'No submitted commit range'
    $event.before = '0' * 40
    Assert-Validation 'unknown prior push SHA rejected' $event 'exact nonzero SHA'
    $event.before = $topic
    Assert-Validation 'unrelated prior push SHA rejected' $event 'must be an ancestor'
    $event.before = $base
    Invoke-Git @('commit', '-q', '--allow-empty', '-m', 'fix: one missing suffix')
    Invoke-Git @('commit', '-q', '--allow-empty', '-m', 'ci: newest entry valid (#43)')
    $env:GITHUB_SHA = $event.after = Invoke-Git @('rev-parse', 'HEAD')
    Assert-Validation 'invalid older introduced commit rejected despite valid tip' $event 'must end with its squash'
} finally {
    Pop-Location
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $prior[$name]) }
    $resolved = (Resolve-Path -LiteralPath $directory).Path
    if ($resolved -cne $directory) { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
