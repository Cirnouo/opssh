using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Opssh.Tray;

internal enum AccountListStatus { Ready, Empty, NotInstalled, Untrusted, Unavailable, TimedOut, InvalidResponse, Busy }
internal sealed record DesktopAccount(string Id, string Label)
{
    internal static bool IsId(string? value) => value is { Length: 26 } && value.All(char.IsAsciiLetterOrDigit);

    internal static bool SameId(string? left, string? right) =>
        string.Equals(left, right, IsId(left) && IsId(right) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static bool IsLabel(string? value)
    {
        if (value is null || !SafeText(value, 256))
            return false;
        var email = value;
        var open = value.LastIndexOf('(');
        if (open > 0 && value.EndsWith(')'))
            email = value[(open + 1)..^1];
        return IsEmail(email);
    }

    internal static bool SafeText(string value, int maxBytes) =>
        value.Length > 0 && value.Trim() == value && Encoding.UTF8.GetByteCount(value) <= maxBytes &&
        !value.Any(ch => char.IsControl(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.Format);

    internal static bool IsEmail(string value) =>
        SafeText(value, 254) && !value.Any(char.IsWhiteSpace) && value.Contains('@') &&
        MailAddress.TryCreate(value, out var address) && address.DisplayName.Length == 0 && address.Address == value;
}
internal sealed record AccountListResult(AccountListStatus Status, IReadOnlyList<DesktopAccount> Accounts);

internal static class OnePasswordAccounts
{
    private const int MaxResponse = 64 * 1024;
    private const int MaxError = 16 * 1024;
    private const int MaxAccounts = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly SemaphoreSlim Work = new(1);

    internal static async Task<AccountListResult> ListAsync(CancellationToken token)
    {
        if (!await Work.WaitAsync(0, token).ConfigureAwait(false))
            return Result(AccountListStatus.Busy);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var stop = deadline.Token;
        var task = Task.Run(async () =>
        {
            try { return await ListCoreAsync(stop).ConfigureAwait(false); }
            finally { Work.Release(); }
        });
        try
        {
            return await task.WaitAsync(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Result(AccountListStatus.TimedOut);
        }
    }

    private static async Task<AccountListResult> ListCoreAsync(CancellationToken token)
    {
        var found = false;
        try
        {
            foreach (var path in CliCandidates())
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(path))
                    continue;
                found = true;
                try
                {
                    using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var executable = LocalExecutable(file);
                    if (executable is not null && OnePasswordSignature.Verify(executable, file))
                        return await RunAsync(executable, token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            return Result(found ? AccountListStatus.Untrusted : AccountListStatus.NotInstalled);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or SecurityException or ArgumentException)
        {
            return Result(AccountListStatus.Unavailable);
        }
    }

    private static string? LocalExecutable(SafeFileHandle file)
    {
        var buffer = new StringBuilder(32768);
        var size = GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Capacity, 0);
        if (size == 0 || size >= buffer.Capacity)
            return null;
        var path = buffer.ToString();
        if (path.Length < 7 || !path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            !char.IsAsciiLetter(path[4]) || path[5] != ':' || path[6] != '\\')
            return null;
        path = path[4..];
        return new DriveInfo(path[..3]).DriveType == DriveType.Fixed &&
            Path.GetFileName(path).Equals("op.exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static IEnumerable<string> CliCandidates()
    {
        return CliCandidates(Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
    }

    internal static IEnumerable<string> CliCandidates(params string?[] paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        foreach (var entry in (path ?? "").Split(';', 129).Take(128))
        {
            var directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (directory.Length is < 3 or > 1024 || directory[1] != ':' ||
                !Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl))
                continue;
            if (new DriveInfo(directory[..3]).DriveType != DriveType.Fixed)
                continue;
            var candidate = Path.GetFullPath(Path.Combine(directory, "op.exe"));
            if (seen.Add(candidate))
                yield return candidate;
        }
    }

    internal static ProcessStartInfo StartInfo(string path, string config)
    {
        var start = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("account");
        start.ArgumentList.Add("list");
        start.ArgumentList.Add("--format=json");
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("OP_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["OP_CONFIG_DIR"] = config;
        start.Environment["OP_BIOMETRIC_UNLOCK_ENABLED"] = "true";
        start.Environment["OP_CACHE"] = "false";
        return start;
    }

    private static async Task<AccountListResult> RunAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var user = identity.User ?? throw new SecurityException("Could not identify the CLI user.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var config = security.CreateDirectory(Path.Combine(Path.GetTempPath(), "opssh-op-" + Guid.NewGuid().ToString("N")));
        try
        {
            using var process = Process.Start(StartInfo(path, config.FullName));
            if (process is null)
                return Result(AccountListStatus.Unavailable);
            using var stop = token.Register(() => Stop(process));
            process.StandardInput.Close();
            var error = DrainErrorAsync(process.StandardError.BaseStream, token);
            var output = ReadAsync(process.StandardOutput.BaseStream, token);
            var read = Task.WhenAll(error, output);
            try
            {
                var first = await Task.WhenAny(error, output).ConfigureAwait(false);
                await first.ConfigureAwait(false);
                await read.ConfigureAwait(false);
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    return Result(AccountListStatus.Unavailable);
                return Parse(await output.ConfigureAwait(false));
            }
            catch (InvalidDataException)
            {
                return Result(AccountListStatus.InvalidResponse);
            }
            finally
            {
                Stop(process);
                try { await read.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException or InvalidDataException) { }
                if (output.IsCompletedSuccessfully)
                    Array.Clear(output.Result);
                else
                    _ = output.ContinueWith(result =>
                    {
                        if (result.IsCompletedSuccessfully)
                            Array.Clear(result.Result);
                        else
                            _ = result.Exception;
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            try { config.Delete(recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task DrainErrorAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        var total = 0;
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0)
                    return;
                total += count;
                if (total > MaxError)
                    throw new InvalidDataException("Account list diagnostics exceeded their limit.");
            }
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[MaxResponse + 1];
        var size = 0;
        try
        {
            while (size < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(size), token).ConfigureAwait(false);
                if (count == 0)
                    return buffer[..size];
                size += count;
            }
            throw new InvalidDataException("Account list output exceeded its limit.");
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
    }

    internal static AccountListResult Parse(ReadOnlyMemory<byte> output)
    {
        if (output.Length > MaxResponse)
            return Result(AccountListStatus.InvalidResponse);
        try
        {
            _ = StrictUtf8.GetCharCount(output.Span);
            using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > MaxAccounts)
                return Result(AccountListStatus.InvalidResponse);

            if (root.GetArrayLength() == 0)
                return Result(AccountListStatus.Empty);
            var accounts = new List<(DesktopAccount Account, string Host)>();
            var first = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in root.EnumerateArray())
            {
                if (ReadAccount(row, out var host) is not { } account)
                    continue;
                if (first.TryAdd(account.Id, accounts.Count))
                    accounts.Add((account, host));
                else
                {
                    var existing = accounts[first[account.Id]];
                    if (existing.Host != host || existing.Account.Label != account.Label)
                        ambiguous.Add(account.Id);
                }
            }
            var valid = accounts.Select(row => row.Account).Where(account => !ambiguous.Contains(account.Id)).ToArray();
            return valid.Length == 0 ? Result(AccountListStatus.InvalidResponse) : new(AccountListStatus.Ready, valid);
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or InvalidOperationException or UriFormatException)
        {
            return Result(AccountListStatus.InvalidResponse);
        }
    }

    private static DesktopAccount? ReadAccount(JsonElement row, out string host)
    {
        host = "";
        if (row.ValueKind != JsonValueKind.Object)
            return null;
        string? uuid = null, url = null, email = null, userName = null;
        foreach (var property in row.EnumerateObject())
        {
            if (property.NameEquals("account_uuid"))
            {
                if (uuid is not null || property.Value.ValueKind != JsonValueKind.String)
                    return null;
                uuid = property.Value.GetString();
            }
            else if (property.NameEquals("url"))
            {
                if (url is not null || property.Value.ValueKind != JsonValueKind.String)
                    return null;
                url = property.Value.GetString();
            }
            else if (property.NameEquals("email"))
            {
                if (email is not null || property.Value.ValueKind != JsonValueKind.String)
                    return null;
                email = property.Value.GetString();
            }
            else if (property.NameEquals("user_name") && property.Value.ValueKind == JsonValueKind.String)
                userName = property.Value.GetString()?.Trim();
        }
        if (uuid is null || !DesktopAccount.IsId(uuid) || email is null || !DesktopAccount.IsEmail(email) ||
            url is not { Length: > 0 and <= 128 } || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return null;
        var address = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        host = uri.IdnHost;
        if (host.Length is < 3 or > 100 || !host.Contains('.') ||
            !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            return null;
        var label = userName is { Length: > 0 } && DesktopAccount.SafeText(userName, 128) &&
            !userName.Contains('(') && !userName.Contains(')') &&
            DesktopAccount.SafeText($"{userName}({email})", 256) ? $"{userName}({email})" : email;
        return new DesktopAccount(uuid, label);
    }

    private static AccountListResult Result(AccountListStatus status) => new(status, []);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
