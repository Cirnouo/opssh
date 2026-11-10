using System.Diagnostics;

namespace Opssh.Tray;

internal static class SigningNotificationChecks
{
    internal static void Run()
    {
        using var process = Process.GetCurrentProcess();
        var verified = new ProcessIdentity((uint)process.Id, (ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc(),
            Environment.ProcessPath ?? throw new InvalidOperationException("Fixture process path is unavailable."));
        using var matching = ApplicationIcon.OpenVerifiedProcess(verified, out _);
        Require(matching is not null, "native icon lookup verifies the current process handle");
        using var stale = ApplicationIcon.OpenVerifiedProcess(verified with { Created = verified.Created - 1 }, out _);
        Require(stale is null, "native icon lookup rejects a stale creation FILETIME for a live PID");

        var now = DateTimeOffset.UnixEpoch;
        var approval = new Approval("request", "Fixture <key>", "SHA256:fixture-fingerprint",
            [new(100, 2, @"C:\fixture\ssh.exe"), new(200, 1, @"C:\fixture\app.exe")],
            now.AddMinutes(1), true, AppCandidate: 1);
        var prompt = new SigningPrompt(approval);
        Require(prompt.Process.Pid == 200 && prompt.Application == "app.exe", "toast uses the selected executable basename");
        Require(prompt.Fingerprint == "SHA256:fixture-fingerprint", "toast keeps the full fingerprint");
        Require(prompt.Scopes.SequenceEqual(["app", "once", "deny"]), "cache-enabled toast action order");
        Require(!prompt.TryConsume("wrong-nonce", "app", now), "an invalid nonce cannot consume the prompt");
        Require(!prompt.TryConsume(prompt.Nonce, "host", now), "unknown choices cannot consume the prompt");
        Require(!prompt.TryConsume(prompt.Nonce, "all", now), "legacy all-apps choices cannot consume the prompt");
        Require(prompt.TryConsume(prompt.Nonce, "app", now), "selected application can be approved");
        Require(!prompt.TryConsume(prompt.Nonce, "once", now), "a consumed nonce cannot be replayed");

        var uncached = new SigningPrompt(approval with { CacheAllowed = false });
        Require(uncached.Nonce != prompt.Nonce && !uncached.TryConsume(prompt.Nonce, "once", now),
            "different pending requests use different nonces");
        Require(uncached.Scopes.SequenceEqual(["once", "deny"]), "cache-off offers only once and deny");
        var chinese = Strings.ReadCatalog("zh-Hans");
        Require(new[] { "Approval.AllowApp", "Approval.AllowOnce", "Approval.Deny" }
            .Select(key => chinese[key]).SequenceEqual(["授权此应用", "仅授权请求", "拒绝"]),
            "Simplified Chinese notification actions use the approved labels");
        Require(!uncached.TryConsume(uncached.Nonce, "app", now), "forged cache action is rejected");
        Require(!uncached.TryConsume(uncached.Nonce, "deny", approval.Deadline), "expired nonce is rejected");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
