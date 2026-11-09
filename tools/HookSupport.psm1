$ErrorActionPreference = 'Stop'

function Get-OpsshGitValue([string[]]$Arguments) {
    $value = & git @Arguments
    if ($LASTEXITCODE -ne 0 -or @($value).Count -ne 1 -or !$value) { throw 'Cannot resolve the Git worktree.' }
    $value
}

function Get-OpsshPathHash([string]$Path) {
    if (!$Path) { return '' }
    $canonical = [IO.Path]::GetFullPath($Path).ToUpperInvariant()
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
}

function Get-OpsshHookResets {
    $keys = & git config --name-only --get-regexp '^hook\..*\.event$'
    if ($LASTEXITCODE -notin 0,1) { throw 'Cannot inspect configured hooks.' }
    foreach ($key in ($keys | Sort-Object -Unique -CaseSensitive)) {
        '-c'
        "$key="
    }
}

function Get-OpsshGitInvocation {
    $id = $PID
    $childStart = (Get-Process -Id $id).StartTime.ToUniversalTime()
    for ($depth = 0; $depth -lt 8; $depth++) {
        $child = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $id" -OperationTimeoutSec 3
        if (!$child.ParentProcessId) { break }
        $parent = Get-Process -Id $child.ParentProcessId
        try {
            $started = $parent.StartTime.ToUniversalTime()
            if ($started -gt $childStart) { break }
            if ($parent.ProcessName -ieq 'git') {
                $null = $parent.Handle
                if ($parent.HasExited) { break }
                return "$($parent.Id)-$($started.Ticks)"
            }
            $childStart = $started
            $id = $parent.Id
        } finally {
            $parent.Dispose()
        }
    }
    throw 'Cannot bind verification to the active Git invocation.'
}

function Get-OpsshHookFile([string]$Directory, [string]$Name) {
    foreach ($candidate in $Name, "$Name.exe") {
        $path = Join-Path $Directory $candidate
        if (Test-Path -LiteralPath $path -PathType Leaf) { return $path }
    }
}

function Test-OpsshLegacyGate([string]$File, [string]$Phase, [string]$Root) {
    if (!$File -or (Split-Path -Parent $File) -ne (Join-Path $Root '.githooks')) { return $false }
    $old = if ($Phase -eq 'pre-commit') {
        'abfa82fa8d7321d6a7250350aad844ddbcc5520b'
    } else {
        '2227f4417b3b8d0c11af26ffb23819ead060ab6c'
    }
    $hash = & git hash-object --no-filters -- $File
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify the previous hook.' }
    if ($hash -eq $old) { return $true }
    $first = Get-Content -LiteralPath $File -TotalCount 2
    return '# opssh worktree gate bootstrap' -cin $first
}

Export-ModuleMember -Function Get-OpsshGitValue,Get-OpsshPathHash,Get-OpsshHookResets,
    Get-OpsshGitInvocation,Get-OpsshHookFile,Test-OpsshLegacyGate
