using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Opssh.Tray;

internal static class GoInterop
{
    internal static async Task CheckAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPSSH_FIXTURE_DIRECTORY")))
            throw new InvalidOperationException("Set OPSSH_FIXTURE_DIRECTORY to an isolated offline fixture directory.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        using var core = await CoreProcess.StartAsync(token);
        using var session = new CoreSession(core.Pipe);
        var request = new TaskCompletionSource<Approval>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ApprovalRequested += approval => request.TrySetResult(approval);
        var hello = await session.StartAsync(token);
        const string prefix = @"\\.\pipe\";
        Require(hello.Pipe.StartsWith(prefix + "opssh-", StringComparison.OrdinalIgnoreCase), "local SSH endpoint");
        Require((await session.RequestAsync("status", null, token)) is
            { Ok: true, State: { Account: "", Keys: [] } }, "fresh core has no configured account or key");
        Require((await session.RequestAsync("connect", new { account = "Offline fixture" }, token)) is
            { Ok: true, State: { Keys: [{ VaultId: "aaaaaaaaaaaaaaaaaaaaaaaaaa", ItemId: "bbbbbbbbbbbbbbbbbbbbbbbbbb" }] } },
            "tray-led connection indexes the generated public key without manual IDs");
        Require((await session.RequestAsync("status", null, token)) is
            {
                Ok: true, State:
                {
                    Account: "Offline fixture", Provider: "offline-fixture", Locked: false, Pending: 0,
                    Keys: [{ Name: "Generated offline key", Loaded: false }],
                    KeyCache: { Mode: "fixed", Seconds: 300 }, ApprovalCache: { Mode: "fixed", Seconds: 600 }
                }
            }, "initialized fixture state");

        using var ssh = new NamedPipeClientStream(".", hello.Pipe[prefix.Length..], PipeDirection.InOut, PipeOptions.Asynchronous);
        await ssh.ConnectAsync(token);
        await SendPacketAsync(ssh, new byte[] { 11 }, token);
        var identities = await ReadPacketAsync(ssh, token);
        Require(identities is [12, ..] && identities.Length >= 9, "SSH identities answer");
        Require(BinaryPrimitives.ReadUInt32BigEndian(identities.AsSpan(1)) == 1, "one indexed identity");
        using var entries = new MemoryStream(identities, 5, identities.Length - 5);
        var blob = ReadString(entries);
        Require(Encoding.UTF8.GetString(ReadString(entries)) == "Generated offline key" && entries.Position == entries.Length,
            "fixture identity comment");

        const string vault = "aaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string item = vault + "/bbbbbbbbbbbbbbbbbbbbbbbbbb";
        Require((await session.RequestAsync("set-scope", new
        {
            @default = "include",
            vaults = new Dictionary<string, string> { [vault] = "exclude" },
            items = new Dictionary<string, string>()
        }, token)) is { Ok: true, State: { Keys: [] } }, "vault exclusion withdraws the public identity");
        await SendPacketAsync(ssh, new byte[] { 11 }, token);
        Require((await ReadPacketAsync(ssh, token)) is [12, 0, 0, 0, 0], "SSH sees no identity after scope narrowing");

        using var sign = new MemoryStream();
        sign.WriteByte(13);
        WriteString(sign, blob);
        WriteString(sign, RandomNumberGenerator.GetBytes(32));
        sign.Write(new byte[4]); // Default Ed25519 flags.
        await SendPacketAsync(ssh, sign.ToArray(), token);
        Require((await ReadPacketAsync(ssh, token)) is [5] && !request.Task.IsCompleted,
            "excluded identity cannot sign or prompt for approval");
        Require((await session.RequestAsync("set-scope", new
        {
            @default = "include",
            vaults = new Dictionary<string, string> { [vault] = "exclude" },
            items = new Dictionary<string, string> { [item] = "include" }
        }, token)) is { Ok: true, State: { Keys: [{ Loaded: false }] } }, "explicit item inclusion overrides the vault exclusion");
        var preload = await session.RequestAsync("preload", new { keyId = item }, token);
        Require(preload is { Ok: true, State.Keys: [{ Loaded: true, Grants: 0 }] } &&
            preload.Data?.GetProperty("loaded").GetInt32() == 1 && !request.Task.IsCompleted,
            "preload caches only private material through the real control channel");
        var repeated = await session.RequestAsync("preload-all", null, token);
        Require(repeated is { Ok: true, State.Keys: [{ Loaded: true, Grants: 0 }] } &&
            repeated.Data?.GetProperty("alreadyLoaded").GetInt32() == 1, "bulk preload skips already loaded keys");
        var logging = await session.RequestAsync("diagnostics", new { enabled = true, epoch = 1, after = 0 }, token);
        Require(DiagnosticEvent.TryReadCoreBatch(logging, 1, 0, out var initialLogs, out _) && initialLogs.Count == 0,
            "enable diagnostics without replaying requests made before consent");
        await SendPacketAsync(ssh, sign.ToArray(), token);
        var approval = await request.Task.WaitAsync(token);
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        Require(approval.Name == "Generated offline key" && approval.Fingerprint == fingerprint && approval.CacheAllowed &&
            approval.Chain[0].Pid == Environment.ProcessId && approval.Chain[0].Created != 0,
            "approval carries the selected key and real requester identity");
        Require(await session.DecideAsync(approval, "once", 0, token), "once decision sent");
        var signed = await ReadPacketAsync(ssh, token);
        Require(signed is [14, ..], "SSH signature response");
        using var signatures = new MemoryStream(signed, 1, signed.Length - 1);
        using var signature = new MemoryStream(ReadString(signatures));
        Require(Encoding.ASCII.GetString(ReadString(signature)) == "ssh-ed25519" && ReadString(signature).Length == 64 &&
            signature.Position == signature.Length && signatures.Position == signatures.Length, "valid signature framing");
        ssh.Dispose();
        for (;;)
        {
            var history = await session.RequestAsync("activity", null, token);
            Require(history.Ok && history.Data is not null, "the core exposes local activity metadata");
            if (history.Data!.Value.GetProperty("entries").EnumerateArray().Any(row =>
                    row.GetProperty("result").GetString() == "published" &&
                    row.GetProperty("key").GetString() == "Generated offline key" &&
                    row.GetProperty("pid").GetUInt32() == Environment.ProcessId))
                break;
            await Task.Delay(40, token);
        }
        var diagnostics = await session.RequestAsync("diagnostics", new { enabled = true, epoch = 1, after = 0 }, token);
        Require(DiagnosticEvent.TryReadCoreBatch(diagnostics, 1, 0, out var logs, out var lastSequence) &&
            lastSequence == 1 && logs is [{ Source: "core", Phase: "sign", Result: "published", Steps:
            [
                { Stage: "peer", Outcome: "verified" },
                { Stage: "request", Outcome: "accepted" },
                { Stage: "approval", Outcome: "allowed" },
                { Stage: "key-cache", Outcome: "hit" },
                { Stage: "crypto", Outcome: "signed" },
                { Stage: "publish", Outcome: "published" }
            ] }] && diagnostics.Data!.Value.GetProperty("events")[0].EnumerateObject().Count() == 5,
            "the core reports a bounded event without signing or requester metadata");
        var cleared = await session.RequestAsync("diagnostics", new { enabled = true, epoch = 2, after = 0 }, token);
        Require(DiagnosticEvent.TryReadCoreBatch(cleared, 2, 0, out var remainingLogs, out _) && remainingLogs.Count == 0,
            "the new log generation starts empty");
        Require((await session.RequestAsync("clear-activity", null, token)).Ok, "clear activity");
        using var replacement = await CoreProcess.StartAsync(token, candidate: true);
        using var candidate = new CoreSession(replacement.Pipe);
        Require(await candidate.StartAsync(token) == hello, "candidate proposes the same endpoint");
        const string selectedAccount = "cccccccccccccccccccccccccc";
        Require((await candidate.RequestAsync("prepare-account", new { account = selectedAccount, name = "Another offline account" }, token)).Ok,
            "candidate obtains vault metadata without an SSH listener");
        Require((await candidate.RequestAsync("activate-account", null, token)).ErrorCode == "pipe-unavailable",
            "candidate cannot replace the active listener prematurely");
        Require((await session.RequestAsync("status", null, token)).State?.Account == "Offline fixture",
            "old account serves while candidate authorization is prepared");
        Require((await session.RequestAsync("pause-ssh", null, token)).Ok, "pause the old endpoint");
        Require((await session.RequestAsync("resume-ssh", null, token)).Ok, "resume the old endpoint before commit");
        Require((await session.RequestAsync("pause-ssh", null, token)).Ok, "release the old endpoint for handover");
        Require((await candidate.RequestAsync("activate-account", null, token)) is
            { Ok: true, State: { Account: selectedAccount, Keys: [], Vaults: [{ Id: vault }], Scope.Default: "exclude" } },
            "successful handover saves the new account and its empty public index");
        Require((await session.RequestAsync("resume-ssh", null, token)).ErrorCode == "account-changed",
            "an old process cannot resume after the durable commit");
        Require((await candidate.RequestAsync("shutdown", null, token)).Ok, "candidate shutdown acknowledgement");
        Require(await replacement.WaitAsync(TimeSpan.FromSeconds(4)), "graceful replacement exit");
        Require((await session.RequestAsync("shutdown", null, token)).Ok, "shutdown acknowledgement");
        Require(await core.WaitAsync(TimeSpan.FromSeconds(4)), "graceful Go core exit");
        Console.WriteLine("Go interop passed (tray-led discovery, vault and item scope, local SSH list/sign approval, shutdown).");
    }

    internal static async Task CheckProductionAsync(string version)
    {
        var localData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (localData is null || !Path.IsPathFullyQualified(localData) || !Directory.Exists(localData) ||
            Directory.EnumerateFileSystemEntries(localData).Any())
            throw new InvalidOperationException("The production handshake requires an isolated empty LOCALAPPDATA directory.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        using var core = await CoreProcess.StartAsync(token);
        using var session = new CoreSession(core.Pipe);
        var hello = await session.StartAsync(token);
        Require(hello.Version == version, "production core version matches the package");
        Require((await session.RequestAsync("status", null, token)) is
            { Ok: true, State: { Account: "", Keys: [] } }, "production core starts unconfigured");
        Require((await session.RequestAsync("shutdown", null, token)).Ok, "production shutdown acknowledgement");
        Require(await core.WaitAsync(TimeSpan.FromSeconds(4)), "graceful production core exit");
        Console.WriteLine("Production core handshake and shutdown passed.");
    }

    private static async Task SendPacketAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        using var packet = new MemoryStream();
        WriteString(packet, payload);
        await stream.WriteAsync(packet.ToArray(), token);
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken token)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, token);
        var size = BinaryPrimitives.ReadUInt32BigEndian(length);
        Require(size is > 0 and <= 256 * 1024, "bounded SSH packet");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, token);
        return payload;
    }

    private static byte[] ReadString(Stream stream)
    {
        Span<byte> length = stackalloc byte[4];
        stream.ReadExactly(length);
        var size = BinaryPrimitives.ReadUInt32BigEndian(length);
        Require(size <= 256 * 1024, "bounded SSH field");
        var payload = new byte[size];
        stream.ReadExactly(payload);
        return payload;
    }

    private static void WriteString(Stream stream, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(data);
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException("Go interop check failed: " + scenario);
    }
}
