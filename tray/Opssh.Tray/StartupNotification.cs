using Microsoft.Windows.AppNotifications.Builder;

namespace Opssh.Tray;

internal sealed class StartupNotification(AppNotifications notifications, Action<string> activated)
{
    private StartupNotificationAction? pending;
    private uint notificationId;
    private Task removing = Task.CompletedTask;

    internal bool Show(string title, string message, string primary, string later, string action)
    {
        Dismiss();
        var request = StartupNotificationAction.Create(action, DateTimeOffset.UtcNow);
        notificationId = notifications.Show(() =>
        {
            var notification = new AppNotificationBuilder()
                .AddArgument(AppNotifications.Startup, request.Nonce).AddArgument("choice", "primary")
                .AddText(title)
                .AddText(message)
                .AddButton(new AppNotificationButton(primary)
                    .AddArgument(AppNotifications.Startup, request.Nonce).AddArgument("choice", "primary"))
                .AddButton(new AppNotificationButton(later)
                    .AddArgument(AppNotifications.Startup, request.Nonce).AddArgument("choice", "later"))
                .BuildNotification();
            notification.Expiration = request.Expires;
            notification.ExpiresOnReboot = true;
            return notification;
        });
        if (notificationId == 0)
            return false;
        pending = request;
        return true;
    }

    internal void Invoke(string nonce, string choice)
    {
        if (!StartupNotificationAction.TryConsume(ref pending, nonce, choice, DateTimeOffset.UtcNow, out var action))
            return;
        Dismiss();
        if (action is not null)
            activated(action);
    }

    internal void Dismiss() => _ = DismissAsync();

    internal Task DismissAsync()
    {
        pending = null;
        if (notificationId != 0)
        {
            removing = notifications.RemoveAsync(notificationId);
            notificationId = 0;
        }
        return removing;
    }
}
