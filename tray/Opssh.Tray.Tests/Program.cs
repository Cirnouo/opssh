using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Opssh.Tray;

if (args is ["--go-interop"])
{
    await GoInterop.CheckAsync();
    return;
}

if (args is ["--production-version", var version])
{
    await GoInterop.CheckProductionAsync(version);
    return;
}

if (args.Length > 0)
{
    Assert(args.Length == 4 && args[0] == "--control-handle" && args[2] == "--tray-handle", "bootstrap arguments");
    using var pipe = new NamedPipeClientStream(PipeDirection.InOut, true, true, new SafePipeHandle(nint.Parse(args[1]), true));
    using var tray = new SafeWaitHandle(nint.Parse(args[3]), true);
    var parent = NativeTest.GetProcessId(tray);
    Assert(parent != 0 && parent != Environment.ProcessId, "inherited real process handle");
    Assert(NativeTest.WaitForSingleObject(tray, 0) == 258, "tray is alive");
    Assert(NativeTest.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var owner) && owner == parent, "inherited process owns private pipe");
    await SendAsync(pipe, """{"v":1,"type":"hello","body":{"pipe":"test","version":"1"}}""");
    var request = await Protocol.ReadAsync(pipe, default);
    Assert(request.Type == "status" && request.Id is not null, "status request with correlation ID");
    await SendAsync(pipe, """{"v":1,"type":"result","id":"stale","body":{"ok":false}}""");
    await SendAsync(pipe, $$$$"""{"v":1,"type":"result","id":"{{{{request.Id}}}}","body":{"ok":true,"state":{"pipe":"test","locked":false,"account":"","keys":[],"keyCache":{"mode":"fixed","seconds":300},"approvalCache":{"mode":"fixed","seconds":600},"provider":"unconfigured","pending":0}}}""");
    request = await Protocol.ReadAsync(pipe, default);
    Assert(request.Type == "shutdown", "graceful shutdown");
    await SendAsync(pipe, $$$"""{"v":1,"type":"result","id":"{{{request.Id}}}","body":{"ok":true}}""");
    return;
}

using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var token = testTimeout.Token;
AppearanceChecks.Run();
DiagnosticChecks.Run();
ActivityWheelChecks.Run();
await WindowsIntegrationChecks.RunAsync(token);
await ReleaseChecks.RunAsync(token);
await FramingAsync(token);
await SupervisionAsync(token);
await ApprovalLifetimeAsync(token);
SigningNotificationChecks.Run();
await SigningNotificationQueueAsync(token);
await ConcurrentCommandsAsync(token);
await DiscoveryStatusAsync(token);
await MalformedResultsAsync(token);
await TruncatedFrameAsync(token);
Console.WriteLine("Tray checks passed (framing, Windows launch and shutdown, approval lifetime, concurrent control replies, discovery status, malformed result rejection, partial frame deadline).");

const string discoveryFields = """
    ,"scope":{"default":"exclude","vaults":{"aaaaaaaaaaaaaaaaaaaaaaaaaa":"include"},"items":{"aaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbbbbbbbbbbbb":"exclude"}},
    "vaults":[{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaa","name":"Fixture vault","items":[{"id":"bbbbbbbbbbbbbbbbbbbbbbbbbb","name":"Fixture key"}]}],
    "index":"scope-too-broad"
    """;

static async Task FramingAsync(CancellationToken token)
{
    using var stream = new MemoryStream();
    await Protocol.WriteAsync(stream, new(1, "decision", "one", JsonSerializer.SerializeToElement(new { scope = "host", candidate = 2 })), token);
    var encoded = stream.ToArray();
    Assert(BinaryPrimitives.ReadInt32BigEndian(encoded) == encoded.Length - 4, "big-endian byte count");
    using var payload = JsonDocument.Parse(encoded.AsMemory(4));
    Assert(payload.RootElement.GetProperty("id").GetString() == "one", "decision ID");
    Assert(payload.RootElement.GetProperty("body").GetProperty("candidate").GetInt32() == 2, "integer chain index");
    foreach (var invalid in new[]
    {
        new byte[] { 0, 0, 0, 0 },
        new byte[] { 0, 4, 0, 1 },
        Frame("""{"v":2,"type":"hello"}"""),
        Frame("""{"v":1,"type":"hello","v":2}"""),
        Frame("""{"v":1,"type":"hello","body":[] }"""),
        Frame("""{"v":1,"type":"hello"}""")[..^1],
    })
    {
        try
        {
            await Protocol.ReadAsync(new MemoryStream(invalid), token);
            throw new Exception("Accepted an invalid control frame.");
        }
        catch (IOException) { }
    }
}

static async Task SupervisionAsync(CancellationToken token)
{
    using var core = await CoreProcess.StartAsync(token);
    using var session = new CoreSession(core.Pipe);
    var hello = await session.StartAsync(token);
    Assert(hello.Pipe == "test", "hello from inherited endpoint");
    var result = await session.RequestAsync("status", null, token);
    Assert(result is { Ok: true, State: { Provider: "unconfigured" } }, "matching status and validated state delivered");
    await session.RequestAsync("shutdown", null, token);
    Assert(await core.WaitAsync(TimeSpan.FromSeconds(3)), "owned child exited after shutdown");
}

static async Task ApprovalLifetimeAsync(CancellationToken token)
{
    var pipeName = "opssh-tests-" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
    using var peer = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await Task.WhenAll(server.WaitForConnectionAsync(token), peer.ConnectAsync(token));
    var clock = new TestClock();
    using var session = new CoreSession(server, clock);
    var approvals = System.Threading.Channels.Channel.CreateUnbounded<Approval>();
    var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    session.ApprovalRequested += approval => approvals.Writer.TryWrite(approval);
    session.ApprovalEnded += id => ended.TrySetResult(id);
    session.Disconnected += () => disconnected.TrySetResult();
    var handshake = session.StartAsync(token);
    await SendAsync(peer, """{"v":1,"type":"hello","body":{"pipe":"test","version":"1"}}""");
    await handshake;
    await SendAsync(peer, Approval("ended", clock.GetUtcNow().AddMinutes(1)));
    var endedRequest = await approvals.Reader.ReadAsync(token);
    Assert(endedRequest.Id == "ended", "approval dispatched");
    await SendAsync(peer, """{"v":1,"type":"approval-ended","id":"ended"}""");
    Assert(await ended.Task.WaitAsync(token) == "ended", "dismissal dispatched");
    Assert(!await session.DecideAsync(endedRequest, "once", 0, token), "ended request retired");
    await SendAsync(peer, Approval("expired", clock.GetUtcNow().AddSeconds(1)));
    var expiredRequest = await approvals.Reader.ReadAsync(token);
    Assert(expiredRequest.Id == "expired", "expiring approval was active");
    clock.Advance(TimeSpan.FromSeconds(2));
    Assert(!await session.DecideAsync(expiredRequest, "once", 0, token), "expired request cannot be approved");
    await SendAsync(peer, Approval("decide", clock.GetUtcNow().AddMinutes(1)));
    var decideRequest = await approvals.Reader.ReadAsync(token);
    Assert(decideRequest.Id == "decide", "new approval after expiry");
    Assert(!await session.DecideAsync(decideRequest, "app", 0, token), "a different verified process is not the selected application");
    Assert(await session.DecideAsync(decideRequest, "app", 1, token), "the selected application can be approved");
    var decision = await Protocol.ReadAsync(peer, token);
    Assert(decision is { Type: "decision", Id: "decide", Body: { } }, "decision bound to its request");
    Assert(decision.Body!.Value.GetProperty("candidate").GetInt32() == 1, "app candidate is the selected chain index");
    Assert(!await session.DecideAsync(decideRequest, "once", 0, token), "decision cannot be replayed");
    await SendAsync(peer, Approval("decide", clock.GetUtcNow().AddMinutes(1)));
    var replacement = await approvals.Reader.ReadAsync(token);
    Assert(replacement.Id == decideRequest.Id && !await session.DecideAsync(decideRequest, "deny", 0, token),
        "a reused ID cannot reactivate a retired approval object");
    Assert(await session.DecideAsync(replacement, "unavailable", 0, token), "replacement can report unavailable");
    decision = await Protocol.ReadAsync(peer, token);
    Assert(decision.Body!.Value.GetProperty("scope").GetString() == "unavailable", "unavailable is a distinct result");
    await SendAsync(peer, Approval("disconnected", clock.GetUtcNow().AddMinutes(1)));
    var disconnectedRequest = await approvals.Reader.ReadAsync(token);
    Assert(disconnectedRequest.Id == "disconnected", "disconnecting request was active");
    peer.Dispose();
    await disconnected.Task.WaitAsync(token);
    Assert(!await session.DecideAsync(disconnectedRequest, "once", 0, token), "disconnected request retired");
}

static async Task SigningNotificationQueueAsync(CancellationToken token)
{
    var pipeName = "opssh-toast-tests-" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
    using var peer = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await Task.WhenAll(server.WaitForConnectionAsync(token), peer.ConnectAsync(token));
    var clock = new TestClock();
    using var session = new CoreSession(server, clock);
    var incoming = System.Threading.Channels.Channel.CreateUnbounded<Approval>();
    session.ApprovalRequested += approval => incoming.Writer.TryWrite(approval);
    var handshake = session.StartAsync(token);
    await SendAsync(peer, """{"v":1,"type":"hello","body":{"pipe":"test","version":"1"}}""");
    await handshake;

    var retiring = new TestSigningNotification();
    SigningPrompt? first = null;
    var shown = 0;
    var queue = new SigningApprovalQueue((prompt, _) =>
    {
        if (++shown == 1)
        {
            first = prompt;
            return Task.FromResult<IAsyncDisposable?>(retiring);
        }
        return Task.FromResult<IAsyncDisposable?>(null);
    }, clock);
    queue.SetOwner(session);
    await SendAsync(peer, Approval("first", clock.GetUtcNow().AddMinutes(1)));
    await SendAsync(peer, Approval("second", clock.GetUtcNow().AddMinutes(1)));
    queue.Enqueue(session, await incoming.Reader.ReadAsync(token));
    queue.Enqueue(session, await incoming.Reader.ReadAsync(token));
    Assert(shown == 1 && first is not null, "one notification is shown while the next request waits");
    Assert(!queue.Invoke("fabricated", "all"), "fake activation cannot approve a pending request");
    Assert(queue.Invoke(first!.Nonce, "all"), "active notification can approve all apps");
    var decision = await Protocol.ReadAsync(peer, token);
    Assert(decision.Id == "first" && decision.Body!.Value.GetProperty("scope").GetString() == "all",
        "first activation is bound to its own request");
    await retiring.Started.Task.WaitAsync(token);
    Assert(shown == 1 && !queue.Invoke(first.Nonce, "once"), "the old nonce retires before removal completes");
    retiring.Complete.TrySetResult();
    decision = await Protocol.ReadAsync(peer, token);
    Assert(decision.Id == "second" && decision.Body!.Value.GetProperty("scope").GetString() == "unavailable",
        "delivery failure reports unavailable for the waiting request");
}

static async Task ConcurrentCommandsAsync(CancellationToken token)
{
    var pipeName = "opssh-tests-" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
    using var peer = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await Task.WhenAll(server.WaitForConnectionAsync(token), peer.ConnectAsync(token));
    using var session = new CoreSession(server);
    var handshake = session.StartAsync(token);
    await SendAsync(peer, """{"v":1,"type":"hello","body":{"pipe":"test","version":"1"}}""");
    await handshake;
    var preload = session.RequestAsync("preload", new { keyId = "fixture" }, token);
    var preloadRequest = await Protocol.ReadAsync(peer, token);
    var urgent = session.RequestAsync("lock", null, token);
    if (urgent.IsFaulted)
        await urgent;
    var urgentRequest = await Protocol.ReadAsync(peer, token);
    Assert(preloadRequest.Type == "preload" && urgentRequest.Type == "lock" && preloadRequest.Id != urgentRequest.Id,
        "priority request is sent while provider command is outstanding");
    await SendAsync(peer, $$$"""{"v":1,"type":"result","id":"{{{urgentRequest.Id}}}","body":{"ok":true}}""");
    Assert((await urgent).Ok && !preload.IsCompleted, "priority reply does not complete the provider command");
    await SendAsync(peer, $$$"""{"v":1,"type":"result","id":"{{{preloadRequest.Id}}}","body":{"ok":false,"error":"Locked"}}""");
    Assert((await preload) is { Ok: false, Error: "Locked" }, "provider reply retains its original correlation");
}

static async Task DiscoveryStatusAsync(CancellationToken token)
{
    const string state = """{"pipe":"test","locked":false,"account":"Fixture","keys":[],"keyCache":{"mode":"off","seconds":0},"approvalCache":{"mode":"off","seconds":0},"provider":"last-success","pending":0}""";
    var pipeName = "opssh-tests-" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    using var peer = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await Task.WhenAll(server.WaitForConnectionAsync(token), peer.ConnectAsync(token));
    using var session = new CoreSession(server);
    var handshake = session.StartAsync(token);
    await SendAsync(peer, """{"v":1,"type":"hello","body":{"pipe":"test","version":"1"}}""");
    await handshake;
    var result = session.RequestAsync("status", null, token);
    var status = await Protocol.ReadAsync(peer, token);
    var fullState = state[..^1] + discoveryFields + "}";
    await SendAsync(peer, $$$"""{"v":1,"type":"result","id":"{{{status.Id}}}","body":{"ok":true,"state":{{{fullState}}}}}""");
    Assert(await result is
    {
        Ok: true,
        State:
        {
            Scope: { Default: "exclude" },
            Vaults: [{ Name: "Fixture vault", Items: [{ Name: "Fixture key" }] }],
            Index: "scope-too-broad"
        }
    } && session.IsConnected, "scope and metadata are delivered with bounded discovery status");
}

static async Task MalformedResultsAsync(CancellationToken token)
{
    const string valid = """{"pipe":"test","locked":false,"account":"Fixture","keys":[],"keyCache":{"mode":"fixed","seconds":300},"approvalCache":{"mode":"off","seconds":0},"provider":"unknown","pending":0}""";
    foreach (var malformed in new[]
    {
        valid.Replace("[]", "[null]"),
        valid.Replace("[]", "[{}]"),
        valid.Replace("\"locked\":false,", ""),
        valid.Replace("\"seconds\":300", "\"seconds\":0"),
        valid.Replace("\"test\"", "\"different\""),
        valid.Replace("\"pending\":0", "\"pending\":-1"),
        valid[..^1] + discoveryFields.Replace("\"default\":\"exclude\"", "\"default\":\"inherit\"") + "}",
        valid[..^1] + discoveryFields.Replace("aaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbbbbbbbbbbbb", "invalid") + "}",
        valid[..^1] + discoveryFields.Replace("\"items\":[{", "\"items\":[null,{") + "}",
        valid[..^1] + discoveryFields.Replace("scope-too-broad", "unsupported") + "}",
    })
    {
        var pipeName = "opssh-tests-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096);
        using var peer = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(token), peer.ConnectAsync(token));
        using var session = new CoreSession(server);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += () => disconnected.TrySetResult();
        var handshake = session.StartAsync(token);
        await SendAsync(peer, """{"v":1,"type":"hello","body":{"pipe":"test","version":"1"}}""");
        await handshake;
        var result = session.RequestAsync("status", null, token);
        var status = await Protocol.ReadAsync(peer, token);
        await SendAsync(peer, $$$"""{"v":1,"type":"result","id":"{{{status.Id}}}","body":{"ok":true,"state":{{{malformed}}}}}""");
        try
        {
            await result;
            throw new Exception("Delivered malformed state to the caller.");
        }
        catch (IOException) { }
        await disconnected.Task.WaitAsync(token);
        Assert(!session.IsConnected, "malformed matching result closes control session");
    }
}

static async Task TruncatedFrameAsync(CancellationToken token)
{
    var pipeName = "opssh-tests-" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous, 4096, 4096);
    using var peer = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await Task.WhenAll(server.WaitForConnectionAsync(token), peer.ConnectAsync(token));
    using var session = new CoreSession(server);
    var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    session.Disconnected += () => disconnected.TrySetResult();
    var handshake = session.StartAsync(token);
    await SendAsync(peer, """{"v":1,"type":"hello","body":{"pipe":"test","version":"1"}}""");
    await handshake;

    // Keep the peer open after sending the first header byte, so EOF cannot retire the session.
    await peer.WriteAsync(new byte[] { 0 }, token);
    await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(6), token);
    Assert(!session.IsConnected, "stalled frame closes control session");
}

static string Approval(string id, DateTimeOffset deadline) =>
    $$$"""{"v":1,"type":"approval","id":"{{{id}}}","body":{"name":"Fixture","fingerprint":"SHA256:fixture","chain":[{"pid":10,"created":100,"path":"fixture.exe"},{"pid":11,"created":90,"path":"host.exe"}],"deadline":"{{{deadline:O}}}","cacheAllowed":true,"appCandidate":1}}""";

static Task SendAsync(Stream pipe, string json) => pipe.WriteAsync(Frame(json)).AsTask();

static byte[] Frame(string json)
{
    var payload = Encoding.UTF8.GetBytes(json);
    var frame = new byte[4 + payload.Length];
    BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
    payload.CopyTo(frame, 4);
    return frame;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

internal sealed class TestSigningNotification : IAsyncDisposable
{
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        Started.TrySetResult();
        await Complete.Task;
    }
}

static class NativeTest
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern uint GetProcessId(SafeWaitHandle process);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
}

internal sealed class TestClock : TimeProvider
{
    private long now = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref now), TimeSpan.Zero);

    internal void Advance(TimeSpan duration) => Interlocked.Add(ref now, duration.Ticks);
}
