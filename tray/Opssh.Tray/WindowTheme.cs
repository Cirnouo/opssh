using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Opssh.Tray;

internal static class WindowTheme
{
    internal static void Apply(Window window, FrameworkElement root, string theme)
    {
        root.Language = Strings.Language;
        root.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            window.AppWindow.TitleBar.PreferredTheme = theme switch
            {
                "light" => TitleBarTheme.Light,
                "dark" => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode
            };
        }
    }
}
