using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Opssh.Tray;

public sealed partial class DashboardWindow
{
    private readonly Dictionary<string, ToggleSwitch> vaultChecks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (StackPanel Host, TextBlock Status, HyperlinkButton Retry)> vaultFeedback = new(StringComparer.Ordinal);
    private readonly List<(StackPanel Host, InfoBar Notice, Button Retry)> pickerNotices = [];
    private (string Id, bool Enabled, string Feedback)? failedVault;
    private string? savingVault;
    private bool pickerForAuthorization;
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
        var selected = SettingRow("\uE77B", Strings.Get("Dashboard.SelectedAccount"), accountName, accountActions);
        selected.MinHeight = 61;
        var access = SettingRow("\uE72E", Strings.Get("Dashboard.DesktopAccess"), Strings.Get("Dashboard.DesktopAccessDescription"), request);
        access.MinHeight = 62;
        page.Children.Add(Rows(selected, access));
        page.Children.Insert(1, BuildPickerNotice());

        vaultCount = Quiet("", 12);
        vaultCount.VerticalAlignment = VerticalAlignment.Center;
        reloadVaults = DashboardButton(Strings.Get("Dashboard.ReloadVaults"), async () => await send("reload-vaults", null, false));
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
        DesktopAccount.IsLabel(state.AccountName) ? state.AccountName! : Strings.Get("Dashboard.SelectedAccount");

    private void UpdateAccountPage(CoreState? previous, CoreState? state)
    {
        if (state is null)
        {
            vaultRows.Children.Clear();
            vaultChecks.Clear();
            vaultFeedback.Clear();
            failedVault = null;
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
                "last-success" => ("Dashboard.ProviderLastSuccess", "DashboardSuccessIconStyle"),
                "denied" or "expired" => ("Dashboard.ProviderNotAuthorized", "DashboardWarningIconStyle"),
                "unavailable" => ("Dashboard.ProviderFailed", "DashboardErrorIconStyle"),
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
        if (previous?.Account != state.Account)
            failedVault = null;
        if (previous is null || previous.Account != state.Account ||
            !ManageableVaults(previous).Select(vault => (vault.Id, vault.Name)).SequenceEqual(vaults.Select(vault => (vault.Id, vault.Name))))
            PopulateVaults(vaults);

        UpdateVaultChoices(state);
    }

    private static DiscoveredVault[] ManageableVaults(CoreState state)
    {
        var visible = state.VaultCatalog ?? state.Vaults ?? [];
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
        vaultFeedback.Clear();
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
            var status = Quiet("", 12);
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            var retry = new HyperlinkButton { Content = Strings.Get("Dashboard.Retry"), Padding = new Thickness(0) };
            retry.Click += async (_, _) =>
            {
                if (failedVault is { } failure && failure.Id == vault.Id)
                    await SetVaultAsync(vault.Id, failure.Enabled);
            };
            var detail = new StackPanel { Spacing = 4, Children = { status, retry } };
            var row = SettingRow("\uE8B7", label, "", check, detail);
            detail.Visibility = Visibility.Collapsed;
            vaultFeedback.Add(vault.Id, (detail, status, retry));
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
        vaultCount.Text = Strings.Get("Dashboard.AllowedVaultCount", allowed);
        goToKeys.Visibility = allowed > 0 ? Visibility.Visible : Visibility.Collapsed;
        vaultHint.Text = Strings.Get(vaultChecks.Count == 0 ? "Dashboard.NoVaults" :
            allowed == 0 ? "Dashboard.ChooseVaults" : "Dashboard.DiscoverAfterVaultChange");
    }

    private void UpdateAccountAvailability()
    {
        var enabled = HasAccount && CanManageAccount;
        switchAccount.IsEnabled = logout.IsEnabled = reloadVaults.IsEnabled = enabled && !changingVault;
        var known = displayedState?.VaultCatalog ?? displayedState?.Vaults ?? [];
        foreach (var (id, check) in vaultChecks)
        {
            var present = known.Any(vault => vault.Id == id);
            check.IsEnabled = enabled && !changingVault && (present || check.IsOn);
            var (host, status, retry) = vaultFeedback[id];
            var failed = failedVault is { } failure && failure.Id == id;
            status.Text = savingVault == id ? Strings.Get("Dashboard.VaultSaving") :
                failed ? Strings.Get("Dashboard.VaultSaveFailed", Strings.Get(failedVault!.Value.Feedback)) : "";
            status.Style = StyleFor(failed ? "DashboardErrorTextStyle" : "DashboardQuietTextStyle");
            host.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            retry.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            retry.IsEnabled = enabled && !changingVault && (present || failedVault?.Enabled == false);
        }
    }

    private async Task SetVaultAsync(string vaultId, bool enabled)
    {
        if (closed || !HasAccount || !CanManageAccount || changingVault)
            return;
        var account = displayedState!.Account;
        changingVault = true;
        savingVault = vaultId;
        failedVault = null;
        UpdateAccountAvailability();
        try
        {
            var reply = await actions.Request("set-vault", new { vaultId, enabled }, windowLifetime.Token);
            if (closed || displayedState?.Account != account)
                return;
            if (reply.State is { } latest && latest.Account == account)
                ShowState(latest, false);
            if (!reply.Ok)
                failedVault = (vaultId, enabled, CoreFeedback.ResultKey(reply.ErrorCode, reply.Error));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (!closed && displayedState?.Account == account)
                failedVault = (vaultId, enabled, "Error.ControlRequestFailed");
        }
        finally
        {
            changingVault = false;
            savingVault = null;
            if (!closed && displayedState is { } state)
            {
                UpdateVaultChoices(state);
                UpdateAccountAvailability();
            }
        }
    }

    private StackPanel BuildPickerNotice()
    {
        var notice = new InfoBar { IsOpen = true, IsClosable = false };
        var retry = DashboardButton(Strings.Get("Dashboard.Retry"), async () => await PickAccountAsync(pickerForAuthorization));
        retry.Style = StyleFor("DashboardAccentButtonStyle");
        retry.HorizontalAlignment = HorizontalAlignment.Left;
        var host = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed, Children = { notice, retry } };
        pickerNotices.Add((host, notice, retry));
        return host;
    }

    private void SetPickerNotice(string? key, bool canRetry)
    {
        foreach (var (host, notice, retry) in pickerNotices)
        {
            host.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
            notice.Message = key is null ? "" : Strings.Get(key);
            notice.Severity = canRetry ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            retry.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    internal Task ShowAccountPickerAsync(bool authorizeCurrent = false) => PickAccountAsync(authorizeCurrent);

    private async Task PickAccountAsync(bool authorizeCurrent = false)
    {
        if (closed || !CanManageAccount || activeDialog is not null || changingVault)
            return;
        var opener = HasAccount ? switchAccount : connectButton;
        var currentId = displayedState?.Account;
        var token = windowLifetime.Token;
        pickerForAuthorization = authorizeCurrent;
        changingAccount = true;
        UpdateAvailability();
        SetPickerNotice("Dashboard.AccountsLoading", false);
        try
        {
            var result = await actions.ListAccounts(token);
            if (closed || !connected || displayedState?.Account != currentId)
                return;
            var choices = result.Accounts.Where(account => authorizeCurrent || !DesktopAccount.SameId(account.Id, currentId)).ToArray();
            var key = result.Status switch
            {
                AccountListStatus.Ready => choices.Length == 0 ? "Dashboard.NoOtherAccount" : null,
                AccountListStatus.Empty => "Dashboard.NoAccounts",
                AccountListStatus.NotInstalled => "Dashboard.AccountsCliMissing",
                AccountListStatus.Untrusted => "Dashboard.AccountsCliUntrusted",
                AccountListStatus.TimedOut => "Dashboard.AccountsTimedOut",
                AccountListStatus.Busy => "Dashboard.AccountsBusy",
                AccountListStatus.InvalidResponse => "Dashboard.AccountsInvalidResponse",
                _ => "Dashboard.AccountsUnavailable"
            };
            if (result.Accounts.FirstOrDefault(account => DesktopAccount.SameId(account.Id, currentId)) is { } saved &&
                displayedState!.AccountName != saved.Label)
            {
                var updated = await actions.Request("update-account-label", new { account = saved.Id, name = saved.Label }, token);
                if (closed || !connected || displayedState?.Account != currentId)
                    return;
                if (updated.Ok && updated.State is { } latest)
                    ShowState(latest, false);
            }

            SetPickerNotice(key, key is not null);
            if (key is not null)
                return;

            var picker = new ComboBox
            {
                Header = Strings.Get("Settings.Account"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(picker, Strings.Get("Dashboard.ChooseAccount"));
            foreach (var account in choices)
                picker.Items.Add(new ComboBoxItem { Content = account.Label, Tag = account });
            picker.SelectedIndex = 0;
            var switchNotice = Quiet(Strings.Get("Dashboard.SwitchAccountDescription"));
            var contents = new StackPanel
            {
                Spacing = 10,
                Children = { picker, Quiet(Strings.Get("Dashboard.EnableSdk")), switchNotice }
            };
            var dialog = new ContentDialog
            {
                Title = Strings.Get(authorizeCurrent ? "Dashboard.AuthorizeAccountTitle" :
                    string.IsNullOrEmpty(currentId) ? "Dashboard.ConnectAccount" : "Dashboard.SwitchAccountTitle"),
                Content = contents,
                PrimaryButtonText = Strings.Get("Dashboard.ContinueInPassword"),
                CloseButtonText = Strings.Get("Dashboard.Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            DesktopAccount? SelectedAccount() => (picker.SelectedItem as ComboBoxItem)?.Tag as DesktopAccount;
            void UpdateSwitchNotice()
            {
                switchNotice.Visibility = !string.IsNullOrEmpty(currentId) && !DesktopAccount.SameId(SelectedAccount()?.Id, currentId)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateSwitchNotice();
            picker.SelectionChanged += (_, _) => UpdateSwitchNotice();
            dialog.Opened += (_, _) => picker.Focus(FocusState.Programmatic);
            var selectedResult = await ShowDialogAsync(dialog);
            if (closed || !connected || displayedState?.Account != currentId ||
                selectedResult != ContentDialogResult.Primary || SelectedAccount() is not { } selected)
                return;

            string? failure;
            if (DesktopAccount.SameId(selected.Id, currentId))
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(39).ToUnixTimeMilliseconds();
                var reply = await actions.Request("authorize-desktop", new { account = currentId, deadline }, token);
                if (!closed && displayedState?.Account == currentId && reply.State is { } latest)
                    ShowState(latest, false);
                failure = reply.Ok ? null : CoreFeedback.ResultKey(reply.ErrorCode, reply.Error);
            }
            else
                failure = await actions.SwitchAccount(selected.Id, selected.Label, token);
            if (!closed && failure is not null)
                ShowMessage(Strings.Get(failure), InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (!closed && displayedState?.Account == currentId)
                SetPickerNotice("Dashboard.AccountsUnavailable", true);
        }
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
            Content = Ui.Text(Strings.Get("Dashboard.LogoutDescription", DisplayedAccountName(displayedState!))),
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
