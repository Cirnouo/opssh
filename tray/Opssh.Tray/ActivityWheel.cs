namespace Opssh.Tray;

internal static class ActivityWheel
{
    internal static bool Route(int delta, Func<int, bool>? command, Func<int, bool> table)
    {
        if (command?.Invoke(delta) == true)
            return true;
        table(delta);
        return false;
    }

    internal static bool Move(int delta, double offset, double maximum, Func<double, bool> change)
    {
        var target = Math.Clamp(offset + delta, 0, maximum);
        return target != offset && change(target);
    }
}
