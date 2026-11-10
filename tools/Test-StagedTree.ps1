param([ValidateSet('pre-commit', 'commit-msg')][string]$Phase)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'HookSupport.psm1') -Force

$phases = 'go-format','go-modules','go-tests','go-vet','tray-test-restore','tray-test-build','tray-tests',
    'fixture-build','go-tray-interop','tray-restore','tray-publish','core-build','portable-launcher-build','release-inspection'

function Get-RecordFile([string]$Path) {
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or $file.Length -gt 4MB -or
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid validation record.' }
    $file
}

function Read-Record([string]$Path) {
    $file = Get-RecordFile $Path
    [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json -Depth 12 -AsHashtable
}

function Read-Report([string]$Path) {
    $file = Get-RecordFile $Path
    $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -gt 4MB) { throw 'The Windows validation report is too large.' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $stream.ReadExactly($bytes)
    } finally {
        $stream.Dispose()
    }
    @{
        hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        contents = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -Depth 12 -AsHashtable
    }
}

function Write-Record([string]$Path, $Record) {
    $pending = "$Path.pending"
    [IO.File]::WriteAllText($pending, ($Record | ConvertTo-Json -Depth 10))
    [IO.File]::Move($pending, $Path)
}

function Get-RunDirectory([string]$Token) {
    if ($Token -cnotmatch '^[a-f0-9]{32}$') { throw 'Invalid validation directory identity.' }
    Join-Path ([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) "opssh-gate-$Token"
}

function Confirm-Directory([string]$Path) {
    $directory = Get-Item -LiteralPath $Path -Force
    if (!$directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $directory.FullName -ne $Path) { throw 'Invalid validation directory.' }
}

function Remove-RunDirectory([string]$Token) {
    $expected = Get-RunDirectory $Token
    if (!(Test-Path -LiteralPath $expected)) { return }
    Confirm-Directory $expected
    Remove-Item -LiteralPath $expected -Recurse -Force
}

function Get-IndexFingerprint($Index) {
    @{
        indexPath = Get-OpsshPathHash $Index.IndexPath
        indexSha256 = $Index.IndexSha256
        sharedPath = Get-OpsshPathHash $Index.SharedIndexPath
        sharedSha256 = $Index.SharedIndexSha256
    }
}

function Confirm-Index($Expected) {
    $actual = Get-IndexFingerprint (Get-OpsshIndexIdentity -RepositoryRoot $root)
    foreach ($field in $actual.Keys) {
        if ($Expected[$field] -cne $actual[$field]) { throw 'The staged index changed during validation.' }
    }
}

function Test-JsonInteger($Value) {
    $Value -is [long] -or $Value -is [int]
}

function Confirm-Report($Report, [string]$Output) {
    if (!(Test-JsonInteger $report.schema) -or $report.schema -ne 1 -or
        $report.outcome -isnot [string] -or $report.outcome -cne 'passed' -or
        $report.phases -isnot [array] -or $report.phases.Count -ne $phases.Count -or
        $report.release -isnot [array]) {
        throw 'The Windows validation report is incomplete.'
    }
    $verifiedPhases = for ($i = 0; $i -lt $phases.Count; $i++) {
        $entry = $report.phases[$i]
        if ($entry.id -isnot [string] -or $entry.id -cne $phases[$i] -or
            $entry.outcome -isnot [string] -or $entry.outcome -cne 'passed' -or
            !(Test-JsonInteger $entry.exitCode) -or $entry.exitCode -ne 0) {
            throw 'A required Windows phase did not pass.'
        }
        @{ id = $phases[$i]; outcome = 'passed'; exitCode = 0 }
    }

    $release = Join-Path $Output 'release'
    Confirm-Directory $Output
    Confirm-Directory $release
    $files = [Collections.Generic.Dictionary[string,IO.FileInfo]]::new([StringComparer]::OrdinalIgnoreCase)
    $bytes = 0L
    Get-ChildItem -LiteralPath $release -Force -Recurse | ForEach-Object {
        $item = $_
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Invalid release evidence path.' }
        if (!$item.PSIsContainer) {
            $relative = [IO.Path]::GetRelativePath($release, $item.FullName).Replace('\', '/')
            $bytes += $item.Length
            if (!$files.TryAdd($relative, $item) -or $files.Count -gt 8192 -or $bytes -gt 2GB) {
                throw 'Invalid release file inventory.'
            }
        }
    }
    if ($files.Count -ne $report.release.Count) { throw 'Release evidence does not match the current output.' }
    foreach ($name in 'Microsoft.UI.Xaml.dll','opssh-core.exe','opssh-portable.exe','opssh-tray.dll','opssh-tray.exe','opssh-tray.pri') {
        if (!$files.ContainsKey($name) -or $files[$name].Length -eq 0) { throw 'Missing Windows release component.' }
    }
    $verifiedRelease = foreach ($entry in $report.release) {
        $item = $null
        if ($entry.path -isnot [string] -or !$files.Remove($entry.path, [ref]$item) -or
            !(Test-JsonInteger $entry.size) -or $item.Length -ne $entry.size) {
            throw 'A Windows release file changed after validation.'
        }
        $digest = (Get-FileHash -LiteralPath $item.FullName).Hash
        if ($entry.sha256 -isnot [string] -or $digest -ne $entry.sha256) {
            throw 'A Windows release file changed after validation.'
        }
        @{
            path = [IO.Path]::GetRelativePath($release, $item.FullName).Replace('\', '/')
            size = $item.Length; sha256 = $digest.ToLowerInvariant()
        }
    }
    @{ schema = 1; outcome = 'passed'; phases = @($verifiedPhases); release = @($verifiedRelease) }
}

function Write-FailedAttempt([string]$Path, [string]$ReportPath, [string]$Tree, [string]$Stage, $Result) {
    $completed = @(
        if (Test-Path -LiteralPath $ReportPath -PathType Leaf) {
            Confirm-Directory (Split-Path -Parent $ReportPath)
            $report = Read-Record $ReportPath
            for ($i = 0; $i -lt [Math]::Min($phases.Count, $report.phases.Count); $i++) {
                $entry = $report.phases[$i]
                if ($entry.id -isnot [string] -or $entry.id -cne $phases[$i] -or
                    $entry.outcome -isnot [string] -or $entry.outcome -cnotin 'passed','failed' -or
                    !(Test-JsonInteger $entry.exitCode)) { break }
                @{ id = $phases[$i]; outcome = $entry.outcome; exitCode = $entry.exitCode }
            }
        }
    )
    Write-Record $Path @{
        schema = 1; tree = $Tree; outcome = 'failed'; stage = $Stage
        phases = $completed; process = $Result; completedAt = [DateTime]::UtcNow.ToString('o')
    }
}

function Clear-StaleReceipts([string]$Directory) {
    foreach ($item in (Get-ChildItem -LiteralPath $Directory -File | Select-Object -First 64)) {
        if ($item.Name -notmatch '^([1-9][0-9]*)-([0-9]+)\.(json|running|consumed)(\.cleanup)?$') { continue }
        try {
            $process = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue
            $ticks = [long]$Matches[2]
            if ($process) {
                try {
                    if ($process.StartTime.ToUniversalTime().Ticks -eq $ticks) { continue }
                } finally { $process.Dispose() }
            }
            $claimed = $item.FullName
            if (!$claimed.EndsWith('.cleanup', [StringComparison]::Ordinal)) {
                $claimed += '.cleanup'
                [IO.File]::Move($item.FullName, $claimed)
            }
            $old = Read-Record $claimed
            if ($old.token -cmatch '^[a-f0-9]{32}$') { Remove-RunDirectory $old.token }
            Remove-Item -LiteralPath $claimed
        } catch { Write-Warning 'A stale staged validation record could not be cleaned.' }
    }
}

$stage = 'installation'
try {
    $root = Get-OpsshGitValue @('rev-parse', '--show-toplevel')
    $gitDir = Get-OpsshGitValue @('rev-parse', '--absolute-git-dir')
    $installPath = Join-Path $PSScriptRoot 'install.json'
    $install = Read-Record $installPath
    if ($install.schema -ne 1 -or $install.owner -cne (Get-OpsshPathHash $gitDir)) {
        throw 'Install worktree-private hooks for this checkout.'
    }
    if ($args.Count -eq 2 -and $args[0] -ceq 'opssh-installation-check' -and $args[1] -cmatch '^[a-f0-9]{32}$') {
        $probe = Join-Path $PSScriptRoot "probe-$($args[1])"
        $request = [IO.File]::ReadAllText($probe)
        if ($request -cnotin "$Phase/0", "$Phase/23") { exit 1 }
        Remove-Item -LiteralPath $probe
        exit [int]($request.Split('/')[1])
    }
    if (($Phase -eq 'pre-commit' -and $args.Count -ne 0) -or
        ($Phase -eq 'commit-msg' -and $args.Count -ne 1)) { throw 'Invalid hook arguments.' }

    $stage = 'git-invocation'
    $invocation = Get-OpsshGitInvocation
    $previous = if ($install.previous) { [IO.Path]::GetFullPath($install.previous, $root) } else {
        Join-Path (Get-OpsshGitValue @('rev-parse', '--path-format=absolute', '--git-common-dir')) 'hooks'
    }
    $oldHook = Get-OpsshHookFile $previous $Phase
    $stage = 'prior-hook'
    if ($oldHook -and !(Test-OpsshLegacyGate $oldHook $Phase $root)) {
        $resets = @(Get-OpsshHookResets)
        & git -c "core.hooksPath=$previous" @resets hook run --ignore-missing $Phase -- @args
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }

    Import-Module (Join-Path $PSScriptRoot 'StagedSnapshot.psm1') -Force
    $state = Join-Path $gitDir 'opssh-validation'
    $receipts = Join-Path $state 'receipts'
    $evidence = Join-Path $state 'evidence'
    foreach ($directory in $state, $receipts, $evidence) {
        $item = New-Item -ItemType Directory -Force -Path $directory
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Invalid validation state directory.' }
    }
    $receiptPath = Join-Path $receipts "$invocation.json"
    $installationHash = (Get-FileHash -LiteralPath $installPath).Hash

    if ($Phase -eq 'commit-msg') {
        $stage = 'receipt'
        $receipt = Read-Record $receiptPath
        if ($receipt.schema -ne 1 -or $receipt.invocation -cne $invocation -or
            $receipt.installation -cne $installationHash) { throw 'The staged validation receipt does not match.' }
        $run = Get-RunDirectory $receipt.token
        Confirm-Directory $run
        $reportPath = Join-Path $run 'report.json'
        $record = Read-Report $reportPath
        if ($record.hash -cne $receipt.reportHash) { throw 'The validation report changed.' }
        $stage = 'index-recheck'
        Confirm-Index $receipt.index
        $stage = 'artifact-recheck'
        $verified = Confirm-Report $record.contents (Join-Path $run 'out')
        Confirm-Index $receipt.index
        $stage = 'receipt-consumption'
        $consumed = Join-Path $receipts "$invocation.consumed"
        [IO.File]::Move($receiptPath, $consumed)
        try {
            Write-Record (Join-Path $evidence "$($receipt.token).json") @{
                schema = 1; tree = $receipt.tree; report = $verified; completedAt = [DateTime]::UtcNow.ToString('o')
            }
        } finally {
            Remove-RunDirectory $receipt.token
            Remove-Item -LiteralPath $consumed
        }
        Write-Host "Consumed staged validation receipt for tree $($receipt.tree)"
        exit 0
    }

    $stage = 'stale-receipts'
    Clear-StaleReceipts $receipts
    Get-ChildItem -LiteralPath $evidence -File -Filter '*.json' | Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -Skip 12 | ForEach-Object {
            if ($_.BaseName -cmatch '^[a-f0-9]{32}(\.failed)?$') {
                Remove-Item -LiteralPath $_.FullName -ErrorAction SilentlyContinue
            }
        }
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $token = [Guid]::NewGuid().ToString('N')
    $run = Get-RunDirectory $token
    $activePath = Join-Path $receipts "$invocation.running"
    Write-Record $activePath @{ schema = 1; token = $token }
    New-Item -ItemType Directory -Path $run | Out-Null
    $admitted = $false
    $reportPath = Join-Path $run 'report.json'
    $snapshot = $null
    $result = $null
    try {
        $stage = 'snapshot'
        $source = Join-Path $run 'source'
        $snapshot = New-OpsshStagedSnapshot -RepositoryRoot $root -ScratchRoot $run -Destination $source
        $index = Get-IndexFingerprint $snapshot
        $driver = Join-Path $source 'tools/Test-Windows.ps1'
        if (!(Test-Path -LiteralPath $driver -PathType Leaf)) { throw 'The staged Windows driver is missing.' }
        Import-Module (Join-Path $PSScriptRoot 'ValidationProcess.psm1') -Force
        $stage = 'windows-validation'
        $remaining = 1500000 - $deadline.ElapsedMilliseconds
        if ($remaining -le 0) { throw 'The staged validation deadline expired.' }
        $out = Join-Path $run 'out'
        $result = Invoke-OpsshValidationProcess -DriverPath $driver -OutputDirectory $out -ReportPath $reportPath -TimeoutMilliseconds $remaining -GitInvocation $invocation
        if (!$result.CleanedUp -or $result.TimedOut -or $result.Canceled -or $result.ExitCode -ne 0) {
            throw 'The staged Windows validation did not complete successfully.'
        }
        $stage = 'evidence-admission'
        Confirm-Directory $run
        $record = Read-Report $reportPath
        $null = Confirm-Report $record.contents $out
        Confirm-Index $index
        if ((Get-OpsshGitInvocation) -cne $invocation) { throw 'The Git validation owner is no longer active.' }
        if ($deadline.ElapsedMilliseconds -ge 1500000) { throw 'The staged validation deadline expired.' }
        Write-Record $receiptPath @{
            schema = 1; invocation = $invocation; installation = $installationHash; index = $index
            tree = $snapshot.Tree; token = $token; reportHash = $record.hash
        }
        $admitted = $true
        Write-Host "Validated staged tree $($snapshot.Tree)"
    } catch {
        try {
            Write-FailedAttempt (Join-Path $evidence "$token.failed.json") $reportPath $snapshot.Tree $stage $result
        } catch { Write-Warning 'Unable to record the failed validation attempt.' }
        throw
    } finally {
        if (!$admitted) {
            try { Remove-RunDirectory $token } catch { Write-Warning 'Unable to clean the failed validation attempt.' }
        }
        Remove-Item -LiteralPath $activePath -ErrorAction SilentlyContinue
    }
} catch {
    [Console]::Error.WriteLine("Commit validation failed at $stage. Run Install-TestHooks.ps1 -Check and verify the staged tree.")
    exit 1
}
