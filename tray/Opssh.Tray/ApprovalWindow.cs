using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Opssh.Tray;

internal sealed class ApprovalWindow : Window
{
    internal Approval Approval { get; }

    private readonly DispatcherQueueTimer timer;
    private bool dismissed;

    internal ApprovalWindow(Approval approval, Action<string, int> decide)
    {
        Approval = approval;
        Title = "opssh — SSH signing request";
        AppWindow.Resize(new SizeInt32(660, 560));
        var root = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        root.Children.Add(Ui.Heading("Allow this SSH signing request?", 24));
        root.Children.Add(Ui.Text(approval.Name + Environment.NewLine + approval.Fingerprint));
        root.Children.Add(Ui.Heading("Verified process chain"));
        var processes = new StackPanel { Spacing = 8 };
        var host = new ComboBox { Header = "Ancestor process", HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var index = 0; index < approval.Chain.Length; index++)
        {
            var process = approval.Chain[index];
            var label = index == 0 ? "SSH client" : $"Ancestor {index}";
            processes.Children.Add(Ui.Text($"{label}  ·  PID {process.Pid}  ·  Created {Created(process.Created)}{Environment.NewLine}{process.Path}"));
            if (index > 0)
                host.Items.Add(new ComboBoxItem { Content = $"{label}  ·  PID {process.Pid}  ·  {Path.GetFileName(process.Path)}", Tag = index });
        }
        root.Children.Add(new ScrollViewer { Content = processes, MaxHeight = 195 });
        var countdown = Ui.Text("");
        root.Children.Add(countdown);
        var buttons = Ui.Row(Ui.Button("Deny", () => decide("deny", 0)), Ui.Button("Allow once", () => decide("once", 0)));
        if (approval.CacheAllowed)
        {
            buttons.Children.Add(Ui.Button("Allow this client", () => decide("client", 0)));
            if (host.Items.Count > 0)
            {
                var allowHost = Ui.Button("Allow selected ancestor", () =>
                {
                    if ((host.SelectedItem as ComboBoxItem)?.Tag is int index)
                        decide("host", index);
                });
                allowHost.IsEnabled = false;
                host.SelectionChanged += (_, _) => allowHost.IsEnabled = host.SelectedIndex >= 0;
                root.Children.Add(host);
                buttons.Children.Add(allowHost);
            }
        }
        root.Children.Add(buttons);
        Content = new ScrollViewer { Content = root };

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
        countdown.Text = Remaining();
        timer.Start();

        void Tick()
        {
            countdown.Text = Remaining();
            if (Approval.Deadline <= DateTimeOffset.UtcNow)
            {
                timer.Stop();
                decide("deny", 0);
            }
        }

        string Remaining() => $"Expires in {Math.Max(0, Math.Ceiling((Approval.Deadline - DateTimeOffset.UtcNow).TotalSeconds))} seconds";
    }

    internal void Dismiss()
    {
        if (dismissed)
            return;
        dismissed = true;
        timer.Stop();
        Close();
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
