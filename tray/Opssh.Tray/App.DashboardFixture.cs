using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Opssh.Tray;

public sealed partial class App
{
    private DashboardFixture? dashboardFixture;

    private bool TryRunDashboardFixture(string? activationArguments)
    {
        var commandLine = Environment.GetCommandLineArgs()[1..];
        var flag = Array.IndexOf(commandLine, "--dashboard-fixture");
        if (flag < 0)
        {
            commandLine = activationArguments?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            flag = Array.IndexOf(commandLine, "--dashboard-fixture");
        }
        string[] options = flag < 0 ? [] : commandLine[(flag + 1)..];
        var scenario = options.ElementAtOrDefault(0) ?? "ready";
        var page = options.ElementAtOrDefault(1) ?? (scenario == "discover" ? "keys" : "account");
        var theme = options.ElementAtOrDefault(2) is "light" or "dark" ? options[2] : "system";
        var language = options.ElementAtOrDefault(3) is "en" or "zh-Hans" ? options[3] : "system";

        // An opt-in fixture build always stays on its in-memory path, including a launch without arguments.
        DispatcherShutdownMode = Microsoft.UI.Xaml.DispatcherShutdownMode.OnExplicitShutdown;
        dashboardFixture = new DashboardFixture(scenario, new UiPreferences(language, theme), Exit);
        dashboardFixture.Open(page);
        return true;
    }

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

        private readonly string scenario;
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

        internal DashboardFixture(string scenario, UiPreferences preferences, Action exit)
        {
            this.scenario = scenario;
            this.preferences = preferences;
            this.exit = exit;
            var ready = scenario is "ready" or "provider-failed" or "vault-save" or "discover" or "cache" or "logs";
            var account = ready || scenario is "new-account" or "no-access" or "startup" ? Accounts[1] : null;
            failVaultSave = scenario == "vault-save";
            state = new CoreState(DedicatedPipe, false, account?.Id ?? "", [], new CachePolicy("fixed", 300),
                new CachePolicy("fixed", 600), "unknown", 0);
            SetAccount(account?.Id ?? "", account?.Label);
            if (scenario is "no-access" or "startup")
                state = state with { LastProvider = null, ProviderAt = null };
            if (!ready)
                return;

            grants.AddRange([
                new("workstation", SampleId(1), "warp.exe", 10432, "client", Now.AddMinutes(8)),
                new("workstation", SampleId(2), "WindowsTerminal.exe", 8420, "host", Now.AddMinutes(6)),
                new("build", SampleId(3), "Code.exe", 13108, "client", Now.AddMinutes(4))
            ]);
            activity = SampleActivity();
            state = state with
            {
                Scope = new DiscoveryScope("exclude", new() { ["work"] = "include", ["personal"] = "include" }, new()),
                Keys = Catalog[..3].Select(key => key with { Loaded = key.VaultId == "work", Grants = GrantCount(key.Id) }).ToArray(),
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
                (wait, action) => { preferences = preferences with { WaitForPassword = wait, StartupAction = action }; return true; },
                () => defaultAgent, SetDefaultAgentAsync, DiscoveryControl: DiscoveryControlAsync);
            var next = new DashboardWindow(SendAsync, (command, body) => SendAsync(command, body, false),
                () => Task.CompletedTask, preferences, ApplyAppearance, CheckUpdatesAsync,
                enabled => { preferences = preferences with { AutoCheckUpdates = enabled }; return true; }, actions);
            window = next;
            next.Closed += (_, _) =>
            {
                if (ReferenceEquals(window, next))
                {
                    window = null!;
                    exit();
                }
            };
            next.ShowReleaseCheck(release);
            next.SetAvailability(true, false, "");
            next.ShowState(state, resetSettings: true);
            next.SelectPage(page);
            if (bounds is { } value)
                next.AppWindow.MoveAndResize(value);
            next.Activate();
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

        private Task<string?> SwitchAccountAsync(string id, string label, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SetAccount(id, label);
            window.ShowState(state, resetSettings: false);
            window.SelectPage("account");
            return Task.FromResult<string?>(null);
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

        private Task<string?> SetDefaultAgentAsync(bool enabled, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            defaultAgent = enabled;
            state = state with { Pipe = enabled ? DefaultPipe : DedicatedPipe };
            window.ShowState(state, resetSettings: false);
            return Task.FromResult<string?>(null);
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
                        Keys = Catalog.Where(key => state.Scope?.Vaults.ContainsKey(key.VaultId) == true)
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
                    RecordProviderSuccess();
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
