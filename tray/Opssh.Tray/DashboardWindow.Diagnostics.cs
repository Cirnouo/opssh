using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Opssh.Tray;

public sealed partial class DashboardWindow
{
    private TextBlock? diagnosticUsage;
    private Button? clearDiagnosticLogs;
    private bool diagnosticUsageReading;
    private bool diagnosticUsagePending;
    private bool diagnosticActionPending;
#if OPSSH_DASHBOARD_FIXTURE
    private FrameworkElement? diagnosticSection;
#endif

    private StackPanel BuildDiagnosticSettings(UiPreferences preferences, DiagnosticActions diagnostics)
    {
        var enabled = Switch(Strings.Get("Settings.EnableLogs"), preferences.DiagnosticLogging);
        var level = Choice(Strings.Get("Settings.LogLevel"), preferences.DiagnosticLevel,
            ("ERROR", "ERROR"), ("WARN", "WARN"), ("INFO", "INFO"), ("DEBUG", "DEBUG"));
        level.MinWidth = 70;
        var levelDescription = Quiet("", 12);
        var savedEnabled = preferences.DiagnosticLogging;
        var savedLevel = preferences.DiagnosticLevel;
        var resetting = false;
        UpdateLevel();
        void UpdateLevel()
        {
            level.IsEnabled = savedEnabled;
            levelDescription.Text = Strings.Get(savedEnabled ? "Settings.LogLevelDefault" : "Settings.LogLevelDisabled");
        }
        void ChangePolicy()
        {
            if (resetting || closed)
                return;
            var selected = ChoiceId(level) ?? savedLevel;
            if (diagnostics.SetPolicy(enabled.IsOn, selected))
            {
                savedEnabled = enabled.IsOn;
                savedLevel = selected;
            }
            else
            {
                resetting = true;
                enabled.IsOn = savedEnabled;
                SelectChoice(level, savedLevel);
                resetting = false;
                ShowMessage(Strings.Get("Settings.LogPolicyFailed"), InfoBarSeverity.Warning);
            }
            UpdateLevel();
        }
        enabled.Toggled += (_, _) => ChangePolicy();
        level.SelectionChanged += (_, _) => ChangePolicy();

        var open = DashboardButton(Strings.Get("Settings.OpenLogDirectory"), async () =>
        {
            if (closed || diagnosticActionPending)
                return;
            diagnosticActionPending = true;
            try
            {
                var state = await diagnostics.Read();
                if (closed)
                    return;
                if (state is null)
                {
                    ShowMessage(Strings.Get("Settings.LogDirectoryFailed"), InfoBarSeverity.Warning);
                    return;
                }

                var path = new HyperlinkButton
                {
                    Content = Mono(@"%LOCALAPPDATA%\opssh\logs\"),
                    Padding = new Thickness(0)
                };
                var directoryError = Quiet(Strings.Get("Settings.LogDirectoryFailed"), 12);
                directoryError.Style = StyleFor("DashboardErrorTextStyle");
                directoryError.Visibility = Visibility.Collapsed;
                path.Click += async (_, _) =>
                {
                    if (closed || !path.IsEnabled)
                        return;
                    path.IsEnabled = false;
                    var opened = await diagnostics.OpenDirectory();
                    if (closed)
                        return;
                    path.IsEnabled = true;
                    directoryError.Visibility = opened ? Visibility.Collapsed : Visibility.Visible;
                };
                var details = new StackPanel { Spacing = 8, Children = { path, directoryError } };
                if (state.Files.Count == 0)
                    details.Children.Add(Quiet(Strings.Get("Settings.LogDirectoryEmpty")));
                foreach (var file in state.Files)
                    details.Children.Add(SettingsLayout.Row(Mono(file.Name), Mono(FormatDiagnosticBytes(file.Bytes))));
                await ShowDialogAsync(new ContentDialog
                {
                    Title = Strings.Get("Settings.LogDirectory"),
                    Content = details,
                    CloseButtonText = Strings.Get("Settings.CloseLogDirectory")
                });
            }
            finally
            {
                diagnosticActionPending = false;
                RefreshDiagnosticUsage();
            }
        });

        diagnosticUsage = Mono("—");
        var clear = DashboardButton(Strings.Get("Settings.ClearLogs"), async () =>
        {
            if (closed || diagnosticActionPending)
                return;
            diagnosticActionPending = true;
            clearDiagnosticLogs!.IsEnabled = false;
            try
            {
                var current = await diagnostics.Read();
                if (closed)
                    return;
                if (current is null)
                {
                    ShowMessage(Strings.Get("Settings.ClearLogsFailed"), InfoBarSeverity.Warning);
                    return;
                }
                if (current.Files.Count == 0)
                    return;
                var choice = await ShowDialogAsync(new ContentDialog
                {
                    Title = Strings.Get("Settings.ClearLogsTitle"),
                    Content = Ui.Text(Strings.Get("Settings.ClearLogsDescription", FormatDiagnosticBytes(current.Bytes))),
                    PrimaryButtonText = Strings.Get("Settings.ClearLogs"),
                    CloseButtonText = Strings.Get("Dashboard.Cancel"),
                    DefaultButton = ContentDialogButton.Close
                });
                if (closed || choice != ContentDialogResult.Primary)
                    return;
                var cleared = await diagnostics.Clear();
                if (closed)
                    return;
                ShowMessage(Strings.Get(cleared ? "Settings.LogsCleared" : "Settings.ClearLogsFailed"),
                    cleared ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                if (cleared)
                    open.Focus(FocusState.Programmatic);
            }
            finally
            {
                diagnosticActionPending = false;
                RefreshDiagnosticUsage();
            }
        });
        clear.IsEnabled = false;
        clearDiagnosticLogs = clear;
        var usage = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { diagnosticUsage, clear }
        };
        var section = Section(Strings.Get("Settings.Logs"), Rows(
            SettingRow("\uE838", Strings.Get("Settings.LogDirectory"), Mono(@"logs\"), open),
            SettingRow("\uE8A5", Strings.Get("Settings.EnableLogs"), Strings.Get("Settings.EnableLogsDescription"), enabled),
            SettingRow("\uE71C", Strings.Get("Settings.LogLevel"), levelDescription, level),
            SettingRow("\uEDA2", Strings.Get("Settings.LogUsage"), "", usage)));
#if OPSSH_DASHBOARD_FIXTURE
        diagnosticSection = section;
#endif
        return section;
    }

    internal void RefreshDiagnosticUsage() => _ = RefreshDiagnosticUsageAsync();

    private async Task RefreshDiagnosticUsageAsync()
    {
        if (closed || CurrentPage != "settings" || diagnosticUsage is null || clearDiagnosticLogs is null ||
            actions.Diagnostics is not { } diagnostics)
            return;
        if (diagnosticUsageReading)
        {
            diagnosticUsagePending = true;
            return;
        }
        diagnosticUsageReading = true;
        try
        {
            do
            {
                diagnosticUsagePending = false;
                var state = await diagnostics.Read();
                if (closed || CurrentPage != "settings")
                    return;
                if (!diagnosticUsagePending)
                {
                    diagnosticUsage.Text = state is null ? "—" : FormatDiagnosticBytes(state.Bytes);
                    clearDiagnosticLogs.IsEnabled = !diagnosticActionPending && state is { Files.Count: > 0 };
                }
            }
            while (diagnosticUsagePending);
        }
        finally { diagnosticUsageReading = false; }
    }

    private static string FormatDiagnosticBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => Strings.Get("Settings.LogSizeMB", bytes / (1024.0 * 1024)),
        >= 1024 => Strings.Get("Settings.LogSizeKB", bytes / 1024.0),
        _ => Strings.Get("Settings.LogSizeB", bytes)
    };

#if OPSSH_DASHBOARD_FIXTURE
    internal void RevealDiagnosticSection()
    {
        if (diagnosticSection is not { } section)
            return;
        void Reveal() => DispatcherQueue.TryEnqueue(() =>
        {
            if (!closed && CurrentPage == "settings")
                section.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        });
        if (section.IsLoaded)
            Reveal();
        else
            section.Loaded += OnLoaded;
        void OnLoaded(object sender, RoutedEventArgs args)
        {
            section.Loaded -= OnLoaded;
            Reveal();
        }
    }
#endif
}
