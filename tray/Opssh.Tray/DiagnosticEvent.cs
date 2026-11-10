using System.Diagnostics;
using System.Text.Json;

namespace Opssh.Tray;

internal sealed record DiagnosticEvent(DateTimeOffset Time, string Source, string Phase, string Result, long ElapsedMs,
    DiagnosticPreloadCounts? Counts = null, DiagnosticStep[]? Steps = null)
{
    public string Level => Phase is "preload" or "preload-all" ? PreloadLevel(Result) ?? "ERROR" : Result switch
    {
        "failed" => "ERROR",
        "timeout" or "disconnected" or "unavailable" => "WARN",
        "canceled" => "DEBUG",
        _ => "INFO"
    };

    internal bool IsValid => Time != default && ElapsedMs is >= 0 and <= 86_400_000 &&
        (Steps is null || ((Source == "core" && Phase == "sign") ||
            (Source == "tray" && Phase is ("preload" or "preload-all") && Result is not ("started" or "timeout" or "disconnected" or "control-failed"))) &&
            DiagnosticStep.Valid(Steps, Phase == "sign")) &&
        ((Source, Phase, Result) switch
        {
            ("core", "sign", "published" or "denied" or "timeout" or "canceled" or "failed" or "unavailable") => Counts is null,
            ("tray", "core-start", "completed" or "timeout" or "failed") => Counts is null,
            ("tray", "core-session", "disconnected") => Counts is null,
            ("tray", "discover" or "provider-access", "completed" or "timeout" or "canceled" or "failed") => Counts is null,
            ("tray", "preload" or "preload-all", "started") => ElapsedMs == 0 && Counts is null,
            ("tray", "preload" or "preload-all", "timeout" or "disconnected" or "control-failed") => Counts is null,
            ("tray", "preload" or "preload-all", _) => PreloadLevel(Result) is not null &&
                (Counts is null || Counts.IsValid(Phase == "preload" ? 1 : 64)),
            _ => false
        });

    private static string? PreloadLevel(string? outcome) => outcome switch
    {
        "started" or "canceled" => "DEBUG",
        "completed" => "INFO",
        "timeout" or "disconnected" or "provider-timeout" or "preload-partial" or "desktop-session-expired" or
            "desktop-access-denied" or "desktop-busy" or "cache-off" or "locked" or "busy" or "too-many-commands" or
            "stale" or "unknown-key" or "restart-required" => "WARN",
        "failed" or "control-failed" or "request-failed" or "invalid-command" or "private-field-missing" or
            "private-field-invalid" or "invalid-key" or "key-mismatch" or "private-lookup" => "ERROR",
        _ => null
    };

    internal static string PreloadReplyOutcome(Result reply) => reply.Ok ? "completed" : reply.ErrorCode switch
    {
        { } code when code is not ("started" or "completed" or "timeout" or "disconnected" or "control-failed") &&
            PreloadLevel(code) is not null => code,
        _ => "failed"
    };

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
                outcome.GetString() is not { Length: <= 11 } label ||
                !row.TryGetProperty("elapsedMs", out var elapsed) || elapsed.ValueKind != JsonValueKind.Number ||
                !elapsed.TryGetInt64(out var duration))
                return false;
            var entry = new DiagnosticEvent(date.ToUniversalTime(), "core", "sign", label, duration,
                Steps: DiagnosticStep.Read(row, signing: true));
            if (!entry.IsValid)
                return false;
            entries.Add(entry);
            latest = next;
        }
        return true;
    }
}

internal sealed record DiagnosticStep(string Stage, string Outcome, long ElapsedMs)
{
    private int Order(bool signing) => Stage switch
    {
        "peer" when signing && Outcome is ("verified" or "unverified") => 1,
        "request" when Outcome is "accepted" or "invalid" or "unknown-key" or "unsupported-flags" or "locked" or
            "stale" or "busy" or "canceled" or "cache-off" => 2,
        "approval" when signing && Outcome is ("cached" or "allowed" or "denied" or "unavailable" or "timeout" or
            "busy" or "canceled" or "stale") => 3,
        "key-cache" when Outcome is "hit" or "miss" or "joined" or "busy" or "stale" or "canceled" => 4,
        "material-wait" when Outcome is "ready" or "failed" or "timeout" or "canceled" or "stale" => 5,
        "provider-read" when Outcome is "resolved" or "field-not-found" or "empty" or "invalid" or "session-expired" or
            "denied" or "busy" or "unavailable" or "timeout" or "canceled" => 5,
        "private-parse" when Outcome is "parsed" or "invalid" => 6,
        "public-match" when Outcome is "matched" or "mismatch" => 7,
        "crypto" when signing && Outcome is ("signed" or "invalid" or "stale" or "canceled") => 8,
        "publish" when signing && Outcome is ("published" or "failed" or "stale" or "canceled") => 9,
        _ => 0
    };

    internal static bool Valid(DiagnosticStep[] steps, bool signing)
    {
        if (steps.Length is < 1 or > 9)
            return false;
        var previous = 0;
        foreach (var step in steps)
        {
            var order = step.Order(signing);
            if (order <= previous || step.ElapsedMs is < 0 or > 86_400_000)
                return false;
            previous = order;
        }
        return true;
    }

    internal static DiagnosticStep[]? Read(JsonElement body, bool signing)
    {
        if (!body.TryGetProperty("steps", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 9)
            return null;
        var steps = new DiagnosticStep[array.GetArrayLength()];
        var index = 0;
        foreach (var row in array.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 3 ||
                !row.TryGetProperty("stage", out var stage) || stage.ValueKind != JsonValueKind.String ||
                stage.GetString() is not { Length: > 0 and <= 13 } name ||
                !row.TryGetProperty("outcome", out var outcome) || outcome.ValueKind != JsonValueKind.String ||
                outcome.GetString() is not { Length: > 0 and <= 17 } label ||
                !row.TryGetProperty("elapsedMs", out var elapsed) || elapsed.ValueKind != JsonValueKind.Number ||
                !elapsed.TryGetInt64(out var duration))
                return null;
            steps[index++] = new DiagnosticStep(name, label, duration);
        }
        return Valid(steps, signing) ? steps : null;
    }

    internal static DiagnosticStep[]? ReadPreload(JsonElement? data, long epoch) =>
        data is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("stepsEpoch", out var generation) &&
        generation.ValueKind == JsonValueKind.Number && generation.TryGetInt64(out var actual) && actual == epoch
            ? Read(body, signing: false) : null;
}

internal sealed record DiagnosticPreloadCounts(int Selected, int Loaded, int AlreadyLoaded, int Failed, int Skipped)
{
    internal bool IsValid(int limit) => Selected >= 0 && Selected <= limit &&
        Loaded >= 0 && Loaded <= Selected && AlreadyLoaded >= 0 && AlreadyLoaded <= Selected &&
        Failed >= 0 && Failed <= Selected && Skipped >= 0 && Skipped <= Selected &&
        Loaded + AlreadyLoaded + Failed + Skipped == Selected;

    internal static DiagnosticPreloadCounts? Read(JsonElement? data, int limit)
    {
        if (data is not { ValueKind: JsonValueKind.Object } body || !ReadCount(body, "selected", out var selected) ||
            !ReadCount(body, "loaded", out var loaded) || !ReadCount(body, "alreadyLoaded", out var alreadyLoaded) ||
            !ReadCount(body, "failed", out var failed) || !ReadCount(body, "skipped", out var skipped))
            return null;
        var counts = new DiagnosticPreloadCounts(selected, loaded, alreadyLoaded, failed, skipped);
        return counts.IsValid(limit) ? counts : null;
    }

    private static bool ReadCount(JsonElement body, string name, out int count)
    {
        count = 0;
        return body.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out count);
    }
}

internal sealed class PreloadDiagnostic(string phase, CoreSession session, long epoch, Guid? generation)
{
    private readonly long start = Stopwatch.GetTimestamp();
    private bool finished;

    internal CoreSession Session { get; } = session;
    internal long Epoch { get; } = epoch;
    internal Guid? Generation { get; } = generation;

    internal DiagnosticEvent Started() => new(DateTimeOffset.UtcNow, "tray", phase, "started", 0);

    internal DiagnosticEvent? Complete(Result reply) => Finish(DiagnosticEvent.PreloadReplyOutcome(reply),
        DiagnosticPreloadCounts.Read(reply.Data, phase == "preload" ? 1 : 64), DiagnosticStep.ReadPreload(reply.Data, Epoch));

    internal DiagnosticEvent? Fail(Exception? error = null) => Finish(!Session.IsConnected ? "disconnected" :
        error is OperationCanceledException ? "timeout" : "control-failed", null);

    private DiagnosticEvent? Finish(string outcome, DiagnosticPreloadCounts? counts, DiagnosticStep[]? steps = null)
    {
        if (finished)
            return null;
        finished = true;
        var elapsed = Math.Min((long)Stopwatch.GetElapsedTime(start).TotalMilliseconds, 86_400_000);
        return new DiagnosticEvent(DateTimeOffset.UtcNow, "tray", phase, outcome, elapsed, counts, steps);
    }
}

internal readonly record struct DiagnosticLogFile(string Name, long Bytes);

internal sealed record DiagnosticLogState(IReadOnlyList<DiagnosticLogFile> Files)
{
    internal long Bytes => Files.Sum(file => file.Bytes);
}
