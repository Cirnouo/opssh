using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;

namespace Opssh.Tray;

public sealed partial class App
{
    private readonly DefaultAgent defaultAgent = DefaultAgent.ForCurrentUser();
    private CancellationTokenSource? startupWait;
    private StartupNotification? startupNotification;
    private CoreSession? startupPromptSession;
    private string? startupPromptAccount;
    private bool requestedStartupAuthorization;
    private bool defaultAgentActive;
    private bool defaultAgentDesired;
    private bool changingDefaultAgent;

    private static bool FromStartupNotification()
    {
        try { return AppInstance.GetCurrent().GetActivatedEventArgs()?.Kind == ExtendedActivationKind.AppNotification; }
        catch (COMException) { return false; }
    }

    private bool SetLoginStartup(bool enabled) => LoginStartup.Set(enabled) ==
        (enabled ? LoginStartupStatus.On : LoginStartupStatus.Off);

    private bool SetStartupPolicy(bool wait, string action)
    {
        var next = preferences with { WaitForPassword = wait, StartupAction = action };
        if (!next.TrySave())
            return false;
        preferences = next;
        ConfigureStartupWait();
        return true;
    }

    private async Task<string?> SetDefaultAgentAsync(bool enabled, CancellationToken token)
    {
        var current = session;
        if (current is null || quitting || transitioning || changingDefaultAgent || (operationComplete is not null && !polling))
            return "Error.Busy";
        changingDefaultAgent = true;
        try
        {
            using var claim = new Mutex(initiallyOwned: false, @"Global\opssh-default-agent-transition", out var created);
            if (!created)
                return "Error.Busy";
            return await defaultAgent.SetAsync(enabled,
                (value, cancellation) => SetAgentPipeAsync(current, value, cancellation), token);
        }
        catch (UnauthorizedAccessException)
        {
            return "Error.Busy";
        }
        finally
        {
            defaultAgentDesired = defaultAgent.Read()?.Enabled == true;
            changingDefaultAgent = false;
        }
    }

    private async Task RestoreDefaultAgentAsync(CoreSession current)
    {
        defaultAgentActive = false;
        var desired = defaultAgent.Read();
        defaultAgentDesired = desired?.Enabled == true;
        if (desired is null || desired.Before is not null)
            ShowMessage("Startup.DefaultRecovery");
        else if (desired.Enabled)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!await SetAgentPipeAsync(current, true, timeout.Token) && current == session)
                ShowMessage("Error.PipeUnavailable");
        }
    }

    private async Task<bool> SetAgentPipeAsync(CoreSession current, bool enabled, CancellationToken token)
    {
        if (current != session || quitting || !current.IsConnected)
            return false;
        try
        {
            var response = await current.RequestAsync("default-agent", new { enabled }, token);
            if (current != session || !response.Ok)
                return false;
            defaultAgentActive = enabled;
            return true;
        }
        catch (Exception ex) when (IsControlFailure(ex)) { return false; }
    }

    private void ConfigureStartupWait()
    {
        CancelStartupWait();
        if (session is not { } current || quitting || !preferences.WaitForPassword || requestedStartupAuthorization)
            return;
        var wait = new CancellationTokenSource();
        startupWait = wait;
        _ = WaitForProviderAsync(current, wait);
    }

    private void CancelStartupWait()
    {
        var wait = startupWait;
        startupWait = null;
        wait?.Cancel();
        startupPromptSession = null;
        startupPromptAccount = null;
        startupNotification?.Dismiss();
    }

    private async Task WaitForProviderAsync(CoreSession current, CancellationTokenSource wait)
    {
        try
        {
            await OnePasswordReadiness.WaitAsync(wait.Token);
            if (quitting || session != current || startupWait != wait)
                return;
            using var statusTimeout = CancellationTokenSource.CreateLinkedTokenSource(wait.Token);
            statusTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await current.RequestAsync("status", null, statusTimeout.Token);
            if (quitting || session != current || startupWait != wait || response.State is not { } status)
                return;

            requestedStartupAuthorization = true;
            if (status.Account.Length != 0 && preferences.StartupAction == "request")
            {
                await RequestStartupAuthorizationAsync(current, status.Account, wait.Token);
                return;
            }
            startupPromptSession = current;
            startupPromptAccount = status.Account;
            startupNotification ??= new StartupNotification(action => dispatcher.TryEnqueue(() => _ = StartupActionAsync(action)));
            if (!startupNotification.Show("opssh", Strings.Get(status.Account.Length == 0
                    ? "Startup.ChooseMessage" : "Startup.AuthorizeMessage"),
                Strings.Get(status.Account.Length == 0 ? "Startup.Choose" : "Startup.Authorize"),
                Strings.Get("Startup.Later"), "startup"))
                ShowMessage("Startup.ToastUnavailable");
        }
        catch (Exception ex) when (IsControlFailure(ex)) { }
        finally
        {
            if (startupWait == wait)
                startupWait = null;
            wait.Dispose();
        }
    }

    private async Task StartupActionAsync(string action)
    {
        var current = startupPromptSession;
        var account = startupPromptAccount;
        startupPromptSession = null;
        startupPromptAccount = null;
        if (action != "startup" || current is null || current != session || account is null || quitting)
            return;
        if (account.Length == 0)
        {
            OpenSettings();
            OpenStartupAccountPicker();
        }
        else
        {
            await RequestStartupAuthorizationAsync(current, account, CancellationToken.None);
        }
    }

    partial void OpenStartupAccountPicker();

    private async Task RequestStartupAuthorizationAsync(CoreSession current, string account, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(40).ToUnixTimeMilliseconds();
            var response = await current.RequestAsync("authorize-desktop", new { account, deadline }, timeout.Token);
            if (current == session && !quitting && !response.Ok)
                ShowMessage(CoreFeedback.ResultKey(response.ErrorCode, response.Error));
        }
        catch (Exception ex) when (IsControlFailure(ex)) { }
    }
}
