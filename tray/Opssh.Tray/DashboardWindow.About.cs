using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Opssh.Tray;

public sealed partial class DashboardWindow
{
    private static readonly Uri ProjectUrl = new("https://github.com/Cirnouo/opssh");
    private Button checkForUpdates = null!;
    private TextBlock updateStatus = null!;
    private HyperlinkButton releaseLink = null!;
    private ProgressRing updateProgress = null!;

    private StackPanel BuildAbout(bool autoCheck, Func<Task> checkUpdates, Func<bool, bool> setAutoCheckUpdates)
    {
        var page = Page(Strings.Get("Settings.About"));
        var brand = new Grid { ColumnSpacing = 15, Margin = new Thickness(0, 8, 0, 8) };
        brand.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brand.ColumnDefinitions.Add(new ColumnDefinition());
        var logo = Logo("\uE756", 48, 25);
        logo.VerticalAlignment = VerticalAlignment.Center;
        brand.Children.Add(logo);
        var product = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Heading("opssh", 16, AutomationHeadingLevel.Level2), Quiet(Strings.Get("Dashboard.AboutDescription")) }
        };
        Grid.SetColumn(product, 1);
        brand.Children.Add(product);
        page.Children.Add(brand);

        checkForUpdates = Ui.Button(Strings.Get("Update.Check"), async () => await checkUpdates());
        updateStatus = Quiet(Strings.Get("Update.CheckDescription"), 12);
        AutomationProperties.SetLiveSetting(updateStatus, AutomationLiveSetting.Polite);
        updateProgress = new ProgressRing
        {
            Height = 16, Width = 16, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(updateProgress, Strings.Get("Update.Checking"));
        releaseLink = new HyperlinkButton
        {
            Content = Strings.Get("Update.ViewRelease"),
            NavigateUri = ReleaseCheck.ReleasesPage,
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(0)
        };
        var updateAction = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { updateProgress, checkForUpdates }
        };
        var release = new StackPanel { Spacing = 2, Children = { updateStatus, releaseLink } };

        var auto = Switch(Strings.Get("Dashboard.AutoCheckUpdates"), autoCheck);
        var restoring = false;
        auto.Toggled += (_, _) =>
        {
            if (restoring || setAutoCheckUpdates(auto.IsOn))
                return;
            restoring = true;
            auto.IsOn = !auto.IsOn;
            restoring = false;
            ShowMessage(Strings.Get("Update.SaveFailed"), InfoBarSeverity.Warning);
        };

        page.Children.Add(Section(Strings.Get("Dashboard.VersionAndUpdates"), Cards(
            SettingRow("\uE7B8", Strings.Get("Dashboard.CurrentVersion"), "Windows x64", Mono(App.InstalledVersion.ToString(3), 14)),
            SettingRow("\uE895", Strings.Get("Update.Check"), release, updateAction),
            SettingRow("\uE896", Strings.Get("Dashboard.AutoCheckUpdates"), Strings.Get("Update.AutoDescription"), auto))));

        var mark = (PathIcon)((DataTemplate)Application.Current.Resources["DashboardGitHubIcon"]).LoadContent();
        var repository = new HyperlinkButton { Content = "Cirnouo/opssh", NavigateUri = ProjectUrl, Padding = new Thickness(0) };
        page.Children.Add(Section(Strings.Get("Dashboard.Project"), Cards(
            SettingRow("\uE77B", Strings.Get("Dashboard.Developer"), "", Ui.Text("Cirnouo")),
            SettingRow("\uE8F4", Strings.Get("Dashboard.License"), "", Ui.Text("Apache License 2.0")),
            SettingRow("", "GitHub", Strings.Get("Dashboard.GitHubDescription"), repository, symbol: mark))));
        return page;
    }

    internal void ShowReleaseCheck(ReleaseCheckResult result)
    {
        var checking = result.Status == ReleaseCheckStatus.Checking;
        checkForUpdates.IsEnabled = !checking;
        checkForUpdates.Content = Strings.Get(checking ? "Dashboard.CheckingUpdate" : "Update.Check");
        updateProgress.IsActive = checking;
        updateProgress.Visibility = checking ? Visibility.Visible : Visibility.Collapsed;
        releaseLink.Visibility = result.Status == ReleaseCheckStatus.Available ? Visibility.Visible : Visibility.Collapsed;
        updateStatus.Text = result.Status switch
        {
            ReleaseCheckStatus.Checking => Strings.Get("Update.Checking"),
            ReleaseCheckStatus.Current => Strings.Get("Update.Current"),
            ReleaseCheckStatus.Available => Strings.Get("Update.Available", result.Latest!.ToString(3)),
            ReleaseCheckStatus.NoRelease => Strings.Get("Update.NoRelease"),
            ReleaseCheckStatus.Unavailable => Strings.Get("Update.Unavailable"),
            _ => Strings.Get("Update.CheckDescription")
        };
    }
}
