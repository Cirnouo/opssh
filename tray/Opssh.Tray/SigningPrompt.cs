using System.Globalization;
using System.Text;

namespace Opssh.Tray;

internal sealed class SigningPrompt(Approval approval)
{
    private static readonly IReadOnlyList<string> cachedScopes = Array.AsReadOnly(new[] { "app", "once", "deny" });
    private static readonly IReadOnlyList<string> uncachedScopes = Array.AsReadOnly(new[] { "once", "deny" });
    private bool consumed;

    internal string Nonce { get; } = Guid.NewGuid().ToString("N");
    internal Approval Approval { get; } = approval;
    internal ProcessIdentity Process => Approval.Chain[Approval.AppCandidate];
    internal string Application
    {
        get
        {
            const int limit = 28;
            var name = SafeText(Path.GetFileName(Process.Path));
            if (name.Length <= limit)
                return name;
            var cut = char.IsHighSurrogate(name[limit - 1]) ? limit - 1 : limit;
            return name[..cut] + "…";
        }
    }
    internal string KeyName => Approval.NameHint is { } hint ? Strings.Get("Approval.UnnamedKey", hint) : SafeText(Approval.Name);
    internal string Fingerprint => Approval.Fingerprint;
    internal IReadOnlyList<string> Scopes => Approval.CacheAllowed ? cachedScopes : uncachedScopes;

    internal bool TryConsume(string nonce, string scope, DateTimeOffset now)
    {
        if (consumed || now >= Approval.Deadline || nonce != Nonce || !Scopes.Contains(scope))
            return false;
        consumed = true;
        return true;
    }

    private static string SafeText(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator || rune.Value is 0xfffe or 0xffff)
                result.Append('�');
            else
                result.Append(rune);
        }
        return result.ToString();
    }
}
