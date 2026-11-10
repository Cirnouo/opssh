// Adapted from Microsoft WSL Settings ShellPage. See THIRD-PARTY-NOTICES.md.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Opssh.Tray;

public sealed partial class DashboardShell : Page
{
    public DashboardShell()
    {
        InitializeComponent();
        AutomationProperties.SetName(PaneToggleBtn, Strings.Get("Dashboard.ToggleNavigation"));
        UpdateTitleBar(NavigationViewControl.DisplayMode);
    }

    internal Grid Root => ShellRoot;
    internal Grid TitleBar => AppTitleBar;
    internal NavigationView Navigation => NavigationViewControl;
    internal Frame Routes => NavigationFrame;
    internal Grid Viewport => ContentSurface;
    internal InfoBar Feedback => FeedbackBar;

    internal void UseSystemTitleBar()
    {
        AppTitleBar.Visibility = Visibility.Collapsed;
        TitleRow.Height = 32;
    }

    private void NavigationViewControl_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) =>
        UpdateTitleBar(args.DisplayMode);

    private void UpdateTitleBar(NavigationViewDisplayMode mode)
    {
        var expanded = mode == NavigationViewDisplayMode.Expanded;
        if (expanded)
            NavigationViewControl.IsPaneOpen = true;
        PaneToggleBtn.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        AppTitleBar.Margin = new Thickness(expanded ? 16 : 48, 0, 0, 0);
        AppTitleBarText.Margin = new Thickness(expanded ? 16 : 12, 0, 0, 0);
    }

    private void PaneToggleBtn_Click(object sender, RoutedEventArgs args) =>
        NavigationViewControl.IsPaneOpen = !NavigationViewControl.IsPaneOpen;
}
