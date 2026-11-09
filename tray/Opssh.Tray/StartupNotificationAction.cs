namespace Opssh.Tray;

internal sealed record StartupNotificationAction(string Nonce, string Action, DateTimeOffset Expires)
{
    internal static StartupNotificationAction Create(string action, DateTimeOffset now) =>
        new(Guid.NewGuid().ToString("N"), action, now.AddMinutes(5));

    internal static bool TryConsume(ref StartupNotificationAction? pending, string? nonce, string? choice,
        DateTimeOffset now, out string? action)
    {
        action = null;
        if (pending is null)
            return false;
        if (now >= pending.Expires)
        {
            pending = null;
            return false;
        }
        if (pending.Nonce != nonce || choice is not ("primary" or "later"))
            return false;
        if (choice == "primary")
            action = pending.Action;
        pending = null;
        return true;
    }
}
