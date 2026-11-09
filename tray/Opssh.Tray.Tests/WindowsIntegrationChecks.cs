using System.Text;
using Opssh.Tray;

internal static class WindowsIntegrationChecks
{
    internal static async Task RunAsync(CancellationToken token)
    {
        CheckAccounts();
        CheckStartup();
        CheckNotification();
        await CheckReadinessAsync(token);
        await DefaultAgentChecks.RunAsync(token);
    }

    private static void CheckNotification()
    {
        var now = DateTimeOffset.UnixEpoch;
        StartupNotificationAction? pending = StartupNotificationAction.Create("authorize", now);
        var nonce = pending.Nonce;
        Require(!StartupNotificationAction.TryConsume(ref pending, "old", "primary", now, out _) &&
            !StartupNotificationAction.TryConsume(ref pending, nonce, "unknown", now, out _),
            "reject invalid notification actions");
        Require(StartupNotificationAction.TryConsume(ref pending, nonce, "primary", now, out var action) &&
            action == "authorize" &&
            !StartupNotificationAction.TryConsume(ref pending, nonce, "primary", now, out _), "one-use notification action");
        pending = StartupNotificationAction.Create("choose", now);
        Require(StartupNotificationAction.TryConsume(ref pending, pending.Nonce, "later", now, out action) &&
            pending is null && action is null, "dismissal consumes without activating");
        pending = StartupNotificationAction.Create("choose", now);
        Require(!StartupNotificationAction.TryConsume(ref pending, pending.Nonce, "primary", now.AddMinutes(5), out _) &&
            pending is null, "expiration retires the notification");
    }

    private static void CheckAccounts()
    {
        const string id = "aaaaaaaaaaaaaaaaaaaaaaaaaa";
        var result = OnePasswordAccounts.Parse(Encoding.UTF8.GetBytes("""
            [{"account_uuid":"aaaaaaaaaaaaaaaaaaaaaaaaaa","url":"team.1password.test","email":"person@example.test"},
             {"user_uuid":"bbbbbbbbbbbbbbbbbbbbbbbbbb","url":"other.1password.test"},
             {"account_uuid":"cccccccccccccccccccccccccc","url":"https://person@example.test/"}]
            """));
        Require(result is { Status: AccountListStatus.Ready, Accounts.Count: 1 } &&
            result.Accounts[0] == new DesktopAccount(id, "team.1password.test"), "account UUID and safe label only");
        Require(OnePasswordAccounts.Parse("[]"u8.ToArray()).Status == AccountListStatus.Empty,
            "empty account list");
        Require(OnePasswordAccounts.Parse("{}"u8.ToArray()).Status == AccountListStatus.InvalidResponse,
            "invalid account list root");
        Require(OnePasswordAccounts.Parse(new byte[65537]).Status == AccountListStatus.InvalidResponse,
            "oversized account response");
        Require(OnePasswordAccounts.Parse(new byte[] { (byte)'[', 0xff, (byte)']' }).Status == AccountListStatus.InvalidResponse,
            "malformed account response encoding");
        Require(OnePasswordAccounts.Parse("""
            [{"account_uuid":"aaaaaaaaaaaaaaaaaaaaaaaaaa","account_uuid":"bbbbbbbbbbbbbbbbbbbbbbbbbb","url":"team.1password.test"}]
            """u8.ToArray()).Status == AccountListStatus.Empty, "ambiguous UUID row");
        Require(OnePasswordAccounts.Parse("""
            [{"account_uuid":"aaaaaaaaaaaaaaaaaaaaaaaaaa","url":"team.1password.test"},
             {"account_uuid":"AAAAAAAAAAAAAAAAAAAAAAAAAA","url":"other.1password.test"}]
            """u8.ToArray()).Status == AccountListStatus.Empty, "ambiguous account identity");
        var start = OnePasswordAccounts.StartInfo(@"C:\Fixture\op.exe", @"C:\Fixture\empty");
        Require(!start.UseShellExecute && start.CreateNoWindow &&
            start.ArgumentList.SequenceEqual(new[] { "account", "list", "--format=json" }), "fixed CLI action");
        Require(start.Environment["OP_CONFIG_DIR"] == @"C:\Fixture\empty" &&
            start.Environment["OP_BIOMETRIC_UNLOCK_ENABLED"] == "true" &&
            start.Environment["OP_CACHE"] == "false" &&
            start.Environment.Keys.Count(key => key.StartsWith("OP_", StringComparison.OrdinalIgnoreCase)) == 3,
            "isolated CLI environment");
    }

    private static void CheckStartup()
    {
        const string exe = @"C:\Program Files\Fixture\opssh-tray.exe";
        var command = LoginStartup.Command(exe);
        Require(command == "\"C:\\Program Files\\Fixture\\opssh-tray.exe\"", "quoted login executable");
        Require(LoginStartup.Classify(null, command) == LoginStartupStatus.Off &&
            LoginStartup.Classify(command, command) == LoginStartupStatus.On &&
            LoginStartup.Classify(@"C:\Other\opssh.exe", command) == LoginStartupStatus.Conflict &&
            LoginStartup.Command(@"relative\opssh-tray.exe") is null &&
            LoginStartup.Command("C:\\" + new string('a', 260) + ".exe") is null, "registration ownership");
    }

    private static async Task CheckReadinessAsync(CancellationToken token)
    {
        var probe = false;
        var subscribed = false;
        var signaled = false;
        await OnePasswordReadiness.ObserveAsync(() => probe, (check, stop) =>
        {
            subscribed = true;
            Require(!check(), "process initially absent");
            probe = true;
            signaled = check();
            return Task.FromResult(signaled);
        }, _ => throw new Exception("Fallback ran after event success."), token);
        Require(subscribed && signaled, "event readiness path");

        var polls = 0;
        probe = false;
        await OnePasswordReadiness.ObserveAsync(() => probe, (_, _) => Task.FromResult(false), _ =>
        {
            polls++;
            probe = true;
            return Task.CompletedTask;
        }, token);
        Require(polls == 1, "fallback detects process");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await OnePasswordReadiness.ObserveAsync(() => false, (_, _) => throw new Exception("Subscribed after cancellation."),
                _ => throw new Exception("Polled after cancellation."), canceled.Token);
            throw new Exception("Readiness ignored cancellation.");
        }
        catch (OperationCanceledException) { }
    }

    private static void Require(bool condition, string assertion)
    {
        if (!condition)
            throw new Exception("Windows integration check failed: " + assertion);
    }
}
