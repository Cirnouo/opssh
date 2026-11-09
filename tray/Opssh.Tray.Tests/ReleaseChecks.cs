using System.Net;
using System.Text;
using Opssh.Tray;

internal static class ReleaseChecks
{
    private static readonly Version Installed = new(0, 9, 2, 0);

    internal static async Task RunAsync(CancellationToken token)
    {
        await RequireAsync(ReleaseCheckStatus.Available, Release("v0.10.0"), token, latest: new(0, 10, 0));
        await RequireAsync(ReleaseCheckStatus.Available, Release("0.9.3"), token, latest: new(0, 9, 3));
        await RequireAsync(ReleaseCheckStatus.Current, Release("v0.9.2"), token);
        await RequireAsync(ReleaseCheckStatus.Current, Release("v0.8.99"), token);
        await RequireAsync(ReleaseCheckStatus.NoRelease, "", token, HttpStatusCode.NotFound);
        await RequireAsync(ReleaseCheckStatus.NoRelease, Release("v10.0.0", draft: true), token);
        await RequireAsync(ReleaseCheckStatus.NoRelease, Release("v10.0.0-beta.1", prerelease: true), token);
        await RequireAsync(ReleaseCheckStatus.Unavailable, "", token, HttpStatusCode.TooManyRequests);
        await RequireAsync(ReleaseCheckStatus.Unavailable, "", token, HttpStatusCode.Found);

        foreach (var tag in new[] { "v0.10", "v0.10.0.1", "v00.10.0", "v+0.10.0", "v0.10.0 ", "v0.10.0-beta.1", "v2147483648.0.0" })
            await RequireAsync(ReleaseCheckStatus.Unavailable, Release(tag), token);
        foreach (var body in new[]
        {
            "[",
            "[]",
            """{"tag_name":"v0.10.0"}""",
            """{"tag_name":"v0.10.0","draft":false,"prerelease":"false"}""",
            """{"tag_name":"v0.10.0","draft":true,"draft":false,"prerelease":false}""",
            """{"tag_name":"v0.10.0\uDEAD","draft":false,"prerelease":false}""",
        })
            await RequireAsync(ReleaseCheckStatus.Unavailable, body, token);

        using var networkFailure = new HttpClient(new ReleaseHandler((_, _) => throw new HttpRequestException("fixture failure")));
        Require((await new ReleaseCheck(networkFailure).CheckAsync(Installed, token)).Status == ReleaseCheckStatus.Unavailable,
            "a transport error produces generic unavailability");
        Require(ReleaseCheck.ReleasesPage == new Uri("https://github.com/Cirnouo/opssh/releases"), "fixed releases page");

        await BodyLimitAsync(token);
        await CancellationAsync(token);
        Console.WriteLine("Tray release checks passed (public endpoint, stable versions, failure statuses, response size, and cancellation).");
    }

    private static string Release(string tag, bool draft = false, bool prerelease = false) =>
        $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"https://example.invalid/release"}""";

    private static async Task RequireAsync(ReleaseCheckStatus status, string body, CancellationToken token,
        HttpStatusCode httpStatus = HttpStatusCode.OK, Version? latest = null)
    {
        var calls = 0;
        using var client = new HttpClient(new ReleaseHandler((request, _) =>
        {
            calls++;
            Require(request.Method == HttpMethod.Get &&
                request.RequestUri == new Uri("https://api.github.com/repos/Cirnouo/opssh/releases/latest"),
                "requests use the fixed public GitHub metadata endpoint");
            Require(request.Headers.Authorization is null && request.Content is null, "metadata request contains no credentials or body");
            Require(request.Headers.UserAgent.ToString() == "opssh", "metadata request identifies the application");
            var response = new HttpResponseMessage(httpStatus) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (httpStatus == HttpStatusCode.Found)
                response.Headers.Location = new Uri("https://example.invalid/redirect");
            return Task.FromResult(response);
        }));
        var result = await new ReleaseCheck(client).CheckAsync(Installed, token);
        Require(result.Status == status && (latest is null || result.Latest == latest), $"expected {status} but got {result.Status}");
        Require(calls == 1, "one metadata request");
    }

    private static async Task BodyLimitAsync(CancellationToken token)
    {
        const int maxBytes = 64 * 1024;
        var body = Release("v0.10.0").PadRight(maxBytes + 1);
        await RequireAsync(ReleaseCheckStatus.Available, body[..maxBytes], token, latest: new(0, 10, 0));
        await RequireAsync(ReleaseCheckStatus.Unavailable, body, token);

        var stream = new UnknownLengthBodyStream(Encoding.UTF8.GetBytes(body.PadRight(maxBytes * 2)));
        using var client = new HttpClient(new ReleaseHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        var result = await new ReleaseCheck(client).CheckAsync(Installed, token);
        Require(result.Status == ReleaseCheckStatus.Unavailable && stream.BytesRead == maxBytes + 1 && stream.Disposed,
            "an oversize body without Content-Length is cut off and disposed");
    }

    private static async Task CancellationAsync(CancellationToken token)
    {
        var body = new WaitingBodyStream();
        using var client = new HttpClient(new ReleaseHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var check = new ReleaseCheck(client).CheckAsync(Installed, caller.Token);
        await body.Reading.Task.WaitAsync(token);
        caller.Cancel();
        try
        {
            await check.WaitAsync(token);
            throw new InvalidOperationException("Tray release check swallowed caller cancellation.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Require(body.Disposed, "caller cancellation interrupts the response body and disposes it");
        }
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException("Tray release check failed: " + scenario);
    }

    private sealed class ReleaseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private abstract class BodyStream : Stream
    {
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class UnknownLengthBodyStream(byte[] body) : BodyStream
    {
        internal int BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Math.Min(buffer.Length, body.Length - BytesRead);
            body.AsMemory(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class WaitingBodyStream : BodyStream
    {
        internal TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
