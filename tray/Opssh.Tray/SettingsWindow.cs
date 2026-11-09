using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Opssh.Tray;

public sealed partial class SettingsWindow : Window
{
    private readonly TextBlock status = Ui.Text(Strings.Get("Settings.Starting"));
    private readonly TextBlock pending = Ui.Text("—");
    private readonly TextBlock accountValue = Ui.Text(Strings.Get("Settings.NotConnected"));
    private readonly TextBlock providerStatus = Ui.Text("—");
    private readonly TextBlock indexStatus = Ui.Text(Strings.Get("Settings.NotIndexed"));
    private readonly TextBlock indexDescription = Ui.Text("");
    private readonly TextBlock keyCount = Ui.Text(Strings.Get("Settings.KeyCountMany", 0));
    private readonly TextBlock keyDetails = Ui.Text(Strings.Get("Settings.NoKeys"));
    private readonly TextBlock version = Ui.Text("—");
    private readonly TextBlock updateStatus = Ui.Text("");
    private readonly HyperlinkButton releaseLink = new()
    {
        Content = Strings.Get("Update.ViewRelease"),
        NavigateUri = ReleaseCheck.ReleasesPage,
        Visibility = Visibility.Collapsed
    };
    private readonly InfoBar message = new() { IsClosable = true };
    private readonly TextBox account = new()
    {
        Header = Strings.Get("Settings.AccountName"),
        MaxLength = 256,
        PlaceholderText = Strings.Get("Settings.AccountPlaceholder"),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBox command = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly ComboBox keys = new() { Header = Strings.Get("Settings.IndexedKey"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly PolicyInputs keyPolicy = new(Strings.Get("Settings.KeyCache"));
    private readonly PolicyInputs approvalPolicy = new(Strings.Get("Settings.ApprovalCache"));
    private readonly TextBlock keyPolicyValue = Ui.Text("—");
    private readonly TextBlock approvalPolicyValue = Ui.Text("—");
    private readonly TextBlock approvalKey = Ui.Text(Strings.Get("Settings.NoKeySelected"));
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
    private readonly Button checkForUpdates;
    private readonly NavigationView navigation;
    private readonly NavigationViewItem discoveryPage;
    private readonly NavigationViewItem appearancePage;
    private string? pipe;
    private string currentAccount = "";
    private bool connected;
    private bool busy;
    private bool urgentBusy;
    private bool locked;
    private bool initialized;
    private bool updatingKeys;
    private CoreState? displayedState;

    internal SettingsWindow(Func<string, object?, bool, Task> send, Func<string, object?, Task> sendUrgent,
        Func<Task> start, UiPreferences preferences, Func<string, string, bool> applyAppearance,
        Func<Task> checkUpdates, Func<bool, bool> setAutoCheckUpdates)
    {
        InitializeComponent();
        Title = Strings.Get("Settings.Title");
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            ExtendsContentIntoTitleBar = true;
            SettingsTitleBar.Title = Title;
            SetTitleBar(SettingsTitleBar);
        }
        else
        {
            SettingsTitleBar.Visibility = Visibility.Collapsed;
        }
        ResizeToWorkArea();
        ApplyTheme(preferences.Theme);
        status.MaxWidth = 260;
        accountValue.MaxWidth = 260;

        restart = Ui.Button(Strings.Get("Settings.RestartCore"), async () => await start());
        toggleLock = Ui.Button(Strings.Get("Settings.Lock"), async () =>
        {
            if (locked)
                await send("unlock", null, false);
            else
                await sendUrgent("lock", null);
        });
        showCommand = Ui.Button(Strings.Get("Settings.ShowCommand"), ToggleShellCommand);
        AutomationProperties.SetName(command, Strings.Get("Settings.CommandAutomationName"));
        refreshIndex = Ui.Button(Strings.Get("Settings.RefreshIndex"), async () => await send("refresh-index", null, false));
        preload = Ui.Button(Strings.Get("Settings.PreloadSelected"), async () =>
        {
            if (SelectedKey is { } key)
                await send("preload", new { keyId = key.Id }, false);
        });
        evict = Ui.Button(Strings.Get("Settings.EvictKeys"), async () => await sendUrgent("evict-keys", null));
        checkForUpdates = Ui.Button(Strings.Get("Update.Check"), async () => await checkUpdates());
        revokeAll = Ui.Button(Strings.Get("Settings.RevokeAll"), async () => await sendUrgent("revoke", null));
        revokeSelected = Ui.Button(Strings.Get("Settings.RevokeSelected"), async () =>
        {
            if (SelectedKey is { } key)
                await sendUrgent("revoke", new { keyId = key.Id });
        });

        var connect = Ui.Button(Strings.Get("Settings.Connect"), async () =>
        {
            var requestedAccount = account.Text.Trim();
            if (requestedAccount.Length == 0)
            {
                ShowMessage(Strings.Get("Settings.EnterAccount"));
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
            {
                UpdateKeyDetails();
                UpdateAvailability();
            }
        };
        keyControls = Container(Flow(keys, keyDetails, preload));
        scope = new SettingsScopeEditor(rule => send("set-scope", rule, false), ShowMessage);

        var saveCaches = Ui.Button(Strings.Get("Settings.ApplyCaches"), async () =>
        {
            if (!keyPolicy.TryRead(out var keyCache) || !approvalPolicy.TryRead(out var approvalCache))
            {
                ShowMessage(Strings.Get("Settings.CacheDurationError", CachePolicy.MaxSeconds));
                return;
            }
            await send("settings", new { account = currentAccount, keyCache, approvalCache }, false);
        });
        cacheControls = Container(Flow(
            SettingsLayout.Row(keyPolicy.Mode, keyPolicy.Seconds),
            SettingsLayout.Row(approvalPolicy.Mode, approvalPolicy.Seconds),
            saveCaches));

        var overview = NavigationItem(Strings.Get("Settings.Overview"), "\uE80F", BuildOverview());
        discoveryPage = NavigationItem(Strings.Get("Settings.Discovery"), "\uE8D7", BuildDiscovery());
        var policies = NavigationItem(Strings.Get("Settings.Policies"), "\uE72E", BuildPolicies());
        appearancePage = NavigationItem(Strings.Get("Settings.Appearance"), "\uE790", BuildAppearance(preferences, applyAppearance));
        var about = NavigationItem(Strings.Get("Settings.About"), "\uE946",
            BuildAbout(preferences.AutoCheckUpdates, setAutoCheckUpdates));
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
            PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
            OpenPaneLength = 224,
            ExpandedModeThresholdWidth = 880,
            CompactModeThresholdWidth = 620,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            AlwaysShowHeader = false,
            IsTitleBarAutoPaddingEnabled = false,
            Content = content
        };
        AutomationProperties.SetName(navigation, Strings.Get("Settings.Settings"));
        navigation.MenuItems.Add(overview);
        navigation.MenuItems.Add(discoveryPage);
        navigation.MenuItems.Add(policies);
        navigation.MenuItems.Add(appearancePage);
        navigation.FooterMenuItems.Add(about);
        navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: StackPanel selected } && !ReferenceEquals(page.Content, selected))
            {
                page.Content = selected;
                page.ChangeView(null, 0, null, true);
            }
        };
        Grid.SetRow(navigation, 1);
        SettingsRoot.Children.Add(navigation);
        page.Content = overview.Tag;
        navigation.SelectedItem = overview;
        SetAvailability(false, false, Strings.Get("Settings.StartingCore"));
    }

    private StackPanel BuildOverview()
    {
        var page = Page(Strings.Get("Settings.Overview"), Strings.Get("Settings.OverviewDescription"));
        page.Children.Add(Section(Strings.Get("Settings.AgentStatus"),
            Card("\uE968", Strings.Get("Settings.CoreStatus"), Strings.Get("Settings.CoreStatusDescription"), Flow(status, restart)),
            Card("\uE7F4", Strings.Get("Settings.PendingApprovals"), Strings.Get("Settings.PendingApprovalsDescription"), pending),
            Card("\uE72E", Strings.Get("Settings.AgentLock"), Strings.Get("Settings.AgentLockDescription"), toggleLock)));
        page.Children.Add(Section(Strings.Get("Settings.ClientSetup"),
            Card("\uE756", "PowerShell", Strings.Get("Settings.PowerShellDescription"), showCommand, command)));
        return page;
    }

    private StackPanel BuildDiscovery()
    {
        var page = Page(Strings.Get("Settings.Discovery"), Strings.Get("Settings.DiscoveryDescription"));
        page.Children.Add(Section(Strings.Get("Settings.Account"),
            Card("\uE77B", Strings.Get("Settings.ProviderAccount"), Strings.Get("Settings.ProviderAccountDescription"),
                accountValue, connectControls),
            Card("\uE8D7", Strings.Get("Settings.DesktopAccess"), Strings.Get("Settings.DesktopAccessDescription"), providerStatus)));
        page.Children.Add(Section(Strings.Get("Settings.KeyDiscovery"),
            Card("\uE721", Strings.Get("Settings.PublicIndex"), Strings.Get("Settings.PublicIndexDescription"), Flow(indexStatus, refreshIndex), indexDescription),
            scope.View,
            Card("\uE192", Strings.Get("Settings.UsableKeys"), Strings.Get("Settings.UsableKeysDescription"),
                keyCount, keyControls)));
        return page;
    }

    private StackPanel BuildPolicies()
    {
        var page = Page(Strings.Get("Settings.Policies"), Strings.Get("Settings.PoliciesDescription"));
        page.Children.Add(Section(Strings.Get("Settings.LocalControls"),
            Card("\uE8C9", Strings.Get("Settings.LoadedPrivateKeys"), Strings.Get("Settings.LoadedPrivateKeysDescription"), evict),
            Card("\uE73E", Strings.Get("Settings.ProcessApprovals"), Strings.Get("Settings.ProcessApprovalsDescription"), revokeAll),
            Card("\uE192", Strings.Get("Settings.KeyApprovals"), Strings.Get("Settings.KeyApprovalsDescription"),
                revokeSelected, SettingsLayout.Row(approvalKey, Ui.Button(Strings.Get("Settings.GoToKeys"), () =>
                {
                    navigation.SelectedItem = discoveryPage;
                    discoveryPage.Focus(FocusState.Programmatic);
                })))));
        page.Children.Add(Section(Strings.Get("Settings.CachePolicies"),
            Card("\uE8D7", Strings.Get("Settings.KeyCache"), Strings.Get("Settings.KeyCacheDescription"), keyPolicyValue),
            Card("\uE73E", Strings.Get("Settings.ApprovalCache"), Strings.Get("Settings.ApprovalCacheDescription"), approvalPolicyValue),
            new Expander
            {
                Header = Strings.Get("Settings.ChangeCaches"),
                Content = cacheControls,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            }));
        return page;
    }

    private StackPanel BuildAppearance(UiPreferences preferences, Func<string, string, bool> applyAppearance)
    {
        var page = Page(Strings.Get("Settings.Appearance"), Strings.Get("Settings.AppearanceDescription"));
        var language = AppearanceChoice(Strings.Get("Settings.Language"), preferences.Language,
            ("system", Strings.Get("Settings.LanguageSystem")),
            ("en", Strings.Get("Settings.LanguageEnglish")), ("zh-Hans", Strings.Get("Settings.LanguageChinese")));
        var theme = AppearanceChoice(Strings.Get("Settings.Theme"), preferences.Theme,
            ("system", Strings.Get("Settings.ThemeSystem")), ("light", Strings.Get("Settings.ThemeLight")), ("dark", Strings.Get("Settings.ThemeDark")));

        var restoring = false;
        void ChangeAppearance(ComboBox sender, SelectionChangedEventArgs args)
        {
            if (restoring || language.SelectedItem is not ComboBoxItem { Tag: string languageId } ||
                theme.SelectedItem is not ComboBoxItem { Tag: string themeId })
                return;

            if (applyAppearance(languageId, themeId))
                return;

            restoring = true;
            sender.SelectedItem = args.RemovedItems.FirstOrDefault();
            restoring = false;
            ShowMessage(Strings.Get("Settings.AppearanceSaveFailed"));
        }

        language.SelectionChanged += (sender, args) => ChangeAppearance((ComboBox)sender, args);
        theme.SelectionChanged += (sender, args) => ChangeAppearance((ComboBox)sender, args);
        page.Children.Add(Card("\uE775", Strings.Get("Settings.Language"), Strings.Get("Settings.LanguageDescription"), language));
        page.Children.Add(Card("\uE790", Strings.Get("Settings.Theme"), Strings.Get("Settings.ThemeDescription"), theme));
        return page;
    }

    private static ComboBox AppearanceChoice(string name, string selected, params (string Id, string Label)[] options)
    {
        var choice = new ComboBox { MinWidth = 156 };
        AutomationProperties.SetName(choice, name);
        foreach (var (id, label) in options)
        {
            var item = new ComboBoxItem { Content = label, Tag = id };
            choice.Items.Add(item);
            if (id == selected)
                choice.SelectedItem = item;
        }
        return choice;
    }

    internal void ApplyTheme(string theme) => WindowTheme.Apply(this, SettingsRoot, theme);

    internal void SelectAppearance() => navigation.SelectedItem = appearancePage;

    private void ResizeToWorkArea()
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new SizeInt32(
            Math.Min(workArea.Width, (int)Math.Round(1060 * scale)),
            Math.Min(workArea.Height, (int)Math.Round(800 * scale))));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private StackPanel BuildAbout(bool autoCheck, Func<bool, bool> setAutoCheckUpdates)
    {
        var page = Page(Strings.Get("Settings.About"), Strings.Get("Settings.AboutDescription"));
        var auto = new ToggleSwitch
        {
            IsOn = autoCheck,
            OnContent = Strings.Get("Update.On"),
            OffContent = Strings.Get("Update.Off"),
            MinWidth = 0
        };
        AutomationProperties.SetName(auto, Strings.Get("Update.AutoCheck"));
        var restoring = false;
        auto.Toggled += (_, _) =>
        {
            if (restoring || setAutoCheckUpdates(auto.IsOn))
                return;
            restoring = true;
            auto.IsOn = !auto.IsOn;
            restoring = false;
            ShowMessage(Strings.Get("Update.SaveFailed"));
        };
        page.Children.Add(Section(Strings.Get("Settings.Version"),
            Card("\uE946", Strings.Get("Update.TrayVersion"), Strings.Get("Update.TrayVersionDescription"),
                Ui.Text(App.InstalledVersion.ToString(3))),
            Card("\uE946", Strings.Get("Settings.CoreVersion"), Strings.Get("Settings.CoreVersionDescription"), version)));
        page.Children.Add(Section(Strings.Get("Update.Section"),
            Card("\uE895", Strings.Get("Update.Check"), Strings.Get("Update.CheckDescription"), checkForUpdates,
                Flow(updateStatus, releaseLink)),
            Card("\uE713", Strings.Get("Update.AutoCheck"), Strings.Get("Update.AutoDescription"), auto)));
        return page;
    }

    internal void ShowReleaseCheck(ReleaseCheckResult result)
    {
        checkForUpdates.IsEnabled = result.Status != ReleaseCheckStatus.Checking;
        releaseLink.Visibility = result.Status == ReleaseCheckStatus.Available ? Visibility.Visible : Visibility.Collapsed;
        updateStatus.Text = result.Status switch
        {
            ReleaseCheckStatus.Checking => Strings.Get("Update.Checking"),
            ReleaseCheckStatus.Current => Strings.Get("Update.Current"),
            ReleaseCheckStatus.Available => Strings.Get("Update.Available", result.Latest!.ToString(3)),
            ReleaseCheckStatus.NoRelease => Strings.Get("Update.NoRelease"),
            ReleaseCheckStatus.Unavailable => Strings.Get("Update.Unavailable"),
            _ => Strings.Get("Update.Idle")
        };
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
            showCommand.Content = Strings.Get("Settings.ShowCommand");
        }
        else if (pipe is not null && pipe.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            command.Text = "$env:SSH_AUTH_SOCK = '" + pipe.Replace("'", "''") + "'";
            command.Visibility = Visibility.Visible;
            showCommand.Content = Strings.Get("Settings.HideCommand");
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
            accountValue.Text = Strings.Get("Settings.NotConnected");
            account.Text = currentAccount = "";
            command.Visibility = Visibility.Collapsed;
            command.Text = "";
            showCommand.Content = Strings.Get("Settings.ShowCommand");
            pipe = null;
            initialized = busy = urgentBusy = locked = false;
            displayedState = null;
            toggleLock.Content = Strings.Get("Settings.Lock");
            updatingKeys = true;
            keys.Items.Clear();
            updatingKeys = false;
            scope.Reset();
            UpdateDiscoveryStatus(null, []);
            UpdateKeyDetails();
        }
        UpdateAvailability();
    }

    internal void SetBusy(bool value)
    {
        if (busy != value)
        {
            busy = value;
            UpdateAvailability();
        }
    }

    internal void SetUrgentBusy(bool value)
    {
        if (urgentBusy != value)
        {
            urgentBusy = value;
            UpdateAvailability();
        }
    }

    internal void SetLockState(bool value)
    {
        locked = value;
        toggleLock.Content = locked ? Strings.Get("Settings.Unlock") : Strings.Get("Settings.Lock");
        status.Text = locked ? Strings.Get("Settings.CoreLocked") : Strings.Get("Settings.CoreConnected");
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        var ready = connected && initialized && !busy && !urgentBusy;
        var canUseAccount = ready && !locked;
        var canUseKeys = canUseAccount && currentAccount.Length > 0;
        var selected = SelectedKey;
        connectControls.IsEnabled = canUseAccount;
        refreshIndex.IsEnabled = canUseKeys;
        scope.View.IsEnabled = canUseAccount;
        keyControls.IsEnabled = connected && initialized && selected is not null;
        preload.IsEnabled = canUseKeys && selected is { Fingerprint.Length: > 0 };
        cacheControls.IsEnabled = ready;
        showCommand.IsEnabled = connected && pipe is not null;
        toggleLock.IsEnabled = connected && !urgentBusy && (!locked || (initialized && !busy));
        evict.IsEnabled = revokeAll.IsEnabled = connected && !urgentBusy;
        revokeSelected.IsEnabled = connected && initialized && !urgentBusy && selected is not null;
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
        var previous = displayedState;
        var accountChanged = !initialized || currentAccount != state.Account;
        var lockChanged = !initialized || locked != state.Locked;
        var pipeChanged = pipe != state.Pipe;
        var keysChanged = previous is null || !previous.Keys.SequenceEqual(state.Keys);
        displayedState = state;

        if (lockChanged)
        {
            locked = state.Locked;
            toggleLock.Content = locked ? Strings.Get("Settings.Unlock") : Strings.Get("Settings.Lock");
            status.Text = locked ? Strings.Get("Settings.CoreLocked") : Strings.Get("Settings.CoreConnected");
        }
        pipe = state.Pipe;
        if (previous?.Pending != state.Pending)
            pending.Text = state.Pending.ToString();
        if (previous?.Provider != state.Provider)
            providerStatus.Text = ProviderStatus(state.Provider);
        if (accountChanged)
        {
            currentAccount = state.Account;
            account.Text = currentAccount;
            accountValue.Text = currentAccount.Length == 0 ? Strings.Get("Settings.NotConnected") : currentAccount;
        }

        if (!initialized || resetSettings)
        {
            keyPolicy.Set(state.KeyCache);
            approvalPolicy.Set(state.ApprovalCache);
        }
        initialized = true;
        if (previous?.KeyCache != state.KeyCache)
            keyPolicyValue.Text = PolicyInputs.Describe(state.KeyCache);
        if (previous?.ApprovalCache != state.ApprovalCache)
            approvalPolicyValue.Text = PolicyInputs.Describe(state.ApprovalCache);
        scope.Update(state.Scope, state.Vaults ?? [], reset: accountChanged);
        if (previous is null || previous.Index != state.Index || accountChanged || keysChanged)
            UpdateDiscoveryStatus(state.Index, state.Keys);

        if (keysChanged)
        {
            var selectedId = SelectedKey?.Id;
            // Loading/error changes update the existing items, preserving focus and an open list.
            if (previous is not null && previous.Keys.Length == state.Keys.Length &&
                previous.Keys.Zip(state.Keys).All(pair => pair.First.Id == pair.Second.Id &&
                    pair.First.Name == pair.Second.Name && pair.First.Unnamed == pair.Second.Unnamed))
            {
                for (var i = 0; i < state.Keys.Length; i++)
                    ((ComboBoxItem)keys.Items[i]).Tag = state.Keys[i];
            }
            else
            {
                updatingKeys = true;
                keys.Items.Clear();
                var labels = SettingsLabels.ById(state.Keys.Select(key => (key.Id, KeyDisplayName(key))));
                foreach (var key in state.Keys)
                    keys.Items.Add(new ComboBoxItem
                    {
                        Content = new TextBlock { Text = labels[key.Id], TextWrapping = TextWrapping.Wrap },
                        Tag = key
                    });
                keys.SelectedItem = keys.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as PublicKey)?.Id == selectedId)
                    ?? keys.Items.FirstOrDefault();
                updatingKeys = false;
            }
            UpdateKeyDetails();
        }

        if (accountChanged || lockChanged || pipeChanged || keysChanged)
            UpdateAvailability();
    }

    private void UpdateDiscoveryStatus(string? index, PublicKey[] list)
    {
        (indexStatus.Text, indexDescription.Text) = index switch
        {
            "ready" => (Strings.Get("Settings.IndexReady"), Strings.Get("Settings.IndexReadyDescription")),
            "partial" => (Strings.Get("Settings.IndexPartial"), Strings.Get("Settings.IndexPartialDescription")),
            "unavailable" => (Strings.Get("Settings.Unavailable"), Strings.Get("Settings.IndexUnavailableDescription")),
            "scope-too-broad" => (Strings.Get("Settings.IndexScopeTooBroad"), Strings.Get("Settings.IndexScopeTooBroadDescription")),
            _ => (Strings.Get("Settings.NotIndexed"), currentAccount.Length == 0 ? Strings.Get("Settings.IndexConnect") : Strings.Get("Settings.IndexRefresh"))
        };
        var count = list.Count(key => key.Fingerprint.Length > 0);
        keyCount.Text = Strings.Get(count == 1 ? "Settings.KeyCountOne" : "Settings.KeyCountMany", count);
    }

    private static string KeyDisplayName(PublicKey key) =>
        key.Unnamed ? Strings.Get("Approval.UnnamedKey", key.ItemId[..8]) : key.Name;

    private PublicKey? SelectedKey => (keys.SelectedItem as ComboBoxItem)?.Tag as PublicKey;

    private void UpdateKeyDetails()
    {
        if (SelectedKey is not { } key)
        {
            approvalKey.Text = keyDetails.Text = Strings.Get("Settings.NoKeySelected");
            return;
        }
        approvalKey.Text = ((keys.SelectedItem as ComboBoxItem)?.Content as TextBlock)?.Text ?? KeyDisplayName(key);
        var error = key.Error switch
        {
            "provider" => Strings.Get("Settings.KeyErrorProvider"),
            "load-timeout" => Strings.Get("Settings.KeyErrorTimeout"),
            "invalid-key" => Strings.Get("Settings.KeyErrorInvalid"),
            "key-mismatch" => Strings.Get("Settings.KeyErrorMismatch"),
            "public-index-missing" => Strings.Get("Settings.KeyErrorIndexMissing"),
            null or "" => "",
            _ => Strings.Get("Settings.KeyErrorGeneric")
        };
        var loadStatus = key.Loading ? Strings.Get("Settings.KeyLoading") : key.Loaded ? Strings.Get("Settings.KeyLoaded") : Strings.Get("Settings.KeyUnloaded");
        keyDetails.Text = (key.Fingerprint.Length == 0 ? Strings.Get("Settings.KeyNoPublic") : key.Fingerprint) + "   ·   " + loadStatus +
            (error.Length == 0 ? "" : Environment.NewLine + error);
    }

    private static string ProviderStatus(string status) => status switch
    {
        "unconfigured" => Strings.Get("Settings.ProviderUnconfigured"),
        "reading" => Strings.Get("Settings.ProviderReading"),
        "last-success" => Strings.Get("Settings.ProviderLastSuccess"),
        "denied" => Strings.Get("Settings.ProviderDenied"),
        "unavailable" => Strings.Get("Settings.Unavailable"),
        "timed-out" => Strings.Get("Settings.ProviderTimedOut"),
        "canceled" => Strings.Get("Settings.ProviderCanceled"),
        "offline-fixture" => Strings.Get("Settings.ProviderOfflineFixture"),
        _ => Strings.Get("Settings.ProviderUnverified")
    };

    private sealed class PolicyInputs
    {
        internal readonly ComboBox Mode;
        internal readonly NumberBox Seconds = new() { Header = Strings.Get("Settings.Seconds"), Width = 114, Minimum = 1, Maximum = CachePolicy.MaxSeconds };
        private static readonly string[] modes = ["off", "fixed", "idle", "until-lock"];

        internal PolicyInputs(string label)
        {
            Mode = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var option in new[] { Strings.Get("Settings.CacheOff"), Strings.Get("Settings.CacheFixed"), Strings.Get("Settings.CacheIdle"), Strings.Get("Settings.CacheUntilLock") })
                Mode.Items.Add(option);
            Mode.SelectionChanged += (_, _) => Seconds.IsEnabled = Mode.SelectedIndex is 1 or 2;
            Set(new CachePolicy("off", 0));
            AutomationProperties.SetName(Seconds, Strings.Get("Settings.CacheSecondsAutomationName", label));
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
            "fixed" => Strings.Get("Settings.CacheFixedValue", policy.Seconds),
            "idle" => Strings.Get("Settings.CacheIdleValue", policy.Seconds),
            "until-lock" => Strings.Get("Settings.CacheUntilLock"),
            _ => Strings.Get("Settings.CacheOff")
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
        var stacked = false;
        row.SizeChanged += (_, args) =>
        {
            var stack = args.NewSize.Width < 420;
            if (stack == stacked)
                return;
            stacked = stack;
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

}
