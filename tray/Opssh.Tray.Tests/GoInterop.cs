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

        using var sign = new MemoryStream();
        sign.WriteByte(13);
        WriteString(sign, blob);
        WriteString(sign, RandomNumberGenerator.GetBytes(32));
        sign.Write(new byte[4]); // Default Ed25519 flags.
        await SendPacketAsync(ssh, sign.ToArray(), token);
        var approval = await request.Task.WaitAsync(token);
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        Require(approval.Name == "Generated offline key" && approval.Fingerprint == fingerprint && approval.CacheAllowed &&
            approval.Chain[0].Pid == Environment.ProcessId && approval.Chain[0].Created != 0,
            "approval carries the selected key and real requester identity");
        Require(await session.DecideAsync(approval.Id, "once", 0, token), "once decision sent");
        var signed = await ReadPacketAsync(ssh, token);
        Require(signed is [14, ..], "SSH signature response");
        using var signatures = new MemoryStream(signed, 1, signed.Length - 1);
        using var signature = new MemoryStream(ReadString(signatures));
        Require(Encoding.ASCII.GetString(ReadString(signature)) == "ssh-ed25519" && ReadString(signature).Length == 64 &&
            signature.Position == signature.Length && signatures.Position == signatures.Length, "valid signature framing");
        ssh.Dispose();
        Require((await session.RequestAsync("shutdown", null, token)).Ok, "shutdown acknowledgement");
        Require(await core.WaitAsync(TimeSpan.FromSeconds(4)), "graceful Go core exit");
        Console.WriteLine("Go interop passed (Windows bootstrap, state, local SSH list/sign approval, shutdown).");
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
