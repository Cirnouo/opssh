using System.Text.Json;

namespace Opssh.Tray;

internal sealed record DiagnosticEvent(DateTimeOffset Time, string Source, string Phase, string Result, long ElapsedMs)
{
    public string Level => Result switch
    {
        "failed" => "ERROR",
        "timeout" or "disconnected" => "WARN",
        "canceled" => "DEBUG",
        _ => "INFO"
    };

    internal bool IsValid => Time != default && ElapsedMs is >= 0 and <= 86_400_000 &&
        ((Source, Phase, Result) switch
        {
            ("core", "sign", "published" or "denied" or "timeout" or "canceled" or "failed") => true,
            ("tray", "core-start", "completed" or "timeout" or "failed") => true,
            ("tray", "core-session", "disconnected") => true,
            ("tray", "discover" or "provider-access", "completed" or "timeout" or "canceled" or "failed") => true,
            _ => false
        });

    internal static bool ValidLevel(string? level) => level is "ERROR" or "WARN" or "INFO" or "DEBUG";

    internal bool IsVisibleAt(string threshold) => Level switch
    {
        "ERROR" => true,
        "WARN" => threshold is "WARN" or "INFO" or "DEBUG",
        "INFO" => threshold is "INFO" or "DEBUG",
        _ => threshold == "DEBUG"
    };

    internal static bool TryReadCoreBatch(Result result, long epoch, long after, out List<DiagnosticEvent> entries, out long latest)
    {
        entries = [];
        latest = after;
        if (!result.Ok || result.Data is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("epoch", out var generation) || generation.ValueKind != JsonValueKind.Number ||
            !generation.TryGetInt64(out var actual) || actual != epoch ||
            !data.TryGetProperty("events", out var batch) || batch.ValueKind != JsonValueKind.Array || batch.GetArrayLength() > 32)
            return false;

        foreach (var row in batch.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("sequence", out var sequence) || sequence.ValueKind != JsonValueKind.Number ||
                !sequence.TryGetInt64(out var next) || next <= latest ||
                !row.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.String ||
                !time.TryGetDateTimeOffset(out var date) ||
                !row.TryGetProperty("result", out var outcome) || outcome.ValueKind != JsonValueKind.String ||
                outcome.GetString() is not { Length: <= 9 } label ||
                !row.TryGetProperty("elapsedMs", out var elapsed) || elapsed.ValueKind != JsonValueKind.Number ||
                !elapsed.TryGetInt64(out var duration))
                return false;
            var entry = new DiagnosticEvent(date.ToUniversalTime(), "core", "sign", label, duration);
            if (!entry.IsValid)
                return false;
            entries.Add(entry);
            latest = next;
        }
        return true;
    }
}

internal readonly record struct DiagnosticLogFile(string Name, long Bytes);

internal sealed record DiagnosticLogState(IReadOnlyList<DiagnosticLogFile> Files)
{
    internal long Bytes => Files.Sum(file => file.Bytes);
}
