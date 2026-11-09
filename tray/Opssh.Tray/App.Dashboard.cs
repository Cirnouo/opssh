namespace Opssh.Tray;

public sealed partial class App
{
    // Keep one CoreSession slot available for eviction and revocation.
    private readonly SemaphoreSlim normalRequests = new(1);

    private async Task<Result> RequestDashboardAsync(string command, object? body, CancellationToken token)
    {
        var current = session;
        if (current is null || quitting || transitioning)
            return new Result(false, null, null, false, "busy");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        return await RequestNormalAsync(current, command, body, timeout.Token);
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
