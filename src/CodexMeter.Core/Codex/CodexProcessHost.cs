using System.Diagnostics;

namespace CodexMeter.Codex;

public interface ICodexProcess : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken cancellationToken);
    Task<string?> ReadLineAsync(int maxBytes, CancellationToken cancellationToken);
    Task DrainStderrAsync(System.Text.StringBuilder sink, int maxBytes, CancellationToken cancellationToken);
    bool HasExited { get; }
    bool KillCalled { get; }
    int? ProcessId { get; }
    string FileName { get; }
    string Arguments { get; }
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
    void KillTree();
}

public interface ICodexProcessFactory
{
    ICodexProcess Start(CodexLaunchCommand command);
}

public sealed class CodexProcessFactory : ICodexProcessFactory
{
    public ICodexProcess Start(CodexLaunchCommand command)
    {
        if (!Path.IsPathRooted(command.ResolvedExecutable)
            || !File.Exists(command.ResolvedExecutable))
        {
            throw new FileNotFoundException("Codex executable was not found.", command.ResolvedExecutable);
        }

        if (!Path.IsPathRooted(command.FileName))
        {
            throw new InvalidOperationException("Codex launch path must be absolute.");
        }

        var start = CreateStartInfo(command);
        var process = Process.Start(start)
                      ?? throw new InvalidOperationException("Failed to start Codex App Server.");
        return new RealCodexProcess(process, command);
    }

    public static ProcessStartInfo CreateStartInfo(CodexLaunchCommand command)
    {
        var start = new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false)
        };

        if (command.CodexHome is { } home)
        {
            start.Environment["CODEX_HOME"] = CodexHomeDiscovery.Normalize(home)
                ?? throw new ArgumentException("Codex home must be an absolute directory.");
            // API-key environment overrides must not select a different account.
            start.Environment.Remove("CODEX_API_KEY");
            start.Environment.Remove("OPENAI_API_KEY");
            start.WorkingDirectory = home;
            var options = " -c analytics.enabled=false";
            if (command.ManagedHome) options += " -c cli_auth_credentials_store=file";
            // The npm shim's outer cmd quote must remain outside all arguments.
            start.Arguments = command.UsesCmd
                ? command.Arguments[..^1] + options + "\""
                : command.Arguments + options;
        }
        return start;
    }
}

internal sealed class RealCodexProcess : ICodexProcess
{
    private readonly Process _process;

    public RealCodexProcess(Process process, CodexLaunchCommand command)
    {
        _process = process;
        FileName = command.FileName;
        Arguments = command.Arguments;
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> ReadLineAsync(int maxBytes, CancellationToken cancellationToken) =>
        ReadBoundedAsync(_process.StandardOutput, maxBytes, cancellationToken);

    public async Task DrainStderrAsync(System.Text.StringBuilder sink, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new char[256];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _process.StandardError.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }

                // Continue draining after the diagnostic budget: a full stderr pipe must not stall login.
                var remaining = maxBytes - System.Text.Encoding.UTF8.GetByteCount(sink.ToString());
                var take = Math.Min(read, remaining);
                while (take > 0 && System.Text.Encoding.UTF8.GetByteCount(buffer, 0, take) > remaining) take--;
                if (take > 0) sink.Append(buffer, 0, take);
            }
        }
        catch
        {
        }
    }
    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }

    public bool KillCalled { get; private set; }
    public int? ProcessId
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.Id : _process.Id;
            }
            catch
            {
                return null;
            }
        }
    }
    public string FileName { get; }
    public string Arguments { get; }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return HasExited;
        }
    }

    public void KillTree()
    {
        KillCalled = true;
        try
        {
            if (!HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup; callers still wait/dispose.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch
            {
            }

            if (!HasExited)
            {
                var exited = await WaitForExitAsync(
                    TimeSpan.FromMilliseconds(CodexProtocol.GracefulShutdownTimeoutMs),
                    CancellationToken.None).ConfigureAwait(false);
                if (!exited)
                {
                    KillTree();
                    await WaitForExitAsync(
                        TimeSpan.FromMilliseconds(CodexProtocol.GracefulShutdownTimeoutMs),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    internal static async Task<string?> ReadBoundedAsync(
        TextReader reader,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        var one = new char[1];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            if (one[0] == '\n')
            {
                if (builder.Length > 0 && builder[^1] == '\r')
                {
                    builder.Length--;
                }

                return builder.ToString();
            }

            builder.Append(one[0]);
            if (System.Text.Encoding.UTF8.GetByteCount(builder.ToString()) > maxBytes)
            {
                throw new CodexProtocolException("JSONL line exceeded the safe maximum size.");
            }
        }
    }
}
