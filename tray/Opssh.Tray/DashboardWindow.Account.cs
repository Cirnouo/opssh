using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Opssh.Tray;

public sealed partial class DashboardWindow
{
    private readonly Dictionary<string, ToggleSwitch> vaultChecks = new(StringComparer.Ordinal);
    private TextBlock accountName = null!;
    private TextBlock providerStatus = null!;
    private TextBlock providerTime = null!;
    private FontIcon providerIndicator = null!;
    private TextBlock vaultCount = null!;
    private TextBlock vaultHint = null!;
    private StackPanel vaultRows = null!;
    private Border vaultCard = null!;
    private Button switchAccount = null!;
    private Button logout = null!;
    private Button reloadVaults = null!;
    private HyperlinkButton goToKeys = null!;
    private bool changingVault;
    private bool paintingVaults;

    private StackPanel BuildAccount()
    {
        var page = Page(Strings.Get("Dashboard.AccountTitle"), Strings.Get("Dashboard.AccountDescription"));
        accountName = Quiet("", 12);
        switchAccount = DashboardButton(Strings.Get("Dashboard.SwitchAccount"), async () => await PickAccountAsync());
        logout = DashboardButton(Strings.Get("Dashboard.Logout"), async () => await LogoutAsync());
        AutomationProperties.SetName(logout, Strings.Get("Dashboard.LogoutName"));
        var accountActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { switchAccount, logout }
        };
        providerStatus = Ui.Text("");
        providerIndicator = Icon("\uE91F", 6);
        var provider = new Grid { ColumnSpacing = 7 };
        provider.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        provider.ColumnDefinitions.Add(new ColumnDefinition());
        provider.Children.Add(providerIndicator);
        provider.Children.Add(providerStatus);
        Grid.SetColumn(providerStatus, 1);
        providerTime = Mono("");
        providerTime.Style = StyleFor("DashboardQuietTextStyle");
        providerTime.Visibility = Visibility.Collapsed;
        AutomationProperties.SetHelpText(providerTime, Strings.Get("Dashboard.ProviderTime"));
        var request = new StackPanel { Spacing = 3, Children = { provider, providerTime }, MaxWidth = 210 };
        page.Children.Add(Rows(
            SettingRow("\uE77B", Strings.Get("Settings.Account"), accountName, accountActions),
            SettingRow("\uE72E", Strings.Get("Dashboard.DesktopAccess"), Strings.Get("Dashboard.DesktopAccessDescription"), request)));

        vaultCount = Quiet("", 12);
        vaultCount.VerticalAlignment = VerticalAlignment.Center;
        reloadVaults = DashboardButton(Strings.Get("Dashboard.ReloadVaults"), async () => await send("reload-vaults", null, false));
        reloadVaults.Visibility = Visibility.Collapsed;
        var vaultActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { vaultCount, reloadVaults }
        };
        var vaultTitle = Heading(Strings.Get("Dashboard.AllowedVaults"), 16, AutomationHeadingLevel.Level2);
        vaultRows = new StackPanel();
        vaultCard = Card(vaultRows);
        vaultHint = Quiet("");
        goToKeys = new HyperlinkButton { Content = Strings.Get("Dashboard.GoToKeys"), Padding = new Thickness(0) };
        goToKeys.Click += (_, _) =>
        {
            SelectPage("keys");
            navigationItems["keys"].Focus(FocusState.Programmatic);
        };
        page.Children.Add(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                SettingsLayout.Row(vaultTitle, vaultActions),
                Quiet(Strings.Get("Dashboard.VaultScopeDescription")),
                vaultCard, goToKeys, vaultHint
            }
        });
        return page;
    }

    private static string DisplayedAccountName(CoreState state) =>
        string.IsNullOrEmpty(state.AccountName) ? state.Account : state.AccountName;

    private void UpdateAccountPage(CoreState? previous, CoreState? state)
    {
        if (state is null)
        {
            vaultRows.Children.Clear();
            vaultChecks.Clear();
            accountName.Text = "";
            providerStatus.Text = "";
            providerTime.Text = "";
            providerTime.Visibility = Visibility.Collapsed;
            return;
        }

        accountName.Text = DisplayedAccountName(state);
        if (previous is null || previous.Provider != state.Provider || previous.LastProvider != state.LastProvider ||
            previous.ProviderAt != state.ProviderAt)
        {
            var provider = state.Provider == "reading" ? state.Provider : state.LastProvider ?? state.Provider;
            var (label, style) = provider switch
            {
                "last-success" => ("Settings.ProviderLastSuccess", "DashboardSuccessIconStyle"),
                "denied" => ("Settings.ProviderDenied", "DashboardErrorIconStyle"),
                "unavailable" => ("Settings.Unavailable", "DashboardErrorIconStyle"),
                "timed-out" => ("Settings.ProviderTimedOut", "DashboardErrorIconStyle"),
                "canceled" => ("Settings.ProviderCanceled", "DashboardWarningIconStyle"),
                "reading" => ("Settings.ProviderReading", "DashboardWarningIconStyle"),
                "offline-fixture" => ("Settings.ProviderOfflineFixture", "DashboardSuccessIconStyle"),
                _ => ("Dashboard.ProviderNotRequested", null)
            };
            providerStatus.Text = Strings.Get(label);
            providerIndicator.Style = style is null ? null : StyleFor(style);
            providerTime.Text = state.ProviderAt is { } timestamp ? LocalTimestamp(timestamp) : "";
            providerTime.Visibility = providerTime.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        var vaults = ManageableVaults(state);
        if (previous is null || previous.Account != state.Account ||
            !ManageableVaults(previous).Select(vault => (vault.Id, vault.Name)).SequenceEqual(vaults.Select(vault => (vault.Id, vault.Name))))
            PopulateVaults(vaults);

        UpdateVaultChoices(state);
    }

    private static DiscoveredVault[] ManageableVaults(CoreState state)
    {
        var visible = state.Vaults ?? [];
        if (state.Scope is not { } scope)
            return visible;
        var known = visible.Select(vault => vault.Id).ToHashSet(StringComparer.Ordinal);
        return visible.Concat(scope.Vaults.Keys
            .Concat(scope.Items.Keys.Select(item => item.Split('/')[0]))
            .Where(id => known.Add(id))
            .Select(id => new DiscoveredVault(id, Strings.Get("Scope.UnnamedVault") + " · " + id[..Math.Min(8, id.Length)], []))).ToArray();
    }

    private void PopulateVaults(DiscoveredVault[] vaults)
    {
        vaultRows.Children.Clear();
        vaultChecks.Clear();
        var labels = SettingsLabels.ById(vaults.Select(vault =>
            (vault.Id, vault.Name.Length > 0 ? vault.Name : Strings.Get("Scope.UnnamedVault"))));
        foreach (var vault in vaults)
        {
            var label = labels[vault.Id];
            var check = Switch(Strings.Get("Dashboard.AllowVault", label), false);
            check.HorizontalAlignment = HorizontalAlignment.Right;
            AutomationProperties.SetName(check, Strings.Get("Dashboard.AllowVault", label));
            check.Toggled += async (_, _) =>
            {
                if (!paintingVaults)
                    await SetVaultAsync(vault.Id, check.IsOn);
            };
            vaultChecks.Add(vault.Id, check);
            if (vaultRows.Children.Count > 0)
                vaultRows.Children.Add(Divider());
            var row = SettingRow("\uE8B7", label, "", check);
            row.Padding = new Thickness(13, 5, 13, 5);
            vaultRows.Children.Add(row);
        }
        vaultCard.Visibility = vaults.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateVaultChoices(CoreState state)
    {
        var allowed = 0;
        paintingVaults = true;
        foreach (var (id, check) in vaultChecks)
        {
            var rule = state.Scope?.Vaults.GetValueOrDefault(id) ?? state.Scope?.Default;
            var enabled = rule == "include";
            var legacyOverride = state.Scope?.Items.Any(item =>
                item.Key.StartsWith(id + "/", StringComparison.Ordinal) && item.Value != rule) == true;
            check.IsOn = enabled || legacyOverride;
            ToolTipService.SetToolTip(check, legacyOverride ? Strings.Get("Dashboard.PartialVault") : null);
            if (enabled || legacyOverride)
                allowed++;
        }
        paintingVaults = false;
        reloadVaults.Visibility = state.Vaults is { Length: > 0 } && allowed > 0
            ? Visibility.Collapsed : Visibility.Visible;
        vaultCount.Text = Strings.Get("Dashboard.AllowedVaultCount", allowed);
        goToKeys.Visibility = allowed > 0 ? Visibility.Visible : Visibility.Collapsed;
        vaultHint.Text = Strings.Get(vaultChecks.Count == 0 ? "Dashboard.NoVaults" :
            allowed == 0 ? "Dashboard.ChooseVaults" : "Dashboard.DiscoverAfterVaultChange");
    }

    private void UpdateAccountAvailability()
    {
        var enabled = HasAccount && CanManageAccount;
        switchAccount.IsEnabled = logout.IsEnabled = reloadVaults.IsEnabled = enabled && !changingVault;
        foreach (var check in vaultChecks.Values)
            check.IsEnabled = enabled && !changingVault;
    }

    private async Task SetVaultAsync(string vaultId, bool enabled)
    {
        if (closed || !HasAccount || !CanManageAccount || changingVault)
            return;
        changingVault = true;
        UpdateAccountAvailability();
        try
        {
            await send("set-vault", new { vaultId, enabled }, false);
        }
        finally
        {
            changingVault = false;
            if (!closed && displayedState is { } state)
            {
                UpdateVaultChoices(state);
                UpdateAccountAvailability();
            }
        }
    }

    internal Task ShowAccountPickerAsync() => PickAccountAsync();

    private async Task PickAccountAsync()
    {
        if (closed || !CanManageAccount || activeDialog is not null)
            return;
        var opener = HasAccount ? switchAccount : connectButton;
        changingAccount = true;
        UpdateAvailability();
        using var listLifetime = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        try
        {
            var currentId = displayedState?.Account;
            var picker = new ComboBox
            {
                Header = Strings.Get("Settings.Account"),
                PlaceholderText = Strings.Get("Dashboard.ChooseAccount"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetName(picker, Strings.Get("Dashboard.ChooseAccount"));
            var status = Quiet(Strings.Get("Dashboard.AccountsLoading"));
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            var retry = new HyperlinkButton
            {
                Content = Strings.Get("Dashboard.Retry"),
                Padding = new Thickness(0),
                Visibility = Visibility.Collapsed
            };
            var contents = new StackPanel
            {
                Spacing = 10,
                Children = { picker, status, Quiet(Strings.Get("Dashboard.EnableSdk")), retry }
            };
            var switchNotice = Quiet(Strings.Get("Dashboard.SwitchAccountDescription"));
            switchNotice.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrEmpty(currentId))
                contents.Children.Add(switchNotice);
            var dialog = new ContentDialog
            {
                Title = Strings.Get(string.IsNullOrEmpty(currentId) ? "Dashboard.ConnectAccount" : "Dashboard.SwitchAccountTitle"),
                Content = contents,
                PrimaryButtonText = Strings.Get("Dashboard.ContinueInPassword"),
                CloseButtonText = Strings.Get("Dashboard.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false
            };
            DesktopAccount? SelectedAccount() => (picker.SelectedItem as ComboBoxItem)?.Tag as DesktopAccount;
            picker.SelectionChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled = SelectedAccount() is { } account && account.Id != currentId;
                switchNotice.Visibility = dialog.IsPrimaryButtonEnabled && !string.IsNullOrEmpty(currentId)
                    ? Visibility.Visible : Visibility.Collapsed;
            };

            async Task LoadAsync()
            {
                retry.Visibility = Visibility.Collapsed;
                picker.Visibility = Visibility.Collapsed;
                picker.Items.Clear();
                status.Text = Strings.Get("Dashboard.AccountsLoading");
                status.Visibility = Visibility.Visible;
                try
                {
                    var result = await actions.ListAccounts(listLifetime.Token);
                    if (listLifetime.IsCancellationRequested)
                        return;
                    foreach (var account in result.Accounts)
                        picker.Items.Add(new ComboBoxItem { Content = account.Label, Tag = account, IsEnabled = account.Id != currentId });
                    var choices = picker.Items.OfType<ComboBoxItem>().Where(item =>
                        (item.Tag as DesktopAccount)?.Id != currentId).ToArray();
                    picker.Visibility = picker.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    var key = result.Status switch
                    {
                        AccountListStatus.Ready => choices.Length == 0 ? "Dashboard.NoOtherAccount" : null,
                        AccountListStatus.Empty => "Dashboard.NoAccounts",
                        AccountListStatus.NotInstalled => "Dashboard.AccountsCliMissing",
                        AccountListStatus.Untrusted => "Dashboard.AccountsCliUntrusted",
                        AccountListStatus.TimedOut => "Dashboard.AccountsTimedOut",
                        AccountListStatus.Busy => "Dashboard.AccountsBusy",
                        _ => "Dashboard.AccountsUnavailable"
                    };
                    status.Text = key is null ? "" : Strings.Get(key);
                    status.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
                    retry.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
                    if (picker.Visibility == Visibility.Visible)
                        picker.Focus(FocusState.Programmatic);
                }
                catch (OperationCanceledException) when (listLifetime.IsCancellationRequested) { }
            }

            Task listing = Task.CompletedTask;
            retry.Click += (_, _) => listing = LoadAsync();
            dialog.Opened += (_, _) => listing = LoadAsync();
            ContentDialogResult selectedResult;
            try
            {
                selectedResult = await ShowDialogAsync(dialog);
            }
            finally
            {
                await listLifetime.CancelAsync();
                await listing;
            }

            if (closed || !connected || displayedState?.Account != currentId ||
                selectedResult != ContentDialogResult.Primary || SelectedAccount() is not { } selected || selected.Id == currentId)
                return;

            var failure = await actions.SwitchAccount(selected.Id, selected.Label, windowLifetime.Token);
            if (!closed && failure is not null)
                ShowMessage(Strings.Get(failure), InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) when (closed) { }
        finally
        {
            changingAccount = false;
            if (!closed)
            {
                UpdateAvailability();
                if (CurrentPage == "account")
                {
                    if (opener.IsEnabled && (HasAccount ? opener == switchAccount : opener == connectButton))
                        opener.Focus(FocusState.Programmatic);
                    else
                        navigationItems["account"].Focus(FocusState.Programmatic);
                }
            }
        }
    }

    private async Task LogoutAsync()
    {
        if (closed || !HasAccount || !CanManageAccount)
            return;
        var account = displayedState!.Account;
        var result = await ShowDialogAsync(new ContentDialog
        {
            Title = Strings.Get("Dashboard.LogoutTitle"),
            Content = Ui.Text(Strings.Get("Dashboard.LogoutDescription")),
            PrimaryButtonText = Strings.Get("Dashboard.Logout"),
            CloseButtonText = Strings.Get("Dashboard.Cancel"),
            DefaultButton = ContentDialogButton.Close
        });
        if (!closed && result == ContentDialogResult.Primary && CanManageAccount && displayedState?.Account == account)
        {
            await send("logout", null, false);
            if (!closed && SignedOutPage && connectButton.IsEnabled)
                connectButton.Focus(FocusState.Programmatic);
        }
    }
}
