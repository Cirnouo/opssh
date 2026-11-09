using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Opssh.Tray;

internal sealed class SettingsScopeEditor
{
    private readonly ComboBox defaultRule = new() { MinWidth = 156 };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock empty = Ui.Text(Strings.Get("Scope.ListEmpty"));
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
    private int itemCount;

    internal SettingsScopeEditor(Func<DiscoveryScope, Task> applyScope, Action<string> showMessage)
    {
        defaultRule.Items.Add(Strings.Get("Scope.IncludeAll"));
        defaultRule.Items.Add(Strings.Get("Scope.ExcludeAll"));
        AutomationProperties.SetName(defaultRule, Strings.Get("Scope.DefaultAutomationName"));
        defaultRule.SelectionChanged += (_, _) =>
        {
            if (rebuilding)
                return;
            draft = draft with { Default = defaultRule.SelectedIndex == 1 ? "exclude" : "include" };
            UpdateSummary();
        };
        apply = Ui.Button(Strings.Get("Scope.Apply"), async () =>
        {
            if (draft.Vaults.Count > DiscoveryScope.MaxVaults || draft.Items.Count > DiscoveryScope.MaxItems)
            {
                showMessage(Strings.Get("Scope.OverrideLimit", DiscoveryScope.MaxVaults, DiscoveryScope.MaxItems));
                return;
            }
            await applyScope(Copy(draft));
        });
        clear = Ui.Button(Strings.Get("Scope.Clear"), () =>
        {
            draft.Vaults.Clear();
            draft.Items.Clear();
            RebuildRows();
        });

        var controls = new StackPanel { Spacing = 12 };
        unavailableOverrides = new Expander
        {
            Header = Strings.Get("Scope.Unavailable"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { Ui.Text(Strings.Get("Scope.UnavailableDescription")), unavailableRows }
            }
        };
        controls.Children.Add(Ui.Text(Strings.Get("Scope.Description")));
        controls.Children.Add(Ui.Text(Strings.Get("Scope.Precedence")));
        controls.Children.Add(Ui.Text(Strings.Get("Scope.CatalogLimit", DiscoveryScope.MaxVaults, DiscoveryScope.MaxItems)));
        controls.Children.Add(Ui.Text(Strings.Get("Scope.ApplyDescription")));
        controls.Children.Add(SettingsLayout.Row(Ui.Text(Strings.Get("Scope.Default")), defaultRule));
        controls.Children.Add(empty);
        controls.Children.Add(vaultRows);
        controls.Children.Add(unavailableOverrides);
        controls.Children.Add(SettingsLayout.Row(apply, clear));
        View = new Expander
        {
            Header = new StackPanel { Spacing = 2, Children = { Ui.Heading(Strings.Get("Scope.DiscoveryScope"), 14), summary } },
            Content = controls,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(View, Strings.Get("Scope.DiscoveryScope"));
        RebuildRows();
    }

    internal Expander View { get; }

    internal void Reset() => Update(null, [], reset: true);

    internal void Update(DiscoveryScope? scope, DiscoveredVault[] vaults, bool reset)
    {
        var next = scope ?? new DiscoveryScope("include", [], []);
        var savedChanged = !SameScope(saved, next);
        var catalogChanged = !SameCatalog(catalog, vaults);
        var replaceDraft = (reset || (savedChanged && SameScope(draft, saved))) && !SameScope(draft, next);
        if (!savedChanged && !catalogChanged && !replaceDraft)
            return;

        saved = next;
        if (replaceDraft)
            draft = Copy(saved);
        if (catalogChanged)
        {
            catalog = vaults;
            itemCount = vaults.Sum(vault => vault.Items.Length);
        }

        if (replaceDraft || catalogChanged)
            RebuildRows();
        else
            UpdateSummary();
    }

    private void RebuildRows()
    {
        rebuilding = true;
        defaultRule.SelectedIndex = draft.Default == "exclude" ? 1 : 0;
        var expanded = vaultSections.Where(section => section.Value.IsExpanded).Select(section => section.Key).ToHashSet(StringComparer.Ordinal);
        vaultSections.Clear();
        vaultRows.Children.Clear();
        var vaultLabels = SettingsLabels.ById(catalog.Select(v => (v.Id, DisplayName(v.Name, Strings.Get("Scope.UnnamedVault")))));
        var listedItems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vault in catalog.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = vaultLabels[vault.Id];
            foreach (var item in vault.Items)
                listedItems.Add(vault.Id + "/" + item.Id);

            var section = new Expander
            {
                Header = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = ItemCount(vault.Items.Length) }
                    }
                },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            // Materialize item controls when the user opens the vault.
            section.Expanding += (sender, _) => sender.Content ??= BuildVaultRules(vault, name);
            if (expanded.Contains(vault.Id))
            {
                section.Content = BuildVaultRules(vault, name);
                section.IsExpanded = true;
            }
            AutomationProperties.SetName(section, name);
            vaultRows.Children.Add(section);
            vaultSections.Add(vault.Id, section);
        }
        unavailableRows.Children.Clear();
        foreach (var rule in draft.Vaults.OrderBy(rule => rule.Key, StringComparer.Ordinal))
        {
            if (!vaultLabels.ContainsKey(rule.Key))
                AddUnavailableRow(draft.Vaults, rule.Key, Strings.Get("Scope.UnknownVault", rule.Key), rule.Value);
        }
        foreach (var rule in draft.Items.OrderBy(rule => rule.Key, StringComparer.Ordinal))
        {
            if (!listedItems.Contains(rule.Key))
            {
                var separator = rule.Key.IndexOf('/');
                var vaultId = rule.Key[..separator];
                var vaultName = vaultLabels.GetValueOrDefault(vaultId, Strings.Get("Scope.UnknownVault", vaultId));
                AddUnavailableRow(draft.Items, rule.Key, Strings.Get("Scope.UnknownItem", rule.Key[(separator + 1)..], vaultName), rule.Value);
            }
        }
        empty.Visibility = catalog.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        rebuilding = false;
        UpdateSummary();
    }

    private StackPanel BuildVaultRules(DiscoveredVault vault, string name)
    {
        var itemLabels = SettingsLabels.ById(vault.Items.Select(i => (i.Id, DisplayName(i.Name, Strings.Get("Scope.UnnamedItem")))));
        var rules = new StackPanel { Spacing = 8 };
        rules.Children.Add(SettingsLayout.Row(Ui.Text(Strings.Get("Scope.VaultRule")),
            Override(draft.Vaults, vault.Id, Strings.Get("Scope.VaultRuleAutomationName", name))));
        foreach (var item in vault.Items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var itemName = itemLabels[item.Id];
            rules.Children.Add(SettingsLayout.Row(Ui.Text(itemName),
                Override(draft.Items, vault.Id + "/" + item.Id, Strings.Get("Scope.ItemRuleAutomationName", itemName, name))));
        }
        return rules;
    }

    private void AddUnavailableRow(Dictionary<string, string> overrides, string id, string name, string rule)
    {
        var label = rule == "include" ? Strings.Get("Scope.Include") : Strings.Get("Scope.Exclude");
        var remove = new Button { Content = Strings.Get("Scope.Remove") };
        AutomationProperties.SetName(remove, Strings.Get("Scope.RemoveAutomationName", name, label));
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
        choice.Items.Add(Strings.Get("Scope.Inherit"));
        choice.Items.Add(Strings.Get("Scope.Include"));
        choice.Items.Add(Strings.Get("Scope.Exclude"));
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
        var defaultLabel = draft.Default == "include" ? Strings.Get("Scope.DefaultInclude") : Strings.Get("Scope.DefaultExclude");
        var vaultCount = Strings.Get(catalog.Length == 1 ? "Scope.VaultCountOne" : "Scope.VaultCountMany", catalog.Length);
        summary.Text = $"{defaultLabel} · {vaultCount} · {ItemCount(itemCount)}" +
            (dirty ? $" · {Strings.Get("Scope.Unsaved")}" : "");
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

    private static string ItemCount(int count) => Strings.Get(count == 1 ? "Scope.ItemCountOne" : "Scope.ItemCountMany", count);
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
                    label += "\n" + Strings.Get("Scope.EntryId", IdPrefix(entry.Id, length));
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
