using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Win32.SafeHandles;
using Windows.Graphics;

namespace Opssh.Tray;

public sealed partial class App : Application
{
    internal static Version InstalledVersion { get; } = typeof(App).Assembly.GetName().Version!;

    private DispatcherQueue dispatcher = null!;
    private DispatcherQueueTimer? statusTimer;
    private DashboardWindow? settings;
    private RectInt32 settingsRestoreBounds;
    private TrayIcon icon = null!;
    private Mutex? instance;
    private CoreProcess? core;
    private CoreSession? session;
    private Hello? hello;
    private CoreState? state;
    private bool? locked;
    private UiPreferences preferences = null!;
    private HttpClient? releaseClient;
    private CancellationTokenSource? updateCancellation;
    private bool checkingAtStartup;
    private ReleaseCheckResult releaseCheck = new(ReleaseCheckStatus.Idle);
    private string availability = "App.Starting";
    private string? message;
    private CancellationTokenSource? operation;
    private TaskCompletionSource? operationComplete;
    private CancellationTokenSource? urgentOperation;
    private TaskCompletionSource? urgentComplete;
    private int stateRevision;
    private bool polling;
    private CoreSession? waitingForPoll;
    private AppNotifications notifications = null!;
    private SigningApprovalQueue signingApprovals = null!;
    private bool transitioning;
    private bool quitting;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if OPSSH_DASHBOARD_FIXTURE
        if (TryRunDashboardFixture(args.Arguments))
            return;
#endif
        instance = ClaimLogonInstance();
        if (instance is null)
        {
            Exit();
            return;
        }
        DispatcherShutdownMode = Microsoft.UI.Xaml.DispatcherShutdownMode.OnExplicitShutdown;
        dispatcher = DispatcherQueue.GetForCurrentThread();
        preferences = UiPreferences.Load();
        defaultAgentDesired = defaultAgent.Read()?.Enabled == true;
        offeredStartupNotification = FromAppNotification();
        notifications = new AppNotifications((kind, nonce, choice) =>
            dispatcher.TryEnqueue(() =>
            {
                if (quitting)
                    return;
                if (kind == AppNotifications.Signing)
                    signingApprovals.Invoke(nonce, choice);
                else
                    startupNotification.Invoke(nonce, choice);
            }));
        startupNotification = new StartupNotification(notifications, ActivateStartupPrompt);
        signingApprovals = new SigningApprovalQueue(new SigningNotification(notifications).ShowAsync);
        Strings.Use(preferences.ResolveLanguage());
        icon = new TrayIcon();
        icon.OpenRequested += OpenSettings;
        icon.RestartRequested += () => _ = StartCoreAsync();
        icon.QuitRequested += () => _ = QuitAsync();
        StartDiagnostics();
        _ = Environment.GetCommandLineArgs() switch
        {
            [_, "--install-login"] => CompleteInstallationAsync(enableDefaultAgent: false),
            [_, "--install-login-default"] => CompleteInstallationAsync(enableDefaultAgent: true),
            _ => StartCoreAsync()
        };
        if (preferences.AutoCheckUpdates)
            _ = CheckForUpdatesAsync(atStartup: true);
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

    private void OpenSettings() => OpenSettings(null, maximize: false);

    private void OpenSettings(RectInt32? appearanceBounds, bool maximize, bool refresh = true)
    {
        if (quitting)
            return;
        if (settings is null)
        {
            settings = new DashboardWindow(RunCommandAsync, RunUrgentAsync, StartCoreAsync, preferences, ApplyAppearance,
                () => CheckForUpdatesAsync(atStartup: false), SetAutoCheckUpdates,
                new DashboardActions(OnePasswordAccounts.ListAsync, SwitchAccountAsync, RequestDashboardAsync,
                    LoginStartup.Read, SetLoginStartup, SetWaitForPassword, () => defaultAgentDesired, SetDefaultAgentAsync,
                    () => defaultAgentActive, RequestDiscoveryControlAsync, CreateDiagnosticActions()));
            settings.Closed += SettingsClosed;
            settings.ShowReleaseCheck(releaseCheck);
            settings.ShowMessage(message is null ? "" : Strings.Get(message));
            settings.SetAvailability(hello is not null, icon.CanRestart, Strings.Get(availability));
            if (hello is not null)
                settings.SetHello(hello);
            // A newly opened editor stays gated until a current session
            // response arrives, even if the tray retained an older snapshot.
            if (locked is { } value)
                settings.SetLockState(value);
            settings.SetBusy(waitingForPoll is not null || (operation is not null && !polling));
            settings.SetUrgentBusy(urgentOperation is not null);
            if (appearanceBounds is { } bounds)
            {
                settings.AppWindow.MoveAndResize(bounds);
                settings.SelectAppearance();
            }
            settingsRestoreBounds = WindowBounds(settings.AppWindow);
            settings.AppWindow.Changed += SettingsBoundsChanged;
            if (maximize && settings.AppWindow.Presenter is OverlappedPresenter restoredPresenter)
                restoredPresenter.Maximize();

            statusTimer = dispatcher.CreateTimer();
            statusTimer.Interval = TimeSpan.FromSeconds(2);
            statusTimer.Tick += PollSettings;
            statusTimer.Start();
        }

        if (settings.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        settings.Activate();
        if (refresh)
            _ = RefreshSettingsAsync();
    }

    private void SettingsClosed(object sender, WindowEventArgs args)
    {
        if (sender is DashboardWindow closed && settings == closed)
            ReleaseSettings(closed);
    }

    private void ReleaseSettings(DashboardWindow closed)
    {
        closed.Closed -= SettingsClosed;
        closed.AppWindow.Changed -= SettingsBoundsChanged;
        settings = null;
        if (statusTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= PollSettings;
            statusTimer = null;
        }
    }

    private void SettingsBoundsChanged(AppWindow window, AppWindowChangedEventArgs args)
    {
        if ((args.DidPositionChange || args.DidSizeChange) &&
            window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            settingsRestoreBounds = WindowBounds(window);
    }

    private static RectInt32 WindowBounds(AppWindow window) =>
        new(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);

    private bool ApplyAppearance(string language, string theme) =>
        ApplyPreferences(preferences with { Language = language, Theme = theme });

    private bool SetAutoCheckUpdates(bool enabled)
    {
        if (!ApplyPreferences(preferences with { AutoCheckUpdates = enabled }))
            return false;
        if (!enabled && checkingAtStartup)
            updateCancellation?.Cancel();
        return true;
    }

    private bool ApplyPreferences(UiPreferences next)
    {
        if (next == preferences)
            return true;
        if (!next.TrySave())
            return false;

        var previous = preferences;
        preferences = next;
        if (next.Theme != previous.Theme)
            settings?.ApplyTheme(next.Theme);
        var language = next.ResolveLanguage();
        if (language != Strings.Language)
        {
            Strings.Use(language);
            icon.RefreshLanguage();
            if (settings is { } current)
                dispatcher.TryEnqueue(() => ReopenAppearance(current));
        }
        return true;
    }

    private async Task CheckForUpdatesAsync(bool atStartup)
    {
        if (quitting || updateCancellation is not null)
            return;

        using var check = new CancellationTokenSource();
        updateCancellation = check;
        checkingAtStartup = atStartup;
        ShowReleaseCheck(new(ReleaseCheckStatus.Checking));
        try
        {
            releaseClient ??= new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                MaxResponseDrainSize = 0
            });
            var result = await new ReleaseCheck(releaseClient).CheckAsync(InstalledVersion, check.Token);
            if (quitting)
                return;
            check.Token.ThrowIfCancellationRequested();
            ShowReleaseCheck(result);
            if (atStartup && result is { Status: ReleaseCheckStatus.Available, Latest: { } latest })
                icon.ShowUpdateAvailable(latest);
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested)
        {
            if (!quitting)
                ShowReleaseCheck(new(ReleaseCheckStatus.Idle));
        }
        finally
        {
            updateCancellation = null;
            checkingAtStartup = false;
        }
    }

    private void ShowReleaseCheck(ReleaseCheckResult result)
    {
        releaseCheck = result;
        settings?.ShowReleaseCheck(result);
    }

    private void ReopenAppearance(DashboardWindow current)
    {
        if (quitting || settings != current)
            return;
        var maximized = current.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        var bounds = maximized ? settingsRestoreBounds : WindowBounds(current.AppWindow);
        var page = current.CurrentPage;
        ReleaseSettings(current);
        current.Close();
        OpenSettings(bounds, maximized);
        settings?.SelectPage(page);
    }

    private void PollSettings(DispatcherQueueTimer sender, object args) => _ = RefreshSettingsAsync();

    private bool SettingsVisible => settings?.AppWindow is { IsVisible: true } window &&
        window.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    private Task RefreshSettingsAsync() => RunCommandAsync("status", null, background: true);

    private void SetAvailability(bool connected, bool canRestart, string key)
    {
        availability = key;
        icon.CanRestart = canRestart;
        if (!connected)
        {
            hello = null;
            state = null;
            locked = null;
        }
        settings?.SetAvailability(connected, canRestart, Strings.Get(key));
    }

    private void ShowMessage(string? key)
    {
        message = key;
        settings?.ShowMessage(key is null ? "" : Strings.Get(key));
    }

    private void SetLockState(bool value)
    {
        locked = value;
        if (state is not null)
            state = state with { Locked = value };
        settings?.SetLockState(value);
        icon.SetTip(value ? "Tray.Locked" : "Tray.Connected");
    }

    private Task StartCoreAsync() => StartCoreAsync(null);

    private async Task StartCoreAsync(object? connectAfterRestart)
    {
        if (transitioning || quitting || session is not null)
            return;
        transitioning = true;
        SetAvailability(false, false, "App.Starting");
        icon.SetTip("Tray.Starting");
        var diagnostic = BeginTrayDiagnostic();
        CoreProcess? child = null;
        CoreSession? candidate = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            child = await CoreProcess.StartAsync(timeout.Token);
            core = child;
            candidate = new CoreSession(child.Pipe);
            var current = candidate;
            ObserveCore(current);
            session = current;
            signingApprovals.SetOwner(current);
            var greeting = await current.StartAsync(timeout.Token);
            if (quitting || session != current)
                return;
            if (!current.IsConnected)
                throw new IOException("The core disconnected during startup.");
            hello = greeting;
            settings?.SetHello(greeting);
            SetAvailability(true, false, "App.Connected");
            icon.SetTip("Tray.Connected");
            RecordTrayDiagnostic(diagnostic, "core-start", "completed");
            await RestoreDefaultAgentAsync(current);
            ConfigureStartupWait();
        }
        catch (Exception e) when (IsControlFailure(e))
        {
            if (session == candidate)
            {
                signingApprovals.SetOwner(null);
                session = null;
                candidate?.Dispose();
                core = null;
                child?.Dispose();
            }
            if (!quitting)
            {
                RecordTrayDiagnostic(diagnostic, "core-start", e is OperationCanceledException ? "timeout" : "failed");
                var key = e switch
                {
                    FileNotFoundException => "Error.CoreMissing",
                    CoreStartupException startup => startup.FeedbackKey,
                    _ => "Error.StartFailed"
                };
                ShowMessage(null);
                SetAvailability(false, true, key);
                icon.SetTip("Tray.Unavailable");
            }
        }
        finally
        {
            transitioning = false;
            icon.CanRestart = !quitting && session is null;
        }
        if (quitting || session is null)
            return;
        if (connectAfterRestart is not null)
            await RunCommandAsync("connect", connectAfterRestart, background: false, afterAccountRestart: true);
        else
            await RefreshSettingsAsync();
    }

    private Task RunCommandAsync(string command, object? body, bool background) =>
        RunCommandAsync(command, body, background, afterAccountRestart: false);

    private async Task RunCommandAsync(string command, object? body, bool background, bool afterAccountRestart)
    {
        var current = session;
        if (current is null || transitioning || quitting || (!background && changingDefaultAgent) ||
            (background && (!SettingsVisible || waitingForPoll is not null || urgentOperation is not null || normalRequests.CurrentCount == 0)) ||
            (state is null && command != "status" && !afterAccountRestart) ||
            (command == "unlock" && urgentOperation is not null))
            return;
        if (operationComplete is { } active)
        {
            if (background || !polling || waitingForPoll is not null)
                return;
            waitingForPoll = current;
            settings?.SetBusy(true);
            await active.Task;
            if (waitingForPoll == current)
                waitingForPoll = null;
            if (current != session || transitioning || quitting)
                return;
            if (command == "unlock" && urgentOperation is not null)
            {
                settings?.SetBusy(false);
                return;
            }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        operation = timeout;
        polling = background;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operationComplete = completed;
        if (!background)
        {
            settings?.SetBusy(true);
            if (command == "reload-vaults")
                ShowMessage("App.ProviderVerification");
            else if (command is "set-scope" or "refresh-index")
                ShowMessage("App.Discovering");
        }
        var diagnosticPhase = command switch
        {
            "discover" or "refresh-index" => "discover",
            "reload-vaults" or "authorize-desktop" => "provider-access",
            _ => null
        };
        var diagnostic = diagnosticPhase is null ? default : BeginTrayDiagnostic();
        var preload = command is "preload" or "preload-all" ? BeginPreloadDiagnostic(current, command) : null;
        var revision = stateRevision;
        var restart = false;
        var refresh = false;
        try
        {
            var result = await RequestNormalAsync(current, command, body, timeout.Token);
            if (current != session || quitting)
                return;
            RecordPreloadDiagnostic(preload, preload?.Complete(result));
            if (diagnosticPhase is not null)
                RecordTrayDiagnostic(diagnostic, diagnosticPhase, result.Ok ? "completed" : result.ErrorCode switch
                {
                    "provider-timeout" => "timeout",
                    "canceled" => "canceled",
                    _ => "failed"
                });
            if (revision != stateRevision || urgentOperation is not null)
                refresh = true;
            else if (result.Ok && command == "unlock" && result.State is null)
            {
                SetLockState(false);
                refresh = true;
            }
            else if (result.State is { } latest)
            {
                state = latest;
                locked = latest.Locked;
                defaultAgentActive = latest.DefaultAgent;
                settings?.ShowState(latest, result.Ok && (command is "settings" or "connect"));
                icon.SetTip(latest.Locked ? "Tray.Locked" : "Tray.Connected");
            }
            restart = result.Ok && result.RestartRequired && (command == "logout" || command == "connect" && !afterAccountRestart);
            if (command == "discover" && result.Ok && result.State is { } discovered)
                settings?.ShowMessage(Strings.Get("Keys.DiscoveryFinished", discovered.Keys.Length));
            else if (!result.Ok)
                ShowMessage(CoreFeedback.ResultKey(result.ErrorCode, result.Error));
            else if (!background)
                ShowMessage(restart ? "App.RestartingAccount" :
                    result.RestartRequired ? "Error.RestartRequired" : "App.Done");
        }
        catch (Exception e) when (IsControlFailure(e))
        {
            if (current == session && !quitting)
            {
                RecordPreloadDiagnostic(preload, preload?.Fail(e));
                if (diagnosticPhase is not null)
                    RecordTrayDiagnostic(diagnostic, diagnosticPhase, e is OperationCanceledException ? "timeout" : "failed");
                ShowMessage("Error.ControlRequestFailed");
            }
        }
        finally
        {
            if (preload is not null && preloadDiagnostic == preload)
                preloadDiagnostic = null;
            if (operation == timeout)
            {
                operation = null;
                operationComplete = null;
                polling = false;
                if (!background && current == session && !quitting)
                    settings?.SetBusy(false);
            }
            completed.TrySetResult();
        }
        if (restart)
        {
            transitioning = true;
            SetAvailability(false, false, "App.Restarting");
            await StopCoreAsync();
            transitioning = false;
            await StartCoreAsync(command == "connect" ? body : null);
        }
        else if (refresh && current == session)
        {
            await RefreshSettingsAsync();
        }
    }

    private async Task RunUrgentAsync(string command, object? body)
    {
        var current = session;
        if (current is null || transitioning || quitting || urgentOperation is not null ||
            (state is null && command == "revoke" && body is not null))
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        urgentOperation = timeout;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        urgentComplete = completed;
        stateRevision++;
        settings?.SetUrgentBusy(true);
        var ownsSlot = false;
        try
        {
            await discoveryControls.WaitAsync(timeout.Token);
            ownsSlot = true;
            if (current != session || quitting)
                return;
            var result = await current.RequestAsync(command, body, timeout.Token);
            if (current != session || quitting)
                return;
            if (result.Ok && command == "lock")
                SetLockState(true);
            ShowMessage(result.Ok ? "App.Done" : CoreFeedback.ResultKey(result.ErrorCode, result.Error));
        }
        catch (Exception e) when (IsControlFailure(e))
        {
            if (current == session && !quitting)
                ShowMessage("Error.ControlRequestFailed");
        }
        finally
        {
            if (ownsSlot)
                discoveryControls.Release();
            if (urgentOperation == timeout)
            {
                stateRevision++;
                urgentOperation = null;
                urgentComplete = null;
                if (current == session && !quitting)
                    settings?.SetUrgentBusy(false);
            }
            completed.TrySetResult();
        }

        if (current == session)
            await RefreshSettingsAsync();
    }

    private async Task CoreDisconnectedAsync(CoreSession current)
    {
        if (current != session)
            return;
        signingApprovals.SetOwner(null);
        if (transitioning || quitting)
            return;
        transitioning = true;
        ShowMessage(null);
        SetAvailability(false, false, "App.Stopped");
        icon.SetTip("Tray.Stopped");
        if (preloadDiagnostic is { } preload && preload.Session == current)
            RecordPreloadDiagnostic(preload, preload.Fail());
        RecordTrayDiagnostic(BeginTrayDiagnostic(), "core-session", "disconnected");
        await StopCoreAsync();
        transitioning = false;
        if (!quitting)
            SetAvailability(false, true, "App.Stopped");
    }

    private void ObserveCore(CoreSession current)
    {
        current.ApprovalRequested += approval => dispatcher.TryEnqueue(() => signingApprovals.Enqueue(current, approval));
        current.ApprovalEnded += id => dispatcher.TryEnqueue(() => signingApprovals.End(current, id));
        current.Disconnected += () => dispatcher.TryEnqueue(() => _ = CoreDisconnectedAsync(current));
    }

    private async Task StopCoreAsync()
    {
        CancelStartupWait();
        defaultAgentActive = false;
        var current = session;
        var child = core;
        var active = operation;
        var urgent = urgentOperation;
        var requests = Task.WhenAll(operationComplete?.Task ?? Task.CompletedTask, urgentComplete?.Task ?? Task.CompletedTask);
        session = null;
        core = null;
        waitingForPoll = null;
        var retireApprovals = signingApprovals.StopAsync();

        try
        {
            active?.Cancel();
            urgent?.Cancel();
            try
            {
                await requests.WaitAsync(TimeSpan.FromMilliseconds(500));
            }
            catch (TimeoutException) { }
            if (current is not null && requests.IsCompleted)
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
            operation = null;
            operationComplete = null;
            polling = false;
            urgentOperation = null;
            urgentComplete = null;
            current?.Dispose();
            child?.Dispose();
            await retireApprovals;
        }
    }

    private async Task QuitAsync()
    {
        if (quitting)
            return;
        quitting = true;
        signingApprovals.SetOwner(null);
        StopDiagnostics();
        updateCancellation?.Cancel();
        accountTransition?.Cancel();
        var retireStartup = startupNotification.DismissAsync();
        CancelStartupWait();
        releaseClient?.Dispose();
        icon.CanRestart = false;
        settings?.Close();
        // If CreateProcess is in progress, let its bounded bootstrap return so
        // Quit can close the exact handles it created.
        while (transitioning)
            await Task.Delay(20);
        await StopCoreAsync();
        await retireStartup;
        notifications.Dispose();
        icon.Dispose();
        instance?.ReleaseMutex();
        instance?.Dispose();
        Exit();
    }

    private static bool IsControlFailure(Exception e) =>
        e is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException;
}
