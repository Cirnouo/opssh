using System.Globalization;
using System.Text.Json;

namespace Opssh.Tray;

internal sealed record UiPreferences(string Language, string Theme, bool AutoCheckUpdates = true)
{
    private const int MaxSize = 4096;

    internal bool IsValid => (Language is "system" or "en" or "zh-Hans") && (Theme is "system" or "light" or "dark");

    private static string SettingsPath =>
        Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "opssh", "tray-settings.json");

    internal static UiPreferences Load() => Load(SettingsPath);

    internal static UiPreferences Load(string path)
    {
        var fallback = new UiPreferences("system", "system");
        if (!Path.IsPathFullyQualified(path))
            return fallback;
        try
        {
            using var stream = File.OpenRead(path);
            var size = stream.Length;
            if (size is <= 0 or > MaxSize)
                return fallback;
            var data = new byte[checked((int)size)];
            stream.ReadExactly(data);
            if (stream.ReadByte() != -1)
                return fallback;
            var saved = JsonSerializer.Deserialize<UiPreferences>(data, Protocol.Json);
            return saved is { IsValid: true } ? saved : fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return fallback;
        }
    }

    internal string ResolveLanguage() => ResolveLanguage(CultureInfo.CurrentUICulture);

    internal string ResolveLanguage(CultureInfo culture)
    {
        if (Language != "system")
            return Language;
        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            if (current.Name is "zh-Hans" or "zh-CN" or "zh-SG")
                return "zh-Hans";
        }
        return "en";
    }

    internal bool TrySave() => TrySave(SettingsPath);

    internal bool TrySave(string path)
    {
        if (!IsValid)
            return false;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Path.IsPathFullyQualified(path))
            return false;
        var temporary = Path.Combine(directory, ".tray-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, this, Protocol.Json);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
