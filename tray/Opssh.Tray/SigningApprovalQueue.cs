namespace Opssh.Tray;

// Called on the tray's dispatcher. Native callbacks are dispatched before reaching this queue.
internal sealed class SigningApprovalQueue(
    Func<SigningPrompt, CancellationToken, Task<IAsyncDisposable?>> show,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly List<Approval> waiting = [];
    private CoreSession? owner;
    private Pending? active;
    private Task processing = Task.CompletedTask;

    internal void SetOwner(CoreSession? session)
    {
        if (owner == session)
            return;
        owner = session;
        waiting.Clear();
        active?.Lifetime.Cancel();
    }

    internal void Enqueue(CoreSession session, Approval approval)
    {
        if (owner != session || !session.IsCurrentApproval(approval))
            return;
        waiting.RemoveAll(item => !session.IsCurrentApproval(item));
        // CoreSession bounds outstanding approvals, including the one being displayed.
        if (waiting.Count < CoreSession.MaxPendingApprovals)
            waiting.Add(approval);
        if (processing.IsCompleted)
            processing = ProcessAsync();
    }

    internal void End(CoreSession session, string id)
    {
        if (owner != session)
            return;
        waiting.RemoveAll(item => item.Id == id);
        if (active is { } pending && pending.Owner == session && pending.Prompt.Approval.Id == id)
            pending.Lifetime.Cancel();
    }

    internal bool Invoke(string nonce, string choice)
    {
        if (active is not { Shown: true } pending || pending.Owner != owner ||
            pending.Lifetime.IsCancellationRequested || !pending.Owner.IsCurrentApproval(pending.Prompt.Approval) ||
            !pending.Prompt.TryConsume(nonce, choice, clock.GetUtcNow()))
            return false;
        pending.Choice.SetResult(choice);
        return true;
    }

    internal async Task StopAsync()
    {
        SetOwner(null);
        await processing;
    }

    private async Task ProcessAsync()
    {
        while (owner is { } session && waiting.Count != 0)
        {
            var approval = waiting[0];
            waiting.RemoveAt(0);
            var remaining = approval.Deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero || !session.IsCurrentApproval(approval))
                continue;

            using var lifetime = new CancellationTokenSource(remaining, clock);
            var pending = new Pending(session, new SigningPrompt(approval), lifetime);
            active = pending;
            IAsyncDisposable? notification = null;
            try
            {
                notification = await show(pending.Prompt, lifetime.Token);
                pending.Shown = notification is not null;
                var choice = notification is null ? "unavailable" : await pending.Choice.Task.WaitAsync(lifetime.Token);
                if (owner != session || lifetime.IsCancellationRequested)
                    continue;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await session.DecideAsync(approval, choice, approval.AppCandidate, timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException or
                UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            finally
            {
                active = null;
                if (notification is not null)
                    await notification.DisposeAsync();
            }
        }
    }

    private sealed class Pending(CoreSession owner, SigningPrompt prompt, CancellationTokenSource lifetime)
    {
        internal CoreSession Owner { get; } = owner;
        internal SigningPrompt Prompt { get; } = prompt;
        internal CancellationTokenSource Lifetime { get; } = lifetime;
        internal TaskCompletionSource<string> Choice { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Shown { get; set; }
    }
}
