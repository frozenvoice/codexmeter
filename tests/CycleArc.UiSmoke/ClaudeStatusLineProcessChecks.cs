using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using CycleArc.Codex;
using CycleArc.Providers.Claude;
using CycleArc.Services;

namespace CycleArc.UiSmoke;

internal static class ClaudeStatusLineProcessChecks
{
    public static void Run(string? executable = null)
    {
        executable ??= Path.Combine(RepositoryRoot(), "src", "CycleArc", "bin", "Release",
            "net8.0-windows10.0.17763.0", "CycleArc.exe");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Build the production executable before checking stdin.");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cyclearc-stdin-test-" + Guid.NewGuid().ToString("N"), "A space O'Brien $x`"));
        var accounts = new CodexAccountStore(root);
        var state = accounts.LoadOrMigrate(Path.Combine(root, "synthetic-codex-home"));
        var profile = accounts.NewClaude("Synthetic Claude");
        accounts.Save(state with { Version = 2, Profiles = state.Profiles.Append(profile).ToArray() });
        var store = new ClaudeStatusLineStore(accounts.ClaudeStatusLinePath(profile.Id), profile.Id);
        var now = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(new
        {
            transcript_path = "never-read-synthetic-path", session_id = "never-save-synthetic-session", prompt = "never-save-synthetic-prompt",
            rate_limits = new { five_hour = new { used_percentage = 23.5, resets_at = now.AddHours(5).ToUnixTimeSeconds() },
                seven_day = new { used_percentage = 41.2, resets_at = now.AddDays(7).ToUnixTimeSeconds() } }
        });
        // A regression that enters desktop startup exits at the mutex, before accessing
        // real settings/accounts. A correctly routed receiver works while it is held.
        using var mutex = new Mutex(false, LegacyInstallation.SingleInstanceMutexName);
        var owns = false;
        try
        {
            try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
            var result = RunProcess(executable, [ClaudeStatusLineCommand.Argument, profile.Id, "--data-root", root], json);
            Check(result.Code == 0 && result.Output.Contains("5h 23.5%", StringComparison.Ordinal), "Production stdin receiver failed.");
            Check(store.Read().State?.LastGood?.SevenDay?.UsedPercentage == 41.2, "Production receiver lost weekly data.");

            using var settings = JsonDocument.Parse(ClaudeStatusLineCommand.SettingsJson(executable, profile.Id, root));
            var command = settings.RootElement.GetProperty("statusLine").GetProperty("command").GetString()!;
            var encoded = command.Split(' ')[^1];
            result = RunProcess("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], json);
            Check(result.Code == 0 && result.Output.Contains("7d 41.2%", StringComparison.Ordinal), "Generated PowerShell statusLine command failed.");
            var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
            var checkedBash = File.Exists(bash);
            if (checkedBash)
            {
                result = RunProcess(bash, ["--noprofile", "--norc", "-c", command], json);
                Check(result.Code == 0 && result.Output.Contains("7d 41.2%", StringComparison.Ordinal), "Generated Git Bash statusLine command failed.");
            }
            result = RunProcess(executable, [ClaudeStatusLineCommand.Argument, profile.Id, "--data-root", root], "{broken");
            Check(result.Code == 1 && store.Read().State?.LastInputStatus == ClaudeInputStatus.Malformed, "Malformed production input was not rejected.");
            Check(new ClaudeQuotaService(store).Snapshot.Status == CodexQuotaStatus.Stale, "Malformed input discarded the last valid percentages.");
            var saved = File.ReadAllText(accounts.ClaudeStatusLinePath(profile.Id));
            Check(!saved.Contains("never-", StringComparison.Ordinal), "Raw statusLine metadata reached disk.");
            result = RunProcess(executable, [ClaudeStatusLineCommand.Argument, profile.Id, "--data-root", root], null);
            Check(result.Code == 1, "Production stdin deadline did not exit.");
            Check(!File.Exists(Path.Combine(root, "settings.json")), "Collector initialized desktop settings.");
            Console.WriteLine("PASS: production Claude stdin receiver, held desktop mutex, isolated registry, malformed-input retention, deadline, PowerShell"
                + (checkedBash ? " and Git Bash" : " (Git Bash not installed)") + "; no live account access.");
        }
        finally
        {
            if (owns) mutex.ReleaseMutex();
            var owned = Directory.GetParent(root)!.FullName;
            if (!owned.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(owned).StartsWith("cyclearc-stdin-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid temporary cleanup target.");
            if (Directory.Exists(owned)) Directory.Delete(owned, true);
        }
    }

    private static (int Code, string Output) RunProcess(string executable, string[] args, string? input)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start test receiver.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null) { process.StandardInput.Write(input); process.StandardInput.Close(); }
            if (!process.WaitForExit(12000)) throw new TimeoutException("StatusLine receiver did not exit.");
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            Check(!output.Result.Contains("never-", StringComparison.Ordinal) && !error.Result.Contains("never-", StringComparison.Ordinal),
                "Raw statusLine data reached process output.");
            return (process.ExitCode, output.Result);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CycleArc.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate production build.");
    }
}
