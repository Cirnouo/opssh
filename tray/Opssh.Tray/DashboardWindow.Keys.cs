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
    private readonly Dictionary<string, (Button Load, Button Evict, Button Revoke)> keyActions = new(StringComparer.Ordinal);
    private readonly HashSet<string> fetchingGrants = new(StringComparer.Ordinal);
    private StackPanel? keysBody;
    private KeysTable? keysTable;
    private TextBlock? keysCount;
    private TextBlock? keysNotice;
    private Button? discoverKeys;
    private Button? preloadKeys;
    private Button? evictKeys;
    private Button? revokeKeys;
    private string? keysAccount;
    private bool preloading;
    private string? preloadingKey;

    private FrameworkElement BuildKeysPage()
    {
        var title = TableHeading("Dashboard.Keys", "Keys.Description", out var headingActions);
        discoverKeys = KeysButton("Keys.Discover", async () => await send("discover", null, false), "\uE721", primary: true);
        headingActions.VerticalAlignment = VerticalAlignment.Center;
        headingActions.Children.Add(discoverKeys);
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
        return new StackPanel { Spacing = 15, Children = { title, keysNotice, keysCount, actionsRow, keysBody } };
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
        keysTable = null;
        if (current is null || current.Keys.Length == 0)
        {
            keysBody.Children.Add(EmptyTable(current?.Index is "ready" or "partial" ? "Keys.NoneFound" : "Keys.BeforeDiscovery"));
            return;
        }
        var widths = new[] { 19.0, 16, 15, 50 };
        string[] columns = ["Keys.Key", "Keys.Loading", "Keys.Grants", "Keys.Actions"];
        keysTable = new KeysTable(TableHeader(widths, columns), grantTable: false);
        var content = keysTable.Content;
        var vaultNames = (current.Vaults ?? []).ToDictionary(vault => vault.Id, vault => vault.Name, StringComparer.Ordinal);
        foreach (var key in current.Keys)
        {
            content.Children.Add(Divider());
            var keyText = new StackPanel { Spacing = 2, Children = { TableText(key.Name) } };
            if (vaultNames.TryGetValue(key.VaultId, out var vault))
                keyText.Children.Add(Quiet(vault));
            keyText.Children.Add(QuietMono(key.Fingerprint.Length == 0 ? "—" : key.Fingerprint, 11));
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
            var load = KeysButton("Keys.Preload", () => PreloadAsync(key.Id), compact: true);
            var evict = KeysButton("Keys.Evict", async () => await sendUrgent("evict-keys", new { keyId = key.Id }), compact: true);
            var revoke = KeysButton("Keys.RevokeKey", async () => await sendUrgent("revoke", new { keyId = key.Id }), compact: true);
            keyActions[key.Id] = (load, evict, revoke);
            load.IsEnabled = Ready && !preloading && current.KeyCache.Mode != "off" && !key.Loaded && !key.Loading && key.Fingerprint.Length > 0;
            evict.IsEnabled = CanEvict(key);
            revoke.IsEnabled = connected && !urgentBusy && key.Grants != 0;
            var buttons = new KeysWrapPanel(5) { Children = { load, evict, revoke } };
            var status = key.Loaded ? "Keys.Loaded" : key.Loading ? "Keys.LoadingNow" : "Keys.NotLoaded";
            content.Children.Add(KeysRow(widths, columns, 79, keyText,
                TableStatus(Strings.Get(status), key.Loaded ? "success" : "neutral", wrap: true), expand, buttons));
            if (!expanded)
                continue;
            content.Children.Add(Divider());
            var grantHost = new Border { Padding = new Thickness(11, 7, 11, 8) };
            grantHosts[key.Id] = grantHost;
            PaintGrants(key.Id, key.Grants);
            content.Children.Add(grantHost);
        }
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
                UpdateKeysAvailability();
        }
    }

    private bool CanEvict(PublicKey key) => connected && !urgentBusy &&
        (key.Loaded || key.Loading || preloading && (preloadingKey is null || preloadingKey == key.Id));

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
        var table = new KeysTable(TableHeader(widths, columns), grantTable: true);
        var buttons = new List<Button>();
        foreach (var grant in rows)
        {
            if (grant is null || grant.Id.Length != 26 || grant.Pid == 0)
                continue;
            table.Content.Children.Add(Divider());
            var button = KeysButton("Keys.RevokeGrant",
                async () => await sendUrgent("revoke", new { keyId, grantId = grant.Id }), compact: true);
            button.IsEnabled = connected && !urgentBusy;
            buttons.Add(button);
            table.Content.Children.Add(KeysRow(widths, columns, 0, QuietMono(grant.Name), QuietMono(grant.Pid.ToString(CultureInfo.InvariantCulture)),
                QuietMono(grant.ExpiresAt is { } expires ? LocalTimestamp(expires) : Strings.Get("Keys.UntilExit")), button));
        }
        grantTables[keyId] = table;
        grantActions[keyId] = buttons;
        var width = KeysLayoutWidth;
        table.SetLayout(width <= CompactGrantTableWidth, width <= CompactKeyTableWidth);
        host.Child = Card(table.Content);
    }

    // The prototype queries the surface's content box before subtracting page padding.
    private double KeysLayoutWidth => PageSurface.ActualWidth - PageSurface.BorderThickness.Left - PageSurface.BorderThickness.Right;

    private void ResizeKeysTables()
    {
        var width = KeysLayoutWidth;
        var narrow = width <= CompactKeyTableWidth;
        keysTable?.SetLayout(narrow, narrow);
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

    private Grid TableHeading(string titleKey, string descriptionKey, out StackPanel actionsPanel)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new StackPanel { Spacing = 5, Children =
            { Heading(Strings.Get(titleKey), 28, AutomationHeadingLevel.Level1), Quiet(Strings.Get(descriptionKey)) } };
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

    private sealed class KeysTable(Grid header, bool grantTable)
    {
        private (bool Compact, bool Narrow)? layout;

        public StackPanel Content { get; } = new() { Children = { header } };

        public void SetLayout(bool compact, bool narrow)
        {
            if (layout == (compact, narrow))
                return;
            layout = (compact, narrow);
            var horizontal = narrow ? 8 : 11;
            var vertical = grantTable ? 7 : 9;
            var padding = new Thickness(horizontal, vertical, horizontal, vertical);
            header.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var cell in header.Children.OfType<Border>())
                cell.Padding = padding;
            if (!grantTable && Content.Children.Count > 1)
                Content.Children[1].Visibility = header.Visibility;
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
