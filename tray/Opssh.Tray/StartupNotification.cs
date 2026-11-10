using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Opssh.Tray;

internal sealed class StartupNotification(Action<string> activated) : IDisposable
{
    private readonly object sync = new();
    private AppNotificationManager? manager;
    private StartupNotificationAction? pending;
    private uint notificationId;
    private bool disposed;

    internal bool Show(string title, string message, string primary, string later, string action)
    {
        lock (sync)
        {
            if (disposed)
                return false;
            DismissLocked();
            try
            {
                if (manager is null)
                {
                    if (!AppNotificationManager.IsSupported())
                        return false;
                    var next = AppNotificationManager.Default;
                    next.NotificationInvoked += OnInvoked;
                    try { next.Register(); }
                    catch
                    {
                        next.NotificationInvoked -= OnInvoked;
                        throw;
                    }
                    manager = next;
                }
                if (manager.Setting != AppNotificationSetting.Enabled)
                    return false;

                var request = StartupNotificationAction.Create(action, DateTimeOffset.UtcNow);
                var notification = new AppNotificationBuilder()
                    .AddArgument("opsshStartup", request.Nonce).AddArgument("choice", "primary")
                    .AddText(title)
                    .AddText(message)
                    .AddButton(new AppNotificationButton(primary)
                        .AddArgument("opsshStartup", request.Nonce).AddArgument("choice", "primary"))
                    .AddButton(new AppNotificationButton(later)
                        .AddArgument("opsshStartup", request.Nonce).AddArgument("choice", "later"))
                    .BuildNotification();
                notification.Expiration = request.Expires;
                notification.ExpiresOnReboot = true;
                pending = request;
                manager.Show(notification);
                notificationId = notification.Id;
                if (notificationId != 0)
                    return true;
            }
            catch (Exception e) when (Unavailable(e)) { }
            pending = null;
            return false;
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var arguments = args.Arguments;
        if (!arguments.TryGetValue("opsshStartup", out var nonce) ||
            !arguments.TryGetValue("choice", out var choice))
            return;

        string? action;
        lock (sync)
        {
            if (!StartupNotificationAction.TryConsume(ref pending, nonce, choice, DateTimeOffset.UtcNow, out action))
                return;
            DismissLocked();
        }
        if (action is not null)
            activated(action);
    }

    internal void Dismiss()
    {
        lock (sync)
            DismissLocked();
    }

    private void DismissLocked()
    {
        pending = null;
        if (notificationId != 0 && manager is not null)
        {
            _ = RemoveAsync(manager, notificationId);
            notificationId = 0;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            DismissLocked();
            if (manager is not null)
            {
                manager.NotificationInvoked -= OnInvoked;
                try { manager.Unregister(); }
                catch (Exception e) when (Unavailable(e)) { }
                manager = null;
            }
        }
    }

    private static async Task RemoveAsync(AppNotificationManager current, uint id)
    {
        try { await current.RemoveByIdAsync(id); }
        catch (Exception e) when (Unavailable(e)) { }
    }

    private static bool Unavailable(Exception e) => e is COMException or UnauthorizedAccessException or
        InvalidOperationException or NotSupportedException or TypeLoadException or TypeInitializationException or
        FileNotFoundException;
}
