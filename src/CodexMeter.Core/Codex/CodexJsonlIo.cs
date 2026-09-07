using System.Text;

namespace CodexMeter.Codex;

public static class CodexJsonlIo
{
    public static async Task<string?> ReadBoundedLineAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            }

            if (one[0] == (byte)'\n')
            {
                var bytes = buffer.ToArray();
                var length = bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes.Length - 1 : bytes.Length;
                return Encoding.UTF8.GetString(bytes, 0, length);
            }

            buffer.WriteByte(one[0]);
            if (buffer.Length > maxBytes)
            {
                throw new CodexProtocolException("JSONL line exceeded the safe maximum size.");
            }
        }
    }

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
