using System.Globalization;
using System.Text;
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
        try
        {
            Require(UiPreferences.Load(path, simplified) == new UiPreferences("zh-Hans", "system"), "first-run Simplified Chinese");
            Require(UiPreferences.Load(path, CultureInfo.GetCultureInfo("en-GB")) == new UiPreferences("en", "system"), "first-run English");
            var preference = new UiPreferences("zh-Hans", "dark");
            Require(preference.TrySave(path), "save language and theme");
            Require(UiPreferences.Load(path, CultureInfo.InvariantCulture) == preference, "preference persists after reload");
            Require(!new UiPreferences("zh-Hant", "dark").TrySave(path), "reject an unsupported locale");
            File.WriteAllText(path, "{\"language\":\"en\",\"theme\":\"invalid\"}");
            Require(UiPreferences.Load(path, simplified) == new UiPreferences("zh-Hans", "system"), "fallback for invalid preferences");
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
