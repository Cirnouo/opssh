using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Opssh.Tray;

public sealed partial class DashboardWindow
{
    private enum AgentStepState { Running, Done, Skipped, Failed, Pending }

    private sealed record AgentStep(DefaultAgentStage Stage, Border Indicator, TextBlock Status, TextBlock Detail);

    internal void EnableDefaultAgentAfterInstall()
    {
        if (DashboardRoot.IsLoaded)
            _ = ChangeDefaultAgentAsync(true, confirmed: true);
        else
            DashboardRoot.Loaded += Apply;
        void Apply(object sender, RoutedEventArgs args)
        {
            DashboardRoot.Loaded -= Apply;
            _ = ChangeDefaultAgentAsync(true, confirmed: true);
        }
    }

    private async Task<bool> ChangeDefaultAgentAsync(bool enable, bool confirmed = false)
    {
        if (closed || changingIntegration || activeDialog is not null)
            return false;
        changingIntegration = true;
        RefreshSystemSettings();
        UpdateSettingsAvailability();

        var steps = new StackPanel();
        var summary = new InfoBar { IsClosable = false };
        var dialog = new ContentDialog
        {
            Title = Strings.Get(enable ? "Dashboard.EnableDefaultTitle" : "Dashboard.DisableDefaultTitle"),
            Content = Ui.Text(Strings.Get(enable ? "Dashboard.EnableDefaultDescription" : "Dashboard.DisableDefaultDescription")),
            PrimaryButtonText = Strings.Get(enable ? "Dashboard.EnableDefaultAction" : "Dashboard.DisableDefaultAction"),
            CloseButtonText = Strings.Get("Dashboard.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogSeparatorThickness"] = new Thickness(0);
        var token = windowLifetime.Token;
        var change = Task.CompletedTask;
        var running = false;
        var succeeded = false;
        var serviceAbsent = false;
        DefaultAgentStage? lastStage = null;
        AgentStep? lastStep = null;

        dialog.Closing += (_, args) => args.Cancel = running && !closed;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            StartChange();
        };
        if (confirmed)
            dialog.Opened += (_, _) => StartChange();

        void StartChange()
        {
            if (closed || running)
                return;
            running = true;
            dialog.Title = Strings.Get(enable ? "Dashboard.EnableDefaultProgress" : "Dashboard.DisableDefaultProgress");
            dialog.Content = new StackPanel { Spacing = 12, Children = { steps, summary } };
            dialog.DefaultButton = ContentDialogButton.None;
            dialog.PrimaryButtonText = "";
            dialog.CloseButtonText = "";
            change = RunChangeAsync();
        }

        try
        {
            await ShowDialogAsync(dialog);
            await change;
        }
        catch (OperationCanceledException) when (closed) { }
        finally
        {
            changingIntegration = false;
            RefreshSystemSettings();
            UpdateSettingsAvailability();
        }
        return succeeded;

        async Task RunChangeAsync()
        {
            string? key;
            try { key = await actions.SetDefaultAgent(enable, Report, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { key = "Error.Canceled"; }
            finally { running = false; }
            if (closed)
                return;
            succeeded = key is null;

            if (lastStep is not null)
            {
                var completed = key is null || lastStage == DefaultAgentStage.RollingBackAfterFailure &&
                    (key is "Startup.DefaultChangeFailed" or "Error.Canceled");
                SetAgentStepState(lastStep, completed ? AgentStepState.Done :
                    key == "Startup.DefaultRecovery" ? AgentStepState.Pending : AgentStepState.Failed);
            }
            RefreshSystemSettings();
            summary.Severity = key is null ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
            if (key == "Startup.DefaultChangeFailed" && lastStage != DefaultAgentStage.RollingBackAfterFailure)
                key = "Dashboard.DefaultChangeFailed";
            summary.Message = Strings.Get(key ?? (enable ? "Dashboard.DefaultEnabled" :
                serviceAbsent ? "Dashboard.DefaultRestoredWithoutService" : "Dashboard.DefaultRestored"));
            summary.IsOpen = true;
            dialog.CloseButtonText = Strings.Get("Dashboard.DefaultClose");
            dialog.DefaultButton = ContentDialogButton.Close;
        }

        void Report(DefaultAgentStage stage)
        {
            if (closed)
                return;
            if (lastStep is not null)
            {
                SetAgentStepState(lastStep, stage == DefaultAgentStage.RollingBackAfterFailure
                    ? AgentStepState.Failed : AgentStepState.Done);
                lastStep = null;
            }
            if (stage == DefaultAgentStage.OpenSshAbsent)
            {
                serviceAbsent = true;
                AddStep(enable ? DefaultAgentStage.StoppingOpenSsh : DefaultAgentStage.ConfiguringOpenSsh,
                    AgentStepState.Skipped);
                AddStep(enable ? DefaultAgentStage.DisablingOpenSsh : DefaultAgentStage.StartingOpenSsh,
                    AgentStepState.Skipped);
                return;
            }
            lastStage = stage;
            lastStep = AddStep(stage, AgentStepState.Running);
        }

        AgentStep AddStep(DefaultAgentStage stage, AgentStepState state)
        {
            var key = stage switch
            {
                DefaultAgentStage.Checking => enable ? "Dashboard.DefaultCheck" : "Dashboard.DefaultCheckRestore",
                DefaultAgentStage.StoppingOpenSsh => "Dashboard.DefaultStop",
                DefaultAgentStage.DisablingOpenSsh => "Dashboard.DefaultDisable",
                DefaultAgentStage.ReservingPipe => "Dashboard.DefaultReservePipe",
                DefaultAgentStage.ReleasingPipe => "Dashboard.DefaultReleasePipe",
                DefaultAgentStage.ConfiguringOpenSsh => "Dashboard.DefaultConfigure",
                DefaultAgentStage.StartingOpenSsh => "Dashboard.DefaultStart",
                DefaultAgentStage.Saving => "Dashboard.DefaultSave",
                _ => "Dashboard.DefaultRollback"
            };
            var title = Strings.Get(key);
            var heading = Ui.Heading(title, 14);
            heading.TextWrapping = TextWrapping.Wrap;
            var detail = Quiet("");
            var label = new StackPanel { Children = { heading, detail } };

            var indicator = new Border
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top
            };
            var status = Quiet("", 12);
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(status, title);
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 8, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 1);
            Grid.SetColumn(status, 2);
            row.Children.Add(indicator);
            row.Children.Add(label);
            row.Children.Add(status);
            steps.Children.Add(row);
            var step = new AgentStep(stage, indicator, status, detail);
            SetAgentStepState(step, state);
            return step;
        }
    }

    private void SetAgentStepState(AgentStep step, AgentStepState state)
    {
        var (key, glyph, style) = state switch
        {
            AgentStepState.Running => ("Dashboard.DefaultRunning", "", null),
            AgentStepState.Done => ("Dashboard.DefaultDone", "\uE73E", "DashboardSuccessIconStyle"),
            AgentStepState.Skipped => ("Dashboard.DefaultSkipped", "\uE738", null),
            AgentStepState.Failed => ("Dashboard.DefaultFailed", "\uE711", "DashboardErrorIconStyle"),
            _ => ("Dashboard.DefaultPending", "\uE7BA", "DashboardWarningIconStyle")
        };
        step.Status.Text = Strings.Get(key);
        var detailKey = state switch
        {
            AgentStepState.Skipped => "Dashboard.DefaultServiceAbsent",
            AgentStepState.Pending => "Dashboard.DefaultPendingDetail",
            AgentStepState.Failed when step.Stage is DefaultAgentStage.ReservingPipe or DefaultAgentStage.ReleasingPipe =>
                "Dashboard.DefaultPipeFailedDetail",
            AgentStepState.Done => step.Stage switch
            {
                DefaultAgentStage.Checking => "Dashboard.DefaultCheckedDetail",
                DefaultAgentStage.StoppingOpenSsh => "Dashboard.DefaultStoppedDetail",
                DefaultAgentStage.DisablingOpenSsh => "Dashboard.DefaultDisabledDetail",
                DefaultAgentStage.ReservingPipe => "Dashboard.DefaultReservedDetail",
                DefaultAgentStage.ReleasingPipe => "Dashboard.DefaultReleasedDetail",
                DefaultAgentStage.ConfiguringOpenSsh => "Dashboard.DefaultConfiguredDetail",
                DefaultAgentStage.StartingOpenSsh => "Dashboard.DefaultStartedDetail",
                DefaultAgentStage.Saving => "Dashboard.DefaultSavedDetail",
                _ => "Dashboard.DefaultRollbackDetail"
            },
            _ => null
        };
        step.Detail.Text = detailKey is null ? "" : Strings.Get(detailKey);
        step.Detail.Visibility = detailKey is null ? Visibility.Collapsed : Visibility.Visible;
        if (step.Indicator.Child is ProgressRing previous)
            previous.IsActive = false;
        UIElement indicator = state == AgentStepState.Running
            ? new ProgressRing { Width = 14, Height = 14, IsActive = true }
            : new FontIcon
            {
                Glyph = glyph, FontSize = 14, Style = style is null ? null : StyleFor(style),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        AutomationProperties.SetAccessibilityView(indicator, AccessibilityView.Raw);
        step.Indicator.Child = indicator;
    }
}
