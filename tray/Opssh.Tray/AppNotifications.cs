using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;

namespace Opssh.Tray;

// The tray owns one Windows notification registration. Delivery is handled on the dispatcher.
internal sealed class AppNotifications(Action<string, string, string> activated) : IDisposable
{
    internal const string Startup = "opsshStartup";
    internal const string Signing = "opsshSigning";
    private AppNotificationManager? manager;
    private bool disposed;

    internal bool IsAvailable()
    {
        if (disposed)
            return false;
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
            return manager.Setting == AppNotificationSetting.Enabled;
        }
        catch (Exception e) when (Unavailable(e)) { return false; }
    }

    internal uint Show(Func<AppNotification> build)
    {
        if (!IsAvailable())
            return 0;
        try
        {
            var notification = build();
            manager!.Show(notification);
            return notification.Id;
        }
        catch (Exception e) when (Unavailable(e)) { return 0; }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            var arguments = args.Arguments;
            if (arguments.Count != 2 || !arguments.TryGetValue("choice", out var choice) ||
                choice is not { Length: > 0 and <= 11 })
                return;
            if (arguments.TryGetValue(Signing, out var nonce) && nonce is { Length: 32 })
                activated(Signing, nonce, choice);
            else if (arguments.TryGetValue(Startup, out nonce) && nonce is { Length: 32 })
                activated(Startup, nonce, choice);
        }
        catch (Exception e) when (Unavailable(e)) { }
    }

    internal async Task RemoveAsync(uint id)
    {
        if (id == 0 || manager is not { } current)
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await current.RemoveByIdAsync(id).AsTask(timeout.Token).WaitAsync(timeout.Token); }
        catch (Exception e) when (Unavailable(e) || e is OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (manager is { } current)
        {
            manager = null;
            current.NotificationInvoked -= OnInvoked;
            try { current.Unregister(); }
            catch (Exception e) when (Unavailable(e)) { }
        }
    }

    internal static bool Unavailable(Exception e) => e is ExternalException or UnauthorizedAccessException or ArgumentException or
        InvalidOperationException or NotSupportedException or TypeLoadException or TypeInitializationException or
        FileNotFoundException;
}
