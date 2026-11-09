[CmdletBinding()]
param([switch]$Confirm)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repo
try {
    $tree = git write-tree
    if ($LASTEXITCODE -ne 0 -or !$tree) { throw 'Could not identify the staged tree.' }
    $receipt = git rev-parse --path-format=absolute --git-path opssh-verified-tree
    if ($LASTEXITCODE -ne 0 -or !$receipt) { throw 'Could not resolve the worktree receipt.' }
    if ($Confirm) {
        if (!(Test-Path -LiteralPath $receipt) -or (Get-Content -Raw -LiteralPath $receipt).Trim() -ne $tree) {
            throw 'The staged tree differs from the validated tree.'
        }
        Remove-Item -LiteralPath $receipt
        exit 0
    }

    if (Test-Path -LiteralPath $receipt) { Remove-Item -LiteralPath $receipt }
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $folder = Join-Path $parent ('opssh-staged-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder | Out-Null
    try {
        $archive = Join-Path $folder 'tree.zip'
        $snapshot = Join-Path $folder 'snapshot'
        git archive --format=zip "--output=$archive" $tree
        if ($LASTEXITCODE -ne 0) { throw 'Could not archive the staged tree.' }
        Expand-Archive -LiteralPath $archive -DestinationPath $snapshot
        & (Join-Path $snapshot 'tools/Test-Windows.ps1')
        $finalTree = git write-tree
        if ($LASTEXITCODE -ne 0 -or $finalTree -ne $tree) { throw 'The staged tree changed during verification.' }
        [IO.File]::WriteAllText($receipt, $tree)
        Write-Host "Validated staged tree $tree"
    } finally {
        $resolved = (Resolve-Path -LiteralPath $folder).ProviderPath
        if ($resolved -ne $folder -or (Split-Path -Parent $resolved).TrimEnd('\') -ne $parent.TrimEnd('\') -or
            !(Split-Path -Leaf $resolved).StartsWith('opssh-staged-')) {
            throw 'Refusing to remove an unexpected snapshot path.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
} finally {
    Pop-Location
}
