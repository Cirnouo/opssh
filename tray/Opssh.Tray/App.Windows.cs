using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;

namespace Opssh.Tray;

public sealed partial class App
{
    private readonly DefaultAgent defaultAgent = DefaultAgent.ForCurrentUser();
    private CancellationTokenSource? startupWait;
    private StartupNotification? startupNotification;
    private (CoreSession Session, string Action)? startupPrompt;
    private bool offeredStartupNotification;
    private long startupIntentRevision;
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

    private bool SetWaitForPassword(bool wait)
    {
        var next = preferences with { WaitForPassword = wait };
        if (!next.TrySave())
            return false;
        preferences = next;
        ConfigureStartupWait();
        return true;
    }

    private async Task<string?> SetDefaultAgentAsync(bool enabled, Action<DefaultAgentStage> report, CancellationToken token)
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
                (value, cancellation) => SetAgentPipeAsync(current, value, cancellation), token, report);
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
            var response = await RequestNormalAsync(current, "default-agent", new { enabled }, token);
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
        if (session is not { } current || quitting || !preferences.WaitForPassword || offeredStartupNotification)
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
        startupPrompt = null;
        startupIntentRevision++;
        startupNotification?.Dismiss();
    }

    private async Task WaitForProviderAsync(CoreSession current, CancellationTokenSource wait)
    {
        try
        {
            await OnePasswordReadiness.WaitAsync(wait.Token);
            if (quitting || session != current || startupWait != wait)
                return;
            offeredStartupNotification = true;
            var action = Guid.NewGuid().ToString("N");
            startupPrompt = (current, action);
            startupNotification ??= new StartupNotification(value => dispatcher.TryEnqueue(() => ActivateStartupPrompt(value)));
            if (!startupNotification.Show("opssh", Strings.Get("Startup.ChooseMessage"),
                Strings.Get("Startup.Choose"), Strings.Get("Startup.Later"), action))
            {
                startupPrompt = null;
                ShowMessage("Startup.ToastUnavailable");
            }
        }
        catch (Exception ex) when (IsControlFailure(ex)) { }
        finally
        {
            if (startupWait == wait)
                startupWait = null;
            wait.Dispose();
        }
    }

    private void ActivateStartupPrompt(string action)
    {
        if (startupPrompt is not { } prompt || prompt.Action != action)
            return;
        startupPrompt = null;
        var revision = startupIntentRevision;
        if (StartupIntentIsCurrent(prompt.Session, revision))
            _ = OpenStartupAccountPickerAsync(prompt.Session, revision);
    }

    private bool StartupIntentIsCurrent(CoreSession owner, long revision) =>
        revision == startupIntentRevision && owner == session && owner.IsConnected &&
        preferences.WaitForPassword && !quitting && !transitioning;
}
