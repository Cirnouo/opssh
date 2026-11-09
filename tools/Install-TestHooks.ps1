#requires -Version 7.4
[CmdletBinding(DefaultParameterSetName='Check')]
param(
    [Parameter(Mandatory, ParameterSetName='Install')][switch]$Install,
    [Parameter(Mandatory, ParameterSetName='Check')][switch]$Check,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'HookSupport.psm1') -Force

function Quote-Sh([string]$Value) {
    $replacement = "'" + [char]34 + "'" + [char]34 + "'"
    "'" + $Value.Replace('\', '/').Replace("'", $replacement) + "'"
}

function Write-Hook([string]$Path, [string]$Line) {
    [IO.File]::WriteAllLines($Path, @('#!/bin/sh', $Line))
    $text = [IO.File]::ReadAllText($Path).Replace([string][char]13, '')
    [IO.File]::WriteAllText($Path, $text)
}

$root = Get-OpsshGitValue @('-C', $RepositoryRoot, 'rev-parse', '--show-toplevel')
Push-Location -LiteralPath $root
try {
    $gitDir = Get-OpsshGitValue @('rev-parse', '--absolute-git-dir')
    $common = Get-OpsshGitValue @('rev-parse', '--path-format=absolute', '--git-common-dir')
    $owner = Get-OpsshPathHash $gitDir
    $current = & git config --path --get core.hooksPath
    if ($LASTEXITCODE -notin 0,1) { throw 'Cannot read the effective hooks directory.' }
    $absolute = if ($current) { [IO.Path]::GetFullPath($current, $root) }
    $installed = if ($absolute -and (Split-Path -Leaf $absolute) -match '^opssh-hooks-[a-f0-9]{32}$') {
        $manifest = Join-Path $absolute 'install.json'
        if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'The inherited opssh hook installation is incomplete.' }
        Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json -AsHashtable
    }

    if ($Check) {
        if (!$installed -or $installed.schema -ne 1 -or $installed.owner -ne $owner -or
            (Split-Path -Parent $absolute) -ne [IO.Path]::GetFullPath($gitDir)) {
            throw 'Install the staged-tree hooks in this worktree before committing.'
        }
        $localPath = & git config --worktree --path --get core.hooksPath
        if ($LASTEXITCODE -ne 0 -or $localPath -ne $current) { throw 'Hooks are not configured at worktree scope.' }
        foreach ($name in $installed.files.Keys) {
            if ($name -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.-]*$' -or
                (Get-FileHash -LiteralPath (Join-Path $absolute $name)).Hash -cne $installed.files[$name]) {
                throw 'The hook installation has changed.'
            }
        }
        $resets = @(Get-OpsshHookResets)
        foreach ($phase in 'pre-commit', 'commit-msg') {
            foreach ($expected in 0, 23) {
                $nonce = [Guid]::NewGuid().ToString('N')
                $probe = Join-Path $absolute "probe-$nonce"
                [IO.File]::WriteAllText($probe, "$phase/$expected")
                try {
                    & git @resets hook run $phase -- opssh-installation-check $nonce
                    if ($LASTEXITCODE -ne $expected -or (Test-Path -LiteralPath $probe)) {
                        throw 'The installed hook failed its exit-code probe.'
                    }
                } finally {
                    if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe }
                }
            }
        }
        Write-Host 'Worktree-private commit hooks verified.'
        return
    }

    $worktreeConfig = & git config --local --type=bool --get extensions.worktreeConfig
    if ($LASTEXITCODE -notin 0,1) { throw 'Cannot read Git worktree config support.' }
    if ($worktreeConfig -ne 'true') {
        $bare = & git config --local --type=bool --get core.bare
        $customRoot = & git config --local --get core.worktree
        if ($bare -eq 'true' -or $customRoot) { throw 'This Git layout requires explicit worktree config migration.' }
        & git config --local extensions.worktreeConfig true
        if ($LASTEXITCODE) { throw 'Cannot enable worktree config.' }
    }

    $previous = if ($installed) { $installed.previous } else { $current }
    $previousAbsolute = if ($previous) { [IO.Path]::GetFullPath($previous, $root) } else { Join-Path $common 'hooks' }
    $destination = Join-Path $gitDir ('opssh-hooks-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $destination | Out-Null
    $names = 'Test-StagedTree.ps1','HookSupport.psm1','StagedSnapshot.psm1','StagedSnapshot.cs',
        'ValidationProcess.psm1','ValidationProcess.cs','Start-ValidationProcess.ps1'
    foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $destination }

    foreach ($phase in 'pre-commit', 'commit-msg') {
        $runner = Quote-Sh (Join-Path $destination 'Test-StagedTree.ps1')
        Write-Hook (Join-Path $destination $phase) ('exec pwsh -NoLogo -NoProfile -NonInteractive -File ' +
            $runner + ' ' + $phase + ' "$@"')
    }

    $hookRoute = if ($previous) { $previous } else { $previousAbsolute }
    $hookDirectories = @($previousAbsolute)
    if (![IO.Path]::IsPathRooted($hookRoute)) {
        # Git runs receive hooks from a Git directory, including in a non-bare repository.
        $hookDirectories += [IO.Path]::GetFullPath($hookRoute, $gitDir), [IO.Path]::GetFullPath($hookRoute, $common)
    }
    $events = $hookDirectories | Sort-Object -Unique | Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -File } |
        ForEach-Object { $_.Name -replace '\.exe$', '' } |
        Where-Object { $_ -match '^[a-z][a-z0-9-]*$' -and $_ -notin 'pre-commit','commit-msg' } |
        Sort-Object -Unique
    foreach ($event in $events) {
        $prior = Quote-Sh (Join-Path $hookRoute $event)
        $priorExe = Quote-Sh (Join-Path $hookRoute "$event.exe")
        Write-Hook (Join-Path $destination $event) ('if test -x ' + $prior + '; then exec ' + $prior +
            ' "$@"; fi; test ! -x ' + $priorExe + ' || exec ' + $priorExe + ' "$@"')
    }
    $files = [ordered]@{}
    foreach ($file in (Get-ChildItem -LiteralPath $destination -File)) {
        $files[$file.Name] = (Get-FileHash -LiteralPath $file.FullName).Hash
    }
    @{ schema = 1; owner = $owner; previous = $previous; files = $files } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'install.json') -Encoding utf8
    & git config --worktree core.hooksPath $destination.Replace('\', '/')
    if ($LASTEXITCODE) { throw 'Cannot activate the worktree-private hooks.' }
    Write-Host 'Worktree-private commit hooks installed. Run Install-TestHooks.ps1 -Check.'
} finally {
    Pop-Location
}
