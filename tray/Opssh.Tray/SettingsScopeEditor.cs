using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Opssh.Tray;

internal sealed class SettingsScopeEditor
{
    private readonly ComboBox defaultRule = new() { MinWidth = 156 };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock empty = Ui.Text("Connect an account or refresh the index to list vaults and SSH items.");
    private readonly StackPanel vaultRows = new() { Spacing = 4 };
    private readonly StackPanel unavailableRows = new() { Spacing = 8 };
    private readonly Expander unavailableOverrides;
    private readonly Dictionary<string, Expander> vaultSections = new(StringComparer.Ordinal);
    private readonly Button apply;
    private readonly Button clear;
    private DiscoveryScope saved = new("include", [], []);
    private DiscoveryScope draft = new("include", [], []);
    private DiscoveredVault[] catalog = [];
    private bool rebuilding;

    internal SettingsScopeEditor(Func<DiscoveryScope, Task> applyScope, Action<string> showMessage)
    {
        defaultRule.Items.Add("Include all");
        defaultRule.Items.Add("Exclude all");
        AutomationProperties.SetName(defaultRule, "Default discovery scope");
        defaultRule.SelectionChanged += (_, _) =>
        {
            if (rebuilding)
                return;
            draft = draft with { Default = defaultRule.SelectedIndex == 1 ? "exclude" : "include" };
            UpdateSummary();
        };
        apply = Ui.Button("Apply scope", async () =>
        {
            if (draft.Vaults.Count > DiscoveryScope.MaxVaults || draft.Items.Count > DiscoveryScope.MaxItems)
            {
                showMessage($"Keep at most {DiscoveryScope.MaxVaults} vault overrides and {DiscoveryScope.MaxItems} SSH item overrides.");
                return;
            }
            await applyScope(Copy(draft));
        });
        clear = Ui.Button("Clear overrides", () =>
        {
            draft.Vaults.Clear();
            draft.Items.Clear();
            RebuildRows();
        });

        var controls = new StackPanel { Spacing = 12 };
        unavailableOverrides = new Expander
        {
            Header = "Overrides outside the discovery list",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { Ui.Text("These rules still apply to the corresponding vaults or items when available."), unavailableRows }
            }
        };
        controls.Children.Add(Ui.Text("Scope controls which SSH keys opssh indexes and uses. 1Password manages account-wide access separately."));
        controls.Children.Add(Ui.Text("An SSH item rule takes precedence over its vault rule. Vaults and items set to Inherit use the nearest rule above them."));
        controls.Children.Add(Ui.Text($"The discovery list shows up to {DiscoveryScope.MaxVaults} vaults and {DiscoveryScope.MaxItems} SSH items."));
        controls.Children.Add(Ui.Text("Apply to save your changes. Closing Settings discards unapplied changes."));
        controls.Children.Add(SettingsLayout.Row(Ui.Text("Default for all SSH items"), defaultRule));
        controls.Children.Add(empty);
        controls.Children.Add(vaultRows);
        controls.Children.Add(unavailableOverrides);
        controls.Children.Add(SettingsLayout.Row(apply, clear));
        View = new Expander
        {
            Header = new StackPanel { Spacing = 2, Children = { Ui.Heading("Discovery scope", 14), summary } },
            Content = controls,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(View, "Discovery scope");
        RebuildRows();
    }

    internal Expander View { get; }

    internal void Reset()
    {
        saved = new DiscoveryScope("include", [], []);
        draft = Copy(saved);
        catalog = [];
        RebuildRows();
    }

    internal void Update(DiscoveryScope? scope, DiscoveredVault[] vaults)
    {
        var previousSaved = saved;
        saved = scope ?? new DiscoveryScope("include", [], []);
        var replaceDraft = SameScope(draft, previousSaved) && !SameScope(draft, saved);
        if (replaceDraft)
            draft = Copy(saved);

        if (replaceDraft || !SameCatalog(catalog, vaults))
        {
            catalog = vaults;
            RebuildRows();
        }
        else
        {
            UpdateSummary();
        }
    }

    private void RebuildRows()
    {
        rebuilding = true;
        defaultRule.SelectedIndex = draft.Default == "exclude" ? 1 : 0;
        var expanded = vaultSections.Where(section => section.Value.IsExpanded).Select(section => section.Key).ToHashSet(StringComparer.Ordinal);
        vaultSections.Clear();
        vaultRows.Children.Clear();
        var vaultLabels = SettingsLabels.ById(catalog.Select(v => (v.Id, DisplayName(v.Name, "Unnamed vault"))));
        var listedItems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vault in catalog.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = vaultLabels[vault.Id];
            var itemLabels = SettingsLabels.ById(vault.Items.Select(i => (i.Id, DisplayName(i.Name, "Unnamed SSH item"))));
            var rules = new StackPanel { Spacing = 8 };
            rules.Children.Add(SettingsLayout.Row(Ui.Text("Vault rule"), Override(draft.Vaults, vault.Id, $"Rule for vault {name}")));
            foreach (var item in vault.Items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var id = vault.Id + "/" + item.Id;
                listedItems.Add(id);
                var itemName = itemLabels[item.Id];
                rules.Children.Add(SettingsLayout.Row(Ui.Text(itemName),
                    Override(draft.Items, id, $"Rule for SSH item {itemName} in {name}")));
            }

            var section = new Expander
            {
                Header = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = Count(vault.Items.Length, "SSH item", "SSH items") }
                    }
                },
                Content = rules,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsExpanded = expanded.Contains(vault.Id)
            };
            AutomationProperties.SetName(section, name);
            vaultRows.Children.Add(section);
            vaultSections.Add(vault.Id, section);
        }
        unavailableRows.Children.Clear();
        foreach (var rule in draft.Vaults.OrderBy(rule => rule.Key, StringComparer.Ordinal))
        {
            if (!vaultLabels.ContainsKey(rule.Key))
                AddUnavailableRow(draft.Vaults, rule.Key, $"Vault {rule.Key}", rule.Value);
        }
        foreach (var rule in draft.Items.OrderBy(rule => rule.Key, StringComparer.Ordinal))
        {
            if (!listedItems.Contains(rule.Key))
            {
                var separator = rule.Key.IndexOf('/');
                var vaultId = rule.Key[..separator];
                var vaultName = vaultLabels.GetValueOrDefault(vaultId, "vault " + vaultId);
                AddUnavailableRow(draft.Items, rule.Key, $"SSH item {rule.Key[(separator + 1)..]} in {vaultName}", rule.Value);
            }
        }
        empty.Visibility = catalog.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        rebuilding = false;
        UpdateSummary();
    }

    private void AddUnavailableRow(Dictionary<string, string> overrides, string id, string name, string rule)
    {
        var label = rule == "include" ? "Include" : "Exclude";
        var remove = new Button { Content = "Remove" };
        AutomationProperties.SetName(remove, $"Remove {label.ToLowerInvariant()} override for {name}");
        var row = SettingsLayout.Row(Ui.Text($"{name} · {label}"), remove);
        remove.Click += (_, _) =>
        {
            overrides.Remove(id);
            unavailableRows.Children.Remove(row);
            UpdateSummary();
            if (apply.IsEnabled)
                apply.Focus(FocusState.Programmatic);
            else
                defaultRule.Focus(FocusState.Programmatic);
        };
        unavailableRows.Children.Add(row);
    }

    private ComboBox Override(Dictionary<string, string> overrides, string id, string label)
    {
        var choice = new ComboBox { MinWidth = 116 };
        choice.Items.Add("Inherit");
        choice.Items.Add("Include");
        choice.Items.Add("Exclude");
        choice.SelectedIndex = overrides.GetValueOrDefault(id) switch
        {
            "include" => 1,
            "exclude" => 2,
            _ => 0
        };
        AutomationProperties.SetName(choice, label);
        choice.SelectionChanged += (_, _) =>
        {
            if (rebuilding)
                return;
            switch (choice.SelectedIndex)
            {
                case 0: overrides.Remove(id); break;
                case 1: overrides[id] = "include"; break;
                case 2: overrides[id] = "exclude"; break;
            }
            UpdateSummary();
        };
        return choice;
    }

    private void UpdateSummary()
    {
        var dirty = !SameScope(draft, saved);
        var defaultLabel = draft.Default == "include" ? "Include all by default" : "Exclude all by default";
        summary.Text = $"{defaultLabel} · {Count(catalog.Length, "vault", "vaults")} · " +
            $"{Count(catalog.Sum(v => v.Items.Length), "SSH item", "SSH items")}" +
            (dirty ? " · Unsaved changes" : "");
        apply.IsEnabled = dirty;
        clear.IsEnabled = draft.Vaults.Count > 0 || draft.Items.Count > 0;
        unavailableOverrides.Visibility = unavailableRows.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool SameCatalog(DiscoveredVault[] left, DiscoveredVault[] right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name && pair.First.Items.SequenceEqual(pair.Second.Items));

    private static bool SameScope(DiscoveryScope left, DiscoveryScope right) =>
        left.Default == right.Default && SameRules(left.Vaults, right.Vaults) && SameRules(left.Items, right.Items);

    private static bool SameRules(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(rule => right.GetValueOrDefault(rule.Key) == rule.Value);

    private static DiscoveryScope Copy(DiscoveryScope scope) =>
        new(scope.Default, new Dictionary<string, string>(scope.Vaults, StringComparer.Ordinal),
            new Dictionary<string, string>(scope.Items, StringComparer.Ordinal));

    private static string DisplayName(string name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : name;

    private static string Count(int count, string singular, string plural) => $"{count} {(count == 1 ? singular : plural)}";
}

internal static class SettingsLabels
{
    internal static Dictionary<string, string> ById(IEnumerable<(string Id, string Name)> entries)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in entries.GroupBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var matching = group.ToArray();
            foreach (var entry in matching)
            {
                var label = entry.Name;
                if (matching.Length > 1)
                {
                    var length = 6;
                    while (matching.Any(other => other.Id != entry.Id &&
                        SamePrefix(entry.Id, other.Id, length)))
                        length++;
                    label += "\nID " + IdPrefix(entry.Id, length);
                }
                labels.Add(entry.Id, label);
            }
        }
        return labels;
    }

    private static bool SamePrefix(string left, string right, int length)
    {
        var separator = left.IndexOf('/');
        return left.AsSpan(0, length).SequenceEqual(right.AsSpan(0, length)) &&
            (separator < 0 || left.AsSpan(separator + 1, length).SequenceEqual(right.AsSpan(separator + 1, length)));
    }

    private static string IdPrefix(string id, int length)
    {
        var separator = id.IndexOf('/');
        return separator < 0 ? id[..length] : id.Substring(separator + 1, length) + "/" + id[..length];
    }
}
