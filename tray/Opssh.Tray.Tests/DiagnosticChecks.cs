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
            Require(store.Clear() && store.Read() is { Bytes: 0, Files.Count: 0 } &&
                File.Exists(Path.Combine(directory, "keep.txt")), "clear only the managed files");
            File.WriteAllBytes(current, []);
            Require(store.Read() is { Bytes: 0, Files.Count: 1 } && store.Clear() &&
                store.Read() is { Bytes: 0, Files.Count: 0 }, "list and clear an empty managed log");
            Require(previous == Guid.Empty && anotherLogon.Append(Guid.Empty, [entry]) is { Saved: false } stale &&
                stale.Generation != Guid.Empty && anotherLogon.Read() is { Bytes: 0 } &&
                anotherLogon.Append(stale.Generation, [entry]) is { Saved: true },
                "a clear prevents old batches from another writer from re-creating logs");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException("Diagnostic checks failed: " + scenario);
    }
}
