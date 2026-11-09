#Requires -Version 7.4

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

class OpsshReleaseFailure : System.Exception {
    OpsshReleaseFailure([string]$message) : base($message) {}
}

function Get-OpsshReleaseVersion {
    param([Parameter(Mandatory)][string]$Tag)

    $parts = [regex]::Match($Tag, '\Av(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,4})\z')
    if (-not $parts.Success -or [int]$parts.Groups[1].Value -gt 255 -or
        [int]$parts.Groups[2].Value -gt 255 -or [int]$parts.Groups[3].Value -gt 65534) {
        throw [OpsshReleaseFailure]::new('Publication requires a canonical stable tag within the Windows package version range.')
    }
    return [version]$Tag.Substring(1)
}

function Test-ReleaseObjectId($value) {
    return $value -is [string] -and $value -cmatch '\A(?!0{40}\z)[a-f0-9]{40}\z'
}

function Test-ReleaseText($value, [string]$expected) {
    return $value -is [string] -and $value -ceq $expected
}

function Confirm-ReleaseApiInput([string]$repository, [string]$token) {
    if ($repository -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_-][A-Za-z0-9_.-]{0,99}\z' -or
        [string]::IsNullOrEmpty($token) -or $token.Length -gt 8192 -or $token -match '[\s\p{C}]') {
        throw [OpsshReleaseFailure]::new('Invalid repository or token input.')
    }
}

function New-ReleaseClient([Net.Http.HttpMessageHandler]$handler) {
    if ($null -eq $handler) {
        $handler = [Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $handler.UseCookies = $false
    }
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(3)
    return $client
}

function Read-ReleaseText([IO.Stream]$stream, [int]$limit, [Threading.CancellationToken]$cancellation, [string]$expectedSha256) {
    $bytes = [IO.MemoryStream]::new()
    try {
        $buffer = [byte[]]::new(16384)
        while ($true) {
            $count = $stream.ReadAsync($buffer, 0, $buffer.Length, $cancellation).GetAwaiter().GetResult()
            if ($count -eq 0) { break }
            if ($bytes.Length + $count -gt $limit) {
                throw [OpsshReleaseFailure]::new('Release input exceeded its byte limit.')
            }
            $bytes.Write($buffer, 0, $count)
        }
        if ($expectedSha256) {
            $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes.ToArray()))
            if ($digest -ine $expectedSha256) {
                throw [OpsshReleaseFailure]::new('Release input digest differs from the verified handoff.')
            }
        }
        return [Text.UTF8Encoding]::new($false, $true).GetString($bytes.GetBuffer(), 0, [int]$bytes.Length)
    } finally {
        $bytes.Dispose()
    }
}

function Open-ReleaseFile([string]$path) {
    $attributes = [IO.File]::GetAttributes($path)
    if ($attributes -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) {
        throw [OpsshReleaseFailure]::new('A release input must be a regular file.')
    }
    $options = [IO.FileOptions]::Asynchronous -bor [IO.FileOptions]::SequentialScan
    return [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read, 65536, $options)
}

function Assert-OpsshReleaseNoticeReadiness {
    param([Parameter(Mandatory)][string]$ManifestPath)

    $path = [IO.Path]::GetFullPath($ManifestPath, $PWD.ProviderPath)
    $stream = Open-ReleaseFile $path
    try {
        $manifest = (Read-ReleaseText $stream 1MB ([Threading.CancellationToken]::None)).TrimStart([char]0xfeff) |
            ConvertFrom-Json -AsHashtable
    } finally { $stream.Dispose() }
    $wasm = $manifest['onePasswordWasm']
    $review = if ($wasm -is [Collections.IDictionary]) { $wasm['attribution'] }
    $evidence = $null
    if ($manifest['schema'] -ne 1 -or $review -isnot [Collections.IDictionary] -or
        $wasm['sha256'] -cnotmatch '\A[a-f0-9]{64}\z' -or $review['wasmSha256'] -cne $wasm['sha256'] -or
        $review['basis'] -cnotin 'upstream-notices','confirmed-redistribution' -or
        $review['evidence'] -isnot [string] -or $review['evidence'].Length -gt 1024 -or
        ![uri]::TryCreate($review['evidence'], [UriKind]::Absolute, [ref]$evidence) -or
        $evidence.Scheme -cne 'https' -or $evidence.UserInfo -or $evidence.Query) {
        throw [OpsshReleaseFailure]::new('Release requires reviewed attribution or redistribution evidence for the exact 1Password WASM digest.')
    }
}

function Invoke-ReleaseRequest {
    param($release, [string]$method, [string]$path, [Collections.IDictionary]$fields, $asset, [switch]$allowMissing)

    $origin = if ($null -eq $asset) { 'https://api.github.com' } else { 'https://uploads.github.com' }
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($method), $origin + $release.ApiRoot + $path)
    try {
        $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $release.Token)
        $request.Headers.Accept.ParseAdd('application/vnd.github+json')
        $request.Headers.UserAgent.ParseAdd('opssh-release/1.0')
        $request.Headers.Add('X-GitHub-Api-Version', '2026-03-10')
        if ($null -ne $fields) {
            $json = ConvertTo-Json -InputObject $fields -Depth 5 -Compress
            $request.Content = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
        }
        if ($null -ne $asset) {
            $request.Content = [Net.Http.StreamContent]::new($asset.Stream)
            $request.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
            $request.Content.Headers.ContentLength = $asset.Size
        }
        $response = $release.Client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $release.Cancellation).GetAwaiter().GetResult()
        try {
            if ($allowMissing -and $response.StatusCode -eq [Net.HttpStatusCode]::NotFound) { return $null }
            if (-not $response.IsSuccessStatusCode) {
                throw [OpsshReleaseFailure]::new("GitHub request failed ($([int]$response.StatusCode)).")
            }
            $content = $response.Content.ReadAsStream($release.Cancellation)
            $text = Read-ReleaseText $content 8MB $release.Cancellation
            $parsed = ConvertFrom-Json -InputObject $text -AsHashtable -Depth 24 -NoEnumerate
            if ($null -eq $parsed) { throw [OpsshReleaseFailure]::new('GitHub returned an empty release API response.') }
            return ,$parsed
        } finally {
            $response.Dispose()
        }
    } finally {
        $request.Dispose()
    }
}

function Confirm-RemoteTag($release) {
    $ref = Invoke-ReleaseRequest $release GET "/git/ref/tags/$($release.Tag)"
    if ($ref -isnot [Collections.IDictionary] -or -not (Test-ReleaseText $ref['ref'] "refs/tags/$($release.Tag)") -or
        $ref['object'] -isnot [Collections.IDictionary] -or -not (Test-ReleaseText $ref['object']['type'] 'tag') -or
        -not (Test-ReleaseText $ref['object']['sha'] $release.TagObject)) {
        throw [OpsshReleaseFailure]::new('The remote tag reference differs from the admitted annotated tag.')
    }
    $tag = Invoke-ReleaseRequest $release GET "/git/tags/$($release.TagObject)"
    if ($tag -isnot [Collections.IDictionary] -or -not (Test-ReleaseText $tag['tag'] $release.Tag) -or
        -not (Test-ReleaseText $tag['sha'] $release.TagObject) -or $tag['object'] -isnot [Collections.IDictionary] -or
        -not (Test-ReleaseText $tag['object']['type'] 'commit') -or -not (Test-ReleaseText $tag['object']['sha'] $release.SourceCommit) -or
        $tag['verification'] -isnot [Collections.IDictionary] -or
        $tag['verification']['verified'] -isnot [bool] -or -not $tag['verification']['verified'] -or
        $tag['verification']['signature'] -isnot [string] -or $tag['verification']['signature'] -cnotmatch
            '\A-----BEGIN SSH SIGNATURE-----\r?\n(?:[A-Za-z0-9+/=]+\r?\n)+-----END SSH SIGNATURE-----(?:\r?\n)?\z') {
        throw [OpsshReleaseFailure]::new('The remote tag is not a GitHub-verified SSH-signed tag directly naming the admitted source commit.')
    }
}

function Test-ReleaseRepository($record, [string]$repository, [long]$repositoryId) {
    return $record -is [Collections.IDictionary] -and $record['id'] -is [long] -and
        $record['id'] -eq $repositoryId -and (Test-ReleaseText $record['full_name'] $repository)
}

function Find-MainReleaseRun($release, [string]$workflow, [string]$repository, [long]$repositoryId) {
    $path = ".github/workflows/$workflow"
    $runs = Invoke-ReleaseRequest $release GET "/actions/workflows/$workflow/runs?event=push&branch=main&head_sha=$($release.SourceCommit)&status=success&per_page=30"
    if ($runs -isnot [Collections.IDictionary] -or $runs['workflow_runs'] -isnot [array] -or $runs['workflow_runs'].Count -gt 30) {
        throw [OpsshReleaseFailure]::new("Invalid GitHub main workflow run response for $workflow.")
    }
    foreach ($run in $runs['workflow_runs']) {
        if ($run -is [Collections.IDictionary] -and $run['path'] -is [string] -and $run['path'] -cin @($path, "$path@main") -and
            (Test-ReleaseText $run['event'] 'push') -and (Test-ReleaseText $run['head_branch'] 'main') -and
            (Test-ReleaseText $run['head_sha'] $release.SourceCommit) -and (Test-ReleaseText $run['status'] 'completed') -and
            (Test-ReleaseText $run['conclusion'] 'success') -and
            $run['id'] -is [long] -and $run['id'] -gt 0 -and $run['run_attempt'] -is [long] -and $run['run_attempt'] -gt 0 -and
            (Test-ReleaseRepository $run['repository'] $repository $repositoryId) -and
            (Test-ReleaseRepository $run['head_repository'] $repository $repositoryId)) {
            return [ordered]@{ workflow = $workflow; id = $run['id']; attempt = $run['run_attempt'] }
        }
    }
    throw [OpsshReleaseFailure]::new("No successful $workflow run on main matches the tagged source commit.")
}

function Invoke-OpsshReleaseAdmission {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][long]$RepositoryId,
        [Parameter(Mandatory)][string]$EventName,
        [Parameter(Mandatory)][string]$EventRef,
        [Parameter(Mandatory)][string]$EventSha,
        [Parameter(Mandatory)][string]$EventPath,
        [Parameter(Mandatory)][string]$CheckoutHead,
        [Parameter(Mandatory)][string]$LocalTagObject,
        [Parameter(Mandatory)][string]$LocalTagCommit,
        [Parameter(Mandatory)][string]$Token,
        [Net.Http.HttpMessageHandler]$Handler,
        [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None
    )

    Confirm-ReleaseApiInput $Repository $Token
    if ($RepositoryId -le 0 -or $EventName -cne 'push' -or -not $EventRef.StartsWith('refs/tags/', [StringComparison]::Ordinal) -or
        -not (Test-ReleaseObjectId $EventSha) -or $CheckoutHead -cne $EventSha -or $LocalTagCommit -cne $EventSha -or
        -not (Test-ReleaseObjectId $LocalTagObject) -or $LocalTagObject -ceq $EventSha) {
        throw [OpsshReleaseFailure]::new('The checkout and event must identify a new annotated tag push.')
    }
    $tag = $EventRef.Substring('refs/tags/'.Length)
    $version = Get-OpsshReleaseVersion -Tag $tag
    $deadline = [Threading.CancellationTokenSource]::CreateLinkedTokenSource($CancellationToken, [Threading.CancellationToken]::None)
    $deadline.CancelAfter([TimeSpan]::FromMinutes(3))
    $client = $null
    try {
        $stream = Open-ReleaseFile ([IO.Path]::GetFullPath($EventPath, $PWD.ProviderPath))
        try {
            $event = ConvertFrom-Json -InputObject (Read-ReleaseText $stream 4MB $deadline.Token) -AsHashtable -Depth 24 -NoEnumerate
        } finally {
            $stream.Dispose()
        }
        if ($event -isnot [Collections.IDictionary] -or -not (Test-ReleaseText $event['ref'] $EventRef) -or
            -not (Test-ReleaseText $event['before'] ('0' * 40)) -or -not (Test-ReleaseText $event['after'] $LocalTagObject) -or
            $event['head_commit'] -isnot [Collections.IDictionary] -or -not (Test-ReleaseText $event['head_commit']['id'] $EventSha) -or
            $event['created'] -isnot [bool] -or -not $event['created'] -or
            $event['deleted'] -isnot [bool] -or $event['deleted'] -or
            $event['forced'] -isnot [bool] -or $event['forced'] -or
            -not (Test-ReleaseRepository $event['repository'] $Repository $RepositoryId)) {
            throw [OpsshReleaseFailure]::new('The event does not identify a new tag push in this repository.')
        }

        $client = New-ReleaseClient $Handler
        $release = @{ Client = $client; Cancellation = $deadline.Token; Token = $Token; ApiRoot = "/repos/$Repository";
            Tag = $tag; TagObject = $LocalTagObject; SourceCommit = $EventSha }
        Confirm-RemoteTag $release
        $main = Invoke-ReleaseRequest $release GET '/git/ref/heads/main'
        if ($main -isnot [Collections.IDictionary] -or -not (Test-ReleaseText $main['ref'] 'refs/heads/main') -or
            $main['object'] -isnot [Collections.IDictionary] -or -not (Test-ReleaseText $main['object']['type'] 'commit') -or
            -not (Test-ReleaseObjectId $main['object']['sha'])) {
            throw [OpsshReleaseFailure]::new('GitHub did not return the current main commit identity.')
        }
        $mainSha = $main['object']['sha']
        $compare = Invoke-ReleaseRequest $release GET "/compare/$EventSha...${mainSha}?per_page=1"
        $expectedStatus = if ($mainSha -ceq $EventSha) { 'identical' } else { 'ahead' }
        if ($compare -isnot [Collections.IDictionary] -or -not (Test-ReleaseText $compare['status'] $expectedStatus) -or
            $compare['base_commit'] -isnot [Collections.IDictionary] -or -not (Test-ReleaseText $compare['base_commit']['sha'] $EventSha) -or
            $compare['merge_base_commit'] -isnot [Collections.IDictionary] -or -not (Test-ReleaseText $compare['merge_base_commit']['sha'] $EventSha)) {
            throw [OpsshReleaseFailure]::new('The tagged commit is outside current main ancestry.')
        }

        $mainRuns = foreach ($workflow in 'windows.yml', 'commit-subjects.yml', 'dependency-audit.yml', 'codeql.yml') {
            Find-MainReleaseRun $release $workflow $Repository $RepositoryId
        }
        Confirm-RemoteTag $release
        return @{ tag = $tag; version = $version.ToString(); tagObject = $LocalTagObject; sourceCommit = $EventSha;
            mainSha = $mainSha; mainRuns = @($mainRuns) }
    } catch [OperationCanceledException] {
        throw [OpsshReleaseFailure]::new('Release admission was canceled or timed out.')
    } catch [OpsshReleaseFailure] {
        throw
    } catch {
        throw [OpsshReleaseFailure]::new('Release admission failed while processing the event or GitHub requests.')
    } finally {
        if ($null -ne $client) { $client.Dispose() }
        $deadline.Dispose()
    }
}

function Find-ReleaseRecord($release) {
    $match = $null
    for ($page = 1; $page -le 20; $page++) {
        $records = Invoke-ReleaseRequest $release GET "/releases?per_page=30&page=$page"
        if ($records -isnot [array] -or $records.Count -gt 30) {
            throw [OpsshReleaseFailure]::new('Invalid GitHub release list.')
        }
        foreach ($record in $records) {
            if ($record -isnot [Collections.IDictionary] -or $record['tag_name'] -isnot [string]) {
                throw [OpsshReleaseFailure]::new('Invalid GitHub release record.')
            }
            if ($record['tag_name'] -ceq $release.Tag) {
                if ($null -ne $match) { throw [OpsshReleaseFailure]::new('Multiple GitHub releases match the tag.') }
                $match = $record
            }
        }
        if ($records.Count -lt 30) { return $match }
    }
    throw [OpsshReleaseFailure]::new('GitHub release listing exceeded the page limit.')
}

function Confirm-ReleaseRecord($release, $record, [bool]$draft, [long]$expectedId = 0) {
    if ($record -isnot [Collections.IDictionary] -or -not (Test-ReleaseText $record['tag_name'] $release.Tag) -or
        $record['id'] -isnot [long] -or $record['id'] -le 0 -or ($expectedId -ne 0 -and $record['id'] -ne $expectedId)) {
        throw [OpsshReleaseFailure]::new('GitHub release identity differs from the admitted tag.')
    }
    if ($record['draft'] -isnot [bool] -or $record['draft'] -ne $draft -or
        $record['prerelease'] -isnot [bool] -or $record['prerelease'] -or
        $record['immutable'] -isnot [bool] -or $record['immutable'] -eq $draft) {
        throw [OpsshReleaseFailure]::new('GitHub release classification is inconsistent with the expected state.')
    }
    if ($draft -and ($record['body'] -isnot [string] -or
        ($record['body'] -csplit '\r?\n', 2)[0] -cne $release.Provenance)) {
        throw [OpsshReleaseFailure]::new('GitHub draft provenance differs from the admitted tag or validated artifact.')
    }
}

function Confirm-ReleaseAsset($asset, $expected) {
    if ($asset -isnot [Collections.IDictionary] -or $asset['id'] -isnot [long] -or $asset['id'] -le 0 -or
        -not (Test-ReleaseText $asset['name'] $expected.Name) -or -not (Test-ReleaseText $asset['state'] 'uploaded') -or
        $asset['size'] -isnot [long] -or $asset['size'] -ne $expected.Size -or
        -not (Test-ReleaseText $asset['digest'] "sha256:$($expected.Sha256)")) {
        throw [OpsshReleaseFailure]::new('GitHub release asset differs from the validated manifest.')
    }
}

function Get-ReleaseAssets($release, [long]$id, [bool]$requireAll) {
    $items = Invoke-ReleaseRequest $release GET "/releases/$id/assets?per_page=4"
    if ($items -isnot [array] -or $items.Count -gt 3 -or ($requireAll -and $items.Count -ne 3)) {
        throw [OpsshReleaseFailure]::new('GitHub release asset count differs from the expected set.')
    }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $ids = [Collections.Generic.HashSet[long]]::new()
    foreach ($item in $items) {
        if ($item -isnot [Collections.IDictionary] -or $item['name'] -isnot [string] -or
            -not $release.Assets.ContainsKey($item['name']) -or -not $names.Add($item['name'])) {
            throw [OpsshReleaseFailure]::new('GitHub release contains an unexpected or duplicate asset name.')
        }
        Confirm-ReleaseAsset $item $release.Assets[$item['name']]
        if (-not $ids.Add($item['id'])) { throw [OpsshReleaseFailure]::new('GitHub release contains a duplicate asset identity.') }
    }
    return ,$names
}

function Publish-Release($release) {
    Confirm-RemoteTag $release
    $record = Find-ReleaseRecord $release
    if ($null -ne $record -and $record['draft'] -ceq $false) {
        Confirm-ReleaseRecord $release $record $false
        $null = Get-ReleaseAssets $release $record['id'] $true
        Confirm-RemoteTag $release
        return @{ status = 'skipped'; releaseId = $record['id'] }
    }

    if ($null -ne $record) { Confirm-ReleaseRecord $release $record $true }
    $baseline = Invoke-ReleaseRequest $release GET '/releases/latest' -allowMissing
    $webRoot = "https://github.com/$($release.Repository)"
    $history = "[Commit history for $($release.Tag)]($webRoot/commits/$($release.Tag))"
    if ($null -ne $baseline) {
        if ($baseline -isnot [Collections.IDictionary] -or $baseline['tag_name'] -isnot [string] -or
            $baseline['draft'] -isnot [bool] -or $baseline['draft'] -or
            $baseline['prerelease'] -isnot [bool] -or $baseline['prerelease']) {
            throw [OpsshReleaseFailure]::new('GitHub latest release is not a stable published baseline.')
        }
        $baselineTag = $baseline['tag_name']
        if ((Get-OpsshReleaseVersion -Tag $baselineTag) -ge $release.Version) {
            throw [OpsshReleaseFailure]::new('The new release version must exceed the published stable baseline.')
        }
        $history = "[Full changelog]($webRoot/compare/$baselineTag...$($release.Tag))"
    }
    $notes = "$($release.Notes)`n$history`n"
    if ($null -eq $record) {
        $fields = @{ tag_name = $release.Tag; name = $release.Tag;
            body = $notes; draft = $true; prerelease = $false }
        Confirm-RemoteTag $release
        $record = Invoke-ReleaseRequest $release POST '/releases' $fields
    }
    Confirm-ReleaseRecord $release $record $true
    $id = $record['id']
    $existing = Get-ReleaseAssets $release $id $false
    foreach ($name in $release.AssetNames) {
        if ($existing.Contains($name)) { continue }
        $current = Invoke-ReleaseRequest $release GET "/releases/$id"
        Confirm-ReleaseRecord $release $current $true $id
        Confirm-RemoteTag $release
        $item = Invoke-ReleaseRequest $release POST "/releases/$id/assets?name=$([uri]::EscapeDataString($name))" -asset $release.Assets[$name]
        Confirm-ReleaseAsset $item $release.Assets[$name]
    }
    $current = Invoke-ReleaseRequest $release GET "/releases/$id"
    Confirm-ReleaseRecord $release $current $true $id
    $null = Get-ReleaseAssets $release $id $true
    Confirm-RemoteTag $release
    $fields = @{ name = $release.Tag; body = $notes; draft = $false; prerelease = $false; make_latest = 'legacy' }
    $record = Invoke-ReleaseRequest $release PATCH "/releases/$id" $fields
    Confirm-ReleaseRecord $release $record $false $id
    $null = Get-ReleaseAssets $release $id $true
    Confirm-RemoteTag $release
    return @{ status = 'published'; releaseId = $id }
}

function Invoke-OpsshRelease {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$TagObject,
        [Parameter(Mandatory)][string]$SourceCommit,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][ValidatePattern('\A[a-fA-F0-9]{64}\z')][string]$ManifestSha256,
        [Parameter(Mandatory)][string]$NotesPath,
        [Parameter(Mandatory)][ValidatePattern('\A[a-fA-F0-9]{64}\z')][string]$NotesSha256,
        [Parameter(Mandatory)][string]$Token,
        [Net.Http.HttpMessageHandler]$Handler,
        [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None
    )

    $version = Get-OpsshReleaseVersion -Tag $Tag
    Confirm-ReleaseApiInput $Repository $Token
    if (-not (Test-ReleaseObjectId $TagObject) -or -not (Test-ReleaseObjectId $SourceCommit) -or $TagObject -ceq $SourceCommit) {
        throw [OpsshReleaseFailure]::new('Invalid tag object or source commit input.')
    }

    $deadline = [Threading.CancellationTokenSource]::CreateLinkedTokenSource($CancellationToken, [Threading.CancellationToken]::None)
    $deadline.CancelAfter([TimeSpan]::FromMinutes(15))
    $streams = [Collections.Generic.List[IO.Stream]]::new()
    $client = $null
    try {
        $manifestFile = [IO.Path]::GetFullPath($ManifestPath, $PWD.ProviderPath)
        $manifestDirectory = [IO.Path]::GetDirectoryName($manifestFile)
        if ([IO.File]::GetAttributes($manifestDirectory) -band [IO.FileAttributes]::ReparsePoint) {
            throw [OpsshReleaseFailure]::new('The release manifest directory must not be a reparse point.')
        }
        $stream = Open-ReleaseFile $manifestFile
        try {
            $manifest = ConvertFrom-Json -InputObject (Read-ReleaseText $stream 16384 $deadline.Token $ManifestSha256) `
                -AsHashtable -Depth 8 -NoEnumerate
        } finally {
            $stream.Dispose()
        }
        $names = @('msi', 'exe', 'zip' | ForEach-Object { "opssh-$version-windows-x64.$_" })
        if ($manifest -isnot [Collections.IDictionary] -or $manifest['schema'] -isnot [long] -or $manifest['schema'] -ne 1 -or
            -not (Test-ReleaseText $manifest['tag'] $Tag) -or -not (Test-ReleaseText $manifest['tagObject'] $TagObject) -or
            -not (Test-ReleaseText $manifest['sourceCommit'] $SourceCommit) -or
            $manifest['assets'] -isnot [array] -or $manifest['assets'].Count -ne $names.Count) {
            throw [OpsshReleaseFailure]::new('Release manifest does not identify the admitted tag and three packages.')
        }
        $assets = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($entry in $manifest['assets']) {
            if ($entry -isnot [Collections.IDictionary] -or $entry['name'] -isnot [string] -or $entry['name'] -cnotin $names -or
                $assets.ContainsKey($entry['name']) -or $entry['size'] -isnot [long] -or $entry['size'] -le 0 -or
                $entry['size'] -gt 1GB -or $entry['sha256'] -isnot [string] -or $entry['sha256'] -cnotmatch '\A[a-f0-9]{64}\z') {
                throw [OpsshReleaseFailure]::new('Release manifest contains an invalid or duplicate package entry.')
            }
            $name = $entry['name']
            $stream = Open-ReleaseFile (Join-Path $manifestDirectory $name)
            $streams.Add($stream)
            if ($stream.Length -ne $entry['size']) { throw [OpsshReleaseFailure]::new('Package length differs from the release manifest.') }
            $hash = [Security.Cryptography.SHA256]::HashDataAsync($stream, $deadline.Token).AsTask().GetAwaiter().GetResult()
            $digest = [Convert]::ToHexString($hash).ToLowerInvariant()
            if ($digest -cne $entry['sha256']) { throw [OpsshReleaseFailure]::new('Package digest differs from the release manifest.') }
            $stream.Position = 0
            $assets.Add($name, @{ Name = $name; Size = $stream.Length; Sha256 = $digest; Stream = $stream })
        }

        $stream = Open-ReleaseFile ([IO.Path]::GetFullPath($NotesPath, $PWD.ProviderPath))
        try {
            $notes = (Read-ReleaseText $stream 65536 $deadline.Token $NotesSha256).Trim()
        } finally {
            $stream.Dispose()
        }
        if ([string]::IsNullOrWhiteSpace($notes) -or $notes.Contains([char]0) -or $notes.Contains('<!-- opssh-release-provenance')) {
            throw [OpsshReleaseFailure]::new('The validated release notes are missing or invalid.')
        }

        $manifestDigest = $ManifestSha256.ToLowerInvariant()
        $notesDigest = $NotesSha256.ToLowerInvariant()
        $marker = "<!-- opssh-release-provenance v1 tag-object=$TagObject source-commit=$SourceCommit manifest-sha256=$manifestDigest notes-sha256=$notesDigest -->"
        $evidence = @($names | ForEach-Object {
            @{ name = $_; size = $assets[$_].Size; sha256 = $assets[$_].Sha256 }
        })
        $checksums = ($evidence | ForEach-Object { "    $($_.sha256)  $($_.name)" }) -join "`n"
        $disclosure = 'The MSI, EXE, and first-party Windows binaries are unsigned. The installers include WiX 7.0.1 code. [WiX 7.0.1 source archive](https://github.com/wixtoolset/wix/archive/642f1a0f7c857ffa4fc9fa3f9d1433aab895dd29.zip)'
        $body = "$marker`n`n$notes`n`n## SHA-256 checksums`n`n$disclosure`n`n$checksums`n"
        $client = New-ReleaseClient $Handler
        $release = @{ Client = $client; Cancellation = $deadline.Token; Token = $Token; ApiRoot = "/repos/$Repository"; Repository = $Repository; Version = $version;
            Tag = $Tag; TagObject = $TagObject; SourceCommit = $SourceCommit; Provenance = $marker; Notes = $body;
            AssetNames = $names; Assets = $assets }
        $result = Publish-Release $release
        $result.tag = $Tag
        $result.tagObject = $TagObject
        $result.sourceCommit = $SourceCommit
        $result.assets = $evidence
        return $result
    } catch [OperationCanceledException] {
        throw [OpsshReleaseFailure]::new('Release publication was canceled or timed out.')
    } catch [OpsshReleaseFailure] {
        throw
    } catch {
        throw [OpsshReleaseFailure]::new('The release publisher failed while processing local files or GitHub requests.')
    } finally {
        foreach ($stream in $streams) { $stream.Dispose() }
        if ($null -ne $client) { $client.Dispose() }
        $deadline.Dispose()
    }
}

Export-ModuleMember -Function Get-OpsshReleaseVersion, Invoke-OpsshRelease, Invoke-OpsshReleaseAdmission, Assert-OpsshReleaseNoticeReadiness
