namespace Opssh.Tray;

public sealed partial class App
{
    // Keep one CoreSession slot available for eviction and revocation.
    private readonly SemaphoreSlim normalRequests = new(1);
    private readonly SemaphoreSlim discoveryControls = new(1);

    private async Task<Result> RequestDiscoveryControlAsync(string command, object? body)
    {
        var current = session;
        if (current is null || quitting || transitioning || urgentOperation is not null)
            return new Result(false, null, null, false, "busy");
        var cancel = command == "cancel-discovery";
        var ownsSlot = false;
        using var timeout = new CancellationTokenSource(cancel ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(500));
        try
        {
            ownsSlot = await discoveryControls.WaitAsync(cancel ? 2000 : 0, timeout.Token);
            if (!ownsSlot)
                return new Result(false, null, null, false, "busy");
            if (current != session || urgentOperation is not null)
                return new Result(false, null, null, false, "busy");
            var result = await current.RequestAsync(command, body, timeout.Token);
            return current == session ? result : new Result(false, null, null, false, "canceled");
        }
        catch (Exception e) when (IsControlFailure(e))
        {
            return new Result(false, null, null, false, "busy");
        }
        finally
        {
            if (ownsSlot)
                discoveryControls.Release();
        }
    }

    private async Task<Result> RequestDashboardAsync(string command, object? body, CancellationToken token)
    {
        var current = session;
        if (current is null || quitting || transitioning)
            return new Result(false, null, null, false, "busy");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(command is "authorize-desktop" or "reload-vaults" ? 41 : 8));
        var revision = stateRevision;
        var reply = await RequestNormalAsync(current, command, body, timeout.Token);
        if (current != session || quitting)
            return new Result(false, null, null, false, "canceled");
        if (reply.State is { } latest)
        {
            if (revision != stateRevision || urgentOperation is not null)
            {
                _ = RefreshSettingsAsync();
                return reply with { State = null };
            }
            state = latest;
        }
        return reply;
    }

    private async Task<Result> RequestNormalAsync(CoreSession current, string command, object? body,
        CancellationToken token, bool duringTransition = false)
    {
        await normalRequests.WaitAsync(token);
        try
        {
            if (current != session || quitting || (transitioning && !duringTransition))
                return new Result(false, null, null, false, "canceled");
            return await current.RequestAsync(command, body, token);
        }
        finally { normalRequests.Release(); }
    }

    partial void OpenStartupAccountPicker() => _ = OpenStartupAccountPickerAsync();

    private async Task OpenStartupAccountPickerAsync()
    {
        var current = settings;
        if (current is null || quitting)
            return;
        await (operationComplete?.Task ?? Task.CompletedTask);
        if (current == settings && !quitting)
        {
            current.SelectPage("account");
            await current.ShowAccountPickerAsync();
        }
    }
}
