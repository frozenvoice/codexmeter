using System.Diagnostics;
using ProMeter.Codex;

namespace ProMeter.Tests;

public class CodexAppServerClientTests
{
    [Fact]
    public async Task ExecutableNotFound_ReturnsCodexNotFound()
    {
        var client = new CodexAppServerClient(new MissingProcessFactory());
        var session = await client.ReadQuotaAsync(
            new CodexLaunchCommand(@"C:\missing\codex.exe", "app-server --stdio", @"C:\missing\codex.exe", false),
            "1.0.0",
            CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.CodexNotFound, session.Status);
        Assert.True(session.ProcessCleanedUp);
    }

    [Fact]
    public async Task InitializeIsSentBeforeAccountRequests()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = CodexScript.Standard };
        var session = await new CodexAppServerClient(factory).ReadQuotaAsync(DummyCommand(), "1.2.3", CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, session.Status);
        Assert.Equal(new[] { "initialize", "initialized", "account/read", "account/rateLimits/read" }, session.SentMethods);
        var received = factory.LastProcess!.Received.Select(line => JsonNode.Parse(line)!["method"]!.ToString()).ToList();
        Assert.Equal("initialize", received[0]);
        Assert.Contains("initialized", received);
        Assert.DoesNotContain(received, method => method.StartsWith("thread/", StringComparison.Ordinal) || method.StartsWith("turn/", StringComparison.Ordinal));
        Assert.Contains("1.2.3", factory.LastProcess.Received[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterleavedNotifications_AreIgnoredAndIdsMatch()
    {
        var factory = new ScriptedCodexProcessFactory
        {
            Responder = line =>
            {
                var method = JsonNode.Parse(line)?["method"]?.ToString();
                return method switch
                {
                    "initialize" =>
                    [
                        """{"method":"thread/started","params":{}}""",
                        """{"id":1,"result":{"ok":true}}"""
                    ],
                    "account/read" =>
                    [
                        """{"method":"item/completed","params":{}}""",
                        """{"id":"2","result":{"loggedIn":true}}"""
                    ],
                    "account/rateLimits/read" => ["""{"id":3,"result":{"rateLimits":{"primary":{"usedPercent":1,"windowDurationMins":300}}}}"""],
                    _ => []
                };
            }
        };
        var session = await new CodexAppServerClient(factory).ReadQuotaAsync(DummyCommand(), "1.0.0", CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, session.Status);
        Assert.NotNull(session.RateLimitsResult);
    }

    [Fact]
    public async Task Timeout_ReturnsPromptly()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = _ => [], ResponseDelay = TimeSpan.FromSeconds(60) };
        var started = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var session = await new CodexAppServerClient(factory).ReadQuotaAsync(DummyCommand(), "1.0.0", cts.Token);
        started.Stop();
        Assert.Equal(CodexQuotaStatus.Cancelled, session.Status);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(session.ProcessCleanedUp);
        Assert.True(factory.LastProcess!.KillCalled || factory.LastProcess.HasExited);
    }

    [Fact]
    public async Task CleanupRunsAfterSuccess()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = CodexScript.Standard };
        var session = await new CodexAppServerClient(factory).ReadQuotaAsync(DummyCommand(), "1.0.0", CancellationToken.None);
        Assert.True(session.ProcessCleanedUp);
        Assert.True(factory.LastProcess!.HasExited || factory.LastProcess.KillCalled);
    }

    [Fact]
    public void Stderr_IsBoundedAndSanitized()
    {
        var text = CodexProtocol.SanitizeDiagnostic("Authorization: Bearer secret-token " + new string('x', 8000));
        Assert.DoesNotContain("Bearer secret-token", text, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= CodexProtocol.MaxStderrBytes + 8);
    }

    [Fact]
    public async Task RealNodeServer_CleansUpAfterSuccess()
    {
        if (!TryNode(out var node, out var script))
        {
            return;
        }

        var command = new CodexLaunchCommand(node, $"\"{script}\"", script, false);
        var client = new CodexAppServerClient(new CodexProcessFactory());
        var session = await client.ReadQuotaAsync(command, "1.0.0", CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, session.Status);
        Assert.True(session.ProcessCleanedUp);
        Assert.DoesNotContain("thread/", string.Join(",", session.SentMethods), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CmdWrapper_LaunchesAndCleansUp()
    {
        if (!TryNode(out var node, out var script))
        {
            return;
        }

        var cmdPath = Path.Combine(Path.GetTempPath(), $"prometer-codex-{Guid.NewGuid():N}.cmd");
        await File.WriteAllTextAsync(cmdPath, $"@echo off{Environment.NewLine}\"{node}\" \"{script}\" %*{Environment.NewLine}");
        var files = new MemoryCodexFileSystem();
        files.Files.Add(Path.GetFullPath(cmdPath));
        var command = CodexExecutableLocator.ValidateConfigured(cmdPath, files);
        Assert.NotNull(command);
        Assert.True(command.UsesCmd);
        var session = await new CodexAppServerClient(new CodexProcessFactory())
            .ReadQuotaAsync(command, "1.0.0", CancellationToken.None);
        Assert.Equal(CodexQuotaStatus.Available, session.Status);
        Assert.True(session.ProcessCleanedUp);
        File.Delete(cmdPath);
    }

    [Fact]
    public async Task HangProcess_CancelCleansTree()
    {
        if (!TryNode(out var node, out _, out var hang))
        {
            return;
        }

        var command = new CodexLaunchCommand(node, $"\"{hang}\"", hang, false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var session = await new CodexAppServerClient(new CodexProcessFactory())
            .ReadQuotaAsync(command, "1.0.0", cts.Token);
        Assert.True(session.Status is CodexQuotaStatus.Cancelled or CodexQuotaStatus.TimedOut);
        Assert.True(session.ProcessCleanedUp);
    }

    private static CodexLaunchCommand DummyCommand() =>
        new(@"C:\Tools\codex.exe", "app-server --stdio", @"C:\Tools\codex.exe", false);

    private static bool TryNode(out string node, out string script) => TryNode(out node, out script, out _);

    private static bool TryNode(out string node, out string script, out string hang)
    {
        node = "node";
        script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-codex-app-server.js");
        hang = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hang-codex-app-server.js");
        try
        {
            var where = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in where.Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(dir, "node.exe");
                if (File.Exists(candidate) && File.Exists(script))
                {
                    node = candidate;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private sealed class MissingProcessFactory : ICodexProcessFactory
    {
        public ICodexProcess Start(CodexLaunchCommand command) =>
            throw new FileNotFoundException("Codex executable was not found.", command.ResolvedExecutable);
    }
}
