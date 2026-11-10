using System.Text.Json;
using Opssh.Tray;

internal static class DiagnosticChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "opssh-diagnostics-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "logs");
        var store = new DiagnosticLogStore(directory);
        try
        {
            Require(store.Read() is { Bytes: 0, Files.Count: 0 } && !Directory.Exists(directory),
                "inspecting an empty store creates no directory");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "unrelated");
            foreach (var file in new[] { "opssh.log", "opssh.1.log", "opssh.2.log" })
            {
                using var output = File.Create(Path.Combine(directory, file));
                output.SetLength(1024 * 1024);
            }

            var entry = new DiagnosticEvent(DateTimeOffset.UnixEpoch, "core", "sign", "published", 42);
            Require(store.Append(Guid.Empty, [entry]) is { Saved: true }, "append rotates a full active log");
            var current = Path.Combine(directory, "opssh.log");
            var rows = File.ReadAllLines(current);
            Require(rows.Length == 1 && store.Read() is { Bytes: > 2097152 and <= 3145728 },
                "append retains a bounded active file and two rotations");
            Require(store.Read() is { } snapshot &&
                snapshot.Files.Select(file => file.Name).SequenceEqual(new[] { "opssh.log", "opssh.1.log", "opssh.2.log" }) &&
                snapshot.Files[0].Bytes == new FileInfo(current).Length,
                "the log directory snapshot lists the managed files and their actual sizes");
            using var record = JsonDocument.Parse(rows[0]);
            Require(record.RootElement.GetProperty("result").GetString() == "published" &&
                record.RootElement.GetProperty("elapsedMs").GetInt64() == 42 &&
                record.RootElement.EnumerateObject().Count() == 6, "persist only the fixed event schema");
            Require(store.Append(Guid.Empty, [entry with { Result = "untrusted error" }]) is null &&
                File.ReadAllLines(current).SequenceEqual(rows), "refuse an event with an unrecognized outcome");
            var anotherLogon = new DiagnosticLogStore(directory);
            var previous = anotherLogon.ReadGeneration();
            Require(store.Clear() is not null && store.Read() is { Bytes: 0, Files.Count: 0 } &&
                File.Exists(Path.Combine(directory, "keep.txt")), "clear only the managed files");
            File.WriteAllBytes(current, []);
            Require(store.Read() is { Bytes: 0, Files.Count: 1 } && store.Clear() is not null &&
                store.Read() is { Bytes: 0, Files.Count: 0 }, "list and clear an empty managed log");
            Require(previous == Guid.Empty && anotherLogon.Append(Guid.Empty, [entry]) is { Saved: false } stale &&
                stale.Generation != Guid.Empty && anotherLogon.Read() is { Bytes: 0 } &&
                anotherLogon.Append(stale.Generation, [entry]) is { Saved: true },
                "a clear prevents old batches from another writer from re-creating logs");

            CheckPreload(Path.Combine(root, "preload"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckPreload(string directory)
    {
        using var session = new CoreSession(Stream.Null);
        var store = new DiagnosticLogStore(directory);
        var bulk = new PreloadDiagnostic("preload-all", session, 1, Guid.Empty);
        var start = bulk.Started();
        var partial = bulk.Complete(new Result(false, "fixture-provider-detail", null, false, "preload-partial",
            JsonSerializer.SerializeToElement(new
            {
                selected = 4, loaded = 1, alreadyLoaded = 1, failed = 1, skipped = 1,
                keyId = "fixture-item-identifier"
            }))) ?? throw new InvalidOperationException("Missing bulk preload completion.");
        Require(start.IsVisibleAt("DEBUG") && !start.IsVisibleAt("INFO") && partial.IsVisibleAt("WARN") &&
            !partial.IsVisibleAt("ERROR"), "preload start and partial completion respect severity thresholds");
        Require(bulk.Fail() is null && store.Append(Guid.Empty, [start, partial]) is { Saved: true },
            "a second terminal signal cannot record another bulk completion");

        var single = new PreloadDiagnostic("preload", session, 1, Guid.Empty);
        var failed = single.Complete(new Result(false, "fixture-provider-detail", null, false, "private-field-missing",
            JsonSerializer.SerializeToElement(new { selected = 1, loaded = 0, alreadyLoaded = 0, failed = 1, skipped = 0 })))
            ?? throw new InvalidOperationException("Missing single preload completion.");
        Require(failed.IsVisibleAt("ERROR") && store.Append(Guid.Empty, [single.Started(), failed]) is { Saved: true },
            "a specific single-key failure is visible at ERROR");

        var path = Path.Combine(directory, "opssh.log");
        var rows = File.ReadAllLines(path);
        Require(rows.Length == 4 && !rows.Any(row => row.Contains("fixture-", StringComparison.Ordinal)),
            "single and bulk requests persist one start and one completion without reply text or identifiers");
        using var bulkStart = JsonDocument.Parse(rows[0]);
        using var bulkCompletion = JsonDocument.Parse(rows[1]);
        using var singleStart = JsonDocument.Parse(rows[2]);
        using var singleCompletion = JsonDocument.Parse(rows[3]);
        Require(bulkStart.RootElement.GetProperty("phase").GetString() == "preload-all" &&
            bulkStart.RootElement.GetProperty("result").GetString() == "started" &&
            !bulkStart.RootElement.TryGetProperty("counts", out _) &&
            singleStart.RootElement.GetProperty("phase").GetString() == "preload" &&
            singleStart.RootElement.GetProperty("result").GetString() == "started", "start records contain only the safe phase and metadata");
        var completion = bulkCompletion.RootElement;
        var counts = completion.GetProperty("counts");
        Require(completion.GetProperty("result").GetString() == "preload-partial" &&
            completion.GetProperty("elapsedMs").GetInt64() >= 0 &&
            counts.EnumerateObject().Count() == 5 && counts.GetProperty("selected").GetInt32() == 4 &&
            counts.GetProperty("loaded").GetInt32() == 1 && counts.GetProperty("alreadyLoaded").GetInt32() == 1 &&
            counts.GetProperty("failed").GetInt32() == 1 && counts.GetProperty("skipped").GetInt32() == 1 &&
            singleCompletion.RootElement.GetProperty("result").GetString() == "private-field-missing",
            "completion preserves elapsed time, bounded counts and the allowlisted failure code");

        var malformed = new PreloadDiagnostic("preload", session, 1, Guid.Empty).Complete(new Result(
            false, null, null, false, "fixture-unrecognized-code",
            JsonSerializer.SerializeToElement(new { selected = 1, loaded = -1, alreadyLoaded = 0, failed = 2, skipped = 0 })));
        Require(malformed is { Result: "failed", Counts: null }, "unrecognized codes and invalid counts cannot be persisted");

        var timeout = new PreloadDiagnostic("preload", session, 1, Guid.Empty).Fail(new OperationCanceledException());
        session.Dispose();
        var disconnected = new PreloadDiagnostic("preload-all", session, 1, Guid.Empty).Fail();
        Require(timeout is { Result: "timeout", Level: "WARN", Counts: null } &&
            disconnected is { Result: "disconnected", Level: "WARN", Counts: null },
            "transport timeout and disconnection have safe outcomes without inferred counts");
        Require(store.Append(Guid.Empty, [failed], () => false) is null && File.ReadAllLines(path).SequenceEqual(rows),
            "a stale queued observation cannot write to the log");
        var fresh = store.Clear();
        Require(fresh is { } generation && generation != Guid.Empty &&
            store.Append(Guid.Empty, [failed]) is { Saved: false } && !File.Exists(path),
            "completion from before clear cannot recreate a log");
        Require(store.Append(fresh, [failed]) is { Saved: true } &&
            new DiagnosticLogStore(directory).Append(null, [failed]) is { Saved: true },
            "post-clear commands and initial writers can record without waiting for the collector");
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException("Diagnostic checks failed: " + scenario);
    }
}
