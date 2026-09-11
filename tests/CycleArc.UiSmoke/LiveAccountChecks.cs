using System.Diagnostics;
using System.IO;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.UiSmoke;

internal static class LiveAccountChecks
{
    // Explicit opt-in only. Uses production account/login/quota code, never a model turn.
    // Prints projected status/percentages only; never protocol bodies, emails, URLs or credentials.
    public static async Task<int> RunAsync(string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        var token = deadline.Token;
        var locator = new CodexExecutableLocator(new WindowsCodexFileSystem());
        var configured = new SettingsStore().Load().CodexExePath;
        var command = locator.Locate(configured);
        if (command is null) { Console.WriteLine("LIVE: CodexNotFound"); return 1; }
        var client = new CodexAppServerClient();
        var initial = await client.ReadQuotaAsync(command with { CodexHome = CodexHomeDiscovery.DefaultHome }, "live-check", token);
        var initialIdentity = CodexAccountIdentity.Parse(initial.AccountResult);
        Console.WriteLine($"LIVE existing: {initialIdentity.Status}; process cleaned={initial.ProcessCleanedUp}");
        if (initialIdentity.Status != CodexQuotaStatus.Available) return 1;

        var store = new CodexAccountStore();
        var manager = new CodexAccountManager(store, CodexHomeDiscovery.DefaultHome,
            profile => new CodexQuotaService(locator, new CodexAppServerClient(),
                new CodexSnapshotStore(store.SnapshotPath(profile)), "live-check", profile: profile), () => configured);
        if (mode is "login" or "relogin")
        {
            var profileId = mode == "relogin" ? manager.Accounts.LastOrDefault(a => a.Profile.IsManaged)?.Profile.Id : null;
            var login = await manager.LoginAsync(profileId, "", (uri, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                Console.WriteLine("BROWSER_LOGIN_REQUIRED: Complete official login with a different Codex account.");
                return Task.CompletedTask;
            }, token);
            Console.WriteLine($"LIVE login: {login.Status}");
            if (login.Status != CodexQuotaStatus.Available) return 1;
        }
        await manager.RefreshManuallyAsync(token);
        var accounts = manager.Accounts;
        for (var i = 0; i < accounts.Count; i++)
        {
            var item = accounts[i];
            Console.WriteLine($"LIVE profile {i + 1}: managed={item.Profile.IsManaged}; status={item.Snapshot.Status}; "
                + $"windows={item.Snapshot.Windows.Count}; used="
                + string.Join(",", item.Snapshot.Windows.Select(w => w.UsedPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")));
        }
        var after = await client.ReadAccountAsync(command with { CodexHome = CodexHomeDiscovery.DefaultHome }, "live-check", token);
        var originalUnchanged = initialIdentity.Fingerprint == CodexAccountIdentity.Parse(after.AccountResult).Fingerprint;
        var distinct = accounts.Where(a => a.Snapshot.Status == CodexQuotaStatus.Available)
            .Select(a => a.Snapshot.IdentityFingerprint).OfType<string>().Distinct().Count();
        var isolatedHomes = accounts.Select(a => a.Profile.HomePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == accounts.Count;
        Console.WriteLine($"LIVE verification: original identity unchanged={originalUnchanged}; distinct signed-in identities={distinct}; separate home paths={isolatedHomes}; final process cleaned={after.ProcessCleanedUp}");
        return originalUnchanged && isolatedHomes && (mode == "read" || distinct >= 2) ? 0 : 2;
    }
}
