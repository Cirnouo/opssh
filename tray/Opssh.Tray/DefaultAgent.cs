using System.ComponentModel;
using System.Text.Json;

namespace Opssh.Tray;

// Only the user-initiated switch can change SCM. Relaunch acquires the pipe
// again and leaves any interrupted SCM transaction available for recovery.
internal sealed class DefaultAgent(IOpenSshService service, string path, TimeSpan? serviceTimeout = null)
{
    internal sealed record Snapshot(bool Exists, int Start, bool Delayed, bool Running)
    {
        internal bool Valid => !Exists || Start is 2 or 3 or 4;
        internal static Snapshot From(OpenSshState state) => new(state.Exists, state.Start, state.Delayed, state.Running);
    }

    internal sealed record Journal(int Version, bool Enabled, Snapshot? Before = null, bool MutatingService = false);
    private Task? pendingMutation;

    internal static DefaultAgent ForCurrentUser() => new(new OpenSshService(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "opssh", "system-agent.json"));

    internal Journal? Read()
    {
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length is < 1 or > 4096)
                return null;
            var journal = JsonSerializer.Deserialize<Journal>(input, Protocol.Json);
            return journal is { Version: 1 } && journal.Before is not { Valid: false } &&
                (!journal.MutatingService || journal.Before is not null) ? journal : null;
        }
        catch (FileNotFoundException) { return new(1, false); }
        catch (DirectoryNotFoundException) { return new(1, false); }
        catch (Exception ex) when (IsFailure(ex) || ex is JsonException) { return null; }
    }

    internal async Task<string?> SetAsync(bool enabled, Func<bool, CancellationToken, Task<bool>> alias,
        CancellationToken cancellationToken)
    {
        var previous = Read();
        if (previous is null)
            return "Error.ConfigurationWrite";

        try
        {
            if (previous.MutatingService)
            {
                if (pendingMutation is not { IsCompleted: true } mutation)
                    return "Startup.DefaultRecovery";
                try { await mutation; }
                catch (OpenSshService.ServiceChangePending) { return "Startup.DefaultRecovery"; }
                catch (Exception ex) when (IsFailure(ex)) { }
                previous = previous with { MutatingService = false };
                Save(previous);
                pendingMutation = null;
            }
            if (previous.Before is not null)
            {
                if (!await RestoreAsync(previous, alias))
                    return "Startup.DefaultRecovery";
                previous = previous with { Before = null };
                Save(previous);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (enabled == previous.Enabled)
                return await alias(enabled, cancellationToken) ? null : "Error.PipeUnavailable";
            var current = service.Read();
            var before = Snapshot.From(current);
            if (!before.Valid)
                return "Startup.DefaultRecovery";
            if (enabled && current is { Automatic: true, Running: false })
                return "Startup.OpenSshStopped";
            if (enabled && !service.PipeAvailable(current is { Automatic: true, Running: true } ? current.Pid : 0))
                return "Error.PipeUnavailable";

            // Persist the compensating action before any external state is touched.
            var transaction = previous with { Before = before };
            Save(transaction);
            try
            {
                if (enabled)
                {
                    if (current is { Automatic: true, Running: true })
                    {
                        await ChangeServiceAsync(transaction, () => service.ChangeRunningAsync(false));
                        await ChangeServiceAsync(transaction, () => service.ChangeStartupAsync(4, delayed: false));
                        if (service.Read() is not { Running: false, Start: 4 })
                            throw new IOException("OpenSSH did not stop and disable.");
                    }
                    if (!await alias(true, cancellationToken))
                        throw new IOException("The default pipe could not be reserved.");
                }
                else
                {
                    if (!await alias(false, cancellationToken))
                        throw new IOException("The default pipe could not be released.");
                    if (before.Exists)
                        await RestoreServiceAsync(transaction, new Snapshot(true, 2, false, true));
                }
                Save(new Journal(1, enabled));
                return null;
            }
            catch (OpenSshService.ServiceChangePending)
            {
                // Do not race a still-running elevated operation with rollback.
                return "Startup.DefaultRecovery";
            }
            catch (Exception ex) when (IsFailure(ex))
            {
                if (!await RestoreAsync(transaction, alias))
                    return "Startup.DefaultRecovery";
                Save(previous);
                return ex is Win32Exception { NativeErrorCode: 1223 } or OperationCanceledException
                    ? "Error.Canceled" : "Startup.DefaultChangeFailed";
            }
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            return ex is OperationCanceledException ? "Error.Canceled" : "Startup.DefaultChangeFailed";
        }
    }

    private async Task ChangeServiceAsync(Journal transaction, Func<Task> change)
    {
        // A killed tray cannot determine whether the elevated command completed.
        // Keep the marker until the command and any accepted SCM transition settle.
        Save(transaction with { MutatingService = true });
        var mutation = change();
        pendingMutation = mutation;
        try { await mutation.WaitAsync(serviceTimeout ?? TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new OpenSshService.ServiceChangePending(); }
        catch (OpenSshService.ServiceChangePending) { throw; }
        catch (Exception ex) when (IsFailure(ex))
        {
            FinishServiceChange(transaction);
            throw;
        }
        FinishServiceChange(transaction);
    }

    private void FinishServiceChange(Journal transaction)
    {
        try { Save(transaction); }
        catch (Exception ex) when (IsFailure(ex)) { throw new OpenSshService.ServiceChangePending(); }
        pendingMutation = null;
    }

    private async Task<bool> RestoreAsync(Journal transaction, Func<bool, CancellationToken, Task<bool>> alias)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (!transaction.Enabled && !await alias(false, timeout.Token))
                return false;
            await RestoreServiceAsync(transaction, transaction.Before!);
            return !transaction.Enabled || await alias(true, timeout.Token);
        }
        catch (Exception ex) when (IsFailure(ex)) { return false; }
    }

    private async Task RestoreServiceAsync(Journal transaction, Snapshot expected)
    {
        var current = service.Read();
        if (current.Exists != expected.Exists)
            throw new IOException("The OpenSSH service changed during the transition.");
        if (!expected.Exists)
            return;
        if (current.Running && !expected.Running)
            await ChangeServiceAsync(transaction, () => service.ChangeRunningAsync(false));
        if (current.Start != expected.Start || current.Delayed != expected.Delayed)
            await ChangeServiceAsync(transaction, () => service.ChangeStartupAsync(expected.Start, expected.Delayed));
        if (!current.Running && expected.Running)
            await ChangeServiceAsync(transaction, () => service.ChangeRunningAsync(true));
        if (Snapshot.From(service.Read()) != expected)
            throw new IOException("The OpenSSH service did not reach the requested state.");
    }

    private void Save(Journal journal)
    {
        var directory = Path.GetDirectoryName(path);
        if (!Path.IsPathFullyQualified(path) || directory is null)
            throw new IOException("The default agent settings path is invalid.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, ".agent-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, journal, Protocol.Json);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (IsFailure(ex)) { }
        }
    }

    private static bool IsFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        Win32Exception or OperationCanceledException or InvalidOperationException;
}
