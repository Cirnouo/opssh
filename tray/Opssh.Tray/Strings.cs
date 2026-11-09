using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Opssh.Tray;

internal static class Strings
{
    private const string ResourcePrefix = "Opssh.Tray.Localization.";
    private static readonly Lazy<FrozenDictionary<string, string>> english = new(() => ReadCatalog("en"));
    private static readonly Lazy<FrozenDictionary<string, string>> simplified = new(() => ReadCatalog("zh-Hans"));
    private static CultureInfo culture = CultureInfo.GetCultureInfo("en");

    internal static string Language => culture.Name;

    internal static void Use(string language) =>
        culture = CultureInfo.GetCultureInfo(language == "zh-Hans" ? "zh-Hans" : "en");

    internal static string Get(string key, params object?[] args)
    {
        var selected = culture;
        var catalog = selected.Name == "zh-Hans" ? simplified.Value : english.Value;
        var text = catalog.GetValueOrDefault(key) ?? english.Value.GetValueOrDefault(key) ?? key;
        return args.Length == 0 ? text : string.Format(selected, text, args);
    }

    internal static FrozenDictionary<string, string> ReadCatalog(string language)
    {
        var assembly = typeof(Strings).Assembly;
        var suffix = "." + language + ".json";
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resource.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var contents = JsonDocument.Parse(stream);
            foreach (var entry in contents.RootElement.EnumerateObject())
            {
                var value = entry.Value.GetString();
                if (string.IsNullOrWhiteSpace(value) || !catalog.TryAdd(entry.Name, value))
                    throw new InvalidDataException("Invalid tray string catalog.");
            }
        }
        if (catalog.Count == 0)
            throw new InvalidDataException("Missing tray string catalog.");
        return catalog.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
