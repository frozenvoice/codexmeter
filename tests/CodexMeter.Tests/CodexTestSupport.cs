using System.IO.Pipes;
using System.Text;
using CodexMeter.Codex;

namespace CodexMeter.Tests;

internal sealed class MemoryCodexFileSystem : ICodexFileSystem
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PathFolders { get; } = [];
    public List<string> CommonFolders { get; } = [];
    public List<string> ExistsChecks { get; } = [];

    public bool FileExists(string path)
    {
        ExistsChecks.Add(path);
        return Files.Contains(path);
    }

    public IReadOnlyList<string> PathEntries() => PathFolders;
    public IReadOnlyList<string> CommonDirectories() => CommonFolders;
}

internal sealed class ScriptedCodexProcessFactory : ICodexProcessFactory
{
    public Func<string, IReadOnlyList<string>>? Responder { get; set; }
    public TimeSpan ResponseDelay { get; set; }
    public CodexLaunchCommand? LastCommand { get; private set; }
    public ScriptedCodexProcess? LastProcess { get; private set; }
    public int StartCount { get; private set; }

    public ICodexProcess Start(CodexLaunchCommand command)
    {
        StartCount++;
        LastCommand = command;
        LastProcess = new ScriptedCodexProcess(Responder ?? (_ => []), ResponseDelay);
        return LastProcess;
    }
}

internal sealed class ScriptedCodexProcess : ICodexProcess
{
    private readonly AnonymousPipeServerStream _stdinServer = new(PipeDirection.In, HandleInheritability.None);
    private readonly AnonymousPipeClientStream _stdinClient;
    private readonly AnonymousPipeServerStream _stdoutServer = new(PipeDirection.Out, HandleInheritability.None);
    private readonly AnonymousPipeClientStream _stdoutClient;
    private readonly MemoryStream _stderr = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly Func<string, IReadOnlyList<string>> _responder;
    private readonly TimeSpan _delay;

    public ScriptedCodexProcess(Func<string, IReadOnlyList<string>> responder, TimeSpan delay)
    {
        _responder = responder;
        _delay = delay;
        _stdinClient = new AnonymousPipeClientStream(PipeDirection.Out, _stdinServer.GetClientHandleAsString());
        _stdoutClient = new AnonymousPipeClientStream(PipeDirection.In, _stdoutServer.GetClientHandleAsString());
        _pump = PumpAsync();
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken) =>
        CodexJsonlIo.WriteLineAsync(_stdinClient, line, cancellationToken);

    public Task<string?> ReadLineAsync(int maxBytes, CancellationToken cancellationToken) =>
        CodexJsonlIo.ReadBoundedLineAsync(_stdoutClient, maxBytes, cancellationToken);

    public Task DrainStderrAsync(StringBuilder sink, int maxBytes, CancellationToken cancellationToken)
    {
        var text = Encoding.UTF8.GetString(_stderr.ToArray());
        if (text.Length > maxBytes)
        {
            text = text[..maxBytes];
        }

        sink.Append(text);
        return Task.CompletedTask;
    }
    public bool HasExited { get; private set; }
    public bool KillCalled { get; private set; }
    public int? ProcessId => null;
    public string FileName { get; set; } = "scripted";
    public string Arguments { get; set; } = "";
    public List<string> Received { get; } = [];

    public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(HasExited);

    public void KillTree()
    {
        KillCalled = true;
        HasExited = true;
        _cts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (!HasExited)
        {
            KillTree();
        }

        _cts.Cancel();
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        await _stdinServer.DisposeAsync();
        await _stdinClient.DisposeAsync();
        await _stdoutServer.DisposeAsync();
        await _stdoutClient.DisposeAsync();
        await _stderr.DisposeAsync();
        _cts.Dispose();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await CodexJsonlIo.ReadBoundedLineAsync(_stdinServer, CodexProtocol.MaxJsonLineBytes, _cts.Token);
                if (line is null)
                {
                    HasExited = true;
                    return;
                }

                Received.Add(line);
                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay, _cts.Token);
                }

                foreach (var response in _responder(line))
                {
                    await CodexJsonlIo.WriteLineAsync(_stdoutServer, response, _cts.Token);
                }
            }
        }
        catch
        {
            HasExited = true;
        }
    }
}

internal static class CodexScript
{
    public static IReadOnlyList<string> Standard(string line)
    {
        var node = JsonNode.Parse(line) as JsonObject;
        var method = node?["method"]?.ToString();
        var id = node?["id"]?.ToString();
        return method switch
        {
            "initialize" => ["{\"id\":" + id + ",\"result\":{\"ok\":true}}"],
            "initialized" => ["{\"method\":\"session/ready\",\"params\":{}}"],
            "account/read" => ["{\"id\":" + id + ",\"result\":{\"loggedIn\":true,\"account\":{\"planType\":\"plus\"}}}"],
            "account/rateLimits/read" =>
            [
                "{\"method\":\"item/completed\",\"params\":{}}",
                "{\"id\":" + id + ",\"result\":{\"ordinaryUsageAllowed\":true,\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":42,\"windowDurationMins\":300,\"resetsAt\":1893456000},\"secondary\":{\"usedPercent\":31,\"windowDurationMins\":10080,\"resetsAt\":1894051200},\"planType\":\"pro\",\"rateLimitReachedType\":null},\"rateLimitsByLimitId\":{\"codex\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":42,\"windowDurationMins\":300,\"resetsAt\":1893456000},\"secondary\":{\"usedPercent\":31,\"windowDurationMins\":10080,\"resetsAt\":1894051200},\"planType\":\"pro\",\"rateLimitReachedType\":null}},\"rateLimitResetCredits\":{\"availableCount\":1,\"credits\":null}}}"
            ],
            _ => []
        };
    }
}
