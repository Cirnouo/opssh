using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Opssh.Tray;

public sealed partial class DashboardWindow
{
    private const double CompactKeyTableWidth = 590;
    private const double CompactGrantTableWidth = 475;

    private sealed record GrantRow(string Id, string Name, uint Pid, string Scope, DateTimeOffset? ExpiresAt);
    private sealed record GrantList(GrantRow[] Grants);
    private readonly HashSet<string> expandedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GrantRow[]> keyGrants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> grantHosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Button>> grantActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KeysTable> grantTables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Button Load, Button Evict, Button Revoke, Border LoadStatus, TextBlock? Failure)> keyActions = new(StringComparer.Ordinal);
    private readonly HashSet<string> fetchingGrants = new(StringComparer.Ordinal);
    private StackPanel? keysBody;
    private TextBlock? keysHeading;
    private TextBlock? keysCount;
    private TextBlock? keysNotice;
    private Button? discoverKeys;
    private Button? cancelDiscovery;
    private Border? discoveryCard;
    private TextBlock? discoveryPhase;
    private TextBlock? discoveryCount;
    private ProgressBar? discoveryBar;
    private string? discoveryId;
    private bool discoveryCancelRequested;
    private Button? preloadKeys;
    private Button? evictKeys;
    private Button? revokeKeys;
    private string? keysAccount;
    private bool preloading;
    private string? preloadingKey;

    private FrameworkElement BuildKeysPage()
    {
        var title = TableHeading("Dashboard.Keys", "Keys.Description", out var headingActions, keyPage: true);
        discoverKeys = KeysButton("Keys.Discover", DiscoverKeysAsync, "\uE721", primary: true);
        cancelDiscovery = KeysButton("Keys.CancelDiscovery", CancelDiscoveryAsync);
        cancelDiscovery.Visibility = Visibility.Collapsed;
        headingActions.VerticalAlignment = VerticalAlignment.Top;
        headingActions.Margin = new Thickness(0, 4, 0, 0);
        headingActions.Children.Add(discoverKeys);
        headingActions.Children.Add(cancelDiscovery);
        discoveryPhase = Ui.Heading(Strings.Get("Keys.ScanVaults"), 14);
        AutomationProperties.SetLiveSetting(discoveryPhase, AutomationLiveSetting.Polite);
        discoveryCount = Quiet("");
        discoveryCount.HorizontalAlignment = HorizontalAlignment.Right;
        var progressHeader = new Grid { Children = { discoveryPhase, discoveryCount } };
        discoveryBar = new ProgressBar { IsIndeterminate = true };
        discoveryCard = Card(new StackPanel
        {
            Padding = new Thickness(13, 10, 13, 10),
            Spacing = 8,
            Children = { progressHeader, discoveryBar }
        });
        discoveryCard.Visibility = Visibility.Collapsed;
        keysNotice = Quiet("", 13);
        keysCount = Quiet("");
        preloadKeys = KeysButton("Keys.PreloadAll", () => PreloadAsync(null), "\uE896");
        evictKeys = KeysButton("Keys.EvictAll", async () => await sendUrgent("evict-keys", null), "\uE74D");
        revokeKeys = KeysButton("Keys.RevokeAll", async () => await sendUrgent("revoke", null), "\uE72E");
        var actionsRow = new KeysWrapPanel(7) { Children = { preloadKeys, evictKeys, revokeKeys } };
        keysBody = new StackPanel();
        void SurfaceSizeChanged(object sender, SizeChangedEventArgs args) => ResizeKeysTables();
        PageSurface.SizeChanged += SurfaceSizeChanged;
        Closed += (_, _) => PageSurface.SizeChanged -= SurfaceSizeChanged;
        return new StackPanel { Spacing = 11, Children = { title, discoveryCard, keysNotice, keysCount, actionsRow, keysBody } };
    }

    private async Task DiscoverKeysAsync()
    {
        if (closed || !Ready || discoveryId is not null)
            return;
        var operationId = Guid.NewGuid().ToString("N");
        discoveryId = operationId;
        discoveryCancelRequested = false;
        using var pollLifetime = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        discoverKeys!.Visibility = Visibility.Collapsed;
        cancelDiscovery!.Visibility = Visibility.Visible;
        cancelDiscovery.IsEnabled = false;
        discoveryCard!.Visibility = Visibility.Visible;
        ShowDiscoveryProgress("vaults", 0, 0);
        ShowMessage("");
        var command = send("discover", new { operationId }, false);
        var poll = TrackDiscoveryAsync(operationId, pollLifetime.Token);
        try
        {
            await command;
        }
        finally
        {
            pollLifetime.Cancel();
            await poll;
            discoveryId = null;
            if (!closed)
            {
                discoveryCard.Visibility = Visibility.Collapsed;
                discoverKeys.Visibility = Visibility.Visible;
                cancelDiscovery.Visibility = Visibility.Collapsed;
                UpdateKeysAvailability();
                if (CurrentPage == "keys")
                    discoverKeys.Focus(FocusState.Programmatic);
            }
        }
    }

    private async Task TrackDiscoveryAsync(string operationId, CancellationToken token)
    {
        if (actions.DiscoveryControl is not { } control)
            return;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var reply = await control("discovery-progress", new { operationId });
                if (!token.IsCancellationRequested && reply.Ok && reply.Data is { ValueKind: JsonValueKind.Object } data &&
                    data.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String &&
                    phase.GetString() is "vaults" or "public" &&
                    data.TryGetProperty("completed", out var done) && done.ValueKind == JsonValueKind.Number && done.TryGetInt32(out var completed) &&
                    data.TryGetProperty("total", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var total) &&
                    completed >= 0 && total >= completed && total <= 256)
                {
                    ShowDiscoveryProgress(phase.GetString()!, completed, total);
                    cancelDiscovery!.IsEnabled = !discoveryCancelRequested;
                }
                await Task.Delay(180, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ShowDiscoveryProgress(string phase, int completed, int total)
    {
        var scanning = phase == "vaults";
        discoveryPhase!.Text = Strings.Get(scanning ? "Keys.ScanVaults" : "Keys.ReadPublic");
        discoveryCount!.Text = scanning && total > 0 ? $"{completed} / {total}" : "";
        discoveryBar!.IsIndeterminate = !scanning || total == 0;
        discoveryBar.Maximum = Math.Max(total, 1);
        discoveryBar.Value = completed;
    }

    private async Task CancelDiscoveryAsync()
    {
        if (closed || discoveryCancelRequested || discoveryId is not { } operationId || actions.DiscoveryControl is not { } control)
            return;
        discoveryCancelRequested = true;
        cancelDiscovery!.IsEnabled = false;
        var reply = await control("cancel-discovery", new { operationId });
        if (!closed && discoveryId == operationId && !reply.Ok && reply.ErrorCode != "discovery-not-active")
        {
            discoveryCancelRequested = false;
            cancelDiscovery.IsEnabled = true;
            ShowMessage(Strings.Get("Keys.DiscoveryCancelRetry"), InfoBarSeverity.Warning);
        }
    }

    partial void UpdateKeysPage(CoreState? previous, CoreState? current)
    {
        if (keysBody is null || keysCount is null || keysNotice is null || discoverKeys is null ||
            preloadKeys is null || evictKeys is null || revokeKeys is null)
            return;
        if (!string.Equals(keysAccount, current?.Account, StringComparison.Ordinal))
        {
            expandedKeys.Clear();
            keyGrants.Clear();
            keysAccount = current?.Account;
        }
        var keys = current?.Keys ?? [];
        var indexed = keys.ToDictionary(key => key.Id, StringComparer.Ordinal);
        expandedKeys.RemoveWhere(id => !indexed.ContainsKey(id));
        foreach (var id in keyGrants.Keys.ToArray())
        {
            var old = previous?.Keys.FirstOrDefault(key => key.Id == id);
            if (!indexed.TryGetValue(id, out var key) || key.Grants == 0 ||
                old is null || old.Fingerprint != key.Fingerprint || old.Grants != key.Grants)
                keyGrants.Remove(id);
        }
        var scope = current?.Scope;
        var mayDiscover = scope is not null && (scope.Default == "include" ||
            scope.Vaults.Values.Contains("include") || scope.Items.Values.Contains("include"));
        var keyCache = current?.KeyCache.Mode != "off";
        discoverKeys.IsEnabled = Ready && mayDiscover;
        preloadKeys.IsEnabled = Ready && !preloading && keyCache && keys.Any(key => !key.Loaded && key.Fingerprint.Length != 0);
        evictKeys.IsEnabled = keys.Any(CanEvict);
        revokeKeys.IsEnabled = connected && !urgentBusy && keys.Any(key => key.Grants != 0);
        keysCount.Text = Strings.Get("Keys.Count", keys.Length, keys.Sum(key => key.Grants));
        keysNotice.Text = !mayDiscover ? Strings.Get("Keys.ChooseVaults") : !keyCache ? Strings.Get("Keys.NoCache") : "";
        keysNotice.Visibility = keysNotice.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (previous is null || current is null || previous.Account != current.Account ||
            previous.KeyCache != current.KeyCache || previous.Index != current.Index ||
            !previous.Keys.SequenceEqual(keys) ||
            !(previous.Vaults ?? []).Select(v => (v.Id, v.Name)).SequenceEqual((current.Vaults ?? []).Select(v => (v.Id, v.Name))))
            RenderKeys(current);
        if (CurrentPage == "keys")
            foreach (var key in keys.Where(key => expandedKeys.Contains(key.Id) && key.Grants != 0))
                _ = RefreshGrantsAsync(key.Id);
    }

    private void RenderKeys(CoreState? current)
    {
        if (keysBody is null)
            return;
        keysBody.Children.Clear();
        grantHosts.Clear();
        grantActions.Clear();
        grantTables.Clear();
        keyActions.Clear();
        if (current is null || current.Keys.Length == 0)
        {
            keysBody.Children.Add(EmptyTable(current?.Index is "ready" or "partial" ? "Keys.NoneFound" : "Keys.BeforeDiscovery"));
            return;
        }
        string[] columns = ["Keys.Key", "Keys.Loading", "Keys.Grants", "Keys.Actions"];
        var header = TableHeader([1, 1, 1, 1], columns);
        var content = new StackPanel { Children = { header } };
        var rows = new List<KeyListRow>();
        var actionWidth = 300.0;
        var vaultNames = (current.Vaults ?? []).ToDictionary(vault => vault.Id, vault => vault.Name, StringComparer.Ordinal);
        foreach (var key in current.Keys)
        {
            content.Children.Add(Divider());
            var name = TableText(key.Name);
            name.FontWeight = FontWeights.SemiBold;
            var keyText = new StackPanel { Spacing = 2, Children = { name } };
            if (vaultNames.TryGetValue(key.VaultId, out var vault))
                keyText.Children.Add(Quiet(vault));
            var fingerprint = QuietMono(key.Fingerprint.Length == 0 ? "—" : key.Fingerprint, 11);
            fingerprint.FontFamily = new FontFamily("Consolas");
            fingerprint.LineHeight = 16;
            fingerprint.LineStackingStrategy = LineStackingStrategy.BaselineToBaseline;
            keyText.Children.Add(fingerprint);
            var expanded = expandedKeys.Contains(key.Id);
            var expand = KeysButton("Keys.Expand", async () =>
            {
                if (!expandedKeys.Add(key.Id))
                    expandedKeys.Remove(key.Id);
                RenderKeys(displayedState);
                if (expandedKeys.Contains(key.Id))
                    await RefreshGrantsAsync(key.Id);
            }, expanded ? "\uE70D" : "\uE76C", compact: true, text: Strings.Get("Keys.GrantCount", key.Grants));
            AutomationProperties.SetName(expand, Strings.Get("Keys.ExpandFor", key.Name, key.Grants));
            var load = KeysButton(key.Error is null or "" || key.Loaded ? "Keys.Preload" : "Keys.RetryPreload",
                () => PreloadAsync(key.Id), compact: true);
            var evict = KeysButton("Keys.Evict", async () => await sendUrgent("evict-keys", new { keyId = key.Id }), compact: true);
            var revoke = KeysButton("Keys.RevokeKey", async () => await sendUrgent("revoke", new { keyId = key.Id }), compact: true);
            var status = new Border { Child = KeyLoadStatus(key) };
            var failure = key.Error is { Length: > 0 } code ? Quiet(Strings.Get(CoreFeedback.ResultKey(code, null)), 12) : null;
            keyActions[key.Id] = (load, evict, revoke, status, failure);
            load.IsEnabled = Ready && !preloading && current.KeyCache.Mode != "off" && !key.Loaded && !key.Loading && key.Fingerprint.Length > 0;
            evict.IsEnabled = CanEvict(key);
            revoke.IsEnabled = connected && !urgentBusy && key.Grants != 0;
            var buttons = new KeysWrapPanel(5) { Children = { load, evict, revoke } };
            if (Strings.Language != "zh-Hans")
            {
                buttons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                actionWidth = Math.Max(actionWidth, buttons.DesiredSize.Width);
            }
            var row = new KeyListRow(columns.Skip(1).Select(column => Quiet(Strings.Get(column), 11)).ToArray(),
                [keyText, status, expand, buttons]);
            rows.Add(row);
            content.Children.Add(row);
            if (failure is not null)
            {
                failure.Margin = new Thickness(14, 0, 14, 11);
                failure.Style = StyleFor("DashboardErrorTextStyle");
                AutomationProperties.SetLiveSetting(failure, AutomationLiveSetting.Polite);
                UpdatePreloadFailure(key, failure);
                content.Children.Add(failure);
            }
            if (!expanded)
                continue;
            content.Children.Add(Divider());
            var grantHost = new Border { Padding = new Thickness(11, 7, 11, 8) };
            grantHosts[key.Id] = grantHost;
            PaintGrants(key.Id, key.Grants);
            content.Children.Add(grantHost);
        }
        ConfigureKeyColumns(header, actionWidth);
        header.Padding = new Thickness(14, 10, 14, 10);
        foreach (var cell in header.Children.OfType<Border>())
            cell.Padding = new Thickness(0);

        bool? compact = null;
        bool? narrow = null;
        void Resize(double width)
        {
            var nextCompact = width < 188 + 106 + 84 + actionWidth + 3 * 12 + 2 * 14;
            var nextNarrow = width < 490;
            if (compact == nextCompact && narrow == nextNarrow)
                return;
            compact = nextCompact;
            narrow = nextNarrow;
            header.Visibility = nextCompact ? Visibility.Collapsed : Visibility.Visible;
            content.Children[1].Visibility = header.Visibility;
            foreach (var row in rows)
                row.SetLayout(nextCompact, nextNarrow, actionWidth);
        }
        content.SizeChanged += (_, args) => Resize(args.NewSize.Width);
        Resize(Math.Max(0, keysBody.ActualWidth - 2));
        ResizeKeysTables();
        keysBody.Children.Add(Card(content));
    }

    private async Task RefreshGrantsAsync(string keyId)
    {
        var key = displayedState?.Keys.FirstOrDefault(key => key.Id == keyId);
        if (closed || !connected || key is not { Grants: > 0 } ||
            !expandedKeys.Contains(keyId) || !fetchingGrants.Add(keyId))
            return;
        var account = keysAccount;
        try
        {
            var reply = await actions.Request("grants", new { keyId }, windowLifetime.Token);
            var latest = displayedState?.Keys.FirstOrDefault(key => key.Id == keyId);
            if (closed || account != keysAccount || latest is null || latest.Fingerprint != key.Fingerprint || latest.Grants != key.Grants ||
                !expandedKeys.Contains(keyId) || !reply.Ok ||
                reply.Data?.Deserialize<GrantList>(Protocol.Json) is not { Grants.Length: <= 64 } data)
                return;
            if (keyGrants.TryGetValue(keyId, out var existing) && existing.SequenceEqual(data.Grants))
                return;
            keyGrants[keyId] = data.Grants;
            PaintGrants(keyId, data.Grants.Length);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException) { }
        finally { fetchingGrants.Remove(keyId); }
    }

    private async Task PreloadAsync(string? keyId)
    {
        if (preloading || !Ready)
            return;
        preloading = true;
        preloadingKey = keyId;
        UpdateKeyLoadStatuses();
        UpdateKeysAvailability();
        try
        {
            if (keyId is null)
                await send("preload-all", null, false);
            else
                await send("preload", new { keyId }, false);
        }
        finally
        {
            preloading = false;
            preloadingKey = null;
            if (!closed)
            {
                UpdateKeyLoadStatuses();
                UpdateKeysAvailability();
            }
        }
    }

    private void UpdateKeyLoadStatuses()
    {
        foreach (var key in displayedState?.Keys ?? [])
            if (keyActions.TryGetValue(key.Id, out var row))
            {
                row.LoadStatus.Child = KeyLoadStatus(key);
                if (row.Failure is { } failure)
                    UpdatePreloadFailure(key, failure);
            }
    }

    private void UpdatePreloadFailure(PublicKey key, TextBlock failure) =>
        failure.Visibility = key.Loaded || key.Loading || IsPreloading(key) ? Visibility.Collapsed : Visibility.Visible;

    private FrameworkElement KeyLoadStatus(PublicKey key)
    {
        if (key.Loaded)
            return TableStatus(Strings.Get("Keys.Loaded"), "success", wrap: true);
        if (key.Loading || IsPreloading(key))
            return TableStatus(Strings.Get("Keys.LoadingNow"), "neutral", wrap: true);
        if (key.Error is { Length: > 0 })
            return TableStatus(Strings.Get("Keys.LoadFailed"), "error", wrap: true);
        return TableStatus(Strings.Get("Keys.NotLoaded"), "neutral", wrap: true);
    }

    private bool IsPreloading(PublicKey key) =>
        preloading && (preloadingKey is null || preloadingKey == key.Id);

    private bool CanEvict(PublicKey key) => connected && !urgentBusy &&
        (key.Loaded || key.Loading || IsPreloading(key));

    private void PaintGrants(string keyId, int count)
    {
        if (!grantHosts.TryGetValue(keyId, out var host))
            return;
        grantActions.Remove(keyId);
        if (count == 0 || !keyGrants.TryGetValue(keyId, out var rows) || rows.Length == 0)
        {
            grantTables.Remove(keyId);
            host.Child = EmptyTable(count == 0 ? "Keys.NoGrants" : "Keys.LoadingGrants");
            return;
        }
        var widths = new[] { 32.0, 13, 30, 25 };
        string[] columns = ["Keys.Process", "Keys.Pid", "Keys.Expires", "Keys.Actions"];
        var table = new KeysTable(TableHeader(widths, columns));
        var buttons = new List<Button>();
        foreach (var grant in rows)
        {
            if (grant is null || grant.Id is not { Length: 26 } || !grant.Id.All(char.IsAsciiLetterOrDigit) ||
                grant is not ({ Scope: "all", Pid: 0 } or { Scope: "app", Pid: > 0, Name.Length: > 0 and <= 512 }))
                continue;
            var name = grant.Scope == "all" ? Strings.Get("Keys.AllApps") : grant.Name;
            var pid = grant.Pid == 0 ? "—" : grant.Pid.ToString(CultureInfo.InvariantCulture);
            var expiry = grant.ExpiresAt is { } expires ? LocalTimestamp(expires) :
                Strings.Get(grant.Scope == "all" ? "Keys.UntilExit" : "Keys.UntilAppExit");
            table.Content.Children.Add(Divider());
            var button = KeysButton("Keys.RevokeGrant",
                async () => await sendUrgent("revoke", new { keyId, grantId = grant.Id }), compact: true);
            button.IsEnabled = connected && !urgentBusy;
            buttons.Add(button);
            table.Content.Children.Add(KeysRow(widths, columns, 0, QuietMono(name), QuietMono(pid),
                QuietMono(expiry), button));
        }
        grantTables[keyId] = table;
        grantActions[keyId] = buttons;
        var width = KeysLayoutWidth;
        table.SetLayout(width <= CompactGrantTableWidth, width <= CompactKeyTableWidth);
        host.Child = Card(table.Content);
    }

    private double KeysLayoutWidth => PageSurface.ActualWidth - PageSurface.BorderThickness.Left - PageSurface.BorderThickness.Right;

    private void ResizeKeysTables()
    {
        var width = KeysLayoutWidth;
        var narrow = width <= CompactKeyTableWidth;
        foreach (var table in grantTables.Values)
            table.SetLayout(width <= CompactGrantTableWidth, narrow);
    }

    private KeysTableRow KeysRow(double[] widths, string[] columns, double wideMinHeight, params FrameworkElement[] cells) =>
        new(widths, columns.Skip(1).Select(column => Quiet(Strings.Get(column), 11)).ToArray(), wideMinHeight, cells);

    private Button KeysButton(string key, Func<Task> execute, string? glyph = null, bool primary = false, bool compact = false,
        string? text = null)
    {
        var button = TableButton(key, execute, primary: primary, compact: compact);
        var label = TableText(text ?? Strings.Get(key));
        label.FontSize = button.FontSize;
        button.Content = glyph is null ? label : IconLabel(Icon(glyph, 14), label);
        button.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(button, label.Text);
        return button;
    }

    private static Grid IconLabel(FrameworkElement icon, TextBlock label)
    {
        var grid = new Grid { ColumnSpacing = 7, Children = { icon, label } };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(label, 1);
        label.VerticalAlignment = VerticalAlignment.Center;
        return grid;
    }

    private Grid TableHeading(string titleKey, string descriptionKey, out StackPanel actionsPanel, bool keyPage = false)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = Heading(Strings.Get(titleKey), keyPage ? 30 : 28, AutomationHeadingLevel.Level1);
        if (keyPage)
            keysHeading = heading;
        var title = new StackPanel { Spacing = keyPage ? 4 : 5, Children = { heading, Quiet(Strings.Get(descriptionKey)) } };
        actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        grid.Children.Add(title);
        Grid.SetColumn(actionsPanel, 1);
        grid.Children.Add(actionsPanel);
        var actions = actionsPanel;
        grid.SizeChanged += (_, args) =>
        {
            var stacked = args.NewSize.Width < 330;
            grid.RowSpacing = stacked ? 6 : 0;
            Grid.SetColumnSpan(title, stacked ? 2 : 1);
            Grid.SetColumn(actions, stacked ? 0 : 1);
            Grid.SetRow(actions, stacked ? 1 : 0);
        };
        return grid;
    }

    private Button TableButton(string key, Func<Task> execute, string? glyph = null, bool primary = false, bool compact = false)
    {
        var button = DashboardButton(Strings.Get(key), async () => await execute());
        if (primary)
            button.Style = StyleFor("DashboardAccentButtonStyle");
        button.FontSize = compact ? 12 : 14;
        button.MinHeight = compact ? 29 : 34;
        button.Padding = compact ? new Thickness(8, 3, 8, 3) : new Thickness(10, 4, 10, 4);
        if (glyph is not null)
        {
            AutomationProperties.SetName(button, Strings.Get(key));
            button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7,
                Children = { Icon(glyph, 14), new TextBlock { Text = Strings.Get(key) } } };
        }
        return button;
    }

    private Grid TableHeader(double[] widths, params string[] keys)
    {
        var cells = keys.Select(key =>
        {
            var value = Quiet(Strings.Get(key), 12);
            value.FontWeight = FontWeights.SemiBold;
            return value;
        }).ToArray();
        var header = TableCells(widths, cells);
        header.Style = StyleFor("DashboardTableHeaderStyle");
        return header;
    }

    private static Grid TableCells(double[] widths, params FrameworkElement[] cells)
    {
        var row = new Grid();
        for (var i = 0; i < widths.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i], GridUnitType.Star) });
            var cell = new Border { Padding = new Thickness(11, 9, 11, 9), Child = cells[i] };
            cells[i].VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cell, i);
            row.Children.Add(cell);
        }
        return row;
    }

    private static TextBlock TableText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private TextBlock QuietMono(string text, double size = 12)
    {
        var block = Quiet(text, size);
        block.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        return block;
    }

    private Border EmptyTable(string key)
    {
        var text = Quiet(Strings.Get(key));
        text.TextAlignment = TextAlignment.Center;
        var empty = Card(text);
        empty.Padding = new Thickness(12, 24, 12, 24);
        return empty;
    }

    private FrameworkElement TableStatus(string status, string tone, bool wrap = false)
    {
        var dot = Quiet("\u25CF", 12);
        if (tone == "success")
            dot.Style = StyleFor("DashboardSuccessTextStyle");
        else if (tone == "error")
            dot.Style = StyleFor("DashboardErrorTextStyle");
        dot.VerticalAlignment = VerticalAlignment.Center;
        if (wrap)
            return IconLabel(dot, TableText(status));
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7, Children = { dot, TableText(status) }
        };
    }

    partial void UpdateKeysAvailability()
    {
        if (displayedState is not { } current)
            return;
        foreach (var button in grantActions.Values.SelectMany(actions => actions))
            button.IsEnabled = connected && !urgentBusy;
        var canCache = current.KeyCache.Mode != "off";
        foreach (var key in current.Keys)
        {
            if (!keyActions.TryGetValue(key.Id, out var row))
                continue;
            row.Load.IsEnabled = Ready && !preloading && canCache && !key.Loaded && !key.Loading && key.Fingerprint.Length > 0;
            row.Evict.IsEnabled = CanEvict(key);
            row.Revoke.IsEnabled = connected && !urgentBusy && key.Grants != 0;
        }
        if (discoverKeys is not null)
            discoverKeys.IsEnabled = Ready && current.Scope is { } scope && (scope.Default == "include" ||
                scope.Vaults.Values.Contains("include") || scope.Items.Values.Contains("include"));
        if (preloadKeys is not null)
            preloadKeys.IsEnabled = Ready && !preloading && canCache && current.Keys.Any(key => !key.Loaded && key.Fingerprint.Length != 0);
        if (evictKeys is not null)
            evictKeys.IsEnabled = current.Keys.Any(CanEvict);
        if (revokeKeys is not null)
            revokeKeys.IsEnabled = connected && !urgentBusy && current.Keys.Any(key => key.Grants != 0);
    }

    private static string LocalTimestamp(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static void ConfigureKeyColumns(Grid grid, double actionWidth)
    {
        grid.ColumnSpacing = 12;
        grid.ColumnDefinitions.Clear();
        grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 188 });
        foreach (var width in new[] { 106, 84, actionWidth })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
    }

    private sealed class KeyListRow : Grid
    {
        private readonly StackPanel[] details = new StackPanel[3];
        private readonly TextBlock[] labels;
        private readonly FrameworkElement identity;
        private readonly FrameworkElement actions;

        internal KeyListRow(TextBlock[] labels, FrameworkElement[] cells)
        {
            this.labels = labels;
            identity = cells[0];
            actions = cells[3];
            RowSpacing = 8;
            ColumnSpacing = 12;
            identity.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(identity);
            for (var i = 0; i < details.Length; i++)
            {
                var detail = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { labels[i], cells[i + 1] } };
                details[i] = detail;
                Children.Add(detail);
            }
        }

        internal void SetLayout(bool compact, bool narrow, double actionWidth)
        {
            Padding = new Thickness(compact ? 12 : 14, 10, compact ? 12 : 14, compact ? 13 : 10);
            RowDefinitions.Clear();
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (compact)
            {
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (narrow)
                    RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                ColumnDefinitions.Clear();
                ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                ColumnDefinitions.Add(new ColumnDefinition { Width = narrow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                if (!narrow)
                    ColumnDefinitions.Add(new ColumnDefinition());
            }
            else
                ConfigureKeyColumns(this, actionWidth);

            var columns = ColumnDefinitions.Count;
            Grid.SetColumnSpan(identity, compact ? columns : 1);
            for (var i = 0; i < details.Length; i++)
            {
                var detail = details[i];
                var stackedActions = compact && narrow && i == details.Length - 1;
                labels[i].Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                Grid.SetRow(detail, stackedActions ? 2 : compact ? 1 : 0);
                Grid.SetColumn(detail, stackedActions ? 0 : compact ? i : i + 1);
                Grid.SetColumnSpan(detail, stackedActions ? columns : 1);
            }
            details[0].MaxWidth = 106;
            actions.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
    }

    private sealed class KeysTable(Grid header)
    {
        private (bool Compact, bool Narrow)? layout;

        public StackPanel Content { get; } = new() { Children = { header } };

        public void SetLayout(bool compact, bool narrow)
        {
            if (layout == (compact, narrow))
                return;
            layout = (compact, narrow);
            var horizontal = narrow ? 8 : 11;
            var padding = new Thickness(horizontal, 7, horizontal, 7);
            header.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var cell in header.Children.OfType<Border>())
                cell.Padding = padding;
            foreach (var row in Content.Children.OfType<KeysTableRow>())
                row.SetLayout(compact, padding);
        }
    }

    private sealed class KeysTableRow : KeysWrapPanel
    {
        private readonly double[] widths;
        private readonly TextBlock[] labels;
        private readonly double wideMinHeight;
        private bool compact;

        public KeysTableRow(double[] widths, TextBlock[] labels, double wideMinHeight, FrameworkElement[] cells)
            : base(0, firstFullWidth: true)
        {
            this.widths = widths;
            this.labels = labels;
            this.wideMinHeight = wideMinHeight;
            Children.Add(new Border { Child = cells[0], VerticalAlignment = VerticalAlignment.Center });
            for (var i = 1; i < cells.Length; i++)
            {
                var content = new StackPanel { Children = { labels[i - 1], cells[i] } };
                Children.Add(new Border { Child = content, VerticalAlignment = VerticalAlignment.Center });
            }
        }

        public void SetLayout(bool compact, Thickness padding)
        {
            this.compact = compact;
            MinHeight = compact && wideMinHeight > 0 ? 144 : wideMinHeight;
            foreach (var label in labels)
                label.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            foreach (var cell in Children.OfType<Border>())
                cell.Padding = padding;
            InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (compact || !double.IsFinite(availableSize.Width))
                return base.MeasureOverride(availableSize);
            var height = 0.0;
            for (var i = 0; i < widths.Length; i++)
            {
                var child = Children[i];
                child.Measure(new Size(availableSize.Width * widths[i] / 100, availableSize.Height));
                height = Math.Max(height, child.DesiredSize.Height);
            }
            return new Size(availableSize.Width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (compact)
                return base.ArrangeOverride(finalSize);
            var x = 0.0;
            for (var i = 0; i < widths.Length; i++)
            {
                var width = finalSize.Width * widths[i] / 100;
                Children[i].Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width;
            }
            return finalSize;
        }
    }

    private class KeysWrapPanel(double spacing, bool firstFullWidth = false) : Panel
    {
        protected override Size MeasureOverride(Size availableSize) => Layout(availableSize.Width, arrange: false);

        protected override Size ArrangeOverride(Size finalSize)
        {
            Layout(finalSize.Width, arrange: true);
            return finalSize;
        }

        private Size Layout(double availableWidth, bool arrange)
        {
            var lineStart = 0;
            var lineWidth = 0.0;
            var lineHeight = 0.0;
            var width = 0.0;
            var y = 0.0;
            for (var i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (!arrange)
                    child.Measure(new Size(availableWidth, double.PositiveInfinity));
                var childWidth = ChildWidth(i);
                // XAML rounds a measured child and its arranged slot separately.
                // Keep a subpixel difference from creating a line only during arrange.
                if (i > lineStart && ((firstFullWidth && i == 1) || lineWidth + spacing + childWidth > availableWidth + 1))
                {
                    FinishLine(i);
                    y += spacing;
                }
                lineWidth += (i == lineStart ? 0 : spacing) + childWidth;
                lineHeight = Math.Max(lineHeight, child.DesiredSize.Height);
            }
            FinishLine(Children.Count);
            return new Size(Math.Ceiling(width), y);

            double ChildWidth(int index) => firstFullWidth && index == 0 && double.IsFinite(availableWidth)
                ? availableWidth : Math.Min(Children[index].DesiredSize.Width, availableWidth);

            void FinishLine(int end)
            {
                if (arrange)
                {
                    var x = 0.0;
                    for (var i = lineStart; i < end; i++)
                    {
                        var childWidth = ChildWidth(i);
                        Children[i].Arrange(new Rect(x, y, childWidth, lineHeight));
                        x += childWidth + spacing;
                    }
                }
                width = Math.Max(width, lineWidth);
                y += lineHeight;
                lineStart = end;
                lineWidth = 0;
                lineHeight = 0;
            }
        }
    }
}
