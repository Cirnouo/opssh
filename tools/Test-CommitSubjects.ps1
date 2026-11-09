[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = 'Cirnouo/opssh'
$mainRef = 'refs/heads/main'
$conventional = '\A(?:feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(?:\([^()\p{Cc}]+\))?!?: [^\s\p{Cc}][^\p{Cc}]*\z'

function Assert-Commit([string]$Sha) {
    if ($Sha -cnotmatch '\A[0-9a-f]{40}\z' -or $Sha -eq ('0' * 40)) {
        throw 'The event must identify a commit by its exact nonzero SHA.'
    }
    $type = & git cat-file -t $Sha
    if ($LASTEXITCODE -ne 0 -or $type -cne 'commit') {
        throw 'An event commit is absent from the checkout history.'
    }
}

if ($env:GITHUB_REPOSITORY -cne $repo) { throw 'Unexpected workflow repository.' }
$event = Get-Content -Raw -LiteralPath $env:GITHUB_EVENT_PATH | ConvertFrom-Json
if ($event.repository.full_name -cne $repo) { throw 'Unexpected event repository.' }
$sha = $env:GITHUB_SHA
Assert-Commit $sha
$head = & git rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0 -or $head -cne $sha) { throw 'The checkout differs from the event SHA.' }

if ($env:GITHUB_EVENT_NAME -ceq 'pull_request') {
    $pr = $event.pull_request
    if ($pr.base.ref -cne 'main' -or $pr.base.repo.full_name -cne $repo -or
        $event.number -isnot [ValueType] -or $event.number -le 0 -or
        $env:GITHUB_REF -cne "refs/pull/$($event.number)/merge") {
        throw 'The pull request must target main in this repository.'
    }
    Assert-Commit $pr.base.sha
    Assert-Commit $pr.head.sha
    $parents = & git show -s --format=%P $sha
    if ($LASTEXITCODE -ne 0 -or $parents -cne "$($pr.base.sha) $($pr.head.sha)") {
        throw 'The PR merge checkout does not have the exact event base and head.'
    }
    $range = "$($pr.base.sha)..$($pr.head.sha)"
    $ownSuffix = '\s*\(#0*' + $event.number + '\)\s*\z'

    # Reruns reuse the original event. Read the title as it exists now.
    $currentJson = & gh api --method GET "repos/$repo/pulls/$($event.number)"
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the current pull request.' }
    $current = $currentJson | ConvertFrom-Json
    if ($current.number -ne $event.number -or $current.state -cne 'open' -or
        $current.base.ref -cne 'main' -or $current.base.repo.full_name -cne $repo -or
        $current.base.sha -cne $pr.base.sha -or $current.head.sha -cne $pr.head.sha) {
        throw 'The current pull request differs from the event target, base or head.'
    }
    if ($current.title -cnotmatch $conventional -or $current.title -cmatch $ownSuffix) {
        throw 'The current PR title must be conventional and omit its own PR number suffix.'
    }
} elseif ($env:GITHUB_EVENT_NAME -ceq 'push') {
    if ($env:GITHUB_REF -cne $mainRef -or $event.ref -cne $mainRef -or $event.after -cne $sha -or
        $event.created -ne $false -or $event.deleted -ne $false -or $event.forced -ne $false) {
        throw 'Expected an ordinary push to main with the exact event tip.'
    }
    Assert-Commit $event.before
    & git merge-base --is-ancestor $event.before $sha
    if ($LASTEXITCODE -ne 0) { throw 'The prior main commit must be an ancestor of the push tip.' }
    $range = "$($event.before)..$sha"
    $mergeCount = & git rev-list --count --merges $range
    if ($LASTEXITCODE -ne 0 -or $mergeCount -cne '0') {
        throw 'The introduced main history must be linear.'
    }
} else {
    throw 'Unexpected commit validation event.'
}

$subjects = @(& git log --format=%s $range --)
if ($LASTEXITCODE -ne 0 -or $subjects.Count -eq 0) { throw 'No submitted commit range was established.' }
foreach ($subject in $subjects) {
    if ($env:GITHUB_EVENT_NAME -ceq 'push') {
        if ($subject -cnotmatch '\A(?<title>.+) \(#(?<number>[1-9][0-9]*)\)\z') {
            throw 'Every introduced main commit must end with its squash PR number suffix.'
        }
        $title = $Matches.title
        $ownSuffix = '\s*\(#0*' + $Matches.number + '\)\s*\z'
    } else {
        $title = $subject
    }
    if ($title -cnotmatch $conventional -or $title -cmatch $ownSuffix) {
        throw 'A submitted commit subject is not conventional or repeats its own PR number suffix.'
    }
}
Write-Host "Validated $($subjects.Count) submitted commit subject(s)."
