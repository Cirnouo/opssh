using System.Net;
using System.Text.Json;
using System.Text.Unicode;

namespace Opssh.Tray;

internal enum ReleaseCheckStatus { Idle, Checking, Current, Available, NoRelease, Unavailable }

internal sealed record ReleaseCheckResult(ReleaseCheckStatus Status, Version? Latest = null);

internal sealed class ReleaseCheck(HttpClient client)
{
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly Uri LatestRelease = new("https://api.github.com/repos/Cirnouo/opssh/releases/latest");
    internal static Uri ReleasesPage { get; } = new("https://github.com/Cirnouo/opssh/releases");

    internal async Task<ReleaseCheckResult> CheckAsync(Version installed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.UserAgent.ParseAdd("opssh");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(ReleaseCheckStatus.NoRelease);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxResponseBytes)
                return new(ReleaseCheckStatus.Unavailable);

            using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var buffer = new byte[MaxResponseBytes + 1];
            var size = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (size > MaxResponseBytes)
                return new(ReleaseCheckStatus.Unavailable);

            var result = ParseRelease(buffer.AsMemory(0, size), installed);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(ReleaseCheckStatus.Unavailable);
        }
    }

    private static ReleaseCheckResult ParseRelease(ReadOnlyMemory<byte> payload, Version installed)
    {
        if (!Utf8.IsValid(payload.Span))
            return new(ReleaseCheckStatus.Unavailable);

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("draft", out var draft) || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new(ReleaseCheckStatus.Unavailable);

            if (draft.GetBoolean() || prerelease.GetBoolean())
                return new(ReleaseCheckStatus.NoRelease);

            var number = tag.GetString()!;
            if (number.StartsWith('v'))
                number = number[1..];
            if (!Version.TryParse(number, out var latest) || latest.Build < 0 || latest.Revision >= 0 || latest.ToString() != number)
                return new(ReleaseCheckStatus.Unavailable);

            var current = new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build));
            return latest > current ? new(ReleaseCheckStatus.Available, latest) : new(ReleaseCheckStatus.Current);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return new(ReleaseCheckStatus.Unavailable);
        }
    }
}
