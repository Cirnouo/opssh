using System.Text.Json;
using System.Text.Json.Serialization;

namespace Opssh.Tray;

internal sealed class CoreStartupException(string message) : IOException(message);
internal sealed record Hello(string Pipe, string Version);
internal sealed record CachePolicy(
    [property: JsonRequired] string Mode,
    [property: JsonRequired] int Seconds)
{
    internal const int MaxSeconds = 30 * 24 * 60 * 60;

    internal bool IsValid => Mode switch
    {
        "off" or "until-lock" => Seconds == 0,
        "fixed" or "idle" => Seconds is > 0 and <= MaxSeconds,
        _ => false
    };
}

internal sealed record PublicKey(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string VaultId,
    [property: JsonRequired] string ItemId,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Fingerprint,
    [property: JsonRequired] bool Loaded,
    [property: JsonRequired] bool Loading,
    string? Error);

internal sealed record CoreState(
    [property: JsonRequired] string Pipe,
    [property: JsonRequired] bool Locked,
    [property: JsonRequired] string Account,
    [property: JsonRequired] PublicKey[] Keys,
    [property: JsonRequired] CachePolicy KeyCache,
    [property: JsonRequired] CachePolicy ApprovalCache,
    [property: JsonRequired] string Provider,
    [property: JsonRequired] int Pending);

internal sealed record Result([property: JsonRequired] bool Ok, string? Error, CoreState? State, bool RestartRequired);
internal sealed record ProcessIdentity(uint Pid, ulong Created, string Path);
internal sealed record Approval(string Id, string Name, string Fingerprint, ProcessIdentity[] Chain, DateTimeOffset Deadline, bool CacheAllowed);

internal sealed class CoreSession : IDisposable
{
    private readonly Stream pipe;
    private readonly TimeProvider clock;
    private readonly CancellationToken stopped;
    private readonly object sync = new();
    private readonly SemaphoreSlim writer = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Approval> approvals = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<Hello> greeting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, (string Command, TaskCompletionSource<Result> Reply)> pending = new(StringComparer.Ordinal);
    private bool closed;
    private int started;

    internal CoreSession(Stream pipe, TimeProvider? clock = null)
    {
        this.pipe = pipe;
        this.clock = clock ?? TimeProvider.System;
        stopped = lifetime.Token;
    }

    internal bool IsConnected
    {
        get { lock (sync) return !closed; }
    }

    internal event Action<Approval>? ApprovalRequested;
    internal event Action<string>? ApprovalEnded;
    internal event Action? Disconnected;

    internal Task<Hello> StartAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("The control session has already started.");
        _ = ReadLoopAsync();
        return greeting.Task.WaitAsync(token);
    }

    internal async Task<Result> RequestAsync(string type, object? body, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (closed)
                throw new IOException("The core is disconnected.");
            if (pending.Count >= 2)
                throw new InvalidOperationException("Too many pending control requests.");
            pending.Add(id, (type, reply));
        }
        try
        {
            await SendAsync(new Envelope(1, type, id, body is null ? null : JsonSerializer.SerializeToElement(body, Protocol.Json)), token)
                .ConfigureAwait(false);
            return await reply.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
                pending.Remove(id);
        }
    }

    internal bool IsCurrentApproval(string id)
    {
        lock (sync)
            return approvals.TryGetValue(id, out var approval) && approval.Deadline > clock.GetUtcNow();
    }

    internal async Task<bool> DecideAsync(string id, string scope, int candidate, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, stopped);
        if (stopped.IsCancellationRequested)
            return false;
        await writer.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                if (!approvals.TryGetValue(id, out var approval))
                    return false;
                if (approval.Deadline <= clock.GetUtcNow())
                {
                    approvals.Remove(id);
                    return false;
                }
                if (scope is not ("deny" or "once" or "client" or "host") ||
                    ((scope is "client" or "host") && !approval.CacheAllowed) ||
                    (scope == "host" && (candidate <= 0 || candidate >= approval.Chain.Length)))
                    return false;
                approvals.Remove(id);
            }

            await WriteFrameAsync(new Envelope(1, "decision", id, JsonSerializer.SerializeToElement(new
            {
                scope,
                candidate = scope == "host" ? candidate : 0
            }, Protocol.Json)), stop.Token).ConfigureAwait(false);
            return true;
        }
        finally
        {
            writer.Release();
        }
    }

    private async Task SendAsync(Envelope message, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, stopped);
        await writer.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(message, stop.Token).ConfigureAwait(false);
        }
        finally
        {
            writer.Release();
        }
    }

    private async Task WriteFrameAsync(Envelope message, CancellationToken token)
    {
        try
        {
            await Protocol.WriteAsync(pipe, message, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Cancellation can interrupt a frame mid-write. Retire the whole session.
            Dispose();
            throw;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var first = await Protocol.ReadAsync(pipe, stopped).ConfigureAwait(false);
            if (first.Type == "fatal" && first.Body is { } failure && failure.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 and <= 512 } reason)
            {
                greeting.TrySetException(new CoreStartupException(reason));
                return;
            }
            if (first.Type != "hello" || first.Id is not null || first.Body is not { } helloBody ||
                !helloBody.TryGetProperty("pipe", out var pipeName) || pipeName.ValueKind != JsonValueKind.String ||
                pipeName.GetString() is not { Length: > 0 and <= 256 } address ||
                !helloBody.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
                version.GetString() is not { Length: > 0 and <= 128 } number)
                throw new IOException("Invalid core greeting.");
            greeting.TrySetResult(new Hello(address, number));

            while (true)
            {
                var message = await Protocol.ReadAsync(pipe, stopped).ConfigureAwait(false);
                if (message.Id is not { } id)
                    throw new IOException("Missing control ID.");

                switch (message.Type)
                {
                    case "result":
                        lock (sync)
                        {
                            if (pending.TryGetValue(id, out var waiter))
                                waiter.Reply.TrySetResult(ReadResult(message, waiter.Command, address));
                        }
                        break;

                    case "approval":
                        var body = ReadBody<ApprovalBody>(message);
                        if (body.Chain is not { Length: > 0 and <= 64 } chain ||
                            chain.Any(p => p is null || p.Pid == 0 || p.Created == 0 || string.IsNullOrWhiteSpace(p.Path)) ||
                            string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Fingerprint))
                            throw new IOException("Invalid approval request.");
                        if (body.Deadline <= clock.GetUtcNow())
                            break;
                        var approval = new Approval(id, body.Name, body.Fingerprint, chain, body.Deadline, body.CacheAllowed);
                        lock (sync)
                        {
                            if (closed)
                                return;
                            foreach (var expired in approvals.Values.Where(a => a.Deadline <= clock.GetUtcNow()).ToArray())
                                approvals.Remove(expired.Id);
                            if (approvals.Count >= 64 || !approvals.TryAdd(id, approval))
                                throw new IOException("Control approval queue overflow or duplicate.");
                        }
                        ApprovalRequested?.Invoke(approval);
                        break;

                    case "approval-ended":
                        lock (sync)
                            approvals.Remove(id);
                        ApprovalEnded?.Invoke(id);
                        break;

                    default:
                        throw new IOException("Unknown control message.");
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or ObjectDisposedException or OperationCanceledException)
        { }
        finally
        {
            Dispose();
        }
    }

    private static Result ReadResult(Envelope message, string command, string pipeName)
    {
        var result = ReadBody<Result>(message);
        if (result.Error is not null && !ValidText(result.Error, 512))
            throw new IOException("Invalid control result error.");
        if (result.State is not { } state)
        {
            if ((result.Ok && command == "status") || message.Body!.Value.TryGetProperty("state", out _))
                throw new IOException("Missing control state.");
            return result;
        }

        if (!string.Equals(state.Pipe, pipeName, StringComparison.OrdinalIgnoreCase) ||
            !ValidText(state.Account, 256) ||
            state.Keys is not { Length: <= 64 } keys ||
            (state.Account.Length == 0 && keys.Length != 0) ||
            state.KeyCache is not { IsValid: true } || state.ApprovalCache is not { IsValid: true } ||
            state.Provider is not { Length: > 0 } || !ValidText(state.Provider, 64) ||
            state.Pending is < 0 or > 64)
            throw new IOException("Invalid control state.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (key is null || !ValidItemId(key.VaultId) || !ValidItemId(key.ItemId) ||
                key.Id != key.VaultId + "/" + key.ItemId || !ids.Add(key.Id) ||
                key.Name is not { Length: > 0 } || !ValidText(key.Name, 256) ||
                !ValidText(key.Fingerprint, 128) ||
                (key.Error is not null && !ValidText(key.Error, 64)))
                throw new IOException("Invalid control key state.");
        }
        return result;
    }

    private static bool ValidText(string? value, int maxLength) =>
        value is not null && value.Length <= maxLength && !value.Any(char.IsControl);

    private static bool ValidItemId(string? value) =>
        value is { Length: 26 } && value.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'));

    private static T ReadBody<T>(Envelope message) where T : class =>
        message.Body?.Deserialize<T>(Protocol.Json) ?? throw new IOException("Missing control body.");

    public void Dispose()
    {
        lock (sync)
        {
            if (closed)
                return;
            closed = true;
            approvals.Clear();
            var stopped = new IOException("The core control channel closed.");
            greeting.TrySetException(stopped);
            foreach (var waiter in pending.Values)
                waiter.Reply.TrySetException(stopped);
            pending.Clear();
        }
        lifetime.Cancel();
        lifetime.Dispose();
        pipe.Dispose();
        Disconnected?.Invoke();
    }

    private sealed record ApprovalBody(string Name, string Fingerprint, ProcessIdentity[] Chain, DateTimeOffset Deadline, bool CacheAllowed);
}
