using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Opssh.Tray;

public sealed partial class DashboardWindow
{
    private TextBlock? diagnosticUsage;
    private Button? openLogDirectory;
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

        var open = KeysButton("Settings.OpenLogDirectory", async () =>
        {
            if (closed || diagnosticActionPending)
                return;
            diagnosticActionPending = true;
            openLogDirectory!.IsEnabled = false;
            clearDiagnosticLogs!.IsEnabled = false;
            try
            {
                if (!await diagnostics.OpenDirectory() && !closed)
                    ShowMessage(Strings.Get("Settings.LogDirectoryFailed"), InfoBarSeverity.Warning);
            }
            finally
            {
                diagnosticActionPending = false;
                if (!closed)
                    openLogDirectory.IsEnabled = true;
                RefreshDiagnosticUsage();
            }
        }, "\uE8A7");
        openLogDirectory = open;

        diagnosticUsage = Mono("—", 14);
        diagnosticUsage.FontFamily = new FontFamily("Consolas");
        diagnosticUsage.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        diagnosticUsage.TextWrapping = TextWrapping.NoWrap;
        diagnosticUsage.VerticalAlignment = VerticalAlignment.Center;
        var clear = KeysButton("Settings.ClearLogs", async () =>
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
        }, "\uE74D");
        clear.IsEnabled = false;
        clearDiagnosticLogs = clear;
        var usage = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { diagnosticUsage, clear }
        };
        var usageRow = SettingRow("\uEDA2", Strings.Get("Settings.LogUsage"), "", usage);
        var section = Section(Strings.Get("Settings.Logs"), Cards(
            SettingRow("\uE838", Strings.Get("Settings.LogDirectory"), Mono(@"logs\"), open),
            SettingRow("\uE8A5", Strings.Get("Settings.EnableLogs"), Strings.Get("Settings.EnableLogsDescription"), enabled),
            SettingRow("\uE71C", Strings.Get("Settings.LogLevel"), levelDescription, level),
            usageRow));
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
