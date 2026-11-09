using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Opssh.Tray;

internal static class SettingsLayout
{
    internal static Grid Row(FrameworkElement label, FrameworkElement? action)
    {
        var row = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        row.Children.Add(label);
        if (action is null)
            return row;

        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        label.VerticalAlignment = action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        row.Children.Add(action);
        var stacked = false;
        row.SizeChanged += (_, args) =>
        {
            var stack = args.NewSize.Width < 420;
            if (stack == stacked)
                return;
            stacked = stack;
            Grid.SetColumnSpan(label, stack ? 2 : 1);
            Grid.SetRow(action, stack ? 1 : 0);
            Grid.SetColumn(action, stack ? 0 : 1);
            Grid.SetColumnSpan(action, stack ? 2 : 1);
        };
        return row;
    }
}

internal static class Ui
{
    internal static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    internal static TextBlock Heading(string value, int size = 19) =>
        new() { Text = value, FontSize = size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    internal static Button Button(string label, Action action)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => action();
        return button;
    }

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
