using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Opssh.Tray;

public sealed partial class App : Application
{
    private readonly Queue<Approval> waiting = new();
    private DispatcherQueue dispatcher = null!;
    private DispatcherQueueTimer statusTimer = null!;
    private SettingsWindow settings = null!;
    private TrayIcon icon = null!;
    private Mutex? instance;
    private CoreProcess? core;
    private CoreSession? session;
    private CancellationTokenSource? operation;
    private TaskCompletionSource? operationComplete;
    private CancellationTokenSource? urgentOperation;
    private TaskCompletionSource? urgentComplete;
    private int stateRevision;
    private bool hasCurrentState;
    private bool polling;
    private bool waitingForPoll;
    private ApprovalWindow? prompt;
    private bool transitioning;
    private bool quitting;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        instance = ClaimLogonInstance();
        if (instance is null)
        {
            Exit();
            return;
        }
        dispatcher = DispatcherQueue.GetForCurrentThread();
        settings = new SettingsWindow(RunCommandAsync, RunUrgentAsync, StartCoreAsync);
        icon = new TrayIcon();
        icon.OpenRequested += OpenSettings;
        icon.RestartRequested += () => _ = StartCoreAsync();
        icon.QuitRequested += () => _ = QuitAsync();
        statusTimer = dispatcher.CreateTimer();
        statusTimer.Interval = TimeSpan.FromSeconds(2);
        statusTimer.Tick += (_, _) =>
        {
            if (settings.AppWindow.IsVisible)
                _ = RunCommandAsync("status", null, background: true);
        };
        statusTimer.Start();
        _ = StartCoreAsync();
    }

    private static Mutex? ClaimLogonInstance()
    {
        var logonSid = GetLogonSid();
        if (logonSid is null)
            return null;

        try
        {
            var handle = new Mutex(initiallyOwned: true, @"Local\opssh-tray-" + logonSid.Value, out var created);
            if (created)
                return handle;
            handle.Dispose();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }
        return null;
    }

    private static SecurityIdentifier? GetLogonSid()
    {
        const int tokenLogonSid = 28;
        const uint enabledLogon = 0xc0000004; // SE_GROUP_LOGON_ID | SE_GROUP_ENABLED.
        const uint denyOnly = 0x10;
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        GetTokenInformation(identity.AccessToken, tokenLogonSid, 0, 0, out var size);
        if (size < Marshal.SizeOf<LogonGroup>() || size > 1024)
            return null;

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (!GetTokenInformation(identity.AccessToken, tokenLogonSid, buffer, size, out _))
                return null;
            var group = Marshal.PtrToStructure<LogonGroup>(buffer);
            return group.Count == 1 && group.Sid != 0 && (group.Attributes & (enabledLogon | denyOnly)) == enabledLogon
                ? new SecurityIdentifier(group.Sid)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // TokenLogonSid returns a TOKEN_GROUPS structure containing one SID_AND_ATTRIBUTES.
    [StructLayout(LayoutKind.Sequential)]
    private struct LogonGroup
    {
        internal uint Count;
        internal nint Sid;
        internal uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        nint information, uint length, out uint returnedLength);

    private void OpenSettings()
    {
        if (quitting)
            return;
        settings.Activate();
        if (settings.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Restore();
        _ = RunCommandAsync("status", null, background: true);
    }

    private async Task StartCoreAsync()
    {
        if (transitioning || quitting || session is not null)
            return;
        transitioning = true;
        settings.SetAvailability(false, false, "Starting the core…");
        icon.CanRestart = false;
        icon.SetTip("opssh is starting");
        CoreProcess? child = null;
        CoreSession? candidate = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            child = await CoreProcess.StartAsync(timeout.Token);
            core = child;
            candidate = new CoreSession(child.Pipe);
            var current = candidate;
            current.ApprovalRequested += approval => dispatcher.TryEnqueue(() => QueueApproval(current, approval));
            current.ApprovalEnded += id => dispatcher.TryEnqueue(() => EndApproval(current, id));
            current.Disconnected += () => dispatcher.TryEnqueue(() => _ = CoreDisconnectedAsync(current));
            session = current;
            var hello = await current.StartAsync(timeout.Token);
            if (quitting || session != current)
                return;
            if (!current.IsConnected)
                throw new IOException("The core disconnected during startup.");
            settings.SetHello(hello);
            settings.SetAvailability(true, false, "Core connected");
            icon.SetTip("opssh core connected");
        }
        catch (Exception e) when (IsControlFailure(e))
        {
            if (session == candidate)
            {
                session = null;
                candidate?.Dispose();
                core = null;
                child?.Dispose();
            }
            if (!quitting)
            {
                var message = e switch
                {
                    FileNotFoundException => "The bundled opssh-core.exe is missing.",
                    CoreStartupException startup => startup.Message,
                    _ => "Could not start the core."
                };
                settings.SetAvailability(false, true, message);
                icon.SetTip("opssh core unavailable");
            }
        }
        finally
        {
            transitioning = false;
            icon.CanRestart = !quitting && session is null;
        }
        if (session is not null)
            await RunCommandAsync("status", null, background: true);
    }

    private async Task RunCommandAsync(string command, object? body, bool background = false)
    {
        var current = session;
        if (current is null || transitioning || quitting || (background && waitingForPoll) ||
            (!hasCurrentState && command != "status") ||
            (command == "unlock" && urgentOperation is not null))
            return;
        if (operationComplete is { } active)
        {
            if (background || !polling || waitingForPoll)
                return;
            waitingForPoll = true;
            settings.SetBusy(true);
            await active.Task;
            waitingForPoll = false;
            if (current != session || transitioning || quitting)
                return;
            if (command == "unlock" && urgentOperation is not null)
            {
                settings.SetBusy(false);
                return;
            }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        operation = timeout;
        polling = background;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operationComplete = completed;
        if (!background)
            settings.SetBusy(true);
        var revision = stateRevision;
        var restart = false;
        var refresh = false;
        try
        {
            var result = await current.RequestAsync(command, body, timeout.Token);
            if (current != session || quitting)
                return;
            if (revision != stateRevision || urgentOperation is not null)
                refresh = true;
            else if (result.Ok && command == "unlock" && result.State is null)
            {
                settings.SetLockState(false);
                icon.SetTip("opssh core connected");
                refresh = true;
            }
            else if (result.State is { } state)
            {
                settings.ShowState(state, command == "settings" && result.Ok);
                hasCurrentState = true;
                icon.SetTip(state.Locked ? "opssh core locked" : "opssh core connected");
            }
            if (!result.Ok)
                settings.ShowMessage(result.Error ?? "The core rejected the request.");
            else if (!background)
                settings.ShowMessage(result.RestartRequired ? "Restarting with the new account…" : "Done.");
            restart = result.Ok && result.RestartRequired;
        }
        catch (Exception e) when (IsControlFailure(e))
        {
            if (current == session && !quitting)
                settings.ShowMessage("The core request could not complete.");
        }
        finally
        {
            operation = null;
            operationComplete = null;
            polling = false;
            if (!background && current == session)
                settings.SetBusy(false);
            completed.TrySetResult();
        }
        if (restart)
        {
            transitioning = true;
            settings.SetAvailability(false, false, "Restarting the core…");
            await StopCoreAsync();
            transitioning = false;
            await StartCoreAsync();
        }
        else if (refresh && urgentOperation is null && current == session && !quitting)
        {
            await RunCommandAsync("status", null, background: true);
        }
    }

    private async Task RunUrgentAsync(string command, object? body)
    {
        var current = session;
        if (current is null || transitioning || quitting || urgentOperation is not null ||
            (!hasCurrentState && command == "revoke" && body is not null))
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        urgentOperation = timeout;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        urgentComplete = completed;
        stateRevision++;
        settings.SetUrgentBusy(true);
        try
        {
            var result = await current.RequestAsync(command, body, timeout.Token);
            if (current != session || quitting)
                return;
            if (result.Ok && command == "lock")
            {
                settings.SetLockState(true);
                icon.SetTip("opssh core locked");
            }
            settings.ShowMessage(result.Ok ? "Done." : result.Error ?? "The core rejected the request.");
        }
        catch (Exception e) when (IsControlFailure(e))
        {
            if (current == session && !quitting)
                settings.ShowMessage("The core request could not complete.");
        }
        finally
        {
            stateRevision++;
            urgentOperation = null;
            urgentComplete = null;
            if (current == session)
                settings.SetUrgentBusy(false);
            completed.TrySetResult();
        }

        if (current == session && !quitting)
            await RunCommandAsync("status", null, background: true);
    }

    private async Task CoreDisconnectedAsync(CoreSession current)
    {
        if (current != session || transitioning || quitting)
            return;
        transitioning = true;
        settings.SetAvailability(false, false, "Core stopped");
        icon.SetTip("opssh core stopped");
        await StopCoreAsync();
        transitioning = false;
        if (!quitting)
        {
            settings.SetAvailability(false, true, "Core stopped");
            icon.CanRestart = true;
        }
    }

    private void QueueApproval(CoreSession current, Approval approval)
    {
        if (current != session || quitting || !current.IsCurrentApproval(approval.Id))
            return;
        if (waiting.Count >= 64)
        {
            var valid = waiting.Where(item => current.IsCurrentApproval(item.Id)).ToArray();
            waiting.Clear();
            foreach (var item in valid)
                waiting.Enqueue(item);
        }
        if (waiting.Count < 64)
            waiting.Enqueue(approval);
        ShowNextApproval(current);
    }

    private void EndApproval(CoreSession current, string id)
    {
        if (current != session)
            return;
        if (prompt?.Approval.Id == id)
        {
            prompt.Dismiss();
            prompt = null;
        }
        ShowNextApproval(current);
    }

    private void ShowNextApproval(CoreSession current)
    {
        if (quitting || current != session || prompt is not null)
            return;
        while (waiting.TryDequeue(out var approval))
        {
            if (!current.IsCurrentApproval(approval.Id))
                continue;
            prompt = new ApprovalWindow(approval, (scope, index) => _ = DecideAsync(current, approval.Id, scope, index));
            prompt.Activate();
            return;
        }
    }

    private async Task DecideAsync(CoreSession current, string id, string scope, int candidate)
    {
        if (current != session || prompt?.Approval.Id != id || quitting)
            return;
        prompt.Dismiss();
        prompt = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await current.DecideAsync(id, scope, candidate, timeout.Token);
        }
        catch (Exception e) when (IsControlFailure(e))
        {
            settings.ShowMessage("The approval request has closed.");
        }
        ShowNextApproval(current);
    }

    private async Task StopCoreAsync()
    {
        var current = session;
        var child = core;
        session = null;
        core = null;
        hasCurrentState = false;
        prompt?.Dismiss();
        prompt = null;
        waiting.Clear();

        try
        {
            operation?.Cancel();
            urgentOperation?.Cancel();
            try
            {
                await Task.WhenAll(operationComplete?.Task ?? Task.CompletedTask, urgentComplete?.Task ?? Task.CompletedTask)
                    .WaitAsync(TimeSpan.FromMilliseconds(500));
            }
            catch (TimeoutException) { }
            if (current is not null && operation is null && urgentOperation is null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await current.RequestAsync("shutdown", null, timeout.Token);
                }
                catch (Exception e) when (IsControlFailure(e)) { }
            }
            current?.Dispose();
            if (child is not null)
                await child.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception e) when (IsControlFailure(e)) { }
        finally
        {
            current?.Dispose();
            child?.Dispose();
        }
    }

    private async Task QuitAsync()
    {
        if (quitting)
            return;
        quitting = true;
        icon.CanRestart = false;
        settings.Hide();
        statusTimer.Stop();
        // If CreateProcess is in progress, let its bounded bootstrap return so
        // Quit can close the exact handles it created.
        while (transitioning)
            await Task.Delay(20);
        await StopCoreAsync();
        icon.Dispose();
        instance?.ReleaseMutex();
        instance?.Dispose();
        Exit();
    }

    private static bool IsControlFailure(Exception e) =>
        e is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException;
}
