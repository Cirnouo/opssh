#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Opssh;

public static class StagedSnapshot
{
    private const int MaximumIndexBytes = 64 * 1024 * 1024;
    private const int MaximumTreeBytes = 16 * 1024 * 1024;
    private const long MaximumBlobBytes = 512 * 1024 * 1024;
    private const int MaximumEntries = 25000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
    private static readonly Regex Alias = new("~[0-9]", RegexOptions.CultureInvariant);
    private static readonly Regex Device = new(@"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[1-9¹²³])\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly string[] GitRouting =
    {
        "GIT_DIR", "GIT_COMMON_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CEILING_DIRECTORIES", "GIT_DISCOVERY_ACROSS_FILESYSTEM"
    };

    public sealed record Identity(string IndexPath, string IndexSha256, string? SharedIndexPath, string? SharedIndexSha256,
        string? Tree = null);

    private sealed record Repository(string Root, string GitDirectory, string IndexPath, string Objects, string ObjectFormat)
    {
        internal int OidBytes => ObjectFormat == "sha256" ? 32 : 20;
    }

    private sealed record CapturedIndex(Identity Identity, byte[] Index, byte[]? Shared);

    public static Identity Identify(string git, string repositoryRoot) => Execute(async token =>
    {
        var repo = await LocateAsync(git, repositoryRoot, token).ConfigureAwait(false);
        return (await CaptureAsync(repo, token).ConfigureAwait(false)).Identity;
    });

    public static Identity Create(string git, string repositoryRoot, string scratchRoot, string destination) => Execute(async token =>
    {
        var repo = await LocateAsync(git, repositoryRoot, token).ConfigureAwait(false);
        var scratch = AbsolutePath(scratchRoot);
        var output = AbsolutePath(destination);
        RequireRegular(scratch, directory: true);
        Require(!scratch.Equals(repo.Root, PathComparison) && !IsWithin(scratch, repo.Root) &&
            string.Equals(Path.GetDirectoryName(output), scratch, PathComparison), "unexpected scratch or snapshot location");
        Require(!File.Exists(output) && !Directory.Exists(output), "snapshot destination already exists");

        var captured = await CaptureAsync(repo, token).ConfigureAwait(false);
        var privateGit = Path.Combine(scratch, "git-" + Guid.NewGuid().ToString("N"));
        await RunAsync(git, scratch, token, "initialize private Git directory",
            new[] { "init", "--bare", "--quiet", "--template=", "--object-format=" + repo.ObjectFormat, privateGit },
            keepRouting: false).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(privateGit, "objects", "info", "alternates"),
            repo.Objects.Replace('\\', '/') + "\n", Utf8, token).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(privateGit, "index"), captured.Index, token).ConfigureAwait(false);
        if (captured.Shared != null)
        {
            var sharedName = Path.GetFileName(captured.Identity.SharedIndexPath)!;
            await File.WriteAllBytesAsync(Path.Combine(privateGit, sharedName), captured.Shared, token).ConfigureAwait(false);
        }

        var tree = ReadLines(await RunAsync(git, repo.Root, token, "write tree from private index",
            new[] { "-c", "core.splitIndex=false", "write-tree", "--ignore-cache-tree" },
            keepRouting: false, privateGit: privateGit).ConfigureAwait(false), 1)[0];
        Require(tree.Length == repo.OidBytes * 2 && tree.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "invalid staged tree identity");
        Directory.CreateDirectory(output);
        RequireRegular(output, directory: true);
        using (var child = new GitChild(git, repo.Root, new[] { "cat-file", "--batch" }, token,
            keepRouting: false, privateGit: privateGit))
        {
            using var reader = new TreeReader(child, token);
            await reader.WriteTreeAsync(tree, output, 0).ConfigureAwait(false);
            await reader.CompleteAsync().ConfigureAwait(false);
        }

        var finalRepo = await LocateAsync(git, repositoryRoot, token).ConfigureAwait(false);
        var finalIndex = await CaptureAsync(finalRepo, token).ConfigureAwait(false);
        Require(captured.Identity == finalIndex.Identity, "staged index changed during snapshot creation");
        return captured.Identity with { Tree = tree };
    });

    private static Identity Execute(Func<CancellationToken, Task<Identity>> action)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { return action(deadline.Token).GetAwaiter().GetResult(); }
        catch (SnapshotException) { throw; }
        catch (OperationCanceledException) { throw new SnapshotException("snapshot preparation timed out"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            throw new SnapshotException("snapshot preparation failed while reading or writing local data");
        }
    }

    private static async Task<Repository> LocateAsync(string git, string root, CancellationToken token)
    {
        root = AbsolutePath(root);
        RequireRegular(root, directory: true);
        var paths = ReadLines(await RunAsync(git, root, token, "resolve Git index and object paths",
            new[] { "rev-parse", "--path-format=absolute", "--show-toplevel", "--absolute-git-dir", "--git-path", "index",
                "--git-path", "objects", "--show-object-format=storage" }, keepRouting: true).ConfigureAwait(false), 5);
        Require(AbsolutePath(paths[0]).Equals(root, PathComparison), "repository root did not match Git");
        var indexPath = AbsolutePath(paths[2], root);
        var configuredIndex = Environment.GetEnvironmentVariable("GIT_INDEX_FILE");
        if (!string.IsNullOrEmpty(configuredIndex))
            Require(indexPath.Equals(AbsolutePath(configuredIndex, root), PathComparison), "alternate index did not match Git");
        Require(paths[4] is "sha1" or "sha256", "unsupported Git object format");
        return new Repository(root, AbsolutePath(paths[1]), indexPath, AbsolutePath(paths[3], root), paths[4]);
    }

    private static async Task<CapturedIndex> CaptureAsync(Repository repo, CancellationToken token)
    {
        var index = await ReadIndexAsync(repo.IndexPath, token).ConfigureAwait(false);
        var sharedOid = FindSharedIndex(index, repo.OidBytes, token);
        string? sharedPath = null;
        byte[]? shared = null;
        if (sharedOid != null)
        {
            // Git looks in the worktree gitdir first and falls back to the alternate index's directory.
            var name = "sharedindex." + sharedOid;
            var primary = Path.Combine(repo.GitDirectory, name);
            sharedPath = File.Exists(primary) ? primary : Path.Combine(Path.GetDirectoryName(repo.IndexPath)!, name);
            shared = await ReadIndexAsync(sharedPath, token).ConfigureAwait(false);
            Require(shared.Length >= repo.OidBytes && Hex(shared, shared.Length - repo.OidBytes, repo.OidBytes) == sharedOid,
                "shared index did not match its recorded identity");
        }
        return new CapturedIndex(new Identity(repo.IndexPath, Hash(index), sharedPath, shared == null ? null : Hash(shared)), index, shared);
    }

    private static async Task<byte[]> ReadIndexAsync(string path, CancellationToken token)
    {
        RequireRegular(path, directory: false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
        var length = stream.Length;
        Require(length >= 12 && length <= MaximumIndexBytes, "index size is outside supported bounds");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        Require(stream.Length == length, "index size changed while reading");
        return bytes;
    }

    // Git refreshes the shared-index timestamp even for read-only commands. Inspect only the link extension
    // described by gitformat-index(5) so Git can interpret all index entries against private copies.
    private static string? FindSharedIndex(byte[] index, int oidBytes, CancellationToken token)
    {
        var end = index.Length - oidBytes;
        Require(end >= 12 && index.AsSpan(0, 4).SequenceEqual("DIRC"u8), "invalid Git index header");
        var version = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(4, 4));
        var entries = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(8, 4));
        Require(version is 2 or 3 or 4 && entries <= MaximumEntries, "unsupported Git index version or entry count");
        var position = 12;
        for (var entry = 0; entry < entries; entry++)
        {
            token.ThrowIfCancellationRequested();
            var start = position;
            position += 40 + oidBytes;
            Require(position + 2 < end, "truncated Git index entry");
            var extended = (index[position] & 0x40) != 0;
            position += extended ? 4 : 2;
            if (version == 4)
            {
                // Version four has a prefix-compression integer before the NUL-terminated name and no padding.
                var width = 0;
                while (position < end && (index[position++] & 0x80) != 0)
                    Require(++width <= 5, "invalid Git index path prefix");
            }
            Require(position < end, "truncated Git index path");
            var nul = Array.IndexOf(index, (byte)0, position, end - position);
            Require(nul >= position, "unterminated Git index path");
            position = version == 4 ? nul + 1 : start + ((nul + 1 - start + 7) & ~7);
            Require(position <= end, "truncated Git index entry padding");
        }

        string? shared = null;
        while (position != end)
        {
            token.ThrowIfCancellationRequested();
            Require(end - position >= 8, "truncated Git index extension");
            var length = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(position + 4, 4));
            Require(length <= end - position - 8, "invalid Git index extension length");
            if (index.AsSpan(position, 4).SequenceEqual("link"u8))
            {
                Require(shared == null && length >= oidBytes, "invalid split index extension");
                shared = Hex(index, position + 8, oidBytes);
            }
            position += 8 + (int)length;
        }
        return shared != null && shared.Any(c => c != '0') ? shared : null;
    }

    private sealed class TreeReader : IDisposable
    {
        private readonly GitChild child;
        private readonly Stream input;
        private readonly BufferedStream output;
        private readonly CancellationToken token;
        private readonly byte[] buffer = new byte[65536];
        private int entries;
        private int treeBytes;
        private long blobBytes;

        internal TreeReader(GitChild child, CancellationToken token)
        {
            this.child = child;
            this.token = token;
            input = child.Process.StandardInput.BaseStream;
            output = new BufferedStream(child.Process.StandardOutput.BaseStream, 65536);
        }

        internal async Task WriteTreeAsync(string oid, string destination, int depth)
        {
            Require(depth <= 64 && destination.Length <= 1024, "staged path is too deep or long");
            var size = await RequestAsync(oid, "tree").ConfigureAwait(false);
            Require(size <= MaximumTreeBytes - treeBytes, "staged tree metadata is too large");
            treeBytes += (int)size;
            var data = new byte[(int)size];
            await output.ReadExactlyAsync(data, token).ConfigureAwait(false);
            using var hash = ObjectHash(oid, "tree", size);
            hash.AppendData(data);
            await FinishObjectAsync(oid, hash).ConfigureAwait(false);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var oidBytes = oid.Length / 2;
            var position = 0;
            while (position != data.Length)
            {
                token.ThrowIfCancellationRequested();
                Require(++entries <= MaximumEntries, "staged tree has too many entries");
                var space = Array.IndexOf(data, (byte)' ', position, Math.Min(7, data.Length - position));
                Require(space > position, "invalid staged tree entry");
                var mode = Encoding.ASCII.GetString(data, position, space - position);
                Require(mode is "40000" or "100644" or "100755", "unsupported staged file mode");
                var nul = Array.IndexOf(data, (byte)0, space + 1, Math.Min(1021, data.Length - space - 1));
                Require(nul > space + 1 && data.Length - nul - 1 >= oidBytes, "invalid staged tree path or object");
                var name = Utf8.GetString(data, space + 1, nul - space - 1);
                RequireComponent(name, allowGit: false);
                Require(names.Add(name), "case-colliding staged paths");
                var path = Path.Combine(destination, name);
                Require(path.Length <= 1024, "staged path is too long");
                var targetOid = Hex(data, nul + 1, oidBytes);
                position = nul + 1 + oidBytes;
                if (mode == "40000")
                {
                    Require(!Directory.Exists(path) && !File.Exists(path), "staged directory path already exists");
                    Directory.CreateDirectory(path);
                    RequireRegular(path, directory: true);
                    await WriteTreeAsync(targetOid, path, depth + 1).ConfigureAwait(false);
                }
                else await WriteBlobAsync(targetOid, path).ConfigureAwait(false);
            }
        }

        private async Task WriteBlobAsync(string oid, string path)
        {
            var size = await RequestAsync(oid, "blob").ConfigureAwait(false);
            Require(size <= MaximumBlobBytes - blobBytes, "staged file content is too large");
            blobBytes += size;
            using var hash = ObjectHash(oid, "blob", size);
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            for (var remaining = size; remaining > 0;)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                await output.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
                remaining -= count;
            }
            await FinishObjectAsync(oid, hash).ConfigureAwait(false);
            await target.FlushAsync(token).ConfigureAwait(false);
        }

        private async Task<long> RequestAsync(string oid, string type)
        {
            await input.WriteAsync(Encoding.ASCII.GetBytes(oid + "\n"), token).ConfigureAwait(false);
            await input.FlushAsync(token).ConfigureAwait(false);
            var length = 0;
            while (true)
            {
                await output.ReadExactlyAsync(buffer.AsMemory(length, 1), token).ConfigureAwait(false);
                if (buffer[length] == (byte)'\n') break;
                Require(++length < 128, "invalid Git object response");
            }
            var fields = Encoding.ASCII.GetString(buffer, 0, length).Split(' ');
            long size = 0;
            Require(fields.Length == 3 && fields[0] == oid && fields[1] == type &&
                long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out size), "missing or invalid staged Git object");
            return size;
        }

        private async Task FinishObjectAsync(string oid, IncrementalHash hash)
        {
            await output.ReadExactlyAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false);
            Require(buffer[0] == (byte)'\n' && Hex(hash.GetHashAndReset()) == oid, "staged object bytes did not match their identity");
        }

        internal async Task CompleteAsync()
        {
            child.Process.StandardInput.Close();
            Require(await output.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) == 0, "unexpected Git object output");
            await child.CompleteAsync("read staged Git objects").ConfigureAwait(false);
        }

        public void Dispose() => output.Dispose();
    }

    private static IncrementalHash ObjectHash(string oid, string type, long size)
    {
        var hash = IncrementalHash.CreateHash(oid.Length == 40 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(type + " " + size.ToString(CultureInfo.InvariantCulture) + "\0"));
        return hash;
    }

    private static async Task<byte[]> RunAsync(string git, string root, CancellationToken token, string stage, string[] args,
        bool keepRouting, string? privateGit = null)
    {
        using var child = new GitChild(git, root, args, token, keepRouting, privateGit);
        child.Process.StandardInput.Close();
        var data = new byte[16 * 1024];
        var length = 0;
        while (true)
        {
            var read = await child.Process.StandardOutput.BaseStream.ReadAsync(data.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            Require(length < data.Length, "Git metadata response is too large");
        }
        await child.CompleteAsync(stage).ConfigureAwait(false);
        return data.AsSpan(0, length).ToArray();
    }

    private sealed class GitChild : IDisposable
    {
        private readonly CancellationToken token;
        private readonly CancellationTokenRegistration cancel;
        private readonly Task stderr;
        internal Process Process { get; }

        internal GitChild(string git, string root, string[] args, CancellationToken token, bool keepRouting, string? privateGit)
        {
            this.token = token;
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(git)
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var key in start.Environment.Keys.ToArray())
            {
                if (!key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) continue;
                if (keepRouting && GitRouting.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                if (privateGit != null && key.Equals("GIT_ALTERNATE_OBJECT_DIRECTORIES", StringComparison.OrdinalIgnoreCase)) continue;
                start.Environment.Remove(key);
            }
            start.Environment["GIT_NO_LAZY_FETCH"] = "1";
            start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            if (!keepRouting)
            {
                start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
                start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
                if (privateGit != null) start.Environment["GIT_DIR"] = privateGit;
            }
            foreach (var arg in args) start.ArgumentList.Add(arg);
            Process = Process.Start(start) ?? throw new SnapshotException("could not start Git");
            try
            {
                cancel = token.Register(Stop);
                stderr = Process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal async Task CompleteAsync(string stage)
        {
            await Task.WhenAll(Process.WaitForExitAsync(token), stderr).WaitAsync(token).ConfigureAwait(false);
            Require(Process.ExitCode == 0, "Git could not " + stage);
        }

        private void Stop()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            cancel.Dispose();
            try
            {
                Stop();
                Require(Process.WaitForExit(5000), "Git process cleanup did not finish");
            }
            finally
            {
                using var process = Process;
                using var input = Process.StandardInput;
                using var output = Process.StandardOutput;
                using var error = Process.StandardError;
            }
        }
    }

    private static string[] ReadLines(byte[] data, int count)
    {
        var lines = Utf8.GetString(data).Split('\n');
        Require(lines.Length == count + 1 && lines[count].Length == 0, "invalid Git metadata response");
        return lines.Take(count).Select(line => line.TrimEnd('\r')).ToArray();
    }

    private static string AbsolutePath(string path, string? basis = null)
    {
        var windows = path.Replace('/', '\\');
        Require(!windows.StartsWith(@"\\?\", StringComparison.Ordinal) && !windows.StartsWith(@"\\.\", StringComparison.Ordinal) &&
            !windows.StartsWith(@"\??\", StringComparison.Ordinal) && !Alias.IsMatch(windows), "unsupported Windows path namespace or alias");
        Require(basis != null || Path.IsPathFullyQualified(path), "absolute Windows path required");
        var full = Path.TrimEndingDirectorySeparator(basis == null ? Path.GetFullPath(path) : Path.GetFullPath(path, basis));
        foreach (var part in full.Substring(Path.GetPathRoot(full)!.Length).Split(Path.DirectorySeparatorChar))
            if (part.Length > 0) RequireComponent(part, allowGit: true);
        return full;
    }

    private static void RequireComponent(string name, bool allowGit)
    {
        Require(name.Length is > 0 and <= 255 && !name.EndsWith('.') && !name.EndsWith(' ') &&
            (allowGit || !name.Equals(".git", PathComparison)) && !Alias.IsMatch(name) &&
            !name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) &&
            !Device.IsMatch(name.Split('.')[0].TrimEnd(' ')), "unsafe Windows path component");
    }

    private static void RequireRegular(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        Require(attributes.HasFlag(FileAttributes.Directory) == directory && !attributes.HasFlag(FileAttributes.ReparsePoint),
            "expected an ordinary local file or directory");
        for (var parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent))
            Require(!File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint), "unexpected Windows reparse point");
    }

    private static bool IsWithin(string path, string parent) =>
        path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, PathComparison);
    private static string Hash(byte[] bytes) => Hex(SHA256.HashData(bytes));
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static string Hex(byte[] bytes, int offset, int count) => Convert.ToHexString(bytes, offset, count).ToLowerInvariant();

    private sealed class SnapshotException : IOException
    {
        internal SnapshotException(string message) : base(message) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new SnapshotException(message);
    }
}
