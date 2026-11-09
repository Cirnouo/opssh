namespace Opssh.Tray;

public sealed partial class App
{
    private CancellationTokenSource? accountTransition;

    private async Task<string?> SwitchAccountAsync(string account, string name, CancellationToken cancellationToken)
    {
        var previous = session;
        var previousChild = core;
        if (previous is null || previousChild is null || state is null || transitioning || quitting || changingDefaultAgent)
            return "Error.Busy";
        if (string.Equals(account, state.Account, StringComparison.Ordinal))
            return null;

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(40));
        accountTransition = attempt;
        CancelStartupWait();
        transitioning = true;
        CoreProcess? child = null;
        CoreSession? candidate = null;
        var mayBePaused = false;
        var restart = false;
        var feedback = "Error.ControlRequestFailed";
        string? outcome;
        try
        {
            outcome = await ActivateAsync();
        }
        catch (Exception ex) when (IsControlFailure(ex))
        {
            outcome = cancellationToken.IsCancellationRequested ? "Error.Canceled" : feedback;
        }
        finally
        {
            candidate?.Dispose();
            child?.Dispose();
            if (previous == session && !quitting)
            {
                if (mayBePaused && previous.IsConnected)
                {
                    using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    try
                    {
                        var resumed = await previous.RequestAsync("resume-ssh", null, restore.Token);
                        restart = !resumed.Ok;
                    }
                    catch (Exception ex) when (IsControlFailure(ex)) { restart = true; }
                }
                else
                    restart = !previous.IsConnected;
                if (restart)
                    await StopCoreAsync();
            }
            accountTransition = null;
            transitioning = false;
            if (restart && !quitting)
                await StartCoreAsync();
            else
                ConfigureStartupWait();
        }
        // A lost reply can follow a successful durable commit. The old
        // session refused to resume, so reconcile against the restarted core.
        if (restart && !quitting && session is { } recovered)
        {
            using var check = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try
            {
                var result = await recovered.RequestAsync("status", null, check.Token);
                if (recovered == session && result.State?.Account == account)
                    return null;
            }
            catch (Exception ex) when (IsControlFailure(ex)) { }
        }
        return outcome;

        async Task<string?> ActivateAsync()
        {
            var pending = Task.WhenAll(operationComplete?.Task ?? Task.CompletedTask, urgentComplete?.Task ?? Task.CompletedTask);
            await pending.WaitAsync(attempt.Token);
            if (previous != session || !previous.IsConnected)
                return feedback;

            child = await CoreProcess.StartAsync(attempt.Token, candidate: true);
            candidate = new CoreSession(child.Pipe);
            var promoted = candidate;
            promoted.ApprovalRequested += approval => dispatcher.TryEnqueue(() => QueueApproval(promoted, approval));
            promoted.ApprovalEnded += id => dispatcher.TryEnqueue(() => EndApproval(promoted, id));
            promoted.Disconnected += () => dispatcher.TryEnqueue(() => _ = CoreDisconnectedAsync(promoted));
            var ready = await candidate.StartAsync(attempt.Token);
            if (hello is not { } currentHello || ready != currentHello)
                return feedback;

            var prepared = await candidate.RequestAsync("prepare-account", new { account, name }, attempt.Token);
            if (!prepared.Ok)
                return CoreFeedback.ResultKey(prepared.ErrorCode, prepared.Error);

            // A canceled reply does not prove the command did not run. Mark
            // the old endpoint for recovery before sending the pause.
            mayBePaused = true;
            var pause = await previous.RequestAsync("pause-ssh", null, attempt.Token);
            if (!pause.Ok)
                return CoreFeedback.ResultKey(pause.ErrorCode, pause.Error);

            attempt.Token.ThrowIfCancellationRequested();
            using var commit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var defaultAgent = pause.Data is { } paused && paused.TryGetProperty("defaultAgent", out var value) &&
                value.ValueKind == System.Text.Json.JsonValueKind.True;
            var activated = await candidate.RequestAsync("activate-account", new { defaultAgent }, commit.Token);
            if (!activated.Ok || activated.State is not { } current ||
                !string.Equals(current.Account, account, StringComparison.Ordinal) || !candidate.IsConnected)
                return CoreFeedback.ResultKey(activated.ErrorCode, activated.Error);

            core = child;
            session = promoted;
            hello = ready;
            state = current;
            locked = current.Locked;
            defaultAgentActive = current.DefaultAgent;
            waiting.Clear();
            prompt?.Dismiss();
            prompt = null;
            candidate = null;
            child = null;
            mayBePaused = false;
            previous.Dispose();
            previousChild.Dispose();
            settings?.ShowState(current, true);
            icon.SetTip("Tray.Connected");
            return null;
        }
    }
}
