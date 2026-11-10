[CmdletBinding()]
param([string]$ReleaseDirectory)

$ErrorActionPreference = 'Stop'
if ($ReleaseDirectory) { $ReleaseDirectory = (Get-Item -LiteralPath $ReleaseDirectory).FullName }
$root = Split-Path -Parent $PSScriptRoot
$licenseRoot = Join-Path $root 'third_party/licenses'
$manifestPath = Join-Path $licenseRoot 'manifest.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.schema -ne 1) { throw 'Unexpected distribution notice manifest schema.' }
if ((Get-FileHash -LiteralPath (Join-Path $root 'LICENSE') -Algorithm SHA256).Hash -ine
    'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30') {
    throw 'The first-party license differs from the canonical Apache-2.0 text.'
}
$notices = Get-Content -Raw -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md')
if ([string]::IsNullOrWhiteSpace($manifest.wix.source) -or !$notices.Contains($manifest.wix.source) -or
    !$notices.Contains('third_party/licenses/wix/LICENSE.TXT')) {
    throw 'The distribution notices omit the WiX license or its pinned corresponding source.'
}

$files = @{}
$directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    if ($entry.path -cnotmatch '^(go|nuget|wix)/[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)?$' -or
        $entry.path -match '(^|/)\.\.?(/|$)' -or $files.ContainsKey($entry.path)) {
        throw 'Invalid distribution notice path.'
    }
    $file = Get-Item -LiteralPath (Join-Path $licenseRoot $entry.path) -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $file.Length -ne $entry.size -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $entry.sha256) {
        throw "Distribution notice differs from the upstream record: $($entry.path)"
    }
    $files[$entry.path] = $true
    [void]$directories.Add($entry.path.Substring(0, $entry.path.LastIndexOf('/')))
}
$actualFiles = @(Get-ChildItem -LiteralPath $licenseRoot -File -Recurse | Where-Object FullName -ine $manifestPath)
if ($actualFiles.Count -ne $files.Count -or !$files.Count) { throw 'Distribution notice inventory changed.' }

function Get-ExpectedVersions($Entries, [string]$Prefix) {
    $versions = @{}
    foreach ($entry in $Entries) {
        if (!$entry.name -or !$entry.version -or $versions.ContainsKey($entry.name) -or
            !$entry.directory.StartsWith("$Prefix/", [StringComparison]::Ordinal) -or !$directories.Remove($entry.directory)) {
            throw 'Invalid or unbound dependency notice entry.'
        }
        $versions[$entry.name] = $entry.version
    }
    $versions
}

$goExpected = Get-ExpectedVersions $manifest.go 'go'
$nugetExpected = Get-ExpectedVersions $manifest.nuget 'nuget'
if (!$directories.Remove('wix') -or $directories.Count -or $manifest.wix.version -cne '7.0.1' -or
    $manifest.wix.source -cne 'https://github.com/wixtoolset/wix/archive/642f1a0f7c857ffa4fc9fa3f9d1433aab895dd29.zip' -or
    $manifest.wix.sourceSize -le 0 -or $manifest.wix.sourceSha256 -cnotmatch '\A[0-9a-f]{64}\z') {
    throw 'Unbound distribution notices or incorrect WiX source.'
}
$buildEnvironment = @{ GOOS = 'windows'; GOARCH = 'amd64'; CGO_ENABLED = '0'; GOWORK = 'off'; GOTOOLCHAIN = 'local' }
$priorEnvironment = @{}
Push-Location -LiteralPath $root
try {
    foreach ($name in $buildEnvironment.Keys) {
        $priorEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $buildEnvironment[$name])
    }
    $selected = @(go list -mod=readonly -deps -f '{{if .Module}}{{.Module.Path}}{{end}}' ./cmd/opssh-core |
        Where-Object { $_ -and $_ -ne 'opssh' } | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0 -or !$selected.Count) { throw 'Could not identify production Go dependencies.' }
    $versions = @(go list -m -f '{{.Path}}|{{.Version}}' @selected)
    if ($LASTEXITCODE -ne 0 -or $versions.Count -ne $selected.Count) { throw 'Could not resolve production Go versions.' }
    foreach ($line in $versions) {
        $name, $version = $line -split '\|', 2
        if ($goExpected[$name] -cne $version) { throw "Missing notice for the production Go dependency $name $version." }
        $goExpected.Remove($name)
    }
    $wasm = $manifest.onePasswordWasm
    if ($wasm.module -cne 'github.com/1password/onepassword-sdk-go' -or $wasm.moduleVersion -cnotmatch '\Av[0-9]+\.[0-9]+\.[0-9]+\z' -or
        $wasm.build -cnotmatch '\A[0-9]+\z' -or $wasm.size -le 0 -or $wasm.sha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
        $wasm.upstreamRevision -cnotmatch '\A[a-f0-9]{40}\z') {
        throw 'Invalid 1Password SDK WASM notice provenance.'
    }
    $sdkJson = @(go list -m -json $wasm.module)
    if ($LASTEXITCODE -ne 0) { throw 'Could not locate the pinned 1Password SDK.' }
    $sdk = ($sdkJson -join "`n") | ConvertFrom-Json
    if ($sdk.Version -cne $wasm.moduleVersion -or !$sdk.Dir -or $sdk.Replace) {
        throw 'The 1Password SDK differs from the WASM notice provenance.'
    }
    $wasmFile = Get-Item -LiteralPath (Join-Path $sdk.Dir 'internal/wasm/core.wasm')
    $wasmBuild = (Get-Content -Raw -LiteralPath (Join-Path $sdk.Dir 'internal/release/version-build')).Trim()
    if ($wasmFile.Length -ne $wasm.size -or $wasmBuild -cne $wasm.build -or
        (Get-FileHash -LiteralPath $wasmFile.FullName -Algorithm SHA256).Hash -ine $wasm.sha256) {
        throw 'The embedded WASM differs from the notice inventory.'
    }
    $goVersion = go env GOVERSION
    if ($LASTEXITCODE -ne 0 -or $goExpected.Count -ne 1 -or $goExpected['Go standard library'] -cne $goVersion) {
        throw 'The Go standard library or its notice inventory changed.'
    }
    if ($ReleaseDirectory) {
        $core = Get-Item -LiteralPath (Join-Path $ReleaseDirectory 'opssh-core.exe')
        $builtJson = @(go version -m -json $core.FullName)
        if ($LASTEXITCODE -ne 0) { throw 'Could not read the built core dependency inventory.' }
        $built = ($builtJson -join "`n") | ConvertFrom-Json
        $settings = @{}
        foreach ($entry in $built.Settings) { $settings[$entry.Key] = $entry.Value }
        $builtVersions = @($built.Deps | ForEach-Object {
            if ($_.Replace) { throw 'The built core uses a replacement dependency.' }
            "$($_.Path)|$($_.Version)"
        })
        if ($built.GoVersion -cne $goVersion -or $built.Path -cne 'opssh/cmd/opssh-core' -or
            $settings.GOOS -cne 'windows' -or $settings.GOARCH -cne 'amd64' -or $settings.CGO_ENABLED -cne '0' -or
            (Compare-Object $versions $builtVersions -CaseSensitive)) {
            throw 'The built core dependency inventory differs from the Windows source graph.'
        }
    }
} finally {
    Pop-Location
    foreach ($name in $priorEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $priorEnvironment[$name])
    }
}

$usedNuget = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$lock = Get-Content -Raw -LiteralPath (Join-Path $root 'tray/Opssh.Tray/packages.lock.json') | ConvertFrom-Json -AsHashtable
foreach ($target in $lock.dependencies.Values) {
    foreach ($package in $target.GetEnumerator()) {
        if ($package.Value.resolved -and $package.Key -notlike 'Microsoft.Windows.SDK.BuildTools*') {
            if ($nugetExpected[$package.Key] -cne $package.Value.resolved) {
                throw "Missing notice for the production NuGet graph package $($package.Key)."
            }
            [void]$usedNuget.Add($package.Key)
        }
    }
}
if ($ReleaseDirectory) {
    $deps = Get-Content -Raw -LiteralPath (Join-Path $ReleaseDirectory 'opssh-tray.deps.json') | ConvertFrom-Json -AsHashtable
    $tray = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $ReleaseDirectory 'opssh-tray.dll'))
    $project = "opssh-tray/$($tray.Version.ToString(3))"
    $target = $deps.runtimeTarget.name
    if ($target -cnotmatch '\A\.NETCoreApp,Version=v[0-9]+\.[0-9]+/win-x64\z' -or
        !$deps.libraries.Count -or !$deps.targets -or !$deps.targets[$target] -or
        !$deps.targets[$target][$project] -or $deps.libraries[$project].type -cne 'project') {
        throw 'The current Release has no matching Windows x64 tray dependency inventory.'
    }
    foreach ($item in $deps.libraries.Keys) {
        $name, $version = $item -split '/', 2
        if ($name -eq 'opssh-tray') { continue }
        $name = $name -replace '^runtimepack\.', ''
        if ($name -eq 'Microsoft.Web.WebView2.Core.Projection') { $name = 'Microsoft.Web.WebView2' }
        if ($nugetExpected[$name] -cne $version) {
            throw "Missing notice for the built Windows runtime dependency $name $version."
        }
        [void]$usedNuget.Add($name)
    }
    if (!$usedNuget.SetEquals([string[]]@($nugetExpected.Keys))) {
        throw 'The NuGet notice inventory includes unused dependencies.'
    }
}
Write-Host "Verified $($files.Count) distribution notices and the Windows dependency inventory."
