using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Opssh.Tray;

internal sealed record DashboardActions(
    Func<CancellationToken, Task<AccountListResult>> ListAccounts,
    Func<string, string, CancellationToken, Task<string?>> SwitchAccount,
    Func<string, object?, CancellationToken, Task<Result>> Request,
    Func<LoginStartupStatus> GetLoginStartup,
    Func<bool, bool> SetLoginStartup,
    Func<bool, string, bool> SetStartupPolicy,
    Func<bool> GetDefaultAgent,
    Func<bool, CancellationToken, Task<string?>> SetDefaultAgent,
    Func<bool>? GetDefaultAgentActive = null);

public sealed partial class DashboardWindow : Window
{
    private readonly Func<string, object?, bool, Task> send;
    private readonly Func<string, object?, Task> sendUrgent;
    private readonly DashboardActions actions;
    private readonly CancellationTokenSource windowLifetime = new();
    private readonly Dictionary<string, NavigationViewItem> navigationItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> pages = new(StringComparer.Ordinal);
    private readonly FrameworkElement signedOut;
    private readonly Button connectButton;
    private readonly Button restartButton;
    private CoreState? displayedState;
    private ContentDialog? activeDialog;
    private string? pipe;
    private bool closed;
    private bool connected;
    private bool busy;
    private bool urgentBusy;
    private bool locked;
    private bool changingAccount;
    private bool changingIntegration;
    private bool receivedFirstState;

    internal DashboardWindow(Func<string, object?, bool, Task> send, Func<string, object?, Task> sendUrgent,
        Func<Task> start, UiPreferences preferences, Func<string, string, bool> applyAppearance,
        Func<Task> checkUpdates, Func<bool, bool> setAutoCheckUpdates, DashboardActions actions)
    {
        this.send = send;
        this.sendUrgent = sendUrgent;
        this.actions = actions;
        InitializeComponent();
        Title = "opssh";
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(DashboardTitleBar);
        }
        else
        {
            DashboardTitleBar.Visibility = Visibility.Collapsed;
        }
        ApplyTheme(preferences.Theme);
        ResizeToWorkArea();
        Closed += (_, _) =>
        {
            closed = true;
            windowLifetime.Cancel();
            windowLifetime.Dispose();
        };

        AutomationProperties.SetName(Navigation, Strings.Get("Dashboard.Navigation"));
        AddNavigationItem("keys", "Dashboard.Keys", "\uE192");
        AddNavigationItem("activity", "Dashboard.Activity", "\uE9D9");
        AddNavigationItem("account", "Dashboard.AccountNavigation", "\uE77B");
        AddNavigationItem("settings", "Settings.Settings", "\uE713", footer: true);
        AddNavigationItem("about", "Settings.About", "\uE946", footer: true);

        connectButton = DashboardButton(Strings.Get("Dashboard.Connect"), async () => await PickAccountAsync());
        connectButton.Style = StyleFor("DashboardAccentButtonStyle");
        connectButton.HorizontalAlignment = HorizontalAlignment.Center;
        restartButton = DashboardButton(Strings.Get("Settings.RestartCore"), async () => await start());
        restartButton.HorizontalAlignment = HorizontalAlignment.Center;
        restartButton.Visibility = Visibility.Collapsed;
        signedOut = BuildSignedOut();
        pages["settings"] = ScrollPage(BuildSettings(preferences, applyAppearance));
        pages["about"] = ScrollPage(BuildAbout(preferences.AutoCheckUpdates, checkUpdates, setAutoCheckUpdates));
        pages["account"] = ScrollPage(BuildAccount());
        RefreshSystemSettings();
        Activated += (_, args) =>
        {
            if (!closed && args.WindowActivationState != WindowActivationState.Deactivated)
                RefreshSystemSettings();
        };
        Navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: string id })
            {
                CurrentPage = id;
                ShowPage();
            }
        };
        DashboardRoot.SizeChanged += (_, args) =>
        {
            Navigation.OpenPaneLength = args.NewSize.Width <= 800 ? 152 : 186;
            SetPageMargin(args.NewSize.Width);
        };
        PageSurface.SizeChanged += (_, args) =>
            Feedback.Width = Math.Clamp(args.NewSize.Width - 28, 0, 480);
        SelectPage("account");
        SetAvailability(false, false, Strings.Get("Settings.StartingCore"));
    }

    internal string CurrentPage { get; private set; } = "account";

    internal void SelectPage(string id)
    {
        if (!navigationItems.TryGetValue(id, out var item) || (!HasAccount && id is ("keys" or "activity")))
            item = navigationItems["account"];
        Navigation.SelectedItem = item;
        if (item.Tag is string selected)
            CurrentPage = selected;
        ShowPage();
    }

    private void AddNavigationItem(string id, string labelKey, string glyph, bool footer = false)
    {
        var label = Strings.Get(labelKey);
        var item = new NavigationViewItem
        {
            Content = label,
            Icon = Icon(glyph),
            Tag = id,
            IsEnabled = id is not ("keys" or "activity")
        };
        AutomationProperties.SetName(item, label);
        navigationItems.Add(id, item);
        (footer ? Navigation.FooterMenuItems : Navigation.MenuItems).Add(item);
    }

    private bool HasAccount => displayedState?.Account.Length > 0;
    private bool SignedOutPage => !HasAccount && CurrentPage is ("account" or "keys" or "activity");
    private bool Ready => connected && displayedState is not null && !busy && !urgentBusy;
    private bool CanManageAccount => Ready && !locked && !changingAccount;

    private void ShowPage()
    {
        if (closed)
            return;

        FrameworkElement page;
        if (SignedOutPage)
            page = signedOut;
        else if (!pages.TryGetValue(CurrentPage, out page!))
        {
            page = CurrentPage == "keys" ? ScrollPage(BuildKeysPage()) : BuildActivityPage();
            pages.Add(CurrentPage, page);
            if (CurrentPage == "keys")
                UpdateKeysPage(null, displayedState);
        }

        if (!ReferenceEquals(PageHost.Content, page))
        {
            PageHost.Content = page;
            if (CurrentPage == "activity" && HasAccount)
                UpdateActivityPage(displayedState, displayedState);
        }
        SetPageMargin(DashboardRoot.ActualWidth);
    }

    private void SetPageMargin(double width)
    {
        var padding = width <= 620 ? 14 : Math.Clamp(width * 0.032, 16, 32);
        PageHost.Margin = SignedOutPage ? new Thickness(12, 12, 28, 12) : new Thickness(padding, Math.Max(0, padding - 4), padding + 14,
            CurrentPage == "activity" ? padding : 0);
        if (CurrentPage == "settings" && PageHost.Content is ScrollViewer { Content: Border settingsBody })
            settingsBody.Padding = new Thickness(0, 0, 0, padding);
    }

    private FrameworkElement BuildSignedOut()
    {
        var title = Heading(Strings.Get("Dashboard.Connect"), 28, AutomationHeadingLevel.Level1);
        title.TextAlignment = TextAlignment.Center;
        var detail = Quiet(Strings.Get("Dashboard.ConnectDescription"));
        detail.TextAlignment = TextAlignment.Center;
        var logo = Logo("\uE192", 40, 22);
        logo.HorizontalAlignment = HorizontalAlignment.Center;
        return new StackPanel
        {
            MaxWidth = 320,
            Spacing = 12,
            Padding = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { logo, title, detail, connectButton, restartButton }
        };
    }

    internal void ApplyTheme(string theme) => WindowTheme.Apply(this, DashboardRoot, theme);

    internal void SelectAppearance() => SelectPage("settings");

    internal void SetAvailability(bool value, bool canRestart, string text)
    {
        connected = value;
        restartButton.Visibility = canRestart ? Visibility.Visible : Visibility.Collapsed;
        if (canRestart)
            ShowMessage(text, InfoBarSeverity.Warning);
        if (!value)
        {
            var previous = displayedState;
            displayedState = null;
            pipe = null;
            busy = urgentBusy = locked = false;
            UpdatePages(previous);
        }
        UpdateAvailability();
        ShowPage();
    }

    internal void SetHello(Hello hello)
    {
        pipe = hello.Pipe;
        UpdateSettingsAvailability();
    }

    internal void ShowState(CoreState state, bool resetSettings)
    {
        var previous = displayedState;
        displayedState = state;
        pipe = state.Pipe;
        locked = state.Locked;
        UpdateCacheInputs(previous, state, resetSettings);
        UpdatePages(previous);
        UpdateAvailability();
        if (!receivedFirstState && !changingAccount && HasAccount && CurrentPage == "account")
            SelectPage("keys");
        else
            ShowPage();
        receivedFirstState = true;
    }

    private void UpdatePages(CoreState? previous)
    {
        UpdateAccountPage(previous, displayedState);
        UpdateKeysPage(previous, displayedState);
        UpdateActivityPage(previous, displayedState);
    }

    partial void UpdateKeysPage(CoreState? previous, CoreState? current);
    partial void UpdateActivityPage(CoreState? previous, CoreState? current);

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
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        connectButton.IsEnabled = CanManageAccount;
        navigationItems["keys"].IsEnabled = navigationItems["activity"].IsEnabled = HasAccount;
        if (SignedOutPage && CurrentPage != "account")
            SelectPage("account");
        UpdateSettingsAvailability();
        UpdateAccountAvailability();
        UpdateKeysAvailability();
        UpdateActivityAvailability();
    }

    partial void UpdateKeysAvailability();
    partial void UpdateActivityAvailability();

    internal void ShowMessage(string text) => ShowMessage(text, InfoBarSeverity.Informational);

    private void ShowMessage(string text, InfoBarSeverity severity)
    {
        if (closed)
            return;
        Feedback.Severity = severity;
        Feedback.Message = text;
        Feedback.IsOpen = !string.IsNullOrEmpty(text);
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (closed || activeDialog is not null)
            return ContentDialogResult.None;
        dialog.XamlRoot = DashboardRoot.XamlRoot;
        var width = Math.Clamp(DashboardRoot.ActualWidth - 24, 280, 420);
        dialog.Resources["ContentDialogMinWidth"] = width;
        dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.Resources["ContentDialogPadding"] = new Thickness(22, 17, 22, 17);
        activeDialog = dialog;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            activeDialog = null;
        }
    }

    private void ResizeToWorkArea()
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new SizeInt32(
            Math.Min(workArea.Width, (int)Math.Round(1060 * scale)),
            Math.Min(workArea.Height, (int)Math.Round(642 * scale))));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private Style StyleFor(string key) => (Style)Application.Current.Resources[key];

    private static FontIcon Icon(string glyph, double size = 16) => new()
    {
        Glyph = glyph,
        FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private Border Logo(string glyph, double size, double iconSize) => new()
    {
        Style = StyleFor("DashboardLogoStyle"),
        Width = size,
        Height = size,
        Child = glyph == "\uE756"
            ? new ContentControl
            {
                Width = iconSize, Height = iconSize,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                ContentTemplate = (DataTemplate)Application.Current.Resources["DashboardTerminalIcon"]
            }
            : new FontIcon { Glyph = glyph, FontSize = iconSize, Style = StyleFor("DashboardLogoIconStyle") }
    };

    private static TextBlock Heading(string value, int size, AutomationHeadingLevel level)
    {
        var title = Ui.Heading(value, size);
        if (Strings.Language == "zh-Hans")
            title.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        title.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetHeadingLevel(title, level);
        return title;
    }

    private TextBlock Quiet(string value, double size = 14) => new()
    {
        Style = StyleFor("DashboardQuietTextStyle"),
        Text = value,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock Mono(string value, double size = 12) => new()
    {
        Text = value,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = size,
        TextWrapping = TextWrapping.Wrap
    };

    private Button DashboardButton(string label, Action action)
    {
        var button = Ui.Button(label, action);
        button.Style = StyleFor("DashboardButtonStyle");
        return button;
    }

    private StackPanel Page(string title, string description = "")
    {
        var header = new StackPanel { Spacing = 4, Children = { Heading(title, 28, AutomationHeadingLevel.Level1) } };
        if (description.Length > 0)
            header.Children.Add(Quiet(description));
        return new StackPanel { Spacing = 16, Children = { header } };
    }

    private static ScrollViewer ScrollPage(FrameworkElement page) => new()
    {
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = new Border { Child = page, Padding = new Thickness(0, 0, 0, 20) }
    };

    private StackPanel Section(string label, params UIElement[] contents)
    {
        var section = new StackPanel { Spacing = 8, Children = { Heading(label, 16, AutomationHeadingLevel.Level2) } };
        foreach (var content in contents)
            section.Children.Add(content);
        return section;
    }

    private Border Card(UIElement child) => new() { Style = StyleFor("DashboardCardStyle"), Child = child };

    private Border Divider() => new() { Style = StyleFor("DashboardDividerStyle") };

    private Border Rows(params UIElement[] rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows)
        {
            if (stack.Children.Count > 0)
                stack.Children.Add(Divider());
            stack.Children.Add(row);
        }
        return Card(stack);
    }

    private Grid SettingRow(string glyph, string title, string description, FrameworkElement? action = null,
        FrameworkElement? detail = null, UIElement? symbol = null) =>
        SettingRow(glyph, title, description.Length == 0 ? null : Quiet(description, 12), action, detail, symbol);

    private static Grid SettingRow(string glyph, string title, FrameworkElement? description, FrameworkElement? action = null,
        FrameworkElement? detail = null, UIElement? symbol = null)
    {
        var heading = Ui.Heading(title, 14);
        heading.TextWrapping = TextWrapping.Wrap;
        var label = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { heading } };
        if (description is not null)
            label.Children.Add(description);

        var row = new Grid { MinHeight = 52, Padding = new Thickness(13, 10, 13, 10), ColumnSpacing = 10, RowSpacing = 0 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.Children.Add(symbol ?? Icon(glyph));
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, 2);
            row.Children.Add(action);
        }
        if (detail is not null)
        {
            detail.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(detail, 2);
            Grid.SetColumn(detail, 1);
            Grid.SetColumnSpan(detail, 2);
            row.Children.Add(detail);
        }

        row.SizeChanged += (_, args) =>
        {
            if (action is null)
                return;
            var stack = args.NewSize.Width < 400;
            Grid.SetColumnSpan(label, stack ? 2 : 1);
            Grid.SetColumn(action, stack ? 1 : 2);
            Grid.SetColumnSpan(action, stack ? 2 : 1);
            Grid.SetRow(action, stack ? 1 : 0);
            action.Margin = stack ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        };
        return row;
    }
}
