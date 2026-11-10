using System.Text.Json;

namespace Opssh.Tray;

internal sealed class DiagnosticLogStore(string directory)
{
    private const int MaxFileBytes = 1024 * 1024;
    private static readonly string[] FileNames = ["opssh.log", "opssh.1.log", "opssh.2.log"];
    private readonly object sync = new();
    private readonly SemaphoreSlim queue = new(5);
    private readonly SemaphoreSlim worker = new(1);

    internal static DiagnosticLogStore ForCurrentUser() => new(
        Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "opssh", "logs"));

    internal async Task<T> RunAsync<T>(Func<T> action, T busy)
    {
        if (!queue.Wait(0))
            return busy;
        try
        {
            await worker.WaitAsync().ConfigureAwait(false);
            try { return await Task.Run(action).ConfigureAwait(false); }
            finally { worker.Release(); }
        }
        finally { queue.Release(); }
    }

    internal DiagnosticLogState? Read() => Access<DiagnosticLogState?>(create: false, empty: new DiagnosticLogState([]), failure: null, _ =>
    {
        var files = new List<DiagnosticLogFile>(FileNames.Length);
        foreach (var name in FileNames)
        {
            var path = Path.Combine(directory, name);
            if (FileExists(path))
                files.Add(new DiagnosticLogFile(name, new FileInfo(path).Length));
        }
        return new DiagnosticLogState(files);
    });

    internal string? OpenDirectory() => Access<string?>(create: true, empty: null, failure: null, _ => directory);

    internal Guid? ReadGeneration() => Access<Guid?>(create: false, empty: Guid.Empty, failure: null, ownership => ReadGeneration(ownership));

    internal Guid? Clear() => Access<Guid?>(create: false, empty: Guid.Empty, failure: null, ownership =>
    {
        // Advance the shared generation under the same lock used to append.
        Span<byte> marker = stackalloc byte[16];
        var generation = Guid.NewGuid();
        generation.TryWriteBytes(marker);
        ownership.Write(marker);
        ownership.SetLength(marker.Length);
        ownership.Flush(flushToDisk: true);
        foreach (var name in FileNames)
        {
            var path = Path.Combine(directory, name);
            if (FileExists(path))
                File.Delete(path);
        }
        return generation;
    });

    internal (bool Saved, Guid Generation)? Append(Guid? generation, IReadOnlyList<DiagnosticEvent> entries, Func<bool>? isCurrent = null)
    {
        if (isCurrent?.Invoke() == false || entries.Count > 32 || entries.Any(entry => !entry.IsValid))
            return null;
        using var payload = new MemoryStream();
        foreach (var entry in entries)
        {
            JsonSerializer.Serialize(payload, entry with { Time = entry.Time.ToUniversalTime() }, Protocol.Json);
            payload.WriteByte(10);
        }

        return Access<(bool, Guid)?>(create: entries.Count != 0, empty: (generation is null || generation == Guid.Empty, Guid.Empty), failure: null, ownership =>
        {
            if (isCurrent?.Invoke() == false)
                return null;
            var current = ReadGeneration(ownership);
            if (generation is { } expected && expected != current)
                return (false, current);
            if (entries.Count == 0)
                return (true, current);
            var active = Path.Combine(directory, FileNames[0]);
            var first = Path.Combine(directory, FileNames[1]);
            var second = Path.Combine(directory, FileNames[2]);
            var activeBytes = Length(active);
            if (activeBytes > MaxFileBytes || Length(first) > MaxFileBytes || Length(second) > MaxFileBytes)
                return null;
            if (activeBytes + payload.Length > MaxFileBytes)
            {
                if (FileExists(first))
                    File.Move(first, second, overwrite: true);
                File.Move(active, first, overwrite: true);
                activeBytes = 0;
            }
            using var output = new FileStream(active, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            output.Position = activeBytes;
            try
            {
                payload.WriteTo(output);
                output.Flush();
            }
            catch (IOException)
            {
                output.SetLength(activeBytes);
                throw;
            }
            return (true, current);
        });
    }

    private T Access<T>(bool create, T empty, T failure, Func<FileStream, T> action)
    {
        if (!Path.IsPathFullyQualified(directory))
            return failure;
        lock (sync)
        {
            try
            {
                var parent = Path.GetDirectoryName(directory)!;
                if (!DirectoryExists(parent) && !create)
                    return empty;
                if (!DirectoryExists(directory))
                {
                    if (!create)
                        return empty;
                    Directory.CreateDirectory(directory);
                    _ = DirectoryExists(directory);
                }
                var lockPath = Path.Combine(directory, ".opssh.lock");
                _ = FileExists(lockPath);
                // LocalAppData is shared by multiple logons of the same user.
                using var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return action(ownership);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return failure;
            }
        }
    }

    private static Guid ReadGeneration(FileStream ownership)
    {
        if (ownership.Length == 0)
            return Guid.Empty;
        Span<byte> marker = stackalloc byte[16];
        if (ownership.Length != marker.Length)
            throw new IOException("Invalid diagnostic generation marker.");
        ownership.ReadExactly(marker);
        return new Guid(marker);
    }

    private static long Length(string path) => FileExists(path) ? new FileInfo(path).Length : 0;

    private static bool DirectoryExists(string path) => Exists(path, isDirectory: true);

    private static bool FileExists(string path) => Exists(path, isDirectory: false);

    private static bool Exists(string path, bool isDirectory)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != isDirectory)
                throw new IOException("Unexpected diagnostic storage entry.");
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
