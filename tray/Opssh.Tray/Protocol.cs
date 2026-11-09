using System.Buffers.Binary;
using System.Text.Json;

namespace Opssh.Tray;

internal sealed record Envelope(int V, string Type, string? Id = null, JsonElement? Body = null);

internal static class Protocol
{
    private const int MaxPayload = 256 * 1024;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
        MaxDepth = 16
    };

    internal static async ValueTask<Envelope> ReadAsync(Stream pipe, CancellationToken token)
    {
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(FrameTimeout);
        try
        {
            await pipe.ReadExactlyAsync(header.AsMemory(1), deadline.Token).ConfigureAwait(false);
            var size = BinaryPrimitives.ReadInt32BigEndian(header);
            if (size is <= 0 or > MaxPayload)
                throw new IOException("Invalid control frame size.");

            var payload = new byte[size];
            await pipe.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new IOException("Invalid control envelope.");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!fields.Add(property.Name) || property.Name is not ("v" or "type" or "id" or "body"))
                    throw new IOException("Invalid control envelope.");
            }

            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var v) || v != 1 ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() is not { Length: > 0 and <= 32 } name)
                throw new IOException("Unsupported control envelope.");

            string? id = null;
            if (root.TryGetProperty("id", out var requestId))
            {
                if (requestId.ValueKind != JsonValueKind.String || requestId.GetString() is not { Length: > 0 and <= 64 } value)
                    throw new IOException("Invalid control ID.");
                id = value;
            }

            JsonElement? body = null;
            if (root.TryGetProperty("body", out var content))
            {
                if (content.ValueKind != JsonValueKind.Object)
                    throw new IOException("Invalid control body.");
                body = content.Clone();
            }
            return new Envelope(v, name, id, body);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new IOException("Control frame timed out.");
        }
        catch (JsonException)
        {
            throw new IOException("Invalid control JSON.");
        }
    }

    internal static async ValueTask WriteAsync(Stream pipe, Envelope message, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (payload.Length > MaxPayload)
            throw new IOException("Control frame is too large.");

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await pipe.WriteAsync(frame, token).ConfigureAwait(false);
    }
}
