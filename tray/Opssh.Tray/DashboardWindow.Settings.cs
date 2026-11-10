using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Opssh.Tray;

public sealed partial class DashboardWindow
{
    private const int DefaultKeySeconds = 300;
    private const int DefaultApprovalSeconds = 600;
    private ToggleSwitch loginStartup = null!;
    private ToggleSwitch waitForPassword = null!;
    private ToggleSwitch defaultAgent = null!;
    private HyperlinkButton restoreDefaultAgent = null!;
    private TextBlock loginStartupDescription = null!;
    private FrameworkElement powerShellCard = null!;
    private TextBlock shellCommand = null!;
    private Button copyCommand = null!;
    private CacheEditor keyCacheEditor = null!;
    private CacheEditor approvalCacheEditor = null!;
    private bool updatingSettings;
    private bool savingCache;

    private StackPanel BuildSettings(UiPreferences preferences, Func<string, string, bool> applyAppearance)
    {
        var page = Page(Strings.Get("Settings.Settings"), Strings.Get("Dashboard.SettingsDescription"));
        loginStartup = Switch(Strings.Get("Dashboard.StartWithWindows"), false);
        loginStartupDescription = Quiet(Strings.Get("Dashboard.StartWithWindowsDescription"), 12);
        waitForPassword = Switch(Strings.Get("Dashboard.WaitForPassword"), preferences.WaitForPassword);

        loginStartup.Toggled += async (_, _) =>
        {
            if (updatingSettings)
                return;
            var enabled = loginStartup.IsOn;
            if (!enabled && (actions.GetDefaultAgent() || DefaultAgentActive) &&
                !await ChangeDefaultAgentAsync(false))
            {
                RefreshSystemSettings();
                return;
            }
            if (!closed && !actions.SetLoginStartup(enabled))
                ShowMessage(Strings.Get("Dashboard.StartupSaveFailed"), InfoBarSeverity.Warning);
            RefreshSystemSettings();
        };
        var savedWait = preferences.WaitForPassword;
        waitForPassword.Toggled += (_, _) =>
        {
            if (updatingSettings)
                return;
            if (actions.SetWaitForPassword(waitForPassword.IsOn))
                savedWait = waitForPassword.IsOn;
            else
            {
                updatingSettings = true;
                waitForPassword.IsOn = savedWait;
                updatingSettings = false;
                ShowMessage(Strings.Get("Dashboard.StartupSaveFailed"), InfoBarSeverity.Warning);
            }
        };

        page.Children.Add(Section(Strings.Get("Dashboard.Startup"), Cards(
            SettingRow("\uE7E8", Strings.Get("Dashboard.StartWithWindows"), loginStartupDescription, loginStartup),
            SettingRow("\uE916", Strings.Get("Dashboard.WaitForPassword"), Strings.Get("Dashboard.WaitForPasswordDescription"), waitForPassword))));

        defaultAgent = Switch(Strings.Get("Dashboard.DefaultAgent"), false);
        defaultAgent.Toggled += async (_, _) =>
        {
            if (!updatingSettings && loginStartup.IsOn)
                await ChangeDefaultAgentAsync(defaultAgent.IsOn);
        };
        restoreDefaultAgent = new HyperlinkButton
        {
            Content = Strings.Get("Dashboard.DisableDefaultAction"),
            Padding = new Thickness(0),
            Visibility = Visibility.Collapsed
        };
        restoreDefaultAgent.Click += async (_, _) => await ChangeDefaultAgentAsync(false);
        var agentActions = new StackPanel { Spacing = 4, Children = { defaultAgent, restoreDefaultAgent } };
        shellCommand = Mono("—");
        shellCommand.IsTextSelectionEnabled = true;
        AutomationProperties.SetName(shellCommand, Strings.Get("Settings.CommandAutomationName"));
        var code = new Border { Style = StyleFor("DashboardCodeStyle"), Child = shellCommand };
        copyCommand = Ui.Button(Strings.Get("Dashboard.CopyCommand"), CopyShellCommand);
        AutomationProperties.SetName(copyCommand, Strings.Get("Dashboard.CopyCommand"));
        copyCommand.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7,
            Children = { Icon("\uE8C8", 14), new TextBlock { Text = Strings.Get("Dashboard.CopyCommand") } }
        };
        powerShellCard = SettingRow("\uE756", Strings.Get("Dashboard.PowerShell"), Strings.Get("Dashboard.PowerShellDescription"),
            copyCommand, code);
        page.Children.Add(Section(Strings.Get("Dashboard.SshIntegration"), Cards(
            SettingRow("\uE8A4", Strings.Get("Dashboard.DefaultAgent"), Strings.Get("Dashboard.DefaultAgentDescription"), agentActions),
            powerShellCard)));

        keyCacheEditor = BuildCacheEditor("\uE192", "Settings.KeyCache", "Settings.KeyCacheDescription", DefaultKeySeconds, isKey: true);
        approvalCacheEditor = BuildCacheEditor("\uE73E", "Settings.ApprovalCache", "Settings.ApprovalCacheDescription",
            DefaultApprovalSeconds, isKey: false);
        page.Children.Add(Section(Strings.Get("Settings.CachePolicies"), Cards(keyCacheEditor.Expander, approvalCacheEditor.Expander)));

        if (actions.Diagnostics is { } diagnostics)
            page.Children.Add(BuildDiagnosticSettings(preferences, diagnostics));

        var theme = Choice(Strings.Get("Settings.Theme"), preferences.Theme,
            ("system", Strings.Get("Settings.ThemeSystem")), ("light", Strings.Get("Settings.ThemeLight")),
            ("dark", Strings.Get("Settings.ThemeDark")));
        var language = Choice(Strings.Get("Settings.Language"), preferences.Language,
            ("system", Strings.Get("Settings.LanguageSystem")), ("zh-Hans", Strings.Get("Settings.LanguageChinese")),
            ("en", Strings.Get("Settings.LanguageEnglish")));
        void ChangeAppearance(ComboBox sender, SelectionChangedEventArgs args)
        {
            if (updatingSettings || ChoiceId(language) is not { } languageId || ChoiceId(theme) is not { } themeId ||
                applyAppearance(languageId, themeId))
                return;
            updatingSettings = true;
            sender.SelectedItem = args.RemovedItems.FirstOrDefault();
            updatingSettings = false;
            ShowMessage(Strings.Get("Settings.AppearanceSaveFailed"), InfoBarSeverity.Warning);
        }
        language.SelectionChanged += (sender, args) => ChangeAppearance((ComboBox)sender, args);
        theme.SelectionChanged += (sender, args) => ChangeAppearance((ComboBox)sender, args);
        page.Children.Add(Section(Strings.Get("Dashboard.Preferences"), Cards(
            SettingRow("\uE790", Strings.Get("Settings.Theme"), Strings.Get("Settings.ThemeDescription"), theme),
            SettingRow("\uE8C1", Strings.Get("Settings.Language"), Strings.Get("Settings.LanguageDescription"), language))));
        return page;
    }

    private void RefreshSystemSettings()
    {
        if (closed)
            return;
        updatingSettings = true;
        var startup = actions.GetLoginStartup();
        loginStartup.IsOn = startup == LoginStartupStatus.On;
        loginStartup.IsEnabled = !changingIntegration && startup is (LoginStartupStatus.On or LoginStartupStatus.Off);
        defaultAgent.IsEnabled = loginStartup.IsOn && !changingIntegration;
        var startupStatus = startup switch
        {
            LoginStartupStatus.Conflict => Strings.Get("Dashboard.StartupConflict"),
            LoginStartupStatus.Unavailable => Strings.Get("Dashboard.StartupUnavailable"),
            _ => Strings.Get("Dashboard.StartWithWindowsDescription")
        };
        loginStartupDescription.Text = startupStatus;
        defaultAgent.IsOn = actions.GetDefaultAgent();
        restoreDefaultAgent.Visibility = startup is (LoginStartupStatus.Conflict or LoginStartupStatus.Unavailable) &&
            (defaultAgent.IsOn || DefaultAgentActive) ? Visibility.Visible : Visibility.Collapsed;
        restoreDefaultAgent.IsEnabled = !changingIntegration;
        updatingSettings = false;
    }

    private void UpdateSettingsAvailability()
    {
        if (closed)
            return;
        updatingSettings = true;
        powerShellCard.Visibility = DefaultAgentActive ? Visibility.Collapsed : Visibility.Visible;
        var command = PowerShellCommand();
        shellCommand.Text = command ?? "—";
        copyCommand.IsEnabled = command is not null;
        var cacheEnabled = Ready && !savingCache;
        keyCacheEditor.Mode.IsEnabled = approvalCacheEditor.Mode.IsEnabled = cacheEnabled;
        keyCacheEditor.Duration.IsEnabled = approvalCacheEditor.Duration.IsEnabled = cacheEnabled;
        updatingSettings = false;
    }

    private bool DefaultAgentActive => actions.GetDefaultAgentActive?.Invoke() ?? displayedState?.DefaultAgent == true;

    private string? PowerShellCommand()
    {
        var address = DefaultAgentActive ? null : pipe;
        return address is not null && address.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
            ? "$env:SSH_AUTH_SOCK = '" + address.Replace("'", "''") + "'" : null;
    }

    private void CopyShellCommand()
    {
        if (PowerShellCommand() is not { } command)
            return;
        try
        {
            var data = new DataPackage();
            data.SetText(command);
            Clipboard.SetContent(data);
            ShowMessage(Strings.Get("Dashboard.CommandCopied"), InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            ShowMessage(Strings.Get("Dashboard.CommandCopyFailed"), InfoBarSeverity.Warning);
        }
    }

    private static ComboBox Choice(string name, string selected, params (string Id, string Label)[] options)
    {
        var choice = new ComboBox { MinWidth = 140, MaxWidth = 248 };
        AutomationProperties.SetName(choice, name);
        foreach (var (id, label) in options)
            choice.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        SelectChoice(choice, selected);
        return choice;
    }

    private static string? ChoiceId(ComboBox choice) => (choice.SelectedItem as ComboBoxItem)?.Tag as string;

    private static void SelectChoice(ComboBox choice, string id) =>
        choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string?)item.Tag == id);

    private static ToggleSwitch Switch(string name, bool value)
    {
        var control = new ToggleSwitch
        {
            IsOn = value,
            OnContent = Strings.Get("Update.On"),
            OffContent = Strings.Get("Update.Off"),
            MinWidth = 0
        };
        AutomationProperties.SetName(control, name);
        return control;
    }

    private CacheEditor BuildCacheEditor(string glyph, string titleKey, string descriptionKey, int defaults, bool isKey)
    {
        var title = Strings.Get(titleKey);
        var mode = Choice(Strings.Get("Dashboard.CacheModeName", title), "fixed",
            ("off", Strings.Get("Settings.CacheOff")), ("fixed", Strings.Get("Settings.CacheFixed")),
            ("idle", Strings.Get("Settings.CacheIdle")), ("until-exit", Strings.Get("Dashboard.UntilExit")));
        mode.Header = Strings.Get("Dashboard.CacheMode");
        mode.Width = 200;
        mode.MinWidth = 0;
        mode.HorizontalAlignment = HorizontalAlignment.Left;
        var duration = new TextBox
        {
            Header = Strings.Get("Dashboard.CacheDuration"),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxLength = 7,
            Width = 110,
            InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } }
        };
        AutomationProperties.SetName(duration, Strings.Get("Settings.CacheSecondsAutomationName", title));
        var validation = Quiet(Strings.Get("Settings.CacheDurationError", CachePolicy.MaxSeconds), 12);
        validation.Style = StyleFor("DashboardErrorTextStyle");
        validation.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(validation, AutomationLiveSetting.Polite);
        var hint = Quiet("", 12);

        var controls = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.Children.Add(mode);
        controls.Children.Add(duration);
        Grid.SetColumn(duration, 1);
        controls.SizeChanged += (_, args) =>
        {
            var stacked = args.NewSize.Width < 326;
            mode.Width = Math.Min(200, Math.Max(0, args.NewSize.Width));
            duration.Width = Math.Min(110, Math.Max(0, args.NewSize.Width));
            Grid.SetColumn(duration, stacked ? 0 : 1);
            Grid.SetRow(duration, stacked ? 1 : 0);
        };

        var contents = new StackPanel { Spacing = 6, Children = { controls, validation, hint } };
        var expander = new SettingsExpander
        {
            Header = title,
            HeaderIcon = Icon(glyph),
            Description = Strings.Get(descriptionKey),
            IsExpanded = true,
            Items =
            {
                new SettingsCard
                {
                    Content = contents,
                    ContentAlignment = ContentAlignment.Left,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                }
            }
        };
        var editor = new CacheEditor(expander, mode, duration, hint, validation, defaults, isKey);
        SetCacheEditor(editor, new CachePolicy("fixed", defaults));
        mode.SelectionChanged += async (_, _) => await CommitCacheAsync(editor, modeChanged: true);
        duration.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                await CommitCacheAsync(editor, modeChanged: false);
            }
        };
        duration.LostFocus += async (_, _) => await CommitCacheAsync(editor, modeChanged: false);
        duration.TextChanged += (_, _) => editor.Validation.Visibility = Visibility.Collapsed;
        return editor;
    }

    private void UpdateCacheInputs(CoreState? previous, CoreState current, bool reset)
    {
        var restoring = updatingSettings;
        updatingSettings = true;
        if (reset || previous?.KeyCache != current.KeyCache)
            SetCacheEditor(keyCacheEditor, current.KeyCache);
        if (reset || previous?.ApprovalCache != current.ApprovalCache)
            SetCacheEditor(approvalCacheEditor, current.ApprovalCache);
        updatingSettings = restoring;
    }

    private static void SetCacheEditor(CacheEditor editor, CachePolicy policy)
    {
        var mode = policy.Mode is "off" or "fixed" or "idle" or "until-exit" ? policy.Mode : "fixed";
        if (policy.Seconds > 0)
            editor.LastDuration = policy.Seconds;
        SelectChoice(editor.Mode, mode);
        editor.Duration.Text = editor.LastDuration.ToString(CultureInfo.InvariantCulture);
        editor.Duration.Visibility = mode is "fixed" or "idle" ? Visibility.Visible : Visibility.Collapsed;
        editor.Validation.Visibility = Visibility.Collapsed;
        editor.Hint.Text = Strings.Get(mode switch
        {
            "off" => editor.IsKey ? "Dashboard.KeyCacheOffHint" : "Dashboard.ApprovalCacheOffHint",
            "idle" => "Dashboard.CacheIdleHint",
            "until-exit" => editor.IsKey ? "Dashboard.KeyCacheExitHint" : "Dashboard.ApprovalCacheExitHint",
            _ => "Dashboard.CacheFixedHint"
        });
    }

    private async Task CommitCacheAsync(CacheEditor editor, bool modeChanged)
    {
        if (closed || updatingSettings || savingCache || !Ready || displayedState is not { } state ||
            ChoiceId(editor.Mode) is not { } mode)
            return;
        var seconds = 0;
        if (mode is "fixed" or "idle" &&
            (!int.TryParse(editor.Duration.Text, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
             seconds is < 1 or > CachePolicy.MaxSeconds))
        {
            if (!modeChanged)
            {
                editor.Validation.Visibility = Visibility.Visible;
                return;
            }
            seconds = editor.LastDuration;
        }
        var next = new CachePolicy(mode, seconds);
        var previous = editor.IsKey ? state.KeyCache : state.ApprovalCache;
        if (previous == next)
        {
            updatingSettings = true;
            SetCacheEditor(editor, next);
            updatingSettings = false;
            return;
        }
        savingCache = true;
        updatingSettings = true;
        SetCacheEditor(editor, next);
        updatingSettings = false;
        UpdateSettingsAvailability();
        try
        {
            var keyCache = editor.IsKey ? next : state.KeyCache;
            var approvalCache = editor.IsKey ? state.ApprovalCache : next;
            await send("settings", new { keyCache, approvalCache }, false);
        }
        finally
        {
            savingCache = false;
            if (!closed && displayedState is { } latest)
            {
                UpdateCacheInputs(null, latest, reset: true);
                UpdateSettingsAvailability();
            }
        }
    }

    private sealed record CacheEditor(SettingsExpander Expander, ComboBox Mode, TextBox Duration, TextBlock Hint,
        TextBlock Validation, int DefaultDuration, bool IsKey)
    {
        internal int LastDuration { get; set; } = DefaultDuration;
    }
}
