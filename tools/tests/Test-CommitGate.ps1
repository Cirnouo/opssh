[CmdletBinding()]
param([string]$ToolsDirectory = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$installer = Join-Path ([IO.Path]::GetFullPath($ToolsDirectory, (Get-Location).ProviderPath)) 'Install-TestHooks.ps1'
if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'The staged gate installer is missing.' }

function Invoke-Git([string[]]$Arguments) {
    & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git acceptance command failed: $($Arguments[0])." }
}

function Write-Utf8([string]$Path, [string]$Text) {
    $absolute = [IO.Path]::GetFullPath($Path, (Get-Location).ProviderPath)
    [IO.File]::WriteAllText($absolute, $Text.Replace([string][char]13, ''))
}

function Read-Receipts {
    Get-ChildItem -LiteralPath '.git/opssh-validation/receipts' -File -Filter '*.json' |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -AsHashtable }
}

function Get-ValidationRun([string]$Token) {
    if ($Token -cnotmatch '\A[a-f0-9]{32}\z') { throw 'Invalid fixture validation token.' }
    Join-Path ([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) "opssh-gate-$Token"
}

function Assert-RejectedCommit([string]$Case, [string[]]$Arguments, [string]$Stage) {
    $head = Invoke-Git @('rev-parse', 'HEAD')
    $output = & git @Arguments 2>&1
    $status = $LASTEXITCODE
    if ($status -eq 0 -or (Invoke-Git @('rev-parse', 'HEAD')) -cne $head -or
        ($Stage -and ($output -join [char]10) -notmatch "Commit validation failed at $Stage\.")) {
        throw "Commit gate did not reject $Case at the expected stage."
    }
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('opssh-hook-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$oldGitEnvironment = @(Get-ChildItem Env:GIT_*)
Push-Location -LiteralPath $temporary
try {
    foreach ($entry in $oldGitEnvironment) { Remove-Item -LiteralPath "Env:$($entry.Name)" }
    $env:GIT_CONFIG_GLOBAL = Join-Path $temporary 'empty.gitconfig'
    $env:GIT_CONFIG_NOSYSTEM = '1'
    Write-Utf8 $env:GIT_CONFIG_GLOBAL ''
    Invoke-Git @('init', '--quiet', '--initial-branch=main')
    Invoke-Git @('config', 'user.name', 'Offline fixture')
    Invoke-Git @('config', 'user.email', 'fixture@example.invalid')
    Invoke-Git @('config', 'commit.gpgSign', 'false')
    Invoke-Git @('config', 'core.autocrlf', 'false')
    New-Item -ItemType Directory -Path tools,.prior | Out-Null
    Write-Utf8 '.gitattributes' '*.ignored export-ignore'
    Write-Utf8 'staged.txt' 'staged contents'
    Write-Utf8 'must-be-snapshotted.ignored' 'tracked raw content'
    @'
param([string]$OutputDirectory, [string]$ReportPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ((Get-ChildItem Env:GIT_*) -or
    [IO.File]::ReadAllText((Join-Path $root 'staged.txt')) -cne 'staged contents' -or
    [IO.File]::ReadAllText((Join-Path $root 'must-be-snapshotted.ignored')) -cne 'tracked raw content' -or
    (Test-Path -LiteralPath (Join-Path $root 'untracked.txt'))) {
    throw 'Driver did not receive the exact isolated staged tree.'
}
$release = Join-Path $OutputDirectory 'release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$files = foreach ($name in 'Microsoft.UI.Xaml.dll','opssh-core.exe','opssh-portable.exe','opssh-tray.dll','opssh-tray.exe','opssh-tray.pri') {
    $path = Join-Path $release $name
    [IO.File]::WriteAllText($path, 'fixture')
    @{path = $name; size = 7; sha256 = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()}
}
$phases = foreach ($id in 'go-format','go-modules','go-tests','go-vet','tray-test-restore',
    'tray-test-build','tray-tests','fixture-build','go-tray-interop','tray-restore','tray-publish',
    'core-build','portable-launcher-build','release-inspection') {
    @{id = $id; outcome = 'passed'; exitCode = 0}
}
$report = @{schema = 1; outcome = 'passed'; phases = @($phases); release = @($files); diagnostics = 'synthetic evidence marker'}
if (Test-Path -LiteralPath (Join-Path $root 'malformed-report.txt')) {
    $report.outcome = @()
    $report.phases[0].id = @()
    $report.phases[0].outcome = @()
}
$report |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8
'@ | Set-Content -LiteralPath 'tools/Test-Windows.ps1' -Encoding utf8
    Invoke-Git @('add', '--', 'staged.txt', '.gitattributes', 'must-be-snapshotted.ignored', 'tools/Test-Windows.ps1')
    $tree = Invoke-Git @('write-tree')
    Write-Utf8 'staged.txt' 'unstaged replacement'
    Write-Utf8 'untracked.txt' 'untracked data'
    @'
#!/bin/sh
printf 'legacy-pre\n' >> .git/hook-acceptance
'@ | ForEach-Object { Write-Utf8 '.prior/pre-commit' $_ }
    @'
#!/bin/sh
printf 'legacy-message\n' >> .git/hook-acceptance
test -f "$1"
'@ | ForEach-Object { Write-Utf8 '.prior/commit-msg' $_ }
    New-Item -ItemType Directory -Path '.git/.prior' | Out-Null
    Write-Utf8 '.git/.prior/pre-receive' @'
#!/bin/sh
read -r old new ref || exit 33
test "$ref" = refs/heads/gate-local-receive || exit 34
printf '%s\n' "$new" > hook-receive
'@
    Invoke-Git @('config', 'core.hooksPath', '.prior')
    Invoke-Git @('config', 'hook.fixture.event', 'pre-commit')
    Invoke-Git @('config', 'hook.fixture.command', "printf 'configured-pre\n' >> .git/hook-acceptance")

    & $installer -Install -RepositoryRoot $temporary
    & $installer -Check -RepositoryRoot $temporary
    if (Test-Path -LiteralPath '.git/hook-acceptance') { throw 'Installer probes replayed a prior hook.' }
    Invoke-Git @('commit', '--quiet', '-m', 'test: commit isolated fixture')
    if ((Invoke-Git @('rev-parse', 'HEAD^{tree}')) -cne $tree) { throw 'Committed tree differs from the staged fixture.' }
    $log = (Get-Content -LiteralPath '.git/hook-acceptance') -join ','
    if ($log -cne 'configured-pre,legacy-pre,legacy-message') { throw 'Configured and prior hook order or count changed.' }
    Invoke-Git @('push', '--quiet', '.', 'HEAD:refs/heads/gate-local-receive')
    if ((Get-Content -LiteralPath '.git/hook-receive') -cne (Invoke-Git @('rev-parse', 'HEAD'))) {
        throw 'Relative receive hook did not run from the Git directory with the original stdin.'
    }
    if (Get-ChildItem -LiteralPath '.git/opssh-validation/evidence' -Filter '*.json' |
        Select-String -SimpleMatch 'synthetic evidence marker') { throw 'Unverified report data leaked into retained evidence.' }
    Write-Utf8 'malformed-report.txt' 'exercise wrong JSON field types'
    Invoke-Git @('add', '--', 'malformed-report.txt')
    Assert-RejectedCommit 'malformed report' @('commit', '--quiet', '-m', 'test: reject malformed phase evidence') 'evidence-admission'
    Invoke-Git @('reset', '--quiet', 'HEAD', '--', 'malformed-report.txt')

    @'
#!/bin/sh
git add -- staged.txt
'@ | ForEach-Object { Write-Utf8 '.prior/commit-msg' $_ }
    Write-Utf8 'addition.txt' 'another staged change'
    Invoke-Git @('add', '--', 'addition.txt')
    Assert-RejectedCommit 'a late staged change' @('commit', '--quiet', '-m', 'test: reject a late staged change') 'index-recheck'
    Invoke-Git @('reset', '--quiet', 'HEAD', '--', 'staged.txt')

    Write-Utf8 '.prior/pre-commit' @'
#!/bin/sh
printf 'declined\n' > .git/prior-declined
exit 29
'@
    & git hook run pre-commit
    if ($LASTEXITCODE -ne 29 -or !(Test-Path -LiteralPath '.git/prior-declined')) {
        throw 'The prior pre-commit hook exit status was not preserved.'
    }
    Write-Utf8 '.prior/pre-commit' ("#!/bin/sh", 'exit 0', '' -join [char]10)

    Write-Utf8 '.prior/commit-msg' @'
#!/bin/sh
exec pwsh -NoLogo -NoProfile -NonInteractive -File .prior/tamper.ps1 "$1"
'@
    Write-Utf8 '.prior/tamper.ps1' @'
param([string]$Message)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $Message -PathType Leaf)) { throw 'Fixture hook did not receive the commit message.' }
$receipt = @(Get-ChildItem -LiteralPath '.git/opssh-validation/receipts' -File -Filter '*.json')
if ($receipt.Count -ne 1) { throw 'Fixture hook expected one current receipt.' }
$token = (Get-Content -LiteralPath $receipt[0].FullName -Raw | ConvertFrom-Json -AsHashtable).token
if ($token -cnotmatch '\A[a-f0-9]{32}\z') { throw 'Invalid fixture validation token.' }
$run = Join-Path ([IO.Path]::GetTempPath()) "opssh-gate-$token"
$target = Get-Content -LiteralPath '.git/tamper-target' -Raw
switch ($target) {
    report { [IO.File]::AppendAllText((Join-Path $run 'report.json'), [string][char]10) }
    artifact { [IO.File]::WriteAllText((Join-Path $run 'out/release/opssh-core.exe'), 'changed') }
    default { throw 'Unexpected fixture tamper target.' }
}
Set-Content -LiteralPath '.git/tampered' -Value $target
'@
    foreach ($target in 'report', 'artifact') {
        Write-Utf8 '.git/tamper-target' $target
        $stage = if ($target -eq 'report') { 'receipt' } else { 'artifact-recheck' }
        Assert-RejectedCommit "$target tampering" @('commit', '--quiet', '-m', "test: reject $target tampering") $stage
        if ((Get-Content -LiteralPath '.git/tampered') -cne $target) { throw 'The prior hook did not tamper with the fixture.' }
    }
    Write-Utf8 '.prior/commit-msg' ('#!/bin/sh', 'test -f "$1"', '' -join [char]10)

    Write-Utf8 '.prior/cancel-editor' @'
#!/bin/sh
printf 'canceled\n' > .git/editor-canceled
exit 31
'@
    Assert-RejectedCommit 'a canceled commit editor' @('-c', 'core.editor=sh .prior/cancel-editor', 'commit', '--quiet') ''
    $stale = @(Read-Receipts)
    if ($stale.Count -ne 1 -or !(Test-Path -LiteralPath '.git/editor-canceled')) {
        throw 'The canceled commit did not leave a validated receipt.'
    }
    $staleRun = Get-ValidationRun $stale[0].token
    $replay = & git hook run commit-msg -- .git/COMMIT_EDITMSG 2>&1
    if ($LASTEXITCODE -eq 0 -or ($replay -join [char]10) -notmatch 'Commit validation failed at receipt\.' -or
        (Read-Receipts).token -cne $stale[0].token) {
        throw 'A separate Git invocation accepted or consumed the canceled commit receipt.'
    }
    Invoke-Git @('commit', '--quiet', '-m', 'test: validate a fresh invocation')
    if ((Read-Receipts) -or (Test-Path -LiteralPath $staleRun)) {
        throw 'Fresh verification did not clean up the canceled invocation receipt and scratch.'
    }

    $defaultIndex = Join-Path $temporary '.git/index'
    $defaultHash = (Get-FileHash -LiteralPath $defaultIndex).Hash
    $firstIndex = Join-Path $temporary '.git/first.index'
    $secondIndex = Join-Path $temporary '.git/second.index'
    [IO.File]::Copy($defaultIndex, $firstIndex)
    Write-Utf8 'alternate.txt' 'alternate index change'
    $env:GIT_INDEX_FILE = $firstIndex
    Invoke-Git @('add', '--', 'alternate.txt')
    $alternateTree = Invoke-Git @('write-tree')
    $env:GIT_INDEX_FILE = $null
    Write-Utf8 '.prior/hold-editor.ps1' @'
param([string]$Message)
$ErrorActionPreference = 'Stop'
Set-Content -LiteralPath '.git/editor-ready' -Value ready
$clock = [Diagnostics.Stopwatch]::StartNew()
while (!(Test-Path -LiteralPath '.git/editor-release')) {
    if ($clock.Elapsed.TotalSeconds -ge 90) { throw 'Concurrent fixture editor timed out.' }
    Start-Sleep -Milliseconds 100
}
Set-Content -LiteralPath $Message -Value 'test: commit the first alternate index'
'@
    $git = (Get-Command git -CommandType Application | Select-Object -First 1).Source
    $start = [Diagnostics.ProcessStartInfo]::new($git)
    $start.WorkingDirectory = $temporary
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in '-c', 'core.editor=pwsh -NoLogo -NoProfile -NonInteractive -File .prior/hold-editor.ps1',
        'commit', '--quiet') { $start.ArgumentList.Add($argument) }
    $start.Environment['GIT_INDEX_FILE'] = $firstIndex
    $first = [Diagnostics.Process]::Start($start)
    try {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath '.git/editor-ready')) {
            if ($first.HasExited -or $clock.Elapsed.TotalSeconds -ge 60) { throw 'First alternate-index commit did not reach its editor.' }
            Start-Sleep -Milliseconds 100
        }
        $live = @(Read-Receipts)
        if ($live.Count -ne 1 -or $live[0].tree -cne $alternateTree) { throw 'The live commit did not validate its alternate index.' }

        # The second process validates its own index while the first still owns a live receipt.
        # Only the first process runs git commit, so the two processes never contend for a ref.
        [IO.File]::Copy($firstIndex, $secondIndex)
        $env:GIT_INDEX_FILE = $secondIndex
        Invoke-Git @('hook', 'run', 'pre-commit')
        $env:GIT_INDEX_FILE = $null
        $both = @(Read-Receipts)
        $other = @($both | Where-Object { $_.invocation -cne $live[0].invocation })
        if ($both.Count -ne 2 -or $other.Count -ne 1 -or $first.HasExited -or
            $other[0].tree -cne $live[0].tree -or $other[0].index.indexSha256 -cne $live[0].index.indexSha256 -or
            $other[0].index.indexPath -ceq $live[0].index.indexPath) {
            throw 'Overlapping validations did not preserve separate receipts for byte-identical alternate indexes.'
        }
        Write-Utf8 '.git/editor-release' 'release'
        if (!$first.WaitForExit(60000) -or $first.ExitCode -ne 0) { throw 'The live alternate-index commit could not consume its receipt.' }
        $remaining = @(Read-Receipts)
        if ((Invoke-Git @('rev-parse', 'HEAD^{tree}')) -cne $alternateTree -or
            (Get-FileHash -LiteralPath $defaultIndex).Hash -cne $defaultHash -or
            $remaining.Count -ne 1 -or $remaining[0].invocation -cne $other[0].invocation) {
            throw 'The live commit changed the wrong index or consumed the wrong receipt.'
        }
    } finally {
        $env:GIT_INDEX_FILE = $null
        try {
            if (!$first.HasExited) {
                $first.Kill($true)
                if (!$first.WaitForExit(5000)) { throw 'The concurrent fixture Git process did not exit.' }
            }
        } finally { $first.Dispose() }
    }
    Write-Host 'Commit gate acceptance passed.'
} finally {
    try {
        if (Test-Path -LiteralPath '.git/opssh-validation/receipts') {
            foreach ($receipt in (Read-Receipts)) {
                $run = Get-ValidationRun $receipt.token
                if (Test-Path -LiteralPath $run) {
                    $directory = Get-Item -LiteralPath $run -Force
                    if (!$directory.PSIsContainer -or $directory.FullName -ne $run -or
                        ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                        throw 'Unexpected fixture validation directory.'
                    }
                    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
                }
            }
        }
    } finally {
        Pop-Location
        foreach ($entry in (Get-ChildItem Env:GIT_*)) { Remove-Item -LiteralPath "Env:$($entry.Name)" }
        foreach ($entry in $oldGitEnvironment) { Set-Item -LiteralPath "Env:$($entry.Name)" -Value $entry.Value }
        $absolute = [IO.Path]::GetFullPath($temporary)
        if ((Split-Path -Parent $absolute).TrimEnd('\') -ne ([IO.Path]::GetTempPath()).TrimEnd('\') -or
            !(Split-Path -Leaf $absolute).StartsWith('opssh-hook-test-', [StringComparison]::Ordinal) -or
            ([IO.File]::GetAttributes($absolute) -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Unexpected fixture directory.'
        }
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}
