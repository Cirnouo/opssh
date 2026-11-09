using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Opssh.Tray;

internal sealed class SettingsWindow : Window
{
    private readonly TextBlock status = Ui.Text("Starting…");
    private readonly TextBlock details = Ui.Text("");
    private readonly TextBlock message = Ui.Text("");
    private readonly TextBox account = new() { Header = "1Password account", MaxLength = 256, PlaceholderText = "Account sign-in address or shorthand" };
    private readonly TextBox vault = new() { Header = "Vault ID", MaxLength = 128 };
    private readonly TextBox item = new() { Header = "SSH item ID", MaxLength = 128 };
    private readonly TextBox name = new() { Header = "Display name (optional)", MaxLength = 256 };
    private readonly TextBox command = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly ComboBox keys = new() { Header = "Registered key", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock keyDetails = Ui.Text("No SSH key selected.");
    private readonly PolicyInputs keyPolicy = new("Private-key cache");
    private readonly PolicyInputs approvalPolicy = new("Approval cache");
    private readonly StackPanel controls = new() { Spacing = 12 };
    private readonly ContentControl safety;
    private readonly ContentControl body;
    private readonly Button restart;
    private readonly Button toggleLock;
    private readonly Button revokeSelected;
    private string? pipe;
    private string? version;
    private bool connected;
    private bool busy;
    private bool urgentBusy;
    private bool locked;
    private bool initialized;
    private bool updatingKeys;

    internal SettingsWindow(Func<string, object?, bool, Task> send, Func<string, object?, Task> sendUrgent, Func<Task> start)
    {
        Title = "opssh";
        AppWindow.Resize(new SizeInt32(680, 760));
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };

        var root = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        root.Children.Add(Ui.Heading("opssh", 28));
        root.Children.Add(status);
        root.Children.Add(details);
        root.Children.Add(message);
        restart = Ui.Button("Restart core", async () => await start());
        root.Children.Add(restart);
        root.Children.Add(Ui.Heading("Agent controls"));
        toggleLock = Ui.Button("Lock", async () =>
        {
            if (locked)
                await send("unlock", null, false);
            else
                await sendUrgent("lock", null);
        });
        revokeSelected = Ui.Button("Revoke selected", async () =>
        {
            if (SelectedKey is { } key)
                await sendUrgent("revoke", new { keyId = key.Id });
        });
        var safetyActions = Ui.Row(toggleLock,
            Ui.Button("Evict loaded keys", async () => await sendUrgent("evict-keys", null)),
            Ui.Button("Revoke all approvals", async () => await sendUrgent("revoke", null)),
            revokeSelected);
        safety = new ContentControl { Content = safetyActions, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(safety);
        body = new ContentControl { Content = controls, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(body);
        controls.Children.Add(Ui.Button("Show PowerShell command", () =>
        {
            if (pipe is not null && pipe.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
            {
                command.Text = "$env:SSH_AUTH_SOCK = '" + pipe.Replace("'", "''") + "'";
                command.Visibility = Visibility.Visible;
            }
        }));
        AutomationProperties.SetName(command, "Temporary PowerShell environment command");
        controls.Children.Add(command);
        controls.Children.Add(Ui.Text("Run this command in a PowerShell session to opt in for that session."));

        controls.Children.Add(Ui.Heading("Account and caches"));
        controls.Children.Add(account);
        controls.Children.Add(Ui.Text("The account must be available to the 1Password desktop app."));
        controls.Children.Add(Ui.Row(keyPolicy.Mode, keyPolicy.Seconds));
        controls.Children.Add(Ui.Row(approvalPolicy.Mode, approvalPolicy.Seconds));
        controls.Children.Add(Ui.Button("Save settings", async () =>
        {
            if (!keyPolicy.TryRead(out var keyCache) || !approvalPolicy.TryRead(out var approvalCache))
            {
                ShowMessage($"Enter a whole number of seconds from 1 to {CachePolicy.MaxSeconds} for each timed cache.");
                return;
            }
            await send("settings", new { account = account.Text.Trim(), keyCache, approvalCache }, false);
        }));

        controls.Children.Add(Ui.Heading("SSH keys"));
        controls.Children.Add(keys);
        controls.Children.Add(keyDetails);
        keys.SelectionChanged += (_, _) =>
        {
            if (!updatingKeys)
                UpdateKeyDetails();
        };
        var actions = Ui.Row(
            Ui.Button("Refresh", async () => await KeyAction("refresh")),
            Ui.Button("Preload", async () => await KeyAction("preload")),
            Ui.Button("Remove", async () => await KeyAction("remove-key")));
        controls.Children.Add(actions);
        controls.Children.Add(vault);
        controls.Children.Add(item);
        controls.Children.Add(name);
        controls.Children.Add(Ui.Button("Add SSH item", async () =>
        {
            var vaultId = vault.Text.Trim();
            var itemId = item.Text.Trim();
            if (vaultId.Length == 0 || itemId.Length == 0)
            {
                ShowMessage("Enter a vault ID and an SSH item ID.");
                return;
            }
            await send("add-key", new { vaultId, itemId, name = name.Text.Trim() }, false);
        }));

        Content = new ScrollViewer { Content = root };
        SetAvailability(false, false, "Starting the core…");

        async Task KeyAction(string action)
        {
            if (SelectedKey is not { } key)
            {
                ShowMessage("Select a registered SSH key.");
                return;
            }
            await send(action, new { keyId = key.Id }, false);
        }
    }

    internal void Hide() => AppWindow.Hide();

    internal void SetAvailability(bool connected, bool canRestart, string text)
    {
        status.Text = text;
        this.connected = connected;
        restart.Visibility = canRestart ? Visibility.Visible : Visibility.Collapsed;
        if (!connected)
        {
            details.Text = "";
            command.Visibility = Visibility.Collapsed;
            command.Text = "";
            pipe = null;
            initialized = false;
            busy = false;
            urgentBusy = false;
            locked = false;
            toggleLock.Content = "Lock";
            keys.Items.Clear();
            UpdateKeyDetails();
        }
        UpdateAvailability();
    }

    internal void SetBusy(bool busy)
    {
        this.busy = busy;
        UpdateAvailability();
    }

    internal void SetUrgentBusy(bool busy)
    {
        urgentBusy = busy;
        UpdateAvailability();
    }

    internal void SetLockState(bool value)
    {
        locked = value;
        toggleLock.Content = locked ? "Unlock" : "Lock";
        status.Text = locked ? "Core locked" : "Core connected";
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        body.IsEnabled = connected && initialized && !busy && !urgentBusy;
        safety.IsEnabled = connected && !urgentBusy;
        toggleLock.IsEnabled = !locked || (initialized && !busy);
        revokeSelected.IsEnabled = initialized && SelectedKey is not null;
    }

    internal void ShowMessage(string text) => message.Text = text;

    internal void SetHello(Hello hello)
    {
        pipe = hello.Pipe;
        version = hello.Version;
    }

    internal void ShowState(CoreState state, bool resetSettings)
    {
        SetLockState(state.Locked);
        pipe = state.Pipe;
        details.Text = $"Core {version}   ·   1Password {ProviderStatus(state.Provider)}   ·   {state.Pending} pending";
        if (!initialized || resetSettings)
        {
            account.Text = state.Account;
            keyPolicy.Set(state.KeyCache);
            approvalPolicy.Set(state.ApprovalCache);
            initialized = true;
        }

        var list = state.Keys;
        if (!keys.Items.OfType<ComboBoxItem>().Select(i => i.Tag as PublicKey).SequenceEqual(list))
        {
            var selectedId = SelectedKey?.Id;
            updatingKeys = true;
            keys.Items.Clear();
            foreach (var key in list)
                keys.Items.Add(new ComboBoxItem { Content = key.Name, Tag = key });
            keys.SelectedItem = keys.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as PublicKey)?.Id == selectedId)
                ?? keys.Items.FirstOrDefault();
            updatingKeys = false;
            UpdateKeyDetails();
        }
        UpdateAvailability();
    }

    private PublicKey? SelectedKey => (keys.SelectedItem as ComboBoxItem)?.Tag as PublicKey;

    private void UpdateKeyDetails()
    {
        revokeSelected.IsEnabled = initialized && SelectedKey is not null;
        if (SelectedKey is not { } key)
        {
            keyDetails.Text = "No SSH key selected.";
            return;
        }
        var error = key.Error switch
        {
            "provider" => "1Password could not read the item.",
            "load-timeout" => "The item read timed out.",
            "invalid-key" => "The item has an unsupported or invalid SSH key.",
            "key-mismatch" => "The private key differs from the indexed public key. Refresh the key.",
            "public-index-missing" => "The public index entry is missing. Refresh the key.",
            null or "" => "",
            _ => "The key could not be loaded."
        };
        var loadStatus = key.Loading ? "Loading" : key.Loaded ? "Loaded" : "Unloaded";
        keyDetails.Text = key.Fingerprint + "   ·   " + loadStatus +
            (error.Length == 0 ? "" : Environment.NewLine + error);
    }

    private static string ProviderStatus(string status) => status switch
    {
        "unconfigured" => "account setup required",
        "unknown" => "status unknown",
        "reading" => "reading an item",
        "last-success" => "last read succeeded",
        "denied" => "access denied",
        "unavailable" => "unavailable",
        "timed-out" => "read timed out",
        "canceled" => "read canceled",
        "offline-fixture" => "offline test fixture",
        _ => "status unknown"
    };

    private sealed class PolicyInputs
    {
        internal readonly ComboBox Mode;
        internal readonly NumberBox Seconds = new() { Header = "Seconds", Width = 130, Minimum = 1, Maximum = CachePolicy.MaxSeconds };
        private static readonly string[] modes = ["off", "fixed", "idle", "until-lock"];

        internal PolicyInputs(string label)
        {
            Mode = new ComboBox { Header = label, Width = 220 };
            foreach (var option in new[] { "Off", "Fixed duration", "Idle timeout", "Until lock" })
                Mode.Items.Add(option);
            Mode.SelectionChanged += (_, _) => Seconds.IsEnabled = Mode.SelectedIndex is 1 or 2;
            Set(new CachePolicy("off", 0));
            AutomationProperties.SetName(Seconds, label + " duration in seconds");
        }

        internal void Set(CachePolicy policy)
        {
            Mode.SelectedIndex = Math.Max(0, Array.IndexOf(modes, policy.Mode));
            Seconds.Value = policy.Seconds > 0 ? policy.Seconds : 300;
        }

        internal bool TryRead(out CachePolicy policy)
        {
            var timed = Mode.SelectedIndex is 1 or 2;
            var seconds = Seconds.Value;
            policy = new CachePolicy(modes[Math.Max(0, Mode.SelectedIndex)], 0);
            if (!timed)
                return true;
            if (double.IsNaN(seconds) || seconds is < 1 or > CachePolicy.MaxSeconds || seconds != Math.Truncate(seconds))
                return false;
            policy = policy with { Seconds = (int)seconds };
            return true;
        }
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

    internal static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children)
            row.Children.Add(child);
        return row;
    }
}
