using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CycleArc.Providers.Claude;

public enum ClaudeAuthStatus { SignedIn, SignedOut, NotInstalled, Unsupported, InvalidResponse, Failed, TimedOut, Cancelled }

// Authentication metadata comes from the official CLI. Credentials never enter CycleArc.
public sealed record ClaudeAuthentication(ClaudeAuthStatus Status, string? Email = null,
    string? Plan = null, string? Fingerprint = null)
{
    public static ClaudeAuthentication Parse(string json, int exitCode)
    {
        try
        {
            if (json.Length > 16384) return new(ClaudeAuthStatus.InvalidResponse);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)
                || !root.TryGetProperty("loggedIn", out var loggedIn)
                || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new(ClaudeAuthStatus.InvalidResponse);
            if (!loggedIn.GetBoolean()) return new(exitCode is 0 or 1 ? ClaudeAuthStatus.SignedOut : ClaudeAuthStatus.Failed);
            if (exitCode != 0) return new(ClaudeAuthStatus.Failed);
            var method = String(root, "authMethod", 80);
            var provider = String(root, "apiProvider", 80);
            if (method is null || provider is null) return new(ClaudeAuthStatus.InvalidResponse);
            if (method != "claude.ai" || provider != "firstParty") return new(ClaudeAuthStatus.Unsupported);
            var email = String(root, "email", 320);
            var org = String(root, "orgId", 200, optional: true);
            var plan = String(root, "subscriptionType", 80, optional: true);
            if (string.IsNullOrWhiteSpace(email)) return new(ClaudeAuthStatus.InvalidResponse);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.ToLowerInvariant() + "\n" + org + "\n" + plan)));
            return new(ClaudeAuthStatus.SignedIn, email, plan, hash);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
        { return new(ClaudeAuthStatus.InvalidResponse); }
    }

    private static string? String(JsonElement root, string name, int max, bool optional = false)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return optional ? null : throw new InvalidDataException();
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        var text = value.GetString()!;
        if (text.Length > max || text.Any(char.IsControl)) throw new InvalidDataException();
        return text;
    }
}

public interface IClaudeCli
{
    string? FindExecutable();
    Task<ClaudeAuthentication> AuthenticateAsync(string executable, string? configDirectory, bool login, CancellationToken token);
}

public sealed class ClaudeCli : IClaudeCli
{
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(8);

    public string? FindExecutable()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")]);
        foreach (var directory in dirs)
        {
            if (ClaudeConnectionPaths.Normalize(directory.Trim(' ', '"')) is not { } full) continue;
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                var path = Path.Combine(full, name);
                if (File.Exists(path) && IsExecutablePath(path)) return path;
            }
        }
        return null;
    }

    public static bool IsExecutablePath(string path) => ClaudeConnectionPaths.Normalize(path) is not null
        && Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".cmd"
        && (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || !path.Any(c => c is '"' or '%' or '!' or '^'));

    public static ProcessStartInfo StartInfo(string executable, string? configDirectory, bool login)
    {
        if (!IsExecutablePath(executable) || ClaudeConnectionPaths.Normalize(configDirectory ?? ClaudeConnectionPaths.ImplicitDirectory) is not { } root)
            throw new ArgumentException("Invalid Claude executable or configuration directory.");
        var arguments = login ? "auth login --claudeai" : "auth status --json";
        var batch = Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo
        {
            FileName = batch ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : executable,
            Arguments = batch ? "/d /s /c \"\"" + executable + "\" " + arguments + "\"" : arguments,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = root
        };
        // An unset CLAUDE_CONFIG_DIR is not equivalent to explicitly setting ~/.claude:
        // the official CLI resolves its login metadata differently in those two modes.
        if (configDirectory is null) start.Environment.Remove("CLAUDE_CONFIG_DIR");
        else start.Environment["CLAUDE_CONFIG_DIR"] = root;
        // Scope authentication to this Claude home, without copying or exposing credentials.
        foreach (var name in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN",
                     "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" })
            start.Environment.Remove(name);
        return start;
    }

    public async Task<ClaudeAuthentication> AuthenticateAsync(string executable, string? configDirectory, bool login, CancellationToken token)
    {
        if (!File.Exists(executable)) return new(ClaudeAuthStatus.NotInstalled);
        var root = configDirectory ?? ClaudeConnectionPaths.ImplicitDirectory;
        if (!Directory.Exists(root))
        {
            if (!login) return new(ClaudeAuthStatus.SignedOut);
            Directory.CreateDirectory(root);
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(login ? LoginTimeout : StatusTimeout);
        try
        {
            var result = await RunAsync(StartInfo(executable, configDirectory, login), login ? 0 : 16384, bounded.Token).ConfigureAwait(false);
            if (login)
            {
                if (result.ExitCode != 0) return new(ClaudeAuthStatus.Failed);
                // Browser completion/exit status alone must never imply an authenticated account.
                return await AuthenticateAsync(executable, configDirectory, false, bounded.Token).ConfigureAwait(false);
            }
            return ClaudeAuthentication.Parse(result.Output, result.ExitCode);
        }
        catch (OperationCanceledException) { return new(token.IsCancellationRequested ? ClaudeAuthStatus.Cancelled : ClaudeAuthStatus.TimedOut); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or ArgumentException)
        { return new(ClaudeAuthStatus.Failed); }
    }

    internal static async Task<(int ExitCode, string Output)> RunAsync(ProcessStartInfo start, int captureLimit,
        CancellationToken token, ReadOnlyMemory<byte>? input = null)
    {
        using var process = Process.Start(start) ?? throw new IOException("Claude process could not start.");
        using var cancel = token.Register(() => Kill(process));
        var stdout = DrainAsync(process.StandardOutput, captureLimit, token);
        var stderr = DrainAsync(process.StandardError, 0, token);
        try
        {
            if (input is { } bytes)
            {
                await process.StandardInput.BaseStream.WriteAsync(bytes, token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var output = await stdout.WaitAsync(token).ConfigureAwait(false);
            await stderr.WaitAsync(token).ConfigureAwait(false);
            return (process.ExitCode, output);
        }
        finally
        {
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            process.StandardInput.Dispose(); process.StandardOutput.Dispose(); process.StandardError.Dispose();
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        var tooLong = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (limit == 0) continue; // Login URLs, codes and errors are drained without retaining/logging them.
            if (count > limit - result.Length) tooLong = true;
            result.Append(buffer, 0, Math.Min(count, limit - result.Length));
        }
        return tooLong ? "" : result.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
