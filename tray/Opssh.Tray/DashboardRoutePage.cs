using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Opssh.Tray;

public sealed partial class DashboardRoutePage : Page
{
    public DashboardRoutePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        Content = args.Parameter as UIElement;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        // Keep the live view in the workspace so navigation preserves its state.
        Content = null;
        base.OnNavigatedFrom(args);
    }
}
