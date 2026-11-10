using System.Security;
using Microsoft.Win32;

namespace Opssh.Tray;

internal enum LoginStartupStatus { Off, On, Conflict, Unavailable }

internal static class LoginStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "opssh";

    internal static LoginStartupStatus Read()
    {
        var command = Command(Environment.ProcessPath);
        if (command is null)
            return LoginStartupStatus.Unavailable;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return Read(key, command);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return LoginStartupStatus.Unavailable;
        }
    }

    internal static LoginStartupStatus Set(bool enabled)
    {
        var command = Command(Environment.ProcessPath);
        if (command is null)
            return LoginStartupStatus.Unavailable;

        try
        {
            using var key = enabled ? Registry.CurrentUser.CreateSubKey(RunKey) :
                Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            var status = Read(key, command);
            if (status == LoginStartupStatus.Conflict || key is null)
                return status;

            if (enabled && status == LoginStartupStatus.Off)
                key.SetValue(ValueName, command, RegistryValueKind.String);
            else if (!enabled && status == LoginStartupStatus.On)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return Read(key, command);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return LoginStartupStatus.Unavailable;
        }
    }

    private static LoginStartupStatus Read(RegistryKey? key, string command)
    {
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is not null && key!.GetValueKind(ValueName) != RegistryValueKind.String
            ? LoginStartupStatus.Conflict
            : Classify(value, command);
    }

    internal static LoginStartupStatus Classify(object? value, string? command)
    {
        if (command is null)
            return LoginStartupStatus.Unavailable;
        if (value is null)
            return LoginStartupStatus.Off;
        return value is string text && string.Equals(text, command, StringComparison.OrdinalIgnoreCase)
            ? LoginStartupStatus.On
            : LoginStartupStatus.Conflict;
    }

    internal static string? Command(string? path) =>
        path is { Length: > 0 and <= 258 } && Path.IsPathFullyQualified(path) &&
        Path.GetFileName(path).Equals("opssh-tray.exe", StringComparison.OrdinalIgnoreCase) &&
        !path.Any(c => char.IsControl(c) || c == '"')
            ? $"\"{path}\""
            : null;
}
