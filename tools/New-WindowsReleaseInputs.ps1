using module ./OpsshRelease.psm1

#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$TagObject,
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$ChangelogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ReleaseChanges([string]$changelog, [version]$version) {
    $headingPrefix = '\A {0,3}##[ \t]+\[' + [regex]::Escape($version.ToString()) + '\]'
    $section = [Collections.Generic.List[string]]::new()
    $matchesFound = 0
    $collecting = $false
    $hasBullet = $false
    $fence = ''
    foreach ($line in $changelog -split '\r?\n') {
        if ($fence.Length -gt 0) {
            if ($collecting) { $section.Add($line) }
            if ($line -cmatch ('\A {0,3}' + [regex]::Escape($fence) + [regex]::Escape($fence[0]) + '*[ \t]*\z')) { $fence = '' }
            continue
        }
        $opener = [regex]::Match($line, '\A {0,3}(`{3,}[^`]*|~{3,}.*)\z')
        if ($opener.Success) {
            $fence = [regex]::Match($opener.Groups[1].Value, '\A(`{3,}|~{3,})').Value
            if ($collecting) { $section.Add($line) }
            continue
        }
        if ($line -cmatch '\A {0,3}##(?:[ \t]|\z)') {
            $collecting = $false
            if ($line -cmatch $headingPrefix) {
                $heading = [regex]::Match($line, $headingPrefix + ' - ([0-9]{4}-[0-9]{2}-[0-9]{2})[ \t]*\z')
                $date = [datetime]::MinValue
                if (-not $heading.Success -or -not [datetime]::TryParseExact($heading.Groups[1].Value, 'yyyy-MM-dd',
                    [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$date)) {
                    throw 'The release changelog heading requires a valid calendar date.'
                }
                $matchesFound++
                $collecting = $true
            }
            continue
        }
        if ($collecting) {
            $section.Add($line)
            if ($line -cmatch '\A {0,3}[-+*][ \t]+\S') { $hasBullet = $true }
        }
    }
    if ($matchesFound -ne 1 -or -not $hasBullet) {
        throw 'The tagged changelog requires exactly one dated release section with at least one change.'
    }
    return ($section -join "`n").Trim()
}

$version = Get-OpsshReleaseVersion -Tag $Tag
if ($TagObject -cnotmatch '\A(?!0{40}\z)[a-f0-9]{40}\z' -or $SourceCommit -cnotmatch '\A(?!0{40}\z)[a-f0-9]{40}\z' -or
    $TagObject -ceq $SourceCommit) {
    throw 'Invalid tag object or source commit identity.'
}
$packagePath = [IO.Path]::GetFullPath($PackageDirectory, $PWD.ProviderPath)
$manifestPath = Join-Path $packagePath 'manifest.json'
$notesPath = Join-Path $packagePath 'notes.md'
if ([IO.File]::GetAttributes($packagePath) -band [IO.FileAttributes]::ReparsePoint -or
    (Test-Path -LiteralPath $manifestPath) -or (Test-Path -LiteralPath $notesPath)) {
    throw 'Choose a fresh validated package directory.'
}
$encoding = [Text.UTF8Encoding]::new($false, $true)
$changelogFile = [IO.File]::OpenRead([IO.Path]::GetFullPath($ChangelogPath, $PWD.ProviderPath))
try {
    if ($changelogFile.Length -gt 1MB) { throw 'The changelog exceeds the release notes input limit.' }
    $bytes = [byte[]]::new([int]$changelogFile.Length)
    $changelogFile.ReadExactly($bytes)
    $changes = Get-ReleaseChanges ($encoding.GetString($bytes).TrimStart([char]0xfeff)) $version
} finally {
    $changelogFile.Dispose()
}
if ($encoding.GetByteCount($changes) -gt 65536 -or $changes.Contains([char]0) -or
    $changes.Contains('<!-- opssh-release-provenance')) {
    throw 'The extracted release notes exceed limits or contain reserved content.'
}

$name = "opssh-$version-windows-x64.msi"
foreach ($item in Get-ChildItem -LiteralPath $packagePath -Force) {
    if ($item.Name -cnotin @($name, 'payload-manifest.json') -or $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Expected only the verified MSI and optional payload manifest in the package directory.'
    }
}
$stream = [IO.File]::OpenRead((Join-Path $packagePath $name))
try {
    if ($stream.Length -le 0 -or $stream.Length -gt 1GB) { throw 'Invalid validated MSI size.' }
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
    $asset = @{ name = $name; size = $stream.Length; sha256 = $digest }
} finally {
    $stream.Dispose()
}
$manifest = [ordered]@{ schema = 1; tag = $Tag; tagObject = $TagObject; sourceCommit = $SourceCommit; assets = @($asset) }
[IO.File]::WriteAllText($notesPath, $changes, $encoding)
[IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 5), $encoding)
