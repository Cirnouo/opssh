using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace Opssh.Tray;

internal sealed record DiagnosticActions(
    Func<bool, string, bool> SetPolicy,
    Func<Task<DiagnosticLogState?>> Read,
    Func<Task<bool>> OpenDirectory,
    Func<Task<bool>> Clear);

public sealed partial class App
{
    private readonly DiagnosticLogStore diagnosticStore = DiagnosticLogStore.ForCurrentUser();
    private DispatcherQueueTimer? diagnosticTimer;
    private CoreSession? diagnosticSession;
    private CancellationTokenSource? diagnosticRequest;
    private long diagnosticEpoch = 1;
    private long diagnosticAppliedEpoch;
    private long diagnosticAfter;
    private Guid? diagnosticGeneration;
    private bool diagnosticPolling;
    private bool diagnosticClearing;

    private DiagnosticActions CreateDiagnosticActions() => new(
        SetDiagnosticPolicy, () => diagnosticStore.RunAsync(diagnosticStore.Read, null),
        () => diagnosticStore.RunAsync(OpenDiagnosticDirectory, false), ClearDiagnosticsAsync);

    private void StartDiagnostics()
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += PollDiagnostics;
        diagnosticTimer = timer;
        timer.Start();
        _ = CollectDiagnosticsAsync();
    }

    private void StopDiagnostics()
    {
        if (diagnosticTimer is not { } timer)
            return;
        timer.Stop();
        timer.Tick -= PollDiagnostics;
        diagnosticTimer = null;
        diagnosticSession = null;
        diagnosticRequest?.Cancel();
    }

    private bool SetDiagnosticPolicy(bool enabled, string level)
    {
        var next = preferences with { DiagnosticLogging = enabled, DiagnosticLevel = level };
        if (next == preferences)
            return true;
        if (!ApplyPreferences(next))
            return false;
        AdvanceDiagnosticEpoch();
        _ = CollectDiagnosticsAsync();
        return true;
    }

    private async Task<bool> ClearDiagnosticsAsync()
    {
        if (diagnosticClearing)
            return false;
        diagnosticClearing = true;
        AdvanceDiagnosticEpoch();
        try { return await diagnosticStore.RunAsync(diagnosticStore.Clear, false); }
        finally
        {
            diagnosticGeneration = null;
            diagnosticClearing = false;
            _ = CollectDiagnosticsAsync();
        }
    }

    private void AdvanceDiagnosticEpoch()
    {
        Interlocked.Increment(ref diagnosticEpoch);
        diagnosticAfter = 0;
    }

    private bool OpenDiagnosticDirectory()
    {
        if (diagnosticStore.OpenDirectory() is not { } path)
            return false;
        try
        {
            using var launched = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private void PollDiagnostics(DispatcherQueueTimer sender, object args) => _ = CollectDiagnosticsAsync();

    private async Task CollectDiagnosticsAsync()
    {
        if (diagnosticSession is { IsConnected: false })
            diagnosticSession = null;
        if (diagnosticPolling || diagnosticClearing || quitting)
            return;

        diagnosticPolling = true;
        var epoch = diagnosticEpoch;
        var enabled = preferences.DiagnosticLogging;
        try
        {
            if (enabled && diagnosticGeneration is null)
            {
                var generation = await diagnosticStore.RunAsync(diagnosticStore.ReadGeneration, null);
                if (quitting || epoch != diagnosticEpoch || generation is null)
                    return;
                diagnosticGeneration = generation;
            }

            if (transitioning || urgentOperation is not null || session is not { IsConnected: true } current ||
                (!enabled && (diagnosticSession != current || diagnosticAppliedEpoch == epoch)) || !normalRequests.Wait(0))
                return;

            if (diagnosticSession != current)
            {
                diagnosticSession = current;
                diagnosticAfter = 0;
                diagnosticAppliedEpoch = 0;
            }
            var after = diagnosticAfter;
            Result result;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                diagnosticRequest = timeout;
                result = await current.RequestAsync("diagnostics", new { enabled, epoch, after }, timeout.Token);
            }
            finally
            {
                diagnosticRequest = null;
                normalRequests.Release();
            }
            if (quitting || current != session || epoch != diagnosticEpoch ||
                !DiagnosticEvent.TryReadCoreBatch(result, epoch, after, out var events, out var latest))
                return;

            diagnosticAppliedEpoch = epoch;
            if (!enabled || diagnosticGeneration is not { } currentGeneration || events.Count == 0)
                return;
            var filtered = events.Where(entry => entry.IsVisibleAt(preferences.DiagnosticLevel)).ToArray();
            if (await AppendDiagnosticsAsync(epoch, currentGeneration, filtered, current) && current == session && epoch == diagnosticEpoch)
                diagnosticAfter = latest;
        }
        catch (Exception e) when (IsControlFailure(e))
        {
            // The next bounded snapshot can retry while the session is current.
        }
        finally { diagnosticPolling = false; }
    }

    private (long Epoch, long Start, Guid Generation) BeginTrayDiagnostic() => preferences.DiagnosticLogging && !diagnosticClearing
        ? (diagnosticEpoch, Stopwatch.GetTimestamp(), diagnosticGeneration ?? Guid.Empty)
        : default;

    private void RecordTrayDiagnostic((long Epoch, long Start, Guid Generation) observation, string phase, string outcome)
    {
        if (quitting || diagnosticClearing || !preferences.DiagnosticLogging || observation.Epoch != diagnosticEpoch)
            return;
        var elapsed = Math.Min((long)Stopwatch.GetElapsedTime(observation.Start).TotalMilliseconds, 86_400_000);
        var entry = new DiagnosticEvent(DateTimeOffset.UtcNow, "tray", phase, outcome, elapsed);
        if (entry.IsVisibleAt(preferences.DiagnosticLevel))
            _ = AppendDiagnosticsAsync(observation.Epoch, observation.Generation, [entry]);
    }

    private async Task<bool> AppendDiagnosticsAsync(long epoch, Guid generation, DiagnosticEvent[] entries, CoreSession? current = null)
    {
        var result = await diagnosticStore.RunAsync<(bool Saved, Guid Generation)?>(() =>
        {
            if (Volatile.Read(ref quitting) || Interlocked.Read(ref diagnosticEpoch) != epoch ||
                (current is not null && current != Volatile.Read(ref session)))
                return null;
            return diagnosticStore.Append(generation, entries);
        }, null);
        if (quitting || epoch != diagnosticEpoch || (current is not null && current != session) || result is null)
            return false;
        if (!result.Value.Saved)
        {
            diagnosticGeneration = result.Value.Generation;
            AdvanceDiagnosticEpoch();
        }
        if (entries.Length != 0)
            settings?.RefreshDiagnosticUsage();
        return result.Value.Saved;
    }
}
