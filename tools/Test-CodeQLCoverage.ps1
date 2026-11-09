[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('go', 'csharp', 'actions')][string]$Language,
    [Parameter(Mandatory)][string]$DatabaseLocations,
    [Parameter(Mandatory)][string]$SarifDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$expectedSources = switch ($Language) {
    'go' {
        'cmd/opssh-core/main_windows.go'
        'cmd/opssh-core/release_windows.go'
        'internal/agentwire/wire.go'
        'internal/config/config.go'
        'internal/control/channel.go'
        'internal/desktop/backend.go'
        'internal/engine/engine.go'
        'internal/winipc/control_windows.go'
        'internal/winipc/peer_windows.go'
        'internal/winipc/pipe_windows.go'
    }
    'csharp' {
        'tray/Opssh.Tray/App.cs'
        'tray/Opssh.Tray/CoreSession.cs'
        'tray/Opssh.Tray/DashboardWindow.cs'
        'tray/Opssh.Tray/DashboardWindow.Settings.cs'
        'tray/Opssh.Tray/TrayIcon.cs'
        'tray/Opssh.Tray.Tests/Program.cs'
    }
    'actions' {
        & git -C $repo ls-files -- '.github/workflows/*.yml' '.github/workflows/*.yaml' |
            Where-Object { $_ -cmatch '\A\.github/workflows/[^/]+\.ya?ml\z' }
        if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tracked workflows.' }
    }
}
if (!$expectedSources) { throw 'No expected CodeQL sources were established.' }

$locations = $DatabaseLocations | ConvertFrom-Json -AsHashtable
$database = $locations[$Language]
if (!$database) { throw "The $Language CodeQL database was not reported." }
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $database 'src.zip'))
try {
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    # CodeQL normalizes Windows drive colons when it records absolute archive paths.
    $archiveRoot = $repo.Replace('\', '/').Replace(':', '_').Trim('/')
    foreach ($source in $expectedSources) {
        $matches = @($archive.Entries | Where-Object {
            $name = $_.FullName.Replace('\', '/').TrimStart('/')
            $name.Equals($source, $comparison) -or $name.Equals("$archiveRoot/$source", $comparison)
        })
        if ($matches.Count -ne 1 -or $matches[0].Length -le 0) {
            throw "Missing or ambiguous CodeQL source extraction for $source"
        }
        # The C# archive writes source text without a BOM.
        $reader = [IO.StreamReader]::new($matches[0].Open(), [Text.Encoding]::UTF8, $true)
        try { $archivedSource = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        if ($archivedSource -cne [IO.File]::ReadAllText((Join-Path $repo $source))) {
            throw "The extracted CodeQL source differs from the checkout for $source"
        }
    }
} finally {
    $archive.Dispose()
}

$reports = @(Get-ChildItem -LiteralPath $SarifDirectory -File -Filter '*.sarif')
if ($reports.Count -ne 1 -or $reports[0].Name -cne "$Language.sarif" -or $reports[0].Length -le 0) {
    throw "Expected one nonempty SARIF report for $Language."
}
$report = Get-Content -LiteralPath $reports[0].FullName -Raw | ConvertFrom-Json -Depth 100
if ($report.version -cne '2.1.0' -or @($report.runs).Count -ne 1 -or
    $report.runs[0].tool.driver.name -cne 'CodeQL' -or
    $report.runs[0].automationDetails.id -cne "/language:$Language/") {
    throw "Invalid CodeQL SARIF report for $Language."
}
Write-Host "Verified $Language extraction and a CodeQL SARIF report."
