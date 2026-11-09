using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Opssh.Tray;

public sealed partial class SettingsWindow : Window
{
    private readonly TextBlock status = Ui.Text("Starting…");
    private readonly TextBlock pending = Ui.Text("—");
    private readonly TextBlock accountValue = Ui.Text("Not connected");
    private readonly TextBlock providerStatus = Ui.Text("—");
    private readonly TextBlock indexStatus = Ui.Text("Not indexed");
    private readonly TextBlock indexDescription = Ui.Text("");
    private readonly TextBlock keyCount = Ui.Text("0 usable keys");
    private readonly TextBlock keyDetails = Ui.Text("No usable SSH keys.");
    private readonly TextBlock version = Ui.Text("—");
    private readonly InfoBar message = new() { IsClosable = true };
    private readonly TextBox account = new()
    {
        Header = "Account name or ID",
        MaxLength = 256,
        PlaceholderText = "Account sidebar name or UUID",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBox command = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly ComboBox keys = new() { Header = "Indexed SSH key", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly PolicyInputs keyPolicy = new("Private-key cache");
    private readonly PolicyInputs approvalPolicy = new("Approval cache");
    private readonly TextBlock keyPolicyValue = Ui.Text("—");
    private readonly TextBlock approvalPolicyValue = Ui.Text("—");
    private readonly TextBlock approvalKey = Ui.Text("No indexed SSH key selected.");
    private readonly ContentControl connectControls;
    private readonly ContentControl keyControls;
    private readonly ContentControl cacheControls;
    private readonly SettingsScopeEditor scope;
    private readonly Button restart;
    private readonly Button toggleLock;
    private readonly Button showCommand;
    private readonly Button refreshIndex;
    private readonly Button preload;
    private readonly Button evict;
    private readonly Button revokeAll;
    private readonly Button revokeSelected;
    private readonly NavigationView navigation;
    private readonly NavigationViewItem discoveryPage;
    private string? pipe;
    private string currentAccount = "";
    private bool connected;
    private bool busy;
    private bool urgentBusy;
    private bool locked;
    private bool initialized;
    private bool updatingKeys;

    internal SettingsWindow(Func<string, object?, bool, Task> send, Func<string, object?, Task> sendUrgent, Func<Task> start)
    {
        InitializeComponent();
        Title = "opssh Settings";
        AppWindow.Resize(new SizeInt32(1060, 800));
        status.MaxWidth = 260;
        accountValue.MaxWidth = 260;

        restart = Ui.Button("Restart core", async () => await start());
        toggleLock = Ui.Button("Lock", async () =>
        {
            if (locked)
                await send("unlock", null, false);
            else
                await sendUrgent("lock", null);
        });
        showCommand = Ui.Button("Show command", ToggleShellCommand);
        AutomationProperties.SetName(command, "Temporary PowerShell environment command");
        refreshIndex = Ui.Button("Refresh index", async () => await send("refresh-index", null, false));
        preload = Ui.Button("Preload selected", async () =>
        {
            if (SelectedKey is { } key)
                await send("preload", new { keyId = key.Id }, false);
        });
        evict = Ui.Button("Evict loaded keys", async () => await sendUrgent("evict-keys", null));
        revokeAll = Ui.Button("Revoke all approvals", async () => await sendUrgent("revoke", null));
        revokeSelected = Ui.Button("Revoke selected", async () =>
        {
            if (SelectedKey is { } key)
                await sendUrgent("revoke", new { keyId = key.Id });
        });

        var connect = Ui.Button("Connect", async () =>
        {
            var requestedAccount = account.Text.Trim();
            if (requestedAccount.Length == 0)
            {
                ShowMessage("Enter the name or ID of an account signed in to 1Password.");
                account.Focus(FocusState.Programmatic);
                return;
            }

            await send("connect", new { account = requestedAccount }, false);
        });
        connect.Style = (Style)SettingsRoot.Resources["SettingsConnectButtonStyle"];
        connectControls = Container(SettingsLayout.Row(account, connect));
        connect.VerticalAlignment = VerticalAlignment.Bottom;
        keys.SelectionChanged += (_, _) =>
        {
            if (!updatingKeys)
                UpdateKeyDetails();
        };
        keyControls = Container(Flow(keys, keyDetails, preload));
        scope = new SettingsScopeEditor(rule => send("set-scope", rule, false), ShowMessage);

        var saveCaches = Ui.Button("Apply cache settings", async () =>
        {
            if (!keyPolicy.TryRead(out var keyCache) || !approvalPolicy.TryRead(out var approvalCache))
            {
                ShowMessage($"Enter a whole number of seconds from 1 to {CachePolicy.MaxSeconds} for each timed cache.");
                return;
            }
            await send("settings", new { account = currentAccount, keyCache, approvalCache }, false);
        });
        cacheControls = Container(Flow(
            SettingsLayout.Row(keyPolicy.Mode, keyPolicy.Seconds),
            SettingsLayout.Row(approvalPolicy.Mode, approvalPolicy.Seconds),
            saveCaches));

        var overview = NavigationItem("Overview", "\uE80F", BuildOverview());
        discoveryPage = NavigationItem("1Password & keys", "\uE8D7", BuildDiscovery());
        var policies = NavigationItem("Approval & caches", "\uE72E", BuildPolicies());
        var about = NavigationItem("About", "\uE946", BuildAbout());
        var page = new ScrollViewer { HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition());
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(page);
        message.Margin = new Thickness(24, 0, 24, 16);
        Grid.SetRow(message, 1);
        content.Children.Add(message);

        navigation = new NavigationView
        {
            PaneTitle = "opssh",
            PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
            OpenPaneLength = 224,
            ExpandedModeThresholdWidth = 880,
            CompactModeThresholdWidth = 620,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            AlwaysShowHeader = false,
            Content = content
        };
        AutomationProperties.SetName(navigation, "Settings");
        navigation.MenuItems.Add(overview);
        navigation.MenuItems.Add(discoveryPage);
        navigation.MenuItems.Add(policies);
        navigation.FooterMenuItems.Add(about);
        navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: StackPanel selected })
            {
                page.Content = selected;
                page.ChangeView(null, 0, null, true);
            }
        };
        SettingsRoot.Children.Add(navigation);
        page.Content = overview.Tag;
        navigation.SelectedItem = overview;
        SetAvailability(false, false, "Starting the core…");
    }

    private StackPanel BuildOverview()
    {
        var page = Page("Overview", "Manage the SSH agent on this PC.");
        page.Children.Add(Section("Agent status",
            Card("\uE968", "Core status", "The local process that handles SSH requests.", Flow(status, restart)),
            Card("\uE7F4", "Pending approvals", "SSH requests waiting for a decision.", pending),
            Card("\uE72E", "Agent lock", "Locking clears loaded private keys and existing approvals.", toggleLock)));
        page.Children.Add(Section("SSH client setup",
            Card("\uE756", "PowerShell", "Select opssh as the SSH agent for the current PowerShell session.", showCommand, command)));
        return page;
    }

    private StackPanel BuildDiscovery()
    {
        var page = Page("1Password & keys", "Connect an account, choose its SSH items, and see which keys are ready to use.");
        page.Children.Add(Section("Account",
            Card("\uE77B", "1Password account", "Sign in to the 1Password desktop app. Under Settings → Developer, turn on Integrate with 1Password SDKs. Complete any verification in the 1Password window.",
                accountValue, connectControls),
            Card("\uE8D7", "Desktop access", "The result of the most recent request to 1Password.", providerStatus)));
        page.Children.Add(Section("SSH key discovery",
            Card("\uE721", "Public index", "Discover SSH items and refresh their public keys.", Flow(indexStatus, refreshIndex), indexDescription),
            scope.View,
            Card("\uE192", "Usable SSH keys", "Indexed public keys are available to SSH. Private keys load after approval or when you preload them here.",
                keyCount, keyControls)));
        return page;
    }

    private StackPanel BuildPolicies()
    {
        var page = Page("Approval & caches", "Manage SSH approvals and how long opssh keeps them and loaded private keys.");
        page.Children.Add(Section("Local controls",
            Card("\uE8C9", "Loaded private keys", "Clear private key material from the local cache.", evict),
            Card("\uE73E", "Process approvals", "Clear approvals granted to SSH clients and hosts.", revokeAll),
            Card("\uE192", "Approvals for one key", "Revoke approvals for the key selected on the 1Password & keys page.",
                revokeSelected, SettingsLayout.Row(approvalKey, Ui.Button("Go to keys", () =>
                {
                    navigation.SelectedItem = discoveryPage;
                    discoveryPage.Focus(FocusState.Programmatic);
                })))));
        page.Children.Add(Section("Cache policies",
            Card("\uE8D7", "Private-key cache", "Controls how long loaded private keys are kept in memory.", keyPolicyValue),
            Card("\uE73E", "Approval cache", "Controls how long process approvals can be reused.", approvalPolicyValue),
            new Expander
            {
                Header = "Change cache policies",
                Content = cacheControls,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            }));
        return page;
    }

    private StackPanel BuildAbout()
    {
        var page = Page("About", "opssh connects Windows SSH clients to SSH keys stored in 1Password.");
        page.Children.Add(Section("Version",
            Card("\uE946", "Core version", "Version reported by the bundled SSH agent.", version)));
        return page;
    }

    private static NavigationViewItem NavigationItem(string label, string glyph, StackPanel page) =>
        new() { Content = label, Icon = new FontIcon { Glyph = glyph }, Tag = page };

    private StackPanel Page(string title, string description)
    {
        var heading = Ui.Heading(title, 30);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        heading.TextWrapping = TextWrapping.Wrap;
        return new StackPanel
        {
            Padding = new Thickness(24, 20, 24, 24),
            MaxWidth = 1000,
            Spacing = 24,
            Children = { Flow(heading, Description(description)) }
        };
    }

    private static StackPanel Section(string title, params UIElement[] cards)
    {
        var heading = Ui.Heading(title, 16);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        return Flow([heading, .. cards]);
    }

    private Border Card(string glyph, string title, string description, FrameworkElement? action = null, FrameworkElement? detail = null)
    {
        var name = Ui.Heading(title, 14);
        name.TextWrapping = TextWrapping.Wrap;
        var label = Flow(name, Description(description));
        label.Spacing = 2;

        var icon = new FontIcon { Glyph = glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        var intro = new Grid { ColumnSpacing = 14 };
        intro.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        intro.ColumnDefinitions.Add(new ColumnDefinition());
        intro.Children.Add(icon);
        Grid.SetColumn(label, 1);
        intro.Children.Add(label);

        var contents = Flow(SettingsLayout.Row(intro, action));
        if (detail is not null)
            contents.Children.Add(detail);
        return new Border { Style = (Style)SettingsRoot.Resources["SettingsCardStyle"], Child = contents };
    }

    private TextBlock Description(string description) =>
        new() { Text = description, Style = (Style)SettingsRoot.Resources["SettingsDescriptionStyle"] };

    private static StackPanel Flow(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }

    private static ContentControl Container(FrameworkElement child) =>
        new() { Content = child, HorizontalContentAlignment = HorizontalAlignment.Stretch };

    private void ToggleShellCommand()
    {
        if (command.Visibility == Visibility.Visible)
        {
            command.Visibility = Visibility.Collapsed;
            showCommand.Content = "Show command";
        }
        else if (pipe is not null && pipe.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            command.Text = "$env:SSH_AUTH_SOCK = '" + pipe.Replace("'", "''") + "'";
            command.Visibility = Visibility.Visible;
            showCommand.Content = "Hide command";
        }
    }

    internal void SetAvailability(bool connected, bool canRestart, string text)
    {
        status.Text = text;
        this.connected = connected;
        restart.Visibility = canRestart ? Visibility.Visible : Visibility.Collapsed;
        if (!connected)
        {
            pending.Text = providerStatus.Text = version.Text = "—";
            keyPolicyValue.Text = approvalPolicyValue.Text = "—";
            accountValue.Text = "Not connected";
            account.Text = currentAccount = "";
            command.Visibility = Visibility.Collapsed;
            command.Text = "";
            showCommand.Content = "Show command";
            pipe = null;
            initialized = busy = urgentBusy = locked = false;
            toggleLock.Content = "Lock";
            keys.Items.Clear();
            scope.Reset();
            UpdateDiscoveryStatus(null, []);
            UpdateKeyDetails();
        }
        UpdateAvailability();
    }

    internal void SetBusy(bool value)
    {
        busy = value;
        UpdateAvailability();
    }

    internal void SetUrgentBusy(bool value)
    {
        urgentBusy = value;
        UpdateAvailability();
    }

    internal void SetLockState(bool value)
    {
        locked = value;
        toggleLock.Content = locked ? "Unlock" : "Lock";
        status.Text = locked ? "Core locked" : "Core connected";
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        var ready = connected && initialized && !busy && !urgentBusy;
        var canUseAccount = ready && !locked;
        var canUseKeys = canUseAccount && currentAccount.Length > 0;
        connectControls.IsEnabled = canUseAccount;
        refreshIndex.IsEnabled = canUseKeys;
        scope.View.IsEnabled = canUseAccount;
        keyControls.IsEnabled = connected && initialized && SelectedKey is not null;
        preload.IsEnabled = canUseKeys && SelectedKey is { Fingerprint.Length: > 0 };
        cacheControls.IsEnabled = ready;
        showCommand.IsEnabled = connected && pipe is not null;
        toggleLock.IsEnabled = connected && !urgentBusy && (!locked || (initialized && !busy));
        evict.IsEnabled = revokeAll.IsEnabled = connected && !urgentBusy;
        revokeSelected.IsEnabled = connected && initialized && !urgentBusy && SelectedKey is not null;
    }

    internal void ShowMessage(string text)
    {
        message.Message = text;
        message.IsOpen = text.Length > 0;
    }

    internal void SetHello(Hello hello)
    {
        pipe = hello.Pipe;
        version.Text = hello.Version;
        UpdateAvailability();
    }

    internal void ShowState(CoreState state, bool resetSettings)
    {
        locked = state.Locked;
        toggleLock.Content = locked ? "Unlock" : "Lock";
        status.Text = locked ? "Core locked" : "Core connected";
        pipe = state.Pipe;
        pending.Text = state.Pending.ToString();
        providerStatus.Text = ProviderStatus(state.Provider);
        if (!initialized || currentAccount != state.Account)
        {
            currentAccount = state.Account;
            account.Text = currentAccount;
            accountValue.Text = currentAccount.Length == 0 ? "Not connected" : currentAccount;
            scope.Reset();
        }

        if (!initialized || resetSettings)
        {
            keyPolicy.Set(state.KeyCache);
            approvalPolicy.Set(state.ApprovalCache);
        }
        initialized = true;
        keyPolicyValue.Text = PolicyInputs.Describe(state.KeyCache);
        approvalPolicyValue.Text = PolicyInputs.Describe(state.ApprovalCache);
        scope.Update(state.Scope, state.Vaults ?? []);
        UpdateDiscoveryStatus(state.Index, state.Keys);

        if (!keys.Items.OfType<ComboBoxItem>().Select(i => i.Tag as PublicKey).SequenceEqual(state.Keys))
        {
            var selectedId = SelectedKey?.Id;
            updatingKeys = true;
            keys.Items.Clear();
            var labels = SettingsLabels.ById(state.Keys.Select(key => (key.Id, key.Name)));
            foreach (var key in state.Keys)
                keys.Items.Add(new ComboBoxItem
                {
                    Content = new TextBlock { Text = labels[key.Id], TextWrapping = TextWrapping.Wrap },
                    Tag = key
                });
            keys.SelectedItem = keys.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as PublicKey)?.Id == selectedId)
                ?? keys.Items.FirstOrDefault();
            updatingKeys = false;
            UpdateKeyDetails();
        }
        UpdateAvailability();
    }

    private void UpdateDiscoveryStatus(string? index, PublicKey[] list)
    {
        (indexStatus.Text, indexDescription.Text) = index switch
        {
            "ready" => ("Ready", "Discovery completed for the current scope."),
            "partial" => ("Partially indexed", "Some SSH items could not be indexed. Refresh to try again."),
            "unavailable" => ("Unavailable", "The index could not be refreshed. Check 1Password access and try again."),
            "scope-too-broad" => ("Scope too broad", "Narrow the scope to index up to 64 keys. Discovery can scan up to 256 vaults and 8,192 entries per eligible vault. If the account exceeds the vault limit, use an account with fewer vaults. If a vault exceeds the entry limit, exclude it and remove its item include rules, or reduce its entries."),
            _ => ("Not indexed", currentAccount.Length == 0 ? "Connect an account to discover SSH keys." : "Refresh the index to discover SSH keys.")
        };
        var count = list.Count(key => key.Fingerprint.Length > 0);
        keyCount.Text = count == 1 ? "1 usable key" : $"{count} usable keys";
    }

    private PublicKey? SelectedKey => (keys.SelectedItem as ComboBoxItem)?.Tag as PublicKey;

    private void UpdateKeyDetails()
    {
        UpdateAvailability();
        if (SelectedKey is not { } key)
        {
            approvalKey.Text = keyDetails.Text = "No indexed SSH key selected.";
            return;
        }
        approvalKey.Text = ((keys.SelectedItem as ComboBoxItem)?.Content as TextBlock)?.Text ?? key.Name;
        var error = key.Error switch
        {
            "provider" => "1Password could not read the item.",
            "load-timeout" => "The item read timed out.",
            "invalid-key" => "The item has an unsupported or invalid SSH key.",
            "key-mismatch" => "The private key differs from the indexed public key. Refresh the index.",
            "public-index-missing" => "The public index entry is missing. Refresh the index.",
            null or "" => "",
            _ => "The key could not be loaded."
        };
        var loadStatus = key.Loading ? "Loading" : key.Loaded ? "Loaded" : "Unloaded";
        keyDetails.Text = (key.Fingerprint.Length == 0 ? "No usable public key" : key.Fingerprint) + "   ·   " + loadStatus +
            (error.Length == 0 ? "" : Environment.NewLine + error);
    }

    private static string ProviderStatus(string status) => status switch
    {
        "unconfigured" => "Account setup required",
        "reading" => "Waiting for 1Password",
        "last-success" => "Last request succeeded",
        "denied" => "Access denied",
        "unavailable" => "Unavailable",
        "timed-out" => "Request timed out",
        "canceled" => "Request canceled",
        "offline-fixture" => "Offline test fixture",
        _ => "Access has not been verified"
    };

    private sealed class PolicyInputs
    {
        internal readonly ComboBox Mode;
        internal readonly NumberBox Seconds = new() { Header = "Seconds", Width = 114, Minimum = 1, Maximum = CachePolicy.MaxSeconds };
        private static readonly string[] modes = ["off", "fixed", "idle", "until-lock"];

        internal PolicyInputs(string label)
        {
            Mode = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var option in new[] { "Off", "Fixed duration", "Idle timeout", "Until lock" })
                Mode.Items.Add(option);
            Mode.SelectionChanged += (_, _) => Seconds.IsEnabled = Mode.SelectedIndex is 1 or 2;
            Set(new CachePolicy("off", 0));
            AutomationProperties.SetName(Seconds, label + " duration in seconds");
        }

        internal void Set(CachePolicy policy)
        {
            Mode.SelectedIndex = Math.Max(0, Array.IndexOf(modes, policy.Mode));
            Seconds.Value = policy.Seconds > 0 ? policy.Seconds : 300;
        }

        internal bool TryRead(out CachePolicy policy)
        {
            var timed = Mode.SelectedIndex is 1 or 2;
            var seconds = Seconds.Value;
            policy = new CachePolicy(modes[Math.Max(0, Mode.SelectedIndex)], 0);
            if (!timed)
                return true;
            if (double.IsNaN(seconds) || seconds is < 1 or > CachePolicy.MaxSeconds || seconds != Math.Truncate(seconds))
                return false;
            policy = policy with { Seconds = (int)seconds };
            return true;
        }

        internal static string Describe(CachePolicy policy) => policy.Mode switch
        {
            "fixed" => $"{policy.Seconds} s fixed",
            "idle" => $"{policy.Seconds} s idle",
            "until-lock" => "Until lock",
            _ => "Off"
        };
    }
}
internal static class SettingsLayout
{
    internal static Grid Row(FrameworkElement label, FrameworkElement? action)
    {
        var row = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        row.Children.Add(label);
        if (action is null)
            return row;

        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        label.VerticalAlignment = action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        row.Children.Add(action);
        row.SizeChanged += (_, args) =>
        {
            var stack = args.NewSize.Width < 420;
            Grid.SetColumnSpan(label, stack ? 2 : 1);
            Grid.SetRow(action, stack ? 1 : 0);
            Grid.SetColumn(action, stack ? 0 : 1);
            Grid.SetColumnSpan(action, stack ? 2 : 1);
        };
        return row;
    }
}

internal static class Ui
{
    internal static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    internal static TextBlock Heading(string value, int size = 19) =>
        new() { Text = value, FontSize = size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    internal static Button Button(string label, Action action)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => action();
        return button;
    }

    internal static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children)
            row.Children.Add(child);
        return row;
    }
}
