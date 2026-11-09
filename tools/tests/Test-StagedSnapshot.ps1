#requires -Version 7.4
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True([bool]$Condition, [string]$Case) {
    if (!$Condition) { throw "FAILED $Case" }
}

function Git-Fixture([string[]]$Arguments) {
    $result = & git -C $repo @Arguments
    Assert-True ($LASTEXITCODE -eq 0) 'fixture Git command'
    $result
}

function Write-Fixture([string]$Name, [string]$Content) {
    [IO.File]::WriteAllText((Join-Path $repo $Name), $Content)
}

function Snapshot([string]$Name) {
    New-OpsshStagedSnapshot -RepositoryRoot $repo -ScratchRoot $scratch -Destination (Join-Path $scratch $Name)
}

function Assert-FileBytes([string]$Path, [byte[]]$Expected, [string]$Case) {
    $actual = [IO.File]::ReadAllBytes($Path)
    Assert-True ([Convert]::ToHexString($actual) -ceq [Convert]::ToHexString($Expected)) $Case
}

function Assert-Rejected([scriptblock]$Action, [string]$Case) {
    $rejected = $false
    try { $null = & $Action } catch { $rejected = $true }
    Assert-True $rejected $Case
}

$module = Join-Path (Split-Path -Parent $PSScriptRoot) StagedSnapshot.psm1
Assert-True ([IO.File]::Exists($module)) 'staged snapshot module is available'
Import-Module $module -Force

$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = Join-Path $temp ('opssh-snapshot-test-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $root 'repositorio átomo'
$scratch = Join-Path $root 'scratch'
$null = [IO.Directory]::CreateDirectory($repo)
$null = [IO.Directory]::CreateDirectory($scratch)
$oldIndex = $env:GIT_INDEX_FILE
try {
    $env:GIT_INDEX_FILE = $null
    Git-Fixture @('init', '--quiet', '--template=')
    Git-Fixture @('config', 'user.name', 'Snapshot Fixture')
    Git-Fixture @('config', 'user.email', 'snapshot@example.invalid')
    Git-Fixture @('config', 'core.autocrlf', 'false')
    Git-Fixture @('config', 'core.safecrlf', 'false')
    Write-Fixture '.gitattributes' "raw.bin -text export-ignore`nmessage.txt text eol=crlf export-subst`n"
    [byte[]]$raw = 0, 1, 13, 10, 127, 128, 255
    [IO.File]::WriteAllBytes((Join-Path $repo 'raw.bin'), $raw)
    Write-Fixture 'message.txt' "staged `$Format:%H`$`n"
    Write-Fixture 'café.txt' "accent`n"
    Git-Fixture @('add', '--', '.gitattributes', 'raw.bin', 'message.txt', 'café.txt')
    Write-Fixture 'message.txt' "unstaged`n"
    Write-Fixture 'untracked.txt' "untracked`n"
    $index = Join-Path $repo '.git/index'
    $indexBytes = [IO.File]::ReadAllBytes($index)
    $initial = Snapshot 'initial'
    Assert-True ($initial.IndexPath -eq $index -and $initial.IndexSha256 -eq (Get-FileHash -LiteralPath $index).Hash) 'actual index identity'
    $identity = Get-OpsshIndexIdentity -RepositoryRoot $repo
    Assert-True ($identity.IndexPath -eq $initial.IndexPath -and $identity.IndexSha256 -ceq $initial.IndexSha256) 'receipt recheck reads same index identity'
    Assert-FileBytes $index $indexBytes 'write-tree leaves actual index bytes intact'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $repo ('.git/objects/' + $initial.Tree.Insert(2, '/'))))) 'write-tree writes no loose object to source Git directory'
    Assert-FileBytes (Join-Path $scratch 'initial/raw.bin') $raw 'binary and export-ignore staged bytes'
    Assert-FileBytes (Join-Path $scratch 'initial/message.txt') ([Text.Encoding]::UTF8.GetBytes("staged `$Format:%H`$`n")) 'staged LF and literal export-subst bytes'
    Assert-FileBytes (Join-Path $scratch 'initial/café.txt') ([Text.Encoding]::UTF8.GetBytes("accent`n")) 'UTF-8 staged path'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $scratch 'initial/untracked.txt'))) 'snapshot excludes untracked work'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $scratch 'initial/.git'))) 'snapshot contains only staged paths'

    Git-Fixture @('update-index', '--assume-unchanged', 'message.txt')
    $changed = Snapshot 'same-tree'
    Assert-True ($initial.Tree -ceq $changed.Tree -and $initial.IndexSha256 -cne $changed.IndexSha256) 'same-tree index byte drift is visible'

    $alternate = Join-Path $root 'alternate index'
    [IO.File]::Copy($index, $alternate)
    $env:GIT_INDEX_FILE = '../alternate index'
    Write-Fixture 'alternate.txt' "alternate`n"
    Git-Fixture @('add', '--', 'alternate.txt')
    $result = Snapshot 'alternate'
    Assert-True ($result.IndexPath -eq $alternate -and $result.Tree -cne $initial.Tree) 'relative alternate index identity and staged tree'
    Assert-FileBytes (Join-Path $scratch 'alternate/alternate.txt') ([Text.Encoding]::UTF8.GetBytes("alternate`n")) 'alternate index bytes'
    Assert-True ($env:GIT_INDEX_FILE -ceq '../alternate index') 'snapshot leaves Git routing in caller intact'

    $env:GIT_INDEX_FILE = $null
    Git-Fixture @('add', '--', 'alternate.txt')
    Git-Fixture @('update-index', '--index-version', '4')
    Git-Fixture @('update-index', '--split-index')
    $reported = Git-Fixture @('rev-parse', '--shared-index-path')
    Assert-True (![string]::IsNullOrEmpty($reported)) 'fixture Git generated a split index'
    $shared = [IO.Path]::GetFullPath($reported, $repo)
    [IO.File]::Copy($index, $alternate, $true)
    [IO.File]::WriteAllBytes($index, $indexBytes)
    $env:GIT_INDEX_FILE = '../alternate index'
    $sharedBytes = [IO.File]::ReadAllBytes($shared)
    [IO.File]::SetLastWriteTimeUtc($shared, [datetime]'2026-01-01Z')
    $timestamp = [IO.File]::GetLastWriteTimeUtc($shared)
    $split = Snapshot 'split'
    Assert-True ($split.SharedIndexPath -eq $shared -and $split.SharedIndexSha256 -eq (Get-FileHash -LiteralPath $shared).Hash) 'split base identity'
    $splitIdentity = Get-OpsshIndexIdentity -RepositoryRoot $repo
    Assert-True ($splitIdentity.SharedIndexPath -eq $split.SharedIndexPath -and
        $splitIdentity.SharedIndexSha256 -ceq $split.SharedIndexSha256) 'receipt recheck reads same split base identity'
    Assert-True ($split.Tree -ceq $result.Tree) 'version-four split index retains exact tree'
    Assert-FileBytes $shared $sharedBytes 'shared index bytes stay intact'
    Assert-True ([IO.File]::GetLastWriteTimeUtc($shared) -eq $timestamp) 'shared index timestamp stays intact'

    $fallback = Join-Path $root ([IO.Path]::GetFileName($shared))
    [IO.File]::Move($shared, $fallback)
    $splitFallback = Snapshot 'split-fallback'
    Assert-True ($splitFallback.SharedIndexPath -eq $fallback -and $splitFallback.Tree -ceq $split.Tree) 'split base resolves beside alternate index'

    $env:GIT_INDEX_FILE = $null
    $blob = Git-Fixture @('hash-object', '--', 'raw.bin')
    Git-Fixture @('update-index', '--add', '--cacheinfo', "120000,$blob,link")
    Assert-Rejected { Snapshot 'symlink' } 'symlink index entry is rejected'
    Git-Fixture @('update-index', '--force-remove', '--', 'link')
    Git-Fixture @('-c', 'core.ignoreCase=false', 'update-index', '--add', '--cacheinfo', "100644,$blob,Dir/a")
    Git-Fixture @('-c', 'core.ignoreCase=false', 'update-index', '--add', '--cacheinfo', "100644,$blob,dir/b")
    Assert-Rejected { Snapshot 'case-collision' } 'case-colliding directory entries are rejected'
    Git-Fixture @('update-index', '--force-remove', '--', 'Dir/a', 'dir/b')
    Git-Fixture @('-c', 'core.protectNTFS=false', 'update-index', '--add', '--cacheinfo', "100644,$blob,AUX.txt")
    Assert-True (@(Git-Fixture @('ls-files', '--', 'AUX.txt')).Count -eq 1) 'fixture stages the reserved device path'
    Assert-Rejected { Snapshot 'device' } 'reserved Windows device path is rejected'
    Git-Fixture @('-c', 'core.protectNTFS=false', 'update-index', '--force-remove', '--', 'AUX.txt')
    Git-Fixture @('-c', 'core.protectNTFS=false', 'update-index', '--add', '--cacheinfo', "100644,$blob,code~1.ps1")
    Assert-True ((@(Git-Fixture @('ls-files', '--', 'AUX.txt', 'code~1.ps1')) -join ',') -ceq 'code~1.ps1') 'fixture retains only the alias path'
    Assert-Rejected { Snapshot 'alias' } 'Windows short-name alias path is rejected'
    Git-Fixture @('update-index', '--force-remove', '--', 'code~1.ps1')

    $missing = '0123456789012345678901234567890123456789'
    Git-Fixture @('update-index', '--add', '--cacheinfo', "100644,$missing,missing.txt")
    Assert-Rejected { Snapshot 'missing-object' } 'missing staged object is rejected'
    Git-Fixture @('update-index', '--force-remove', '--', 'missing.txt')

    $git = (Get-Command git -CommandType Application | Select-Object -First 1).Source
    $start = [Diagnostics.ProcessStartInfo]::new($git, 'update-index --index-info')
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.StandardInput.Write("100644 $blob 1`tconflict.txt`n100644 $blob 2`tconflict.txt`n")
        $process.StandardInput.Close()
        $process.WaitForExit()
        Assert-True ($process.ExitCode -eq 0 -and @(Git-Fixture @('ls-files', '--unmerged')).Count -eq 2) 'fixture creates an unmerged index entry'
    } finally { $process.Dispose() }
    Assert-Rejected { Snapshot 'unmerged' } 'unmerged index cannot become a snapshot'
    Git-Fixture @('update-index', '--force-remove', '--', 'conflict.txt')
    $null = Snapshot 'clean-after-invalid'

    $repo = Join-Path $root 'sha256 repository'
    $null = [IO.Directory]::CreateDirectory($repo)
    Git-Fixture @('init', '--quiet', '--template=', '--object-format=sha256')
    Git-Fixture @('config', 'core.autocrlf', 'false')
    Write-Fixture 'sha.txt' "sha256`n"
    Git-Fixture @('add', '--', 'sha.txt')
    $sha = Snapshot 'sha256'
    Assert-True ($sha.Tree -cmatch '\A[a-f0-9]{64}\z') 'SHA-256 staged tree is supported'
    Assert-FileBytes (Join-Path $scratch 'sha256/sha.txt') ([Text.Encoding]::UTF8.GetBytes("sha256`n")) 'SHA-256 raw blob bytes'

    $repo = Join-Path $root 'pathspec repository'
    $null = [IO.Directory]::CreateDirectory($repo)
    Git-Fixture @('init', '--quiet', '--template=')
    Git-Fixture @('config', 'user.name', 'Snapshot Fixture')
    Git-Fixture @('config', 'user.email', 'snapshot@example.invalid')
    Git-Fixture @('config', 'commit.gpgSign', 'false')
    Git-Fixture @('config', 'core.autocrlf', 'false')
    Write-Fixture 'first.txt' "first old`n"
    Write-Fixture 'second.txt' "second old`n"
    Git-Fixture @('add', '--', 'first.txt', 'second.txt')
    Git-Fixture @('commit', '--quiet', '-m', 'initial fixture')
    $null = [IO.Directory]::CreateDirectory((Join-Path $repo '.test-hooks'))
    Git-Fixture @('config', 'core.hooksPath', '.test-hooks')
    Write-Fixture '.test-hooks/pre-commit' @'
#!/bin/sh
set -eu
exec pwsh -NoLogo -NoProfile -File "$(git rev-parse --show-toplevel)/.test-hooks/verify.ps1"
'@
    Write-Fixture '.test-hooks/verify.ps1' @'
$ErrorActionPreference = 'Stop'
Import-Module $env:OPSSH_SNAPSHOT_TEST_MODULE
$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $env:OPSSH_SNAPSHOT_TEST_SCRATCH 'pathspec'
$result = New-OpsshStagedSnapshot -RepositoryRoot $root -ScratchRoot $env:OPSSH_SNAPSHOT_TEST_SCRATCH -Destination $destination
if (!$env:GIT_INDEX_FILE -or $result.IndexPath -ne [IO.Path]::GetFullPath($env:GIT_INDEX_FILE, $root)) {
    throw 'Snapshot ignored the real index supplied to the pathspec hook.'
}
'@
    Write-Fixture 'first.txt' "first selected`n"
    Write-Fixture 'second.txt' "second staged outside pathspec`n"
    Git-Fixture @('add', '--', 'second.txt')
    $env:OPSSH_SNAPSHOT_TEST_MODULE = $module
    $env:OPSSH_SNAPSHOT_TEST_SCRATCH = $scratch
    try { Git-Fixture @('commit', '--quiet', '-m', 'selected pathspec', '--', 'first.txt') }
    finally {
        $env:OPSSH_SNAPSHOT_TEST_MODULE = $null
        $env:OPSSH_SNAPSHOT_TEST_SCRATCH = $null
    }
    Assert-FileBytes (Join-Path $scratch 'pathspec/first.txt') ([Text.Encoding]::UTF8.GetBytes("first selected`n")) 'pathspec snapshot contains selected change'
    Assert-FileBytes (Join-Path $scratch 'pathspec/second.txt') ([Text.Encoding]::UTF8.GetBytes("second old`n")) 'pathspec snapshot excludes separately staged change'

    $linked = Join-Path $root 'linked worktree'
    Git-Fixture @('worktree', 'add', '--quiet', '--detach', $linked)
    $repo = $linked
    $linkedIndex = Git-Fixture @('rev-parse', '--path-format=absolute', '--git-path', 'index')
    $linkedTree = Git-Fixture @('rev-parse', 'HEAD^{tree}')
    $linkedResult = Snapshot 'linked'
    Assert-True ($linkedResult.IndexPath -eq [IO.Path]::GetFullPath($linkedIndex) -and $linkedResult.Tree -ceq $linkedTree) 'linked worktree index and common objects'
    Write-Host 'PASS staged snapshot fixtures'
} finally {
    $env:GIT_INDEX_FILE = $oldIndex
    $root = [IO.Path]::GetFullPath($root)
    if ([IO.Path]::GetDirectoryName($root) -ne [IO.Path]::TrimEndingDirectorySeparator($temp) -or
        [IO.Path]::GetFileName($root) -cnotmatch '\Aopssh-snapshot-test-[a-f0-9]{32}\z' -or
        ([IO.File]::GetAttributes($root) -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Unexpected fixture cleanup path.'
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
