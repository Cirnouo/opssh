using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Opssh.Tray;

public sealed partial class DashboardWindow
{
    private sealed record ActivityRow(string Id, string App, uint Pid, string Key, string Result, DateTimeOffset Time, bool CommandReady);
    private sealed record ActivityBatch(ActivityRow[] Entries, int Total, string? Next);
    private sealed record ActivityCommand(string Command);
    private readonly HashSet<string>[] activityFilters = [new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
    private readonly Dictionary<string, string> activityCommands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> activityCommandLabels = new(StringComparer.Ordinal);
    private readonly Button[] filterButtons = new Button[4];
    private readonly TextBlock[] filterLabels = new TextBlock[4];
    private readonly StackPanel[] filterChoices = new StackPanel[4];
    private static readonly string[] filterKeys = ["Activity.ByApp", "Activity.ByKey", "Activity.ByResult", "Activity.ByDate"];
    private static readonly double[] activityWidths = [56, 14, 11, 19];
    private const double ActivityTableWidth = 960;
    private const double ActivityHeaderHeight = 35;
    private ActivityRow[] activityRows = [];
    private StackPanel? activityBody;
    private ScrollViewer? activityScroll;
    private FrameworkElement? activityTable;
    private FrameworkElement? activityEmpty;
    private TextBlock? activityCount;
    private Button? clearActivity;
    private Button? resetActivityFilters;
    private string? activityAccount;
    private bool activityReading;
    private bool activityClearing;
    private int activityRevision;

    private FrameworkElement BuildActivityPage()
    {
        var page = new Grid { RowSpacing = 16 };
        for (var row = 0; row < 4; row++)
            page.RowDefinitions.Add(new RowDefinition { Height = row == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        var heading = TableHeading("Dashboard.Activity", "Activity.Description", out var headingActions);
        clearActivity = TableButton("Activity.Clear", ConfirmClearActivityAsync, "\uE74D");
        clearActivity.MinHeight = 32;
        headingActions.VerticalAlignment = VerticalAlignment.Center;
        headingActions.Children.Add(clearActivity);
        page.Children.Add(heading);

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        for (var i = 0; i < filterKeys.Length; i++)
        {
            var group = i;
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var button = new Button
            {
                FontSize = 14, MinHeight = 32, Padding = new Thickness(10, 4, 10, 4),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { label, Icon("\uE70D", 12) } }
            };
            filterButtons[group] = button;
            filterLabels[group] = label;
            filterChoices[group] = new StackPanel { MinWidth = 145, MaxWidth = 240, Spacing = 2 };
            var flyout = new Flyout
            {
                Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
                Content = new ScrollViewer { Content = filterChoices[group], MaxHeight = 225 }
            };
            flyout.Opening += (_, _) => PopulateFilter(group);
            button.Flyout = flyout;
            filters.Children.Add(button);
        }
        resetActivityFilters = TableButton("Activity.Reset", () =>
        {
            foreach (var set in activityFilters)
                set.Clear();
            PaintActivity(resetVerticalOffset: true);
            return Task.CompletedTask;
        });
        resetActivityFilters.MinHeight = 32;
        filters.Children.Add(resetActivityFilters);
        var filterScroll = new ScrollViewer { Content = filters, HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(filterScroll, 1);
        page.Children.Add(filterScroll);

        activityCount = Quiet("", 12);
        Grid.SetRow(activityCount, 2);
        page.Children.Add(activityCount);

        activityTable = BuildActivityTable();
        Grid.SetRow(activityTable, 3);
        page.Children.Add(activityTable);

        activityEmpty = EmptyTable("Activity.Empty");
        activityEmpty.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(activityEmpty, 3);
        page.Children.Add(activityEmpty);
        PaintActivity();
        return page;
    }

    private Border BuildActivityTable()
    {
        var body = new StackPanel { Width = ActivityTableWidth, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        body.PointerWheelChanged += (_, args) => ShiftWheel(body, null, args);
        var scroll = new ScrollViewer
        {
            Content = body, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            IsTabStop = true
        };
        AutomationProperties.SetName(scroll, Strings.Get("Activity.Table"));
        activityBody = body;
        activityScroll = scroll;

        var header = TableHeader(activityWidths, "Activity.App", "Keys.Key", "Activity.Result", "Activity.Time");
        var headerOffset = new TranslateTransform();
        header.Width = ActivityTableWidth;
        header.HorizontalAlignment = HorizontalAlignment.Left;
        header.RenderTransform = headerOffset;
        var headerClip = new Grid { Children = { header } };
        headerClip.SizeChanged += (_, args) =>
        {
            var width = args.NewSize.Width;
            headerClip.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, ActivityHeaderHeight) };
            header.Width = body.Width = Math.Max(ActivityTableWidth, width);
        };
        scroll.ViewChanged += (_, _) => headerOffset.X = -scroll.HorizontalOffset;

        var horizontal = ActivityScrollBar(scroll, Orientation.Horizontal);
        var vertical = ActivityScrollBar(scroll, Orientation.Vertical);
        var viewport = new Grid { Children = { headerClip, scroll, horizontal, vertical } };
        viewport.ColumnDefinitions.Add(new ColumnDefinition());
        viewport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        viewport.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ActivityHeaderHeight) });
        viewport.RowDefinitions.Add(new RowDefinition());
        viewport.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(scroll, 1);
        Grid.SetRow(horizontal, 2);
        Grid.SetColumn(vertical, 1);
        Grid.SetRowSpan(vertical, 2);
        viewport.PointerWheelChanged += (_, args) =>
        {
            ShiftWheel(viewport, null, args);
            if (args.Handled || args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
                return;
            var wheel = args.GetCurrentPoint(viewport).Properties;
            args.Handled = wheel.IsHorizontalMouseWheel
                ? MoveHorizontally(scroll, wheel.MouseWheelDelta)
                : scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset - wheel.MouseWheelDelta, 0, scroll.ScrollableHeight), null, disableAnimation: true);
        };
        return Card(viewport);
    }

    private static ScrollBar ActivityScrollBar(ScrollViewer scroll, Orientation orientation, bool showButtons = true)
    {
        var horizontal = orientation == Orientation.Horizontal;
        // The viewer owns scrolling. Separate native bars keep the mouse indicator visible at rest.
        var bar = new ScrollBar { Orientation = orientation, IndicatorMode = ScrollingIndicatorMode.MouseIndicator, SmallChange = 24 };
        if (showButtons)
            bar.Loaded += (_, _) => VisualStateManager.GoToState(bar, "ExpandedWithoutAnimation", false);
        void Synchronize()
        {
            bar.Maximum = horizontal ? scroll.ScrollableWidth : scroll.ScrollableHeight;
            bar.ViewportSize = horizontal ? scroll.ViewportWidth : scroll.ViewportHeight;
            bar.LargeChange = bar.ViewportSize;
            bar.Value = horizontal ? scroll.HorizontalOffset : scroll.VerticalOffset;
        }
        scroll.ViewChanged += (_, _) => Synchronize();
        scroll.RegisterPropertyChangedCallback(horizontal ? ScrollViewer.ScrollableWidthProperty : ScrollViewer.ScrollableHeightProperty, (_, _) => Synchronize());
        scroll.RegisterPropertyChangedCallback(horizontal ? ScrollViewer.ViewportWidthProperty : ScrollViewer.ViewportHeightProperty, (_, _) => Synchronize());
        bar.Scroll += (_, args) =>
        {
            if (horizontal)
                scroll.ChangeView(args.NewValue, null, null, disableAnimation: true);
            else
                scroll.ChangeView(null, args.NewValue, null, disableAnimation: true);
        };
        Synchronize();
        return bar;
    }

    partial void UpdateActivityPage(CoreState? previous, CoreState? current)
    {
        if (!string.Equals(activityAccount, current?.Account, StringComparison.Ordinal))
        {
            activityRevision++;
            activityAccount = current?.Account;
            activityRows = [];
            activityCommands.Clear();
            foreach (var set in activityFilters)
                set.Clear();
            PaintActivity();
        }
        if (CurrentPage == "activity" && connected && HasAccount && !busy && !activityClearing)
            _ = ReadActivityAsync();
    }

    private async Task ReadActivityAsync()
    {
        if (closed || activityReading)
            return;
        activityReading = true;
        var revision = activityRevision;
        try
        {
            var rows = new List<ActivityRow>(200);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? before = null;
            for (var page = 0; page < 7; page++)
            {
                var reply = await actions.Request("activity", before is null ? null : new { before }, windowLifetime.Token);
                if (closed || CurrentPage != "activity" || revision != activityRevision || !reply.Ok ||
                    reply.Data?.Deserialize<ActivityBatch>(Protocol.Json) is not { Entries.Length: <= 32, Total: >= 0 and <= 200 } batch)
                    return;
                rows.AddRange(batch.Entries.Where(IsActivityRow));
                before = batch.Next;
                if (string.IsNullOrEmpty(before) || batch.Entries.Length == 0 || rows.Count >= 200)
                    break;
                if (!IsActivityId(before) || !cursors.Add(before))
                    return;
            }
            var entries = rows.DistinctBy(row => row.Id).Take(200).ToArray();
            if (!activityRows.SequenceEqual(entries))
            {
                activityRows = entries;
                var ids = entries.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var id in activityCommands.Keys.Where(id => !ids.Contains(id)).ToArray())
                    activityCommands.Remove(id);
                PaintActivity();
            }
            foreach (var entry in activityRows.Where(row => row.CommandReady && !activityCommands.ContainsKey(row.Id)).Take(12))
            {
                var reply = await actions.Request("activity-command", new { id = entry.Id }, windowLifetime.Token);
                if (closed || CurrentPage != "activity" || revision != activityRevision)
                    return;
                if (reply.Ok && reply.Data?.Deserialize<ActivityCommand>(Protocol.Json) is { Command.Length: <= 32767 } command)
                {
                    activityCommands[entry.Id] = command.Command;
                    if (activityCommandLabels.TryGetValue(entry.Id, out var text))
                        text.Text = CommandText(entry.Id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or OperationCanceledException) { }
        finally { activityReading = false; }
    }

    private static bool IsActivityRow(ActivityRow? row) =>
        row is { Pid: > 0, App.Length: > 0 and <= 512, Key.Length: <= 512 } &&
        IsActivityId(row.Id) && row.Result is "published" or "denied" or "canceled" or "timeout" or "failed" or "unavailable";

    private static bool IsActivityId(string? id) => id is { Length: 26 } && id.All(char.IsAsciiLetterOrDigit);

    private string CommandText(string id) => activityCommands.TryGetValue(id, out var command) && command.Length != 0
        ? command.Replace("\r", "\\r").Replace("\n", "\\n") : "—";

    private async Task ConfirmClearActivityAsync()
    {
        if (activityClearing || activityRows.Length == 0)
            return;
        activityClearing = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = DashboardRoot.XamlRoot,
                Title = Strings.Get("Activity.ConfirmClear"),
                Content = TableText(Strings.Get("Activity.ConfirmDescription")),
                PrimaryButtonText = Strings.Get("Activity.Clear"),
                CloseButtonText = Strings.Get("Activity.Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || closed)
                return;
            activityRevision++;
            var reply = await actions.Request("clear-activity", null, windowLifetime.Token);
            if (closed)
                return;
            if (reply.Ok)
            {
                activityRows = [];
                activityCommands.Clear();
                foreach (var set in activityFilters)
                    set.Clear();
                PaintActivity();
                ShowMessage(Strings.Get("Activity.Cleared"));
            }
            else
                ShowMessage(Strings.Get(CoreFeedback.ResultKey(reply.ErrorCode, reply.Error)), InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (!closed)
                ShowMessage(Strings.Get("Error.ControlRequestFailed"), InfoBarSeverity.Warning);
        }
        finally { activityClearing = false; }
    }

    private void PopulateFilter(int group)
    {
        var panel = filterChoices[group];
        panel.Children.Clear();
        var values = activityRows.Select(row => FilterValue(row, group)).Distinct(StringComparer.OrdinalIgnoreCase);
        var ordered = group == 3 ? values.OrderDescending(StringComparer.Ordinal) : values.Order(StringComparer.OrdinalIgnoreCase);
        foreach (var value in ordered)
        {
            var check = new CheckBox { Content = group == 2 ? ResultLabel(value) : value, IsChecked = activityFilters[group].Contains(value) };
            check.Checked += (_, _) => { activityFilters[group].Add(value); PaintActivity(resetVerticalOffset: true); };
            check.Unchecked += (_, _) => { activityFilters[group].Remove(value); PaintActivity(resetVerticalOffset: true); };
            panel.Children.Add(check);
        }
    }

    private static string FilterValue(ActivityRow row, int group) => group switch
    {
        0 => row.App,
        1 => row.Key.Length == 0 ? "—" : row.Key,
        2 => row.Result,
        _ => row.Time.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    };

    private static string ResultLabel(string result) => Strings.Get(result switch
    {
        "published" => "Activity.Signed",
        "denied" => "Activity.Denied",
        "canceled" => "Activity.Canceled",
        "timeout" => "Activity.Timeout",
        "unavailable" => "Activity.Unavailable",
        _ => "Activity.Failed"
    });

    private void PaintActivity(bool resetVerticalOffset = false)
    {
        if (activityBody is null || activityCount is null || clearActivity is null || resetActivityFilters is null ||
            activityEmpty is not Border { Child: TextBlock emptyText } || activityTable is null)
            return;
        var shown = activityRows.Where(row => activityFilters.Select((set, i) => set.Count == 0 || set.Contains(FilterValue(row, i))).All(match => match)).ToArray();
        activityCount.Text = Strings.Get("Activity.Count", activityRows.Length, shown.Length);
        clearActivity.IsEnabled = activityRows.Length != 0;
        resetActivityFilters.IsEnabled = activityFilters.Any(set => set.Count != 0);
        for (var i = 0; i < filterKeys.Length; i++)
        {
            filterButtons[i].IsEnabled = activityRows.Length != 0;
            var count = activityFilters[i].Count;
            filterLabels[i].Text = Strings.Get(filterKeys[i]) + (count == 0 ? "" : $" ({count})");
            AutomationProperties.SetName(filterButtons[i], filterLabels[i].Text);
        }
        if (resetVerticalOffset)
            activityScroll?.ChangeView(null, 0, null, disableAnimation: true);
        activityTable.Visibility = shown.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        activityEmpty.Visibility = shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        emptyText.Text = Strings.Get(activityRows.Length == 0 ? "Activity.Empty" : "Activity.NoMatches");
        activityCommandLabels.Clear();
        activityBody.Children.Clear();
        foreach (var entry in shown)
        {
            activityBody.Children.Add(Divider());
            var app = TableText(entry.App);
            app.FontWeight = FontWeights.Bold;
            var pid = QuietMono($"PID {entry.Pid}");
            pid.VerticalAlignment = VerticalAlignment.Center;
            var owner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { app, pid } };
            var content = new StackPanel { Spacing = 4, Children = { owner, BuildCommandStrip(entry) } };
            var row = TableCells(activityWidths, content, TableText(FilterValue(entry, 1)),
                TableStatus(ResultLabel(entry.Result), entry.Result == "published" ? "success" : entry.Result is "denied" or "failed" ? "error" : "neutral"),
                QuietMono(LocalTimestamp(entry.Time)));
            row.MinHeight = 69;
            activityBody.Children.Add(row);
        }
    }

    private FrameworkElement BuildCommandStrip(ActivityRow entry)
    {
        var label = QuietMono(CommandText(entry.Id));
        label.Style = null;
        label.FontWeight = FontWeights.SemiBold;
        label.TextWrapping = TextWrapping.NoWrap;
        activityCommandLabels[entry.Id] = label;
        var content = new Grid
        {
            MinHeight = 25, Padding = new Thickness(7, 4, 7, 4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { label }
        };
        var scroll = new ScrollViewer
        {
            Content = content, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        AutomationProperties.SetName(scroll, Strings.Get("Activity.Command"));
        var bar = ActivityScrollBar(scroll, Orientation.Horizontal, showButtons: false);
        var strip = new StackPanel { Children = { scroll, bar } };
        var border = new Border { Child = strip, Style = StyleFor("DashboardCodeStyle"), Padding = new Thickness(0), BorderThickness = new Thickness(0) };
        Microsoft.UI.Dispatching.DispatcherQueueTimer? timer = null;
        var hovering = false;
        void UpdateBar()
        {
            bar.Visibility = scroll.ScrollableWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
            // Reserve room only for overflowing commands. Showing the thumb must not move the rows.
            var show = hovering || timer?.IsRunning == true;
            bar.Opacity = show ? 1 : 0;
            bar.IsHitTestVisible = show;
        }
        border.PointerEntered += (_, _) => { hovering = true; UpdateBar(); };
        border.PointerExited += (_, _) => { hovering = false; UpdateBar(); };
        border.Unloaded += (_, _) => { hovering = false; timer?.Stop(); UpdateBar(); };
        scroll.RegisterPropertyChangedCallback(ScrollViewer.ScrollableWidthProperty, (_, _) => UpdateBar());
        scroll.RegisterPropertyChangedCallback(ScrollViewer.ViewportWidthProperty, (_, _) => content.MinWidth = scroll.ViewportWidth);
        void ShowWheelIndicator()
        {
            if (timer is null)
            {
                timer = DashboardRoot.DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(700);
                timer.IsRepeating = false;
                timer.Tick += (sender, _) => { sender.Stop(); UpdateBar(); };
            }
            timer.Stop();
            timer.Start();
            UpdateBar();
        }
        content.PointerWheelChanged += (_, args) => ShiftWheel(content, scroll, args, ShowWheelIndicator);
        border.PointerWheelChanged += (_, args) => ShiftWheel(border, scroll, args, ShowWheelIndicator);
        UpdateBar();
        return border;
    }

    private void ShiftWheel(UIElement source, ScrollViewer? command, PointerRoutedEventArgs args, Action? moved = null)
    {
        if (args.Handled || !args.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift) || args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
            return;
        var wheel = args.GetCurrentPoint(source).Properties;
        if (wheel.MouseWheelDelta == 0)
            return;
        args.Handled = true;
        var delta = wheel.IsHorizontalMouseWheel ? wheel.MouseWheelDelta : -wheel.MouseWheelDelta;
        if (ActivityWheel.Route(delta,
            command is null ? null : value => MoveHorizontally(command, value),
            value => activityScroll is { } table && MoveHorizontally(table, value)))
            moved?.Invoke();
    }

    private static bool MoveHorizontally(ScrollViewer target, int delta) =>
        ActivityWheel.Move(delta, target.HorizontalOffset, target.ScrollableWidth,
            next => target.ChangeView(next, null, null, disableAnimation: true));
}
