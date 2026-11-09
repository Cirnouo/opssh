using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Opssh.Tray;

internal sealed class ApprovalWindow : Window
{
    internal Approval Approval { get; }

    private readonly DispatcherQueueTimer timer;
    private readonly ScrollViewer view;
    private readonly Action localize;
    private bool dismissed;

    internal ApprovalWindow(Approval approval, Action<string, int> decide, string theme)
    {
        Approval = approval;
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new SizeInt32(
            Math.Min(workArea.Width, (int)Math.Round(660 * scale)),
            Math.Min(workArea.Height, (int)Math.Round(560 * scale))));
        var root = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        var title = Ui.Heading("", 24);
        title.TextWrapping = TextWrapping.Wrap;
        var key = Ui.Text("");
        var chainHeading = Ui.Heading("");
        root.Children.Add(title);
        root.Children.Add(key);
        root.Children.Add(chainHeading);
        var processes = new StackPanel { Spacing = 8 };
        var host = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var processRows = new TextBlock[approval.Chain.Length];
        for (var index = 0; index < approval.Chain.Length; index++)
        {
            processRows[index] = Ui.Text("");
            processes.Children.Add(processRows[index]);
            if (index > 0 && approval.CacheAllowed)
                host.Items.Add(new ComboBoxItem { Tag = index });
        }
        root.Children.Add(new ScrollViewer { Content = processes, MaxHeight = 195 });
        var countdown = Ui.Text("");
        root.Children.Add(countdown);
        var deny = Ui.Button("", () => decide("deny", 0));
        var allowOnce = Ui.Button("", () => decide("once", 0));
        var allowClient = Ui.Button("", () => decide("client", 0));
        var allowHost = Ui.Button("", () =>
        {
            if ((host.SelectedItem as ComboBoxItem)?.Tag is int index)
                decide("host", index);
        });
        if (host.Items.Count > 0)
        {
            allowHost.IsEnabled = false;
            host.SelectionChanged += (_, _) => allowHost.IsEnabled = host.SelectedIndex >= 0;
            root.Children.Add(host);
        }
        root.Children.Add(ButtonRow(deny, allowOnce));
        if (approval.CacheAllowed)
            root.Children.Add(ButtonRow(allowClient, host.Items.Count > 0 ? allowHost : null));
        view = new ScrollViewer { Content = root };
        Content = view;
        ApplyTheme(theme);

        AppWindow.Closing += (_, args) =>
        {
            if (dismissed)
                return;
            args.Cancel = true;
            DispatcherQueue.TryEnqueue(() => decide("deny", 0));
        };
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.Tick += (_, _) => Tick();
        localize = () =>
        {
            view.Language = Strings.Language;
            Title = Strings.Get("Approval.Title");
            title.Text = Strings.Get("Approval.Question");
            var name = approval.NameHint is { } hint ? Strings.Get("Approval.UnnamedKey", hint) : approval.Name;
            key.Text = name + Environment.NewLine + approval.Fingerprint;
            chainHeading.Text = Strings.Get("Approval.VerifiedChain");
            host.Header = Strings.Get("Approval.AncestorProcess");
            SetButtonLabel(deny, "Approval.Deny");
            SetButtonLabel(allowOnce, "Approval.AllowOnce");
            SetButtonLabel(allowClient, "Approval.AllowClient");
            SetButtonLabel(allowHost, "Approval.AllowAncestor");
            for (var index = 0; index < approval.Chain.Length; index++)
            {
                var process = approval.Chain[index];
                var label = index == 0 ? Strings.Get("Approval.SshClient") : Strings.Get("Approval.Ancestor", index);
                processRows[index].Text = Strings.Get("Approval.ProcessDetails", label, process.Pid, Created(process.Created)) +
                    Environment.NewLine + process.Path;
                if (index > 0 && approval.CacheAllowed)
                    ((ComboBoxItem)host.Items[index - 1]).Content =
                        Strings.Get("Approval.ProcessChoice", label, process.Pid, Path.GetFileName(process.Path));
            }
            countdown.Text = Remaining();
        };
        ApplyLanguage();
        timer.Start();

        void Tick()
        {
            var remaining = Remaining();
            if (countdown.Text != remaining)
                countdown.Text = remaining;
            if (Approval.Deadline <= DateTimeOffset.UtcNow)
            {
                timer.Stop();
                decide("deny", 0);
            }
        }

        string Remaining()
        {
            var seconds = Math.Max(0, Math.Ceiling((Approval.Deadline - DateTimeOffset.UtcNow).TotalSeconds));
            return seconds == 1 ? Strings.Get("Approval.ExpiresInOne") : Strings.Get("Approval.ExpiresIn", seconds);
        }
    }

    internal void ApplyTheme(string theme) => WindowTheme.Apply(this, view, theme);

    internal void ApplyLanguage() => localize();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    internal void Dismiss()
    {
        if (dismissed)
            return;
        dismissed = true;
        timer.Stop();
        Close();
    }

    private static Grid ButtonRow(Button first, Button? second)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(first);
        if (second is not null)
        {
            Grid.SetColumn(second, 1);
            row.Children.Add(second);
        }
        return row;
    }

    private static void SetButtonLabel(Button button, string key)
    {
        var text = button.Content as TextBlock;
        if (text is null)
        {
            text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            button.Content = text;
        }
        text.Text = Strings.Get(key);
        AutomationProperties.SetName(button, text.Text);
    }

    private static string Created(ulong fileTime)
    {
        try
        {
            return DateTimeOffset.FromFileTime(checked((long)fileTime)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz");
        }
        catch (Exception e) when (e is OverflowException or ArgumentOutOfRangeException)
        {
            return fileTime.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
