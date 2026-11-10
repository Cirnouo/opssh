#Requires -Version 7.4

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'OpsshRelease.psm1') -Force

Add-Type -TypeDefinition @'
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public sealed class FixtureReleaseHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond;

    public FixtureReleaseHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
    {
        this.respond = respond;
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return respond(request, token);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        return Task.FromResult(Send(request, token));
    }
}
'@

$tag = 'v2.3.4'
$tagObject = 'a' * 40
$source = 'b' * 40
$token = 'fixture-token-never-for-network.' + ('x' * 520) + '.signature'
$repository = 'fixture/opssh'
$root = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'opssh-release-fixture-' + [Guid]::NewGuid().ToString('N'))
$manifestPath = Join-Path $root 'manifest.json'
$notesPath = Join-Path $root 'notes.md'

function Assert-Fixture([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function New-FakeRelease([bool]$draft, [string]$body = $provenance) {
    return @{ id = 123; tag_name = $tag; name = $tag; target_commitish = $source; body = $body;
        draft = $draft; prerelease = $false; immutable = -not $draft }
}

function New-FakeAsset([string]$name, [long]$id) {
    $entry = $entries[$name]
    return @{ id = $id; name = $name; state = 'uploaded'; size = $entry.size;
        digest = 'sha256:' + $entry.sha256; content_type = 'application/octet-stream' }
}

function Send-FakeResponse([object]$value, [int]$status = 200) {
    $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]$status)
    $json = ConvertTo-Json -InputObject $value -Depth 10 -Compress
    $response.Content = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
    return $response
}

function Invoke-FakeReleaseRequest($request, $cancellation) {
    $method = $request.Method.Method
    $uri = $request.RequestUri
    Assert-Fixture ($uri.Scheme -ceq 'https' -and $uri.IsDefaultPort) 'HTTP request outside HTTPS.'
    Assert-Fixture ($request.Headers.Authorization.Scheme -ceq 'Bearer' -and
        $request.Headers.Authorization.Parameter -ceq $token) 'Missing scoped authorization.'
    $prefix = "/repos/$repository"
    Assert-Fixture ($uri.AbsolutePath.StartsWith($prefix + '/', [StringComparison]::Ordinal)) 'Request outside repository.'
    $route = $uri.AbsolutePath.Substring($prefix.Length)
    if ($state.Redirect) {
        $response = Send-FakeResponse @{ message = 'fixture-private-body' } 302
        $response.Headers.Location = [uri]'https://fixture.invalid/credential-sink'
        return $response
    }
    if ($state.OversizedResponse) {
        $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::OK)
        $response.Content = [Net.Http.ByteArrayContent]::new([byte[]]::new(8MB + 1))
        return $response
    }
    if ($method -eq 'GET' -and $uri.Host -ceq 'api.github.com') {
        switch -Regex ($route) {
            '^/git/ref/tags/v2\.3\.4$' {
                $state.RefReads++
                $sha = if ($state.RefReads -ge $state.MoveTagAt) { 'c' * 40 } else { $tagObject }
                return Send-FakeResponse @{ ref = "refs/tags/$tag"; object = @{ type = $state.RefType; sha = $sha } }
            }
            '^/git/tags/a{40}$' {
                $state.TagReads++
                return Send-FakeResponse @{ tag = $tag; sha = $tagObject;
                    object = @{ type = $state.TagTargetType; sha = $source };
                    verification = @{ verified = $state.Verified; signature = $state.Signature } }
            }
            '^/git/ref/heads/main$' {
                return Send-FakeResponse @{ ref = 'refs/heads/main'; object = @{ type = 'commit'; sha = 'd' * 40 } }
            }
            '^/compare/b{40}\.\.\.d{40}$' {
                $mergeBase = if ($state.Ancestor) { $source } else { 'e' * 40 }
                $comparison = if ($state.Ancestor) { 'ahead' } else { 'diverged' }
                return Send-FakeResponse @{ base_commit = @{ sha = $source }; merge_base_commit = @{ sha = $mergeBase };
                    status = $comparison }
            }
            '^/actions/workflows/([a-z-]+\.yml)/runs$' {
                $workflow = $Matches[1]
                Assert-Fixture ($state.MainRuns.ContainsKey($workflow)) 'Unexpected main workflow query.'
                Assert-Fixture ($uri.Query.Contains("head_sha=$source") -and $uri.Query.Contains('event=push') -and
                    $uri.Query.Contains('branch=main')) 'Workflow run query is not scoped to the exact main push.'
                $runs = @($state.MainRuns[$workflow] | Where-Object { $null -ne $_ })
                return Send-FakeResponse @{ total_count = $runs.Count; workflow_runs = $runs }
            }
            '^/releases$' {
                $state.ReleaseListReads++
                return Send-FakeResponse @($state.Release, $state.OtherRelease | Where-Object { $null -ne $_ })
            }
            '^/releases/latest$' {
                if ($null -eq $state.LatestRelease) { return Send-FakeResponse @{ message = 'Not Found' } 404 }
                return Send-FakeResponse $state.LatestRelease
            }
            '^/releases/123$' { return Send-FakeResponse $state.Release }
            '^/releases/123/assets$' { return Send-FakeResponse ($state.Assets.ToArray()) }
        }
    }
    if ($method -eq 'POST' -and $route -ceq '/releases' -and $uri.Host -ceq 'api.github.com') {
        $state.Writes.Add('create')
        $body = $request.Content.ReadAsStringAsync($cancellation).GetAwaiter().GetResult() | ConvertFrom-Json -AsHashtable
        Assert-Fixture ($null -eq $state.Release -and $body.draft -ceq $true -and $body.prerelease -ceq $false -and
            $body.tag_name -ceq $tag -and $body.name -ceq $tag -and !$body.ContainsKey('target_commitish') -and
            $body.body.StartsWith($provenance, [StringComparison]::Ordinal)) 'Unexpected draft fields.'
        $state.Release = New-FakeRelease $true $body.body
        return Send-FakeResponse $state.Release 201
    }
    if ($method -eq 'POST' -and $route -ceq '/releases/123/assets' -and $uri.Host -ceq 'uploads.github.com') {
        $name = [uri]::UnescapeDataString($uri.Query.Substring(6))
        $state.Writes.Add("upload $name")
        Assert-Fixture ($uri.Query.StartsWith('?name=') -and $entries.ContainsKey($name) -and $state.Release.draft) 'Unexpected upload route or state.'
        Assert-Fixture ($request.Content -is [Net.Http.StreamContent] -and
            $request.Content.Headers.ContentLength -eq $entries[$name].size) 'Asset was not uploaded as a length-bounded stream.'
        $data = $request.Content.ReadAsByteArrayAsync($cancellation).GetAwaiter().GetResult()
        $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$data)).ToLowerInvariant()
        Assert-Fixture ($actual -ceq $entries[$name].sha256) 'Upload used different bytes.'
        if ($name -ceq $state.FailUpload) {
            return Send-FakeResponse @{ message = "fixture-private-body $token" } 502
        }
        $asset = New-FakeAsset $name ($state.Assets.Count + 1)
        $state.Assets.Add($asset)
        if ($state.BadUploadDigest) { $asset.digest = 'sha256:' + ('f' * 64) }
        return Send-FakeResponse $asset 201
    }
    if ($method -eq 'PATCH' -and $route -ceq '/releases/123' -and $uri.Host -ceq 'api.github.com') {
        $state.Writes.Add('publish')
        $body = $request.Content.ReadAsStringAsync($cancellation).GetAwaiter().GetResult() | ConvertFrom-Json -AsHashtable
        Assert-Fixture ($state.Release.draft -and $state.Assets.Count -eq 1 -and $body.draft -ceq $false -and
            $body.prerelease -ceq $false -and $body.make_latest -ceq 'legacy') 'Published before staging the MSI or with wrong classification.'
        if ($state.FailPublish) { return Send-FakeResponse @{ message = 'fixture-private-body' } 502 }
        $state.Release = New-FakeRelease $false $body.body
        if ($state.MutablePublication) { $state.Release.immutable = $false }
        return Send-FakeResponse $state.Release
    }
    throw 'Unexpected fixture request.'
}

function Reset-Fixture {
    $script:state = @{ RefType = 'tag'; TagTargetType = 'commit'; Verified = $true;
        Signature = "-----BEGIN SSH SIGNATURE-----`nZml4dHVyZQ==`n-----END SSH SIGNATURE-----`n";
        RefReads = 0; TagReads = 0; ReleaseListReads = 0; MoveTagAt = [int]::MaxValue; Redirect = $false;
        OversizedResponse = $false; Cancellation = [Threading.CancellationToken]::None;
        Release = $null; OtherRelease = $null; LatestRelease = $null; Assets = [Collections.Generic.List[object]]::new();
        Writes = [Collections.Generic.List[string]]::new(); FailUpload = ''; BadUploadDigest = $false;
        FailPublish = $false; MutablePublication = $false; Ancestor = $true; MainRuns = @{} }
    $runId = 61
    foreach ($workflow in 'windows.yml', 'commit-subjects.yml', 'dependency-audit.yml', 'codeql.yml') {
        $state.MainRuns[$workflow] = @{ id = $runId++; run_attempt = 2; event = 'push'; status = 'completed'; conclusion = 'success';
            head_branch = 'main'; head_sha = $source; path = ".github/workflows/$workflow";
            repository = @{ id = 321; full_name = $repository }; head_repository = @{ id = 321; full_name = $repository } }
    }
}

function New-FixtureHandler {
    $callback = [Func[Net.Http.HttpRequestMessage, Threading.CancellationToken, Net.Http.HttpResponseMessage]]{
        param($request, $cancellation)
        try { return Invoke-FakeReleaseRequest $request $cancellation }
        catch { $script:fixtureFailure = $_; throw }
    }
    $script:fixtureFailure = $null
    return [FixtureReleaseHandler]::new($callback)
}

function Invoke-FixturePublisher([string]$tagArgument = $tag) {
    $handler = New-FixtureHandler
    try {
        Invoke-OpsshRelease -Repository $repository -Tag $tagArgument -TagObject $tagObject -SourceCommit $source `
            -ManifestPath $manifestPath -ManifestSha256 $manifestSha256 -NotesPath $notesPath -NotesSha256 $notesSha256 `
            -Token $token -Handler $handler -CancellationToken $state.Cancellation
    } finally {
        $handler.Dispose()
        if ($null -ne $script:fixtureFailure) { throw $script:fixtureFailure }
    }
}

function Invoke-FixtureAdmission($event) {
    $eventPath = Join-Path $root 'push.json'
    $event | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $eventPath -Encoding utf8
    $handler = New-FixtureHandler
    try {
        Invoke-OpsshReleaseAdmission -Repository $repository -RepositoryId 321 -EventName push -EventRef "refs/tags/$tag" `
            -EventSha $source -EventPath $eventPath -CheckoutHead $source -LocalTagObject $tagObject `
            -LocalTagCommit $source -Token $token -Handler $handler
    } finally {
        $handler.Dispose()
        if ($null -ne $script:fixtureFailure) { throw $script:fixtureFailure }
    }
}

function Assert-PublisherFails([string]$message, [string]$tagArgument = $tag) {
    try { $null = Invoke-FixturePublisher $tagArgument }
    catch {
        Assert-Fixture ($_.Exception.Message -like $message) 'Publisher failed at an unexpected stage.'
        Assert-Fixture ($_.Exception.Message -notmatch 'fixture-private-body|fixture-token|credential-sink') 'Sensitive response data escaped.'
        return
    }
    throw 'Publisher accepted a failing fixture.'
}

$null = [IO.Directory]::CreateDirectory($root)
try {
    $entries = @{}
    $name = 'opssh-2.3.4-windows-x64.msi'
    $file = Join-Path $root $name
    [IO.File]::WriteAllText($file, 'controlled fixture bytes for msi')
    $entries[$name] = @{ name = $name; size = (Get-Item -LiteralPath $file).Length;
        sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
    $manifest = @{ schema = 1; tag = $tag; tagObject = $tagObject; sourceCommit = $source; assets = @($entries.Values) }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    [IO.File]::WriteAllText($notesPath, "## Changes`n`n- Fixture release notes.")
    $manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    $notesSha256 = (Get-FileHash -LiteralPath $notesPath -Algorithm SHA256).Hash
    $originalManifest = [IO.File]::ReadAllBytes($manifestPath)
    try {
        $legacyAsset = $entries[$name].Clone()
        $legacyAsset.name = 'opssh-2.3.4-windows-x64.exe'
        $manifest.assets += $legacyAsset
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
        $manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        Reset-Fixture
        Assert-PublisherFails '*manifest*'
        Assert-Fixture ($state.RefReads -eq 0 -and $state.Writes.Count -eq 0) 'A multi-asset manifest reached GitHub.'
    } finally {
        [IO.File]::WriteAllBytes($manifestPath, $originalManifest)
        $manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    }
    $manifestDigest = $manifestSha256.ToLowerInvariant()
    $notesDigest = $notesSha256.ToLowerInvariant()
    $provenance = "<!-- opssh-release-provenance v1 tag-object=$tagObject source-commit=$source manifest-sha256=$manifestDigest notes-sha256=$notesDigest -->"

    Reset-Fixture
    $result = Invoke-FixturePublisher
    Assert-Fixture ($result.status -ceq 'published' -and $result.releaseId -eq 123 -and $result.assets.Count -eq 1) 'Missing publication evidence.'
    Assert-Fixture ($state.Writes.Count -eq 3 -and $state.Writes[0] -ceq 'create' -and
        $state.Writes[1] -ceq "upload $name" -and $state.Writes[2] -ceq 'publish' -and
        $state.RefReads -ge 5 -and $state.TagReads -eq $state.RefReads) 'Missing repeated tag check or unexpected mutation sequence.'
    foreach ($entry in $entries.Values) {
        Assert-Fixture ($state.Release.body.Contains($entry.name) -and $state.Release.body.Contains($entry.sha256)) 'Release notes omit asset checksum.'
    }
    Assert-Fixture ($state.Release.body.Contains('The MSI and first-party Windows binaries are unsigned.') -and
        $state.Release.body.Contains('https://github.com/wixtoolset/wix/archive/642f1a0f7c857ffa4fc9fa3f9d1433aab895dd29.zip')) 'Release notes omit unsigned status or corresponding WiX source.'
    Assert-Fixture ($state.Release.body.Contains('https://github.com/fixture/opssh/commits/v2.3.4')) 'Initial release notes omit current tag history.'

    $state.Writes.Clear()
    $state.RefReads = 0
    Assert-Fixture ((Invoke-FixturePublisher).status -ceq 'skipped' -and $state.Writes.Count -eq 0 -and $state.RefReads -ge 2) 'Matching immutable release did not skip without writes.'

    Reset-Fixture
    $state.LatestRelease = @{ id = 100; tag_name = 'v2.3.3'; draft = $false; prerelease = $false }
    Assert-Fixture ((Invoke-FixturePublisher).status -ceq 'published' -and
        $state.Release.body.Contains('https://github.com/fixture/opssh/compare/v2.3.3...v2.3.4')) 'Release notes omit stable baseline comparison.'
    Reset-Fixture
    $state.LatestRelease = @{ id = 100; tag_name = 'v2.3.5'; draft = $false; prerelease = $false }
    Assert-PublisherFails '*baseline*'
    Assert-Fixture ($state.Writes.Count -eq 0) 'Older version mutated publication state.'

    Reset-Fixture
    $state.FailUpload = $name
    Assert-PublisherFails '*GitHub request failed (502)*'
    Assert-Fixture ($state.Release.draft -and $state.Assets.Count -eq 0 -and $state.Writes.Count -eq 2) 'Upload failure did not leave a resumable draft.'
    $state.FailUpload = ''
    $state.Writes.Clear()
    Assert-Fixture ((Invoke-FixturePublisher).status -ceq 'published' -and $state.Writes.Count -eq 2 -and
        $state.Writes[0] -ceq "upload $name" -and $state.Writes[1] -ceq 'publish') 'Rerun did not finish the existing draft.'

    Reset-Fixture
    $state.FailPublish = $true
    Assert-PublisherFails '*GitHub request failed (502)*'
    Assert-Fixture ($state.Release.draft -and $state.Assets.Count -eq 1) 'Publication failure did not preserve the staged MSI.'
    $state.FailPublish = $false
    $state.Writes.Clear()
    Assert-Fixture ((Invoke-FixturePublisher).status -ceq 'published' -and $state.Writes.Count -eq 1 -and
        $state.Writes[0] -ceq 'publish') 'Rerun did not reuse the previously uploaded MSI.'

    $state.Assets.Add(@{ name = 'opssh-2.3.4-windows-x64.zip' })
    $state.Writes.Clear()
    Assert-PublisherFails '*asset*'
    Assert-Fixture ($state.Writes.Count -eq 0) 'Publisher accepted a release with an extra asset.'
    $state.Assets.RemoveAt(1)

    $state.Writes.Clear()
    $state.Assets[0].size++
    Assert-PublisherFails '*asset*'
    Assert-Fixture ($state.Writes.Count -eq 0) 'Publisher attempted to repair an immutable asset.'

    Reset-Fixture
    $state.Release = New-FakeRelease $true 'a human-created draft with unrelated provenance'
    Assert-PublisherFails '*provenance*'
    Assert-Fixture ($state.Writes.Count -eq 0) 'Publisher mutated an unrelated draft.'

    Reset-Fixture
    $state.Release = New-FakeRelease $true ($provenance.Replace("notes-sha256=$notesDigest", ('notes-sha256=' + ('0' * 64))))
    Assert-PublisherFails '*provenance*'
    Assert-Fixture ($state.Writes.Count -eq 0) 'Publisher resumed an empty draft prepared with different notes.'

    Reset-Fixture
    $state.Release = New-FakeRelease $true ($provenance.Replace("manifest-sha256=$manifestDigest", ('manifest-sha256=' + ('0' * 64))))
    $state.Assets.Add((New-FakeAsset 'opssh-2.3.4-windows-x64.msi' 1))
    Assert-PublisherFails '*provenance*'
    Assert-Fixture ($state.Writes.Count -eq 0 -and $state.Assets.Count -eq 1) 'Publisher resumed a staged draft prepared with a different manifest.'

    Reset-Fixture
    $state.Release = New-FakeRelease $true
    $asset = New-FakeAsset 'opssh-2.3.4-windows-x64.msi' 1
    $asset.state = 'starter'
    $state.Assets.Add($asset)
    Assert-PublisherFails '*asset*'
    Assert-Fixture ($state.Writes.Count -eq 0) 'Publisher attempted to replace a conflicting draft asset.'

    Reset-Fixture
    $state.BadUploadDigest = $true
    Assert-PublisherFails '*asset*'
    Assert-Fixture ($state.Writes.Count -eq 2 -and $state.Release.draft) 'Digest mismatch did not stop staging.'

    Reset-Fixture
    $state.MoveTagAt = 3
    Assert-PublisherFails '*remote tag*'
    Assert-Fixture ($state.Writes.Count -eq 1 -and $state.Writes[0] -ceq 'create') 'Publisher failed to stop when the tag moved before upload.'

    Reset-Fixture
    $state.MutablePublication = $true
    Assert-PublisherFails '*classification*'
    Assert-Fixture ($state.Writes.Count -eq 3 -and -not $state.Release.draft) 'Fixture did not reach publication readback.'

    foreach ($badTag in 'v02.3.4', 'V2.3.4', 'v256.0.0', 'v2.256.0', 'v2.3.65535', 'v2.3.4-rc.1', 'v2.3.4 ') {
        Reset-Fixture
        Assert-PublisherFails '*canonical stable tag*' $badTag
        Assert-Fixture ($state.RefReads -eq 0 -and $state.Writes.Count -eq 0) 'Invalid version reached GitHub.'
    }
    Assert-Fixture ((Get-OpsshReleaseVersion -Tag 'v255.255.65534').ToString() -ceq '255.255.65534') 'Maximum supported version was rejected.'

    foreach ($badTagShape in 'lightweight', 'nested', 'unverified', 'non-SSH', 'array-type') {
        Reset-Fixture
        switch ($badTagShape) {
            'lightweight' { $state.RefType = 'commit' }
            'nested' { $state.TagTargetType = 'tag' }
            'unverified' { $state.Verified = $false }
            'non-SSH' { $state.Signature = $state.Signature.Replace('SSH SIGNATURE', 'PGP SIGNATURE') }
            'array-type' { $state.RefType = @('tag') }
        }
        Assert-PublisherFails '*tag*'
        Assert-Fixture ($state.Writes.Count -eq 0 -and $state.ReleaseListReads -eq 0) 'Invalid remote tag reached publication.'
    }

    Reset-Fixture
    $state.Redirect = $true
    Assert-PublisherFails '*GitHub request failed (302)*'
    Assert-Fixture ($state.Writes.Count -eq 0) 'Unexpected redirect reached publication.'

    Reset-Fixture
    $state.OversizedResponse = $true
    Assert-PublisherFails '*byte limit*'
    Assert-Fixture ($state.Writes.Count -eq 0) 'Oversized response reached publication.'

    Reset-Fixture
    $cancellation = [Threading.CancellationTokenSource]::new()
    try {
        $state.Cancellation = $cancellation.Token
        $cancellation.Cancel()
        Assert-PublisherFails '*canceled*'
        Assert-Fixture ($state.RefReads -eq 0 -and $state.Writes.Count -eq 0) 'Canceled publication contacted GitHub.'
    } finally {
        $cancellation.Dispose()
    }

    foreach ($inputPath in $manifestPath, $notesPath) {
        Reset-Fixture
        $original = [IO.File]::ReadAllBytes($inputPath)
        try {
            [IO.File]::AppendAllText($inputPath, 'changed')
            Assert-PublisherFails '*digest differs from the verified handoff*'
            Assert-Fixture ($state.RefReads -eq 0 -and $state.Writes.Count -eq 0) 'Changed handoff metadata reached GitHub.'
        } finally {
            [IO.File]::WriteAllBytes($inputPath, $original)
        }
    }

    Reset-Fixture
    [IO.File]::AppendAllText((Join-Path $root 'opssh-2.3.4-windows-x64.msi'), 'changed')
    Assert-PublisherFails '*manifest*'
    Assert-Fixture ($state.RefReads -eq 0 -and $state.Writes.Count -eq 0) 'Changed local asset reached GitHub.'

    $event = @{ ref = "refs/tags/$tag"; before = '0' * 40; after = $tagObject; head_commit = @{ id = $source };
        created = $true; deleted = $false; forced = $false; repository = @{ id = 321; full_name = $repository } }
    Reset-Fixture
    $admission = Invoke-FixtureAdmission $event
    $mainEvidence = ($admission.mainRuns | ForEach-Object { "$($_.workflow)=$($_.id)/$($_.attempt)" }) -join ','
    Assert-Fixture ($admission.tagObject -ceq $tagObject -and $admission.sourceCommit -ceq $source -and $state.Writes.Count -eq 0 -and
        $mainEvidence -ceq 'windows.yml=61/2,commit-subjects.yml=62/2,dependency-audit.yml=63/2,codeql.yml=64/2') 'Exact source was not admitted with all four read-only run identities.'

    foreach ($scenario in 'peeled-after', 'other-tag-object', 'other-event-commit', 'forced', 'other-event-repo',
        'other-main-sha', 'other-main-repo', 'pr-run', 'failed-run',
        'wrong-workflow', 'not-ancestor', 'missing-subjects', 'missing-codeql', 'failed-codeql', 'missing-audit', 'failed-audit') {
        Reset-Fixture
        $bad = $event.Clone()
        $windowsRun = $state.MainRuns['windows.yml']
        $expectedFailure = '*main*'
        switch ($scenario) {
            'peeled-after' { $bad.after = $source; $expectedFailure = '*tag push*' }
            'other-tag-object' { $bad.after = 'c' * 40; $expectedFailure = '*tag push*' }
            'other-event-commit' { $bad.head_commit = @{ id = 'c' * 40 }; $expectedFailure = '*tag push*' }
            'forced' { $bad.forced = $true; $expectedFailure = '*tag push*' }
            'other-event-repo' { $bad.repository = @{ id = 322; full_name = $repository }; $expectedFailure = '*tag push*' }
            'other-main-sha' { $windowsRun.head_sha = 'c' * 40 }
            'other-main-repo' { $windowsRun.head_repository.id = 322 }
            'pr-run' { $windowsRun.event = 'pull_request' }
            'failed-run' { $windowsRun.conclusion = 'failure' }
            'wrong-workflow' { $windowsRun.path = '.github/workflows/other.yml' }
            'not-ancestor' { $state.Ancestor = $false }
            'missing-subjects' { $state.MainRuns['commit-subjects.yml'] = $null; $expectedFailure = '*commit-subjects.yml run on main*' }
            'missing-codeql' { $state.MainRuns['codeql.yml'] = $null; $expectedFailure = '*codeql.yml run on main*' }
            'failed-codeql' { $state.MainRuns['codeql.yml'].conclusion = 'failure'; $expectedFailure = '*codeql.yml run on main*' }
            'missing-audit' { $state.MainRuns['dependency-audit.yml'] = $null; $expectedFailure = '*dependency-audit.yml run on main*' }
            'failed-audit' { $state.MainRuns['dependency-audit.yml'].conclusion = 'failure'; $expectedFailure = '*dependency-audit.yml run on main*' }
        }
        $failure = $null
        try { $null = Invoke-FixtureAdmission $bad } catch { $failure = $_.Exception.Message }
        Assert-Fixture ($failure -like $expectedFailure) "Admission accepted or failed at the wrong stage for $scenario."
        Assert-Fixture ($state.Writes.Count -eq 0) 'Read-only admission issued a mutation.'
    }

    $changelogPath = Join-Path $root 'CHANGELOG.md'
    $changelog = @'
# Changelog

## [Unreleased]

~~~markdown
## [2.3.4] - 2026-11-08
- Fenced decoy.
~~~

## [2.3.4] - 2026-11-09

### Added

- Fixture release.

## [2.3.3] - 2026-11-06

- Older release.
'@
    [IO.File]::WriteAllText($changelogPath, $changelog)
    $assetDirectory = Join-Path $root 'artifact'
    $null = [IO.Directory]::CreateDirectory($assetDirectory)
    foreach ($entry in $entries.Values) {
        [IO.File]::WriteAllText((Join-Path $assetDirectory $entry.name), "controlled fixture bytes for $([IO.Path]::GetExtension($entry.name).Substring(1))")
    }
    & (Join-Path $PSScriptRoot 'New-WindowsReleaseInputs.ps1') -Tag $tag -TagObject $tagObject -SourceCommit $source `
        -PackageDirectory $assetDirectory -ChangelogPath $changelogPath
    $generated = Get-Content -LiteralPath (Join-Path $assetDirectory 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    $releaseNotes = [IO.File]::ReadAllText((Join-Path $assetDirectory 'notes.md'))
    Assert-Fixture ($generated.schema -eq 1 -and $generated.tagObject -ceq $tagObject -and
        $generated.sourceCommit -ceq $source -and $generated.assets.Count -eq 1) 'Generated handoff is missing source or package evidence.'
    foreach ($asset in $generated.assets) {
        Assert-Fixture ($asset.sha256 -ceq $entries[$asset.name].sha256 -and $asset.size -eq $entries[$asset.name].size) 'Generated handoff changed package bytes.'
    }
    Assert-Fixture ($releaseNotes.Contains('Fixture release.') -and $releaseNotes -notmatch 'Fenced decoy|Older release') 'Generated release notes used the wrong changelog section.'

    [IO.File]::Delete((Join-Path $assetDirectory 'manifest.json'))
    [IO.File]::Delete((Join-Path $assetDirectory 'notes.md'))
    $legacyPath = Join-Path $assetDirectory 'opssh-2.3.4-windows-x64.exe'
    [IO.File]::WriteAllText($legacyPath, 'obsolete fixture asset')
    try {
        $failure = $null
        try {
            & (Join-Path $PSScriptRoot 'New-WindowsReleaseInputs.ps1') -Tag $tag -TagObject $tagObject -SourceCommit $source `
                -PackageDirectory $assetDirectory -ChangelogPath $changelogPath
        } catch { $failure = $_.Exception.Message }
        Assert-Fixture ($failure -like '*only the verified MSI*' -and
            -not (Test-Path -LiteralPath (Join-Path $assetDirectory 'manifest.json'))) 'An extra package produced a release handoff.'
    } finally {
        [IO.File]::Delete($legacyPath)
    }
    foreach ($badChangelog in @(
        "## [2.3.4] - 2026-02-30`n`n- Invalid date.",
        "## [2.3.4] - 2026-11-09`n`n- First.`n`n## [2.3.4] - 2026-11-10`n`n- Duplicate.",
        "## [2.3.4] - 2026-11-09`n`n~~~markdown`n- Fenced change only.`n~~~"
    )) {
        [IO.File]::WriteAllText($changelogPath, $badChangelog)
        $failure = $null
        try {
            & (Join-Path $PSScriptRoot 'New-WindowsReleaseInputs.ps1') -Tag $tag -TagObject $tagObject -SourceCommit $source `
                -PackageDirectory $assetDirectory -ChangelogPath $changelogPath
        } catch { $failure = $_.Exception.Message }
        Assert-Fixture ($failure -like '*changelog*' -and -not (Test-Path -LiteralPath (Join-Path $assetDirectory 'manifest.json'))) 'Invalid changelog produced a release handoff.'
    }

    $noticePath = Join-Path $root 'notices.json'
    $notice = @{ schema = 1; onePasswordWasm = @{ sha256 = ('a' * 64); attribution = $null } }
    foreach ($review in @($null, @{ wasmSha256 = ('b' * 64); basis = 'upstream-notices'; evidence = 'https://example.invalid/review' })) {
        $notice.onePasswordWasm.attribution = $review
        $notice | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $noticePath -Encoding utf8
        $failure = $null
        try { Assert-OpsshReleaseNoticeReadiness -ManifestPath $noticePath }
        catch { $failure = $_.Exception.Message }
        Assert-Fixture ($failure -like '*exact 1Password WASM digest*') 'Missing or mismatched embedded attribution evidence was accepted.'
    }
    $notice.onePasswordWasm.attribution = @{
        wasmSha256 = $notice.onePasswordWasm.sha256; basis = 'confirmed-redistribution'; evidence = 'https://example.invalid/review'
    }
    $notice | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $noticePath -Encoding utf8
    Assert-OpsshReleaseNoticeReadiness -ManifestPath $noticePath

    Write-Output 'Windows release publisher fake HTTP tests passed.'
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ([IO.Path]::GetFullPath($root).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($root) -cmatch '\Aopssh-release-fixture-[a-f0-9]{32}\z') {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
