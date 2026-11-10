using System.Globalization;
using System.Text.Json;

namespace Opssh.Tray;

internal sealed record UiPreferences(string Language, string Theme, bool AutoCheckUpdates = true)
{
    private const int MaxSize = 4096;
    private const int CurrentVersion = 3;
    public bool DiagnosticLogging { get; init; }
    public string DiagnosticLevel { get; init; } = "INFO";
    public bool WaitForPassword { get; init; }
    public int Version { get; init; } = CurrentVersion;

    internal bool IsValid => Version == CurrentVersion && DiagnosticEvent.ValidLevel(DiagnosticLevel) &&
        (Language is "system" or "en" or "zh-Hans") && (Theme is "system" or "light" or "dark");

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
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return fallback;
            var version = 0;
            if (root.TryGetProperty("version", out var field) &&
                (field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out version)))
                return fallback;
            var saved = root.Deserialize<UiPreferences>(Protocol.Json);
            var migrated = version switch
            {
                0 or 1 when saved is not null => saved with { Version = CurrentVersion, WaitForPassword = false },
                2 when saved is not null => saved with { Version = CurrentVersion },
                CurrentVersion => saved,
                _ => null
            };
            return migrated is { IsValid: true } ? migrated : fallback;
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
