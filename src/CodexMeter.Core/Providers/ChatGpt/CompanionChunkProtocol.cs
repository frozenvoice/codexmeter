using System.Text;

namespace CodexMeter.Providers.ChatGpt;

public static class CompanionChunkProtocol
{
    public const int MaxAssembledProjectedBytes = 16 * 1024 * 1024;
    public const int ChunkRawBytes = 384 * 1024;
    public const int MaxChunkFrameBytes = 700 * 1024;
    public const int MaxChunkCount = 48;
    public const string InvokeResultStart = "invokeResultStart";
    public const string InvokeResultChunk = "invokeResultChunk";
    public const string InvokeResultEnd = "invokeResultEnd";
    public const string ProtocolError = "chunk protocol error";

    public static bool IsChunkableOperation(CompanionOperation operation) =>
        operation is CompanionOperation.GetConversationHead
            or CompanionOperation.GetConversationFull
            or CompanionOperation.GetConversationLegacy
            or CompanionOperation.GetOlderConversationMessages;

    public static bool IsChunkFrame(string? type) =>
        string.Equals(type, InvokeResultStart, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, InvokeResultChunk, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, InvokeResultEnd, StringComparison.OrdinalIgnoreCase);

    public static ProviderResponse PayloadTooLargeResponse() =>
        new() { Status = 0, Error = "PayloadTooLarge", SchemaMismatch = false };

    public static ProviderResponse ProtocolErrorResponse() =>
        new() { Status = 0, Error = ProtocolError, SchemaMismatch = false };

    public static IReadOnlyList<string> SplitBodyToBase64Chunks(string body, int chunkRawBytes = ChunkRawBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        if (bytes.Length == 0)
        {
            return [];
        }

        var chunks = new List<string>((bytes.Length + chunkRawBytes - 1) / chunkRawBytes);
        for (var offset = 0; offset < bytes.Length; offset += chunkRawBytes)
        {
            var length = Math.Min(chunkRawBytes, bytes.Length - offset);
            chunks.Add(Convert.ToBase64String(bytes, offset, length));
        }

        return chunks;
    }

    public static int Utf8Length(string text) => Encoding.UTF8.GetByteCount(text);

    public static int SerializedFrameBytes(CompanionBridgeMessage message) =>
        Utf8Length(CompanionBridgeProtocol.Serialize(message));

    public static bool FrameFitsNativeLimit(CompanionBridgeMessage message) =>
        SerializedFrameBytes(message) <= MaxChunkFrameBytes
        && SerializedFrameBytes(message) <= CompanionBridgeProtocol.MaxNativeMessageBytes;

    public static IReadOnlyList<CompanionBridgeMessage> BuildFrames(
        string requestId,
        CompanionOperation operation,
        int status,
        string body,
        string? retryAfter = null,
        string? error = null,
        bool schemaMismatch = false)
    {
        var chunks = SplitBodyToBase64Chunks(body);
        var totalBytes = Utf8Length(body);
        var messages = new List<CompanionBridgeMessage>(chunks.Count + 2)
        {
            new()
            {
                Type = InvokeResultStart,
                RequestId = requestId,
                Operation = operation.ToString(),
                Chunked = true,
                ChunkCount = chunks.Count,
                TotalProjectedBytes = totalBytes,
                Status = status,
                RetryAfter = retryAfter,
                Error = error,
                SchemaMismatch = schemaMismatch
            }
        };

        for (var i = 0; i < chunks.Count; i++)
        {
            messages.Add(new CompanionBridgeMessage
            {
                Type = InvokeResultChunk,
                RequestId = requestId,
                Operation = operation.ToString(),
                ChunkIndex = i,
                ChunkCount = chunks.Count,
                Data = chunks[i]
            });
        }

        messages.Add(new CompanionBridgeMessage
        {
            Type = InvokeResultEnd,
            RequestId = requestId,
            Operation = operation.ToString()
        });

        return messages;
    }

    public static bool TryDecodeBase64(string? data, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(data))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(data);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed class CompanionChunkAssembly
{
    public required int Generation { get; init; }
    public required string RequestId { get; init; }
    public required CompanionOperation Operation { get; init; }
    public required int ChunkCount { get; init; }
    public required int TotalProjectedBytes { get; init; }
    public int? Status { get; init; }
    public string? RetryAfter { get; init; }
    public string? Error { get; init; }
    public bool SchemaMismatch { get; init; }
    public byte[]?[] Parts { get; }

    public int ReceivedCount { get; private set; }
    public int ReceivedBytes { get; private set; }

    public CompanionChunkAssembly(int chunkCount)
    {
        Parts = new byte[]?[chunkCount];
    }

    public bool TryAddChunk(int index, byte[] bytes, out ProviderResponse? failure)
    {
        failure = null;
        if (index < 0 || index >= ChunkCount)
        {
            failure = CompanionChunkProtocol.ProtocolErrorResponse();
            return false;
        }

        if (Parts[index] is { } existing)
        {
            if (existing.AsSpan().SequenceEqual(bytes))
            {
                return true;
            }

            failure = CompanionChunkProtocol.ProtocolErrorResponse();
            return false;
        }

        if (ReceivedBytes + bytes.Length > TotalProjectedBytes
            || ReceivedBytes + bytes.Length > CompanionChunkProtocol.MaxAssembledProjectedBytes)
        {
            failure = CompanionChunkProtocol.PayloadTooLargeResponse();
            return false;
        }

        Parts[index] = bytes;
        ReceivedCount++;
        ReceivedBytes += bytes.Length;
        return true;
    }

    public bool TryAssemble(out byte[] assembled, out ProviderResponse? failure)
    {
        assembled = [];
        failure = null;
        if (ReceivedCount != ChunkCount)
        {
            failure = CompanionChunkProtocol.ProtocolErrorResponse();
            return false;
        }

        for (var i = 0; i < Parts.Length; i++)
        {
            if (Parts[i] is null)
            {
                failure = CompanionChunkProtocol.ProtocolErrorResponse();
                return false;
            }
        }

        if (ReceivedBytes != TotalProjectedBytes)
        {
            failure = CompanionChunkProtocol.ProtocolErrorResponse();
            return false;
        }

        assembled = new byte[ReceivedBytes];
        var offset = 0;
        foreach (var part in Parts)
        {
            part!.CopyTo(assembled, offset);
            offset += part.Length;
        }

        return true;
    }
}
