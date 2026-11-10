using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Opssh.Tray;

public sealed partial class App
{
    private DashboardFixture? dashboardFixture;
    private Window? referenceWindow;

    private bool TryRunDashboardFixture(string? activationArguments)
    {
        var commandLine = Environment.GetCommandLineArgs()[1..];
        if (commandLine is ["--dashboard-reference", var reference])
            return OpenDashboardReference(reference);

        var flag = Array.IndexOf(commandLine, "--dashboard-fixture");
        if (flag < 0)
        {
            commandLine = activationArguments?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            flag = Array.IndexOf(commandLine, "--dashboard-fixture");
        }
        string[] options = flag < 0 ? [] : commandLine[(flag + 1)..];
        var scenario = options.ElementAtOrDefault(0) ?? "ready";
        var page = options.ElementAtOrDefault(1) ?? (scenario switch
        {
            "cache" or "logs" or "key-log-open-failure" => "settings",
            "discover" or "key-approval" or "key-preload-failure" or "key-preload-partial" => "keys",
            _ when scenario.StartsWith("default-agent-", StringComparison.Ordinal) => "settings",
            _ => "account"
        });
        var theme = options.ElementAtOrDefault(2) is "light" or "dark" ? options[2] : "system";
        var language = options.ElementAtOrDefault(3) is "en" or "zh-Hans" ? options[3] : "system";

        // An opt-in fixture build always stays on its in-memory path, including a launch without arguments.
        DispatcherShutdownMode = Microsoft.UI.Xaml.DispatcherShutdownMode.OnExplicitShutdown;
        dashboardFixture = new DashboardFixture(scenario, new UiPreferences(language, theme), Exit);
        dashboardFixture.Open(page);
        return true;
    }

    private bool OpenDashboardReference(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase))
        {
            Exit();
            return true;
        }

        DispatcherShutdownMode = Microsoft.UI.Xaml.DispatcherShutdownMode.OnExplicitShutdown;
        var view = new WebView2 { Source = new Uri(path) };
        referenceWindow = new Window { Title = "opssh Visualize reference", Content = view };
        referenceWindow.Closed += (_, _) =>
        {
            view.Close();
            referenceWindow = null;
            Exit();
        };
        ShowFixtureWindow(referenceWindow);
        return true;
    }

    private static void ShowFixtureWindow(Window window)
    {
        // Keep captures behind the user's desktop windows, including after a language change.
        const int windowBottom = 1;
        const uint showWindow = 0x0040;
        const uint noActivate = 0x0010;
        const uint noMove = 0x0002;
        const uint noSize = 0x0001;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (!SetWindowPos(hwnd, windowBottom, 0, 0, 0, 0, showWindow | noActivate | noMove | noSize))
            throw new System.ComponentModel.Win32Exception();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    private sealed class DashboardFixture
    {
        private const string DedicatedPipe = @"\\.\pipe\opssh-agent-0123456789abcdef";
        private const string DefaultPipe = @"\\.\pipe\openssh-ssh-agent";
        private static readonly DateTimeOffset Now = new(new DateTime(2026, 11, 10, 15, 20, 0, DateTimeKind.Local));
        private static readonly DesktopAccount[] Accounts =
        [
            new("demo", "demo@example.test"),
            new("engineering", "Alex Chen(engineering@example.test)")
        ];
        private static readonly DiscoveredVault[] Vaults =
        [
            new("work", "Engineering", [new("workstation", "workstation"), new("build", "build-server")]),
            new("personal", "Personal", [new("lab", "lab-access")]),
            new("archive", "Archive", [new("archived", "archive-access")])
        ];
        private static readonly PublicKey[] Catalog =
        [
            new("workstation", "work", "workstation", "workstation", "SHA256:…qZ2mFY", false, false, null),
            new("build", "work", "build", "build-server", "SHA256:…L4wR8A", false, false, null),
            new("lab", "personal", "lab", "lab-access", "SHA256:…tH9cKQ", false, false, null),
            new("archived", "archive", "archived", "archive-access", "SHA256:…vD6pS8", false, false, null)
        ];

        private static readonly PublicKey[] KeyApprovalCatalog =
        [
            new("vm001", "personal", "vm001", "vm001 ssh key", "SHA256:J8x2VN4tkCQq5pI2aM6aFE8hQ6P0sWD8q1N3RZ2s7lk", false, false, null),
            new("github-auth", "personal", "github-auth", "github authentication key", "SHA256:G7pN3b9fQK2cX6D4yW0eH8sRt5JvLa1mU3nY6pZq8So", false, false, null),
            new("github-sign", "personal", "github-sign", "github signing key", "SHA256:S3mV6k1nR8xH4gZp2uL5J7fQ9aT0iY8bN2cW5oE1dUg", false, false, null)
        ];

        private readonly string scenario;
        private readonly bool keyApprovalPreview;
        private PublicKey[] ActiveCatalog => keyApprovalPreview ? KeyApprovalCatalog : Catalog;
        private readonly string defaultOutcome;
        private readonly Action exit;
        private readonly List<FixtureGrant> grants = [];
        private FixtureActivity[] activity = [];
        private UiPreferences preferences;
        private CoreState state;
        private DashboardWindow window = null!;
        private ReleaseCheckResult release = new(ReleaseCheckStatus.Idle);
        private bool loginStartup;
        private bool defaultAgent;
        private bool firstAccountLookup = true;
        private bool failVaultSave;
        private string? fixtureDiscovery;
        private int discoveryStep;
        private bool discoveryCanceled;
        private bool hasDiagnosticSamples;
        private CancellationTokenSource? signingToastLifetime;
        private Task? signingToastTask;

        internal DashboardFixture(string scenario, UiPreferences preferences, Action exit)
        {
            this.scenario = scenario;
            const string restoreScenario = "default-agent-restore-";
            defaultAgent = scenario.StartsWith(restoreScenario, StringComparison.Ordinal);
            defaultOutcome = defaultAgent ? "default-agent-" + scenario[restoreScenario.Length..] : scenario;
            this.preferences = preferences;
            this.exit = exit;
            keyApprovalPreview = scenario is "key-approval" or "key-preload-failure" or "key-preload-partial" or "key-log-open-failure";
            hasDiagnosticSamples = scenario == "logs" || keyApprovalPreview;
            if (hasDiagnosticSamples)
                this.preferences = preferences with { DiagnosticLogging = true, DiagnosticLevel = keyApprovalPreview ? "DEBUG" : "INFO" };
            if (scenario == "startup")
                this.preferences = preferences with { WaitForPassword = true };
            var ready = keyApprovalPreview || scenario is "ready" or "provider-failed" or "vault-save" or "discover" or "cache" or "logs" ||
                scenario.StartsWith("default-agent-", StringComparison.Ordinal);
            var account = ready || scenario is "new-account" or "no-access" or "startup" ? Accounts[1] : null;
            failVaultSave = scenario == "vault-save";
            state = new CoreState(DedicatedPipe, false, account?.Id ?? "", [], new CachePolicy("fixed", 300),
                new CachePolicy("fixed", 600), "unknown", 0);
            SetAccount(account?.Id ?? "", account?.Label);
            if (defaultAgent)
                state = state with { Pipe = DefaultPipe, DefaultAgent = true };
            if (scenario is "no-access" or "startup")
                state = state with { LastProvider = null, ProviderAt = null };
            if (!ready)
                return;

            if (!keyApprovalPreview)
                grants.AddRange([
                    new("workstation", SampleId(1), "warp.exe", 10432, "app", Now.AddMinutes(8)),
                    new("build", SampleId(3), "Code.exe", 13108, "app", Now.AddMinutes(4))
                ]);
            activity = SampleActivity();
            state = state with
            {
                Scope = new DiscoveryScope("exclude", new() { ["work"] = "include", ["personal"] = "include" }, new()),
                Keys = ActiveCatalog[..3].Select(key => key with { Loaded = key.VaultId == "work", Grants = GrantCount(key.Id) }).ToArray(),
                Index = "ready",
                LastProvider = scenario == "provider-failed" ? "denied" : "last-success"
            };
        }

        internal void Open(string page, RectInt32? bounds = null)
        {
            Strings.Use(preferences.Language == "system" ? "zh-Hans" : preferences.Language);
            var actions = new DashboardActions(ListAccountsAsync, SwitchAccountAsync, RequestAsync,
                () => loginStartup ? LoginStartupStatus.On : LoginStartupStatus.Off,
                enabled => { loginStartup = enabled; return true; },
                wait => { preferences = preferences with { WaitForPassword = wait }; return true; },
                () => defaultAgent, SetDefaultAgentAsync, () => defaultAgent, DiscoveryControlAsync, new DiagnosticActions(
                    (enabled, level) =>
                    {
                        preferences = preferences with { DiagnosticLogging = enabled, DiagnosticLevel = level };
                        return true;
                    },
                    () => Task.FromResult<DiagnosticLogState?>(new(hasDiagnosticSamples
                        ? [new("opssh.log", 384 * 1024), new("opssh.1.log", 1024 * 1024)] : [])),
                    () => Task.FromResult(scenario != "key-log-open-failure"),
                    () => { hasDiagnosticSamples = false; return Task.FromResult(true); }));
            var next = new DashboardWindow(SendAsync, (command, body) => SendAsync(command, body, false),
                () => Task.CompletedTask, preferences, ApplyAppearance, CheckUpdatesAsync,
                enabled => { preferences = preferences with { AutoCheckUpdates = enabled }; return true; }, actions);
            window = next;
            next.Closed += async (_, _) =>
            {
                if (ReferenceEquals(window, next))
                {
                    window = null!;
                    signingToastLifetime?.Cancel();
                    if (signingToastTask is not null)
                        await signingToastTask;
                    exit();
                }
            };
            next.ShowReleaseCheck(release);
            next.SetAvailability(true, false, "");
            next.ShowState(state, resetSettings: true);
            next.SelectPage(page);
            if (bounds is { } value)
                next.AppWindow.MoveAndResize(value);
            ShowFixtureWindow(next);
            if ((scenario == "logs" || keyApprovalPreview) && page == "settings")
                next.RevealDiagnosticSection();
            if (scenario == "startup")
                next.ShowFixtureStartupPrompt(state.AccountName);
            if (scenario is "signing-toast" or "signing-toast-once" && signingToastTask is null)
                signingToastTask = ShowSigningToastAsync(scenario == "signing-toast");
        }

        private async Task ShowSigningToastAsync(bool cacheAllowed)
        {
            if (Environment.ProcessPath is not { } path)
                return;
            using var process = Process.GetCurrentProcess();
            var candidate = new ProcessIdentity((uint)process.Id, (ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc(), path);
            var approval = new Approval("fixture", "workstation", "SHA256:cM0jjnWCB44+awYSYFjkCBnY4i6KKUX4MbXjqZ2mFYI",
                [candidate], DateTimeOffset.UtcNow.AddMinutes(1), cacheAllowed);
            var prompt = new SigningPrompt(approval);
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var chosen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            signingToastLifetime = lifetime;
            using var notifications = new AppNotifications((kind, nonce, choice) =>
                dispatcher.TryEnqueue(() =>
                {
                    if (kind == AppNotifications.Signing && !lifetime.IsCancellationRequested &&
                        prompt.TryConsume(nonce, choice, DateTimeOffset.UtcNow))
                        chosen.TrySetResult();
                }));
            try
            {
                await using var shown = await new SigningNotification(notifications).ShowAsync(prompt, lifetime.Token);
                if (shown is not null)
                    await chosen.Task.WaitAsync(lifetime.Token);
            }
            catch (OperationCanceledException) { }
            finally { signingToastLifetime = null; }
        }

        private bool ApplyAppearance(string language, string theme)
        {
            var effectiveLanguage = language == "system" ? "zh-Hans" : language;
            var next = preferences with { Language = language, Theme = theme };
            if (effectiveLanguage == Strings.Language)
            {
                preferences = next;
                window.ApplyTheme(theme);
                return true;
            }

            var previous = window;
            if (!previous.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(window, previous))
                        return;
                    var appWindow = previous.AppWindow;
                    var bounds = new RectInt32(appWindow.Position.X, appWindow.Position.Y, appWindow.Size.Width, appWindow.Size.Height);
                    Open(previous.CurrentPage, bounds);
                    previous.Close();
                }))
                return false;

            preferences = next;
            return true;
        }

        private async Task<AccountListResult> ListAccountsAsync(CancellationToken token)
        {
            await Task.Delay(650, token);
            var first = firstAccountLookup;
            firstAccountLookup = false;
            return scenario switch
            {
                "no-accounts" => new(AccountListStatus.Empty, []),
                "cli-missing" when first => new(AccountListStatus.NotInstalled, []),
                _ => new(AccountListStatus.Ready, Accounts)
            };
        }

        private async Task<string?> SwitchAccountAsync(string id, string label, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!await window.ConfirmFixtureAuthorizationAsync(label, state.Account.Length != 0))
                return null;
            token.ThrowIfCancellationRequested();
            SetAccount(id, label);
            window.ShowState(state, resetSettings: false);
            window.SelectPage("account");
            window.ShowMessage(Strings.Language == "zh-Hans"
                ? "已连接账户并列出保险库。请选择允许发现的保险库。"
                : "Account connected and vaults listed. Choose the vaults to allow.");
            return null;
        }

        private void SetAccount(string id, string? name)
        {
            var connected = id.Length != 0;
            grants.Clear();
            activity = [];
            state = state with
            {
                Account = id,
                AccountName = name,
                Scope = connected ? new DiscoveryScope("exclude", new(), new()) : null,
                Vaults = connected ? Vaults : [],
                Keys = [],
                Index = null,
                LastProvider = connected ? "last-success" : null,
                ProviderAt = connected ? Now.AddMinutes(-2) : null
            };
        }

        private async Task<string?> SetDefaultAgentAsync(bool enabled, Action<DefaultAgentStage> report, CancellationToken token)
        {
            await StepAsync(DefaultAgentStage.Checking);
            var absent = defaultOutcome == "default-agent-service-absent";
            if (enabled && !await ChangeServiceAsync(DefaultAgentStage.StoppingOpenSsh, DefaultAgentStage.DisablingOpenSsh))
                return "Startup.DefaultRecovery";
            await StepAsync(enabled ? DefaultAgentStage.ReservingPipe : DefaultAgentStage.ReleasingPipe);
            if (defaultOutcome == "default-agent-pipe-failure")
            {
                await StepAsync(DefaultAgentStage.RollingBackAfterFailure);
                return "Startup.DefaultChangeFailed";
            }
            if (!enabled && !await ChangeServiceAsync(DefaultAgentStage.ConfiguringOpenSsh, DefaultAgentStage.StartingOpenSsh))
                return "Startup.DefaultRecovery";
            await StepAsync(DefaultAgentStage.Saving);
            defaultAgent = enabled;
            state = state with { DefaultAgent = enabled, Pipe = enabled ? DefaultPipe : DedicatedPipe };
            window.ShowState(state, resetSettings: false);
            return null;

            async Task<bool> ChangeServiceAsync(DefaultAgentStage first, DefaultAgentStage second)
            {
                if (absent)
                {
                    report(DefaultAgentStage.OpenSshAbsent);
                    return true;
                }
                await StepAsync(first);
                if (defaultOutcome == "default-agent-service-pending")
                    return false;
                await StepAsync(second);
                return true;
            }

            async Task StepAsync(DefaultAgentStage stage)
            {
                token.ThrowIfCancellationRequested();
                report(stage);
                await Task.Delay(TimeSpan.FromMilliseconds(750), token);
            }
        }

        private async Task CheckUpdatesAsync()
        {
            if (release.Status == ReleaseCheckStatus.Checking)
                return;
            release = new ReleaseCheckResult(ReleaseCheckStatus.Checking);
            window.ShowReleaseCheck(release);
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            release = new ReleaseCheckResult(ReleaseCheckStatus.Current);
            window.ShowReleaseCheck(release);
        }

        private async Task SendAsync(string command, object? body, bool resetSettings)
        {
            var data = JsonSerializer.SerializeToElement(body, Protocol.Json);
            var keyId = Field(data, "keyId");
            switch (command)
            {
                case "logout":
                    SetAccount("", null);
                    break;
                case "set-vault":
                    if (state.Scope is not { } scope || Field(data, "vaultId") is not { } vault)
                        return;
                    var allowed = new Dictionary<string, string>(scope.Vaults, StringComparer.Ordinal);
                    var enabled = data.GetProperty("enabled").GetBoolean();
                    if (enabled)
                        allowed[vault] = "include";
                    else
                        allowed.Remove(vault);
                    var remaining = state.Keys.Where(key => allowed.ContainsKey(key.VaultId)).ToArray();
                    grants.RemoveAll(grant => remaining.All(key => key.Id != grant.KeyId));
                    state = state with { Scope = scope with { Vaults = allowed }, Keys = remaining };
                    break;
                case "discover":
                    fixtureDiscovery = Field(data, "operationId");
                    discoveryCanceled = false;
                    var vaultCount = state.Scope?.Vaults.Count ?? 0;
                    for (discoveryStep = 0; discoveryStep <= vaultCount && !discoveryCanceled; discoveryStep++)
                        await Task.Delay(1000);
                    fixtureDiscovery = null;
                    if (discoveryCanceled)
                    {
                        window.ShowMessage(Strings.Get("Keys.DiscoveryCanceled"));
                        return;
                    }
                    state = state with
                    {
                        Keys = ActiveCatalog.Where(key => state.Scope?.Vaults.ContainsKey(key.VaultId) == true)
                            .Select(key => state.Keys.FirstOrDefault(current => current.Id == key.Id) ?? key).ToArray(),
                        Index = "ready"
                    };
                    RecordProviderSuccess();
                    break;
                case "reload-vaults":
                    RecordProviderSuccess();
                    break;
                case "preload":
                case "preload-all":
                    if (keyApprovalPreview)
                    {
                        var owner = window;
                        var account = state.Account;
                        state = state with
                        {
                            Keys = state.Keys.Select(key => !key.Loaded && (keyId is null || key.Id == keyId)
                                ? key with { Loading = true } : key).ToArray()
                        };
                        owner.ShowState(state, resetSettings);
                        await Task.Delay(TimeSpan.FromSeconds(3));
                        if (!ReferenceEquals(owner, window) || account != state.Account)
                            return;
                        var failures = 0;
                        var loads = 0;
                        state = state with
                        {
                            Keys = state.Keys.Select(key =>
                            {
                                if (key.Loaded || keyId is not null && key.Id != keyId)
                                    return key;
                                if (key.Error is null && (scenario == "key-preload-failure" ||
                                    scenario == "key-preload-partial" && key.Id == "github-auth"))
                                {
                                    failures++;
                                    return key with { Loading = false, Error = "private-field-missing" };
                                }
                                loads++;
                                return key with { Loaded = true, Loading = false, Error = null };
                            }).ToArray()
                        };
                        window.ShowState(state, resetSettings);
                        window.ShowMessage(Strings.Get(failures == 0 ? "App.Done" :
                            loads == 0 ? "Keys.PrivateMissing" : "Keys.PreloadPartial"));
                        return;
                    }
                    goto case "evict-keys";
                case "evict-keys":
                    state = state with
                    {
                        Keys = state.Keys.Select(key => keyId is null || key.Id == keyId
                            ? key with { Loaded = command != "evict-keys" } : key).ToArray()
                    };
                    if (command != "evict-keys")
                        RecordProviderSuccess();
                    break;
                case "revoke":
                    var grantId = Field(data, "grantId");
                    grants.RemoveAll(grant => (keyId is null || grant.KeyId == keyId) && (grantId is null || grant.Id == grantId));
                    state = state with { Keys = state.Keys.Select(key => key with { Grants = GrantCount(key.Id) }).ToArray() };
                    break;
                case "settings":
                    var keys = data.GetProperty("keyCache").Deserialize<CachePolicy>(Protocol.Json)!;
                    var approvals = data.GetProperty("approvalCache").Deserialize<CachePolicy>(Protocol.Json)!;
                    if (approvals.Mode == "off")
                        grants.Clear();
                    state = state with
                    {
                        KeyCache = keys,
                        ApprovalCache = approvals,
                        Keys = state.Keys.Select(key => key with
                        {
                            Loaded = keys.Mode != "off" && key.Loaded,
                            Grants = GrantCount(key.Id)
                        }).ToArray()
                    };
                    break;
                default:
                    return;
            }
            window.ShowState(state, resetSettings);
            window.ShowMessage(command == "discover" ? Strings.Get("Keys.DiscoveryFinished", state.Keys.Length) : Strings.Get("App.Done"));
        }

        private Task<Result> DiscoveryControlAsync(string command, object? body)
        {
            var request = JsonSerializer.SerializeToElement(body, Protocol.Json);
            if (fixtureDiscovery is null || fixtureDiscovery != Field(request, "operationId"))
                return Task.FromResult(new Result(false, null, null, false, "discovery-not-active"));
            if (command == "cancel-discovery")
            {
                discoveryCanceled = true;
                return Task.FromResult(new Result(true, null, null, false));
            }
            var total = state.Scope?.Vaults.Count ?? 0;
            var progress = JsonSerializer.SerializeToElement(
                new { phase = discoveryStep < total ? "vaults" : "public", completed = discoveryStep, total }, Protocol.Json);
            return Task.FromResult(new Result(true, null, null, false, Data: progress));
        }

        private async Task<Result> RequestAsync(string command, object? body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var data = JsonSerializer.SerializeToElement(body, Protocol.Json);
            if (command == "set-vault")
            {
                await Task.Delay(600, token);
                if (failVaultSave)
                {
                    failVaultSave = false;
                    return new Result(false, null, state, false, "configuration-write");
                }
                await SendAsync(command, body, false);
                return new Result(true, null, state, false);
            }
            if (command is "authorize-desktop" or "update-account-label")
            {
                if (command == "authorize-desktop")
                {
                    if (!await window.ConfirmFixtureAuthorizationAsync(state.AccountName ?? "", false))
                        return new Result(false, null, state, false, "canceled");
                    RecordProviderSuccess();
                }
                else
                    state = state with { AccountName = Field(data, "name") };
                return new Result(true, null, state, false);
            }
            if (command == "clear-activity")
                activity = [];
            object? reply = command switch
            {
                "grants" => new
                {
                    Grants = grants.Where(grant => grant.KeyId == Field(data, "keyId"))
                        .Select(grant => new { grant.Id, grant.Name, grant.Pid, grant.Scope, grant.ExpiresAt }).ToArray()
                },
                "activity" => new
                {
                    Entries = activity.Select(entry => new { entry.Id, entry.App, entry.Pid, entry.Key, entry.Result, entry.Time, CommandReady = true }).ToArray(),
                    Total = activity.Length
                },
                "activity-command" => activity.FirstOrDefault(entry => entry.Id == Field(data, "id")) is { } match
                    ? new { match.Command } : null,
                _ => null
            };
            return new Result(reply is not null || command == "clear-activity", null, null, false,
                Data: reply is null ? null : JsonSerializer.SerializeToElement(reply, Protocol.Json));
        }

        private void RecordProviderSuccess() => state = state with { LastProvider = "last-success", ProviderAt = Now };

        private int GrantCount(string keyId) => grants.Count(grant => grant.KeyId == keyId);

        private static string? Field(JsonElement value, string field) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var property) ? property.GetString() : null;

        private static string SampleId(int index) => index.ToString("D26", CultureInfo.InvariantCulture);

        private static FixtureActivity[] SampleActivity()
        {
            var examples = new (string App, uint Pid, string Key, string Result)[]
            {
                ("warp.exe", 10432, "workstation", "published"),
                ("Code.exe", 13108, "build-server", "published"),
                ("WindowsTerminal.exe", 8420, "lab-access", "denied"),
                ("Code.exe", 13108, "workstation", "published"),
                ("warp.exe", 10432, "build-server", "canceled"),
                ("WindowsTerminal.exe", 8420, "workstation", "published"),
                ("warp.exe", 10432, "lab-access", "denied")
            };
            var entries = new List<FixtureActivity>(24);
            void Add(int index, DateTimeOffset time)
            {
                var example = examples[index];
                var command = example.Key switch
                {
                    "workstation" => "ssh -v -o ConnectTimeout=10 -o ForwardAgent=no -o ServerAliveInterval=30 workstation.example.test",
                    "build-server" => "ssh -T -o BatchMode=yes -o ControlMaster=no -o StrictHostKeyChecking=yes git@build.example.test",
                    _ => "ssh lab.example.test"
                };
                entries.Add(new FixtureActivity(SampleId(entries.Count + 1), example.App, example.Pid,
                    example.Key, example.Result, time, command));
            }

            for (var i = 0; i < 3; i++)
                Add(i, Now.AddMinutes(-(2 + i * 4)));
            for (var day = 1; day <= 3; day++)
                for (var i = 0; i < examples.Length; i++)
                    Add(i, new DateTimeOffset(Now.LocalDateTime.Date.AddDays(-day).AddHours(17 - i).AddMinutes(18)));
            return entries.ToArray();
        }

        private sealed record FixtureGrant(string KeyId, string Id, string Name, uint Pid, string Scope, DateTimeOffset ExpiresAt);
        private sealed record FixtureActivity(string Id, string App, uint Pid, string Key, string Result, DateTimeOffset Time, string Command);
    }
}

public sealed partial class DashboardWindow
{
    internal async Task<bool> ConfirmFixtureAuthorizationAsync(string label, bool switching)
    {
        if (closed)
            return false;
        var chinese = Strings.Language == "zh-Hans";
        var explanation = switching ? Strings.Get("Dashboard.SwitchAccountDescription") :
            chinese ? "真实验证会在 1Password 窗口完成。" : "1Password handles the actual authorization.";
        var dialog = new ContentDialog
        {
            Title = chinese ? "1Password 授权" : "1Password authorization",
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { Ui.Text(chinese ? $"为 {label} 请求授权。" : $"Request access for {label}."), Quiet(explanation) }
            },
            PrimaryButtonText = chinese ? "模拟批准" : "Simulate approval",
            CloseButtonText = Strings.Get("Dashboard.Cancel")
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    internal void ShowFixtureStartupPrompt(string? label)
    {
        var chinese = Strings.Language == "zh-Hans";
        var prompt = Card(new StackPanel());
        prompt.Width = 312;
        prompt.Margin = new Thickness(0, 0, 18, 18);
        prompt.HorizontalAlignment = HorizontalAlignment.Right;
        prompt.VerticalAlignment = VerticalAlignment.Bottom;
        Canvas.SetZIndex(prompt, 20);
        Grid.SetRowSpan(prompt, DashboardRoot.RowDefinitions.Count);
        var choose = Ui.Button(chinese ? "选择账户" : "Choose account", async () =>
        {
            DashboardRoot.Children.Remove(prompt);
            SelectPage("account");
            await ShowAccountPickerAsync(authorizeCurrent: true);
        });
        choose.Style = StyleFor("AccentButtonStyle");
        var later = Ui.Button(chinese ? "稍后" : "Later", () => DashboardRoot.Children.Remove(prompt));
        var content = new StackPanel
        {
            Padding = new Thickness(14),
            Spacing = 9,
            Children =
            {
                Ui.Heading("opssh", 14),
                Ui.Heading(chinese ? "1Password 已就绪" : "1Password is ready", 14),
                Quiet(chinese ? "选择要授权的 1Password 账户。" : "Choose a 1Password account to authorize."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { choose, later } }
            }
        };
        if (!string.IsNullOrEmpty(label))
            content.Children.Insert(3, Quiet(chinese ? $"当前账户为 {label}。" : $"The current account is {label}."));
        prompt.Child = content;
        DashboardRoot.Children.Add(prompt);
    }
}
