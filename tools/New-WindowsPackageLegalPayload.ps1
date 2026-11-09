[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$WixSourceArchive
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -Raw -LiteralPath (Join-Path $root 'licenses/manifest.json') | ConvertFrom-Json
$wix = $manifest.wix
if ($wix.version -cne '7.0.1' -or
    $wix.source -cne 'https://github.com/wixtoolset/wix/archive/642f1a0f7c857ffa4fc9fa3f9d1433aab895dd29.zip' -or
    $wix.sourceSize -le 0 -or $wix.sourceSize -gt 20MB -or $wix.sourceSha256 -cnotmatch '\A[0-9a-f]{64}\z') {
    throw 'The corresponding WiX source pin is invalid.'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $PWD.ProviderPath)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh legal payload directory.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.md','licenses')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $OutputDirectory $name) -Recurse
}
$archive = Join-Path $OutputDirectory 'licenses/wix/wix-7.0.1-source.zip'
if ($WixSourceArchive) {
    Copy-Item -LiteralPath $WixSourceArchive -Destination $archive
} else {
    & curl.exe --fail --silent --show-error --location --max-redirs 2 --max-time 90 --proto '=https' --proto-redir '=https' `
        --max-filesize $wix.sourceSize --output $archive $wix.source
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the pinned corresponding WiX source.' }
}
if ((Get-Item -LiteralPath $archive).Length -ne $wix.sourceSize -or
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $wix.sourceSha256) {
    throw 'The corresponding WiX source differs from the pinned size or SHA-256.'
}
Write-Host 'Staged license notices and the pinned corresponding WiX source.'
