using System.Globalization;
using System.Text;
using System.Text.Json;
using Opssh.Tray;

internal static class AppearanceChecks
{
    internal static void Run()
    {
        var english = Strings.ReadCatalog("en");
        var chinese = Strings.ReadCatalog("zh-Hans");
        Require(english.Count == chinese.Count, "both BCP 47 catalogs have equal coverage");
        foreach (var (key, text) in english)
        {
            Require(chinese.TryGetValue(key, out var translated), "missing zh-Hans string " + key);
            Require(CompositeFormat.Parse(text).MinimumArgumentCount == CompositeFormat.Parse(translated!).MinimumArgumentCount,
                "localized format arguments match for " + key);
        }
        Strings.Use("zh-Hans");
        Require(Strings.Language == "zh-Hans" && Strings.Get("Settings.Appearance") == chinese["Settings.Appearance"],
            "active Simplified Chinese catalog uses its canonical language tag");
        Strings.Use("en");
        Require(Strings.Language == "en" && Strings.Get("Settings.Appearance") == english["Settings.Appearance"],
            "active English catalog uses its canonical language tag");

        Require(CoreFeedback.ResultKey("public-lookup", "unexpected response") == "Error.PublicLookup",
            "command failure follows its stable feedback code");
        Require(CoreFeedback.ResultKey(null, "unrecognized provider response") == "Error.RequestRejected",
            "unknown response is replaced by localized generic feedback");
        Require(CoreFeedback.StartupKey(null, null) == "Error.StartFailed", "unknown startup feedback is generic");

        var directory = Path.Combine(Path.GetTempPath(), "opssh-preferences-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "tray-settings.json");
        var simplified = CultureInfo.GetCultureInfo("zh-CN");
        var englishCulture = CultureInfo.GetCultureInfo("en-GB");
        var traditional = CultureInfo.GetCultureInfo("zh-TW");
        try
        {
            var defaults = UiPreferences.Load(path);
            Require(defaults == new UiPreferences("system", "system", true), "first-run preferences use system appearance and check for updates");
            Require(defaults.ResolveLanguage(simplified) == "zh-Hans", "system language follows Simplified Chinese");
            Require(defaults.ResolveLanguage(englishCulture) == "en", "system language follows English");
            Require(defaults.ResolveLanguage(traditional) == "en", "system language falls back to English for Traditional Chinese");

            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{\"language\":\"en\",\"theme\":\"dark\"}");
            var legacy = UiPreferences.Load(path);
            Require(legacy == new UiPreferences("en", "dark", true), "legacy preferences preserve explicit appearance and enable update checks");
            Require(legacy.ResolveLanguage(simplified) == "en", "explicit English overrides the system language");
            Require(new UiPreferences("zh-Hans", "system").ResolveLanguage(englishCulture) == "zh-Hans",
                "explicit Simplified Chinese overrides the system language");

            File.WriteAllText(path, """{"version":1,"language":"en","theme":"light","autoCheckUpdates":false}""");
            Require(UiPreferences.Load(path) == new UiPreferences("en", "light", false),
                "version one preserves appearance and updates and defaults to no startup notification");

            foreach (var legacyAction in new[] { "notify", "request" })
            {
                File.WriteAllText(path,
                    $$"""{"version":2,"language":"zh-Hans","theme":"dark","autoCheckUpdates":false,"waitForPassword":true,"startupAction":"{{legacyAction}}"}""");
                var migrated = UiPreferences.Load(path);
                Require(migrated == new UiPreferences("zh-Hans", "dark", false) { WaitForPassword = true },
                    "version two preserves startup opt-in, appearance and updates");
                Require(migrated.TrySave(path), "save migrated preferences");
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                Require(document.RootElement.GetProperty("version").GetInt32() == 3 &&
                    !document.RootElement.TryGetProperty("startupAction", out _) &&
                    UiPreferences.Load(path) == migrated, "version three persists only the notification opt-in");
            }

            File.WriteAllText(path, """{"version":4,"language":"en","theme":"dark","waitForPassword":true}""");
            Require(UiPreferences.Load(path) == defaults, "fallback for an unsupported future version");

            var preference = new UiPreferences("system", "dark", false);
            Require(preference.TrySave(path), "save system language, dark theme and disabled update checks");
            Require(UiPreferences.Load(path) == preference, "appearance and update preferences persist after reload");
            Require(!new UiPreferences("zh-Hant", "dark").TrySave(path), "reject an unsupported locale");
            File.WriteAllText(path, "{\"language\":\"en\",\"theme\":\"invalid\"}");
            Require(UiPreferences.Load(path) == defaults, "fallback for invalid appearance preferences");
            File.WriteAllText(path, "{\"language\":\"en\",\"theme\":\"dark\",\"autoCheckUpdates\":\"false\"}");
            Require(UiPreferences.Load(path) == defaults, "fallback for a non-boolean update preference");
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory);
        }
        Console.WriteLine("Tray appearance checks passed (BCP 47 catalogs, localized feedback, preference persistence and fallback).");
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException("Tray appearance check failed: " + scenario);
    }
}
