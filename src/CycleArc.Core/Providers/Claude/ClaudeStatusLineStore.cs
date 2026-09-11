using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CycleArc.Providers.Claude;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClaudeRateLimitSample(DateTimeOffset ReceivedAt, ClaudeRateLimit? FiveHour, ClaudeRateLimit? SevenDay);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClaudeStatusLineState(int Version, string Provider, string ProfileId, DateTimeOffset LastReceivedAt,
    ClaudeInputStatus LastInputStatus, ClaudeRateLimitSample? LastGood);

public sealed record ClaudeStatusLineRead(ClaudeStatusLineState? State, bool Unavailable = false);

/// <summary>Only projected quota metadata lives here; raw statusLine input never touches disk.</summary>
public sealed class ClaudeStatusLineStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;
    private readonly string _profileId;

    public ClaudeStatusLineStore(string path, string profileId)
    {
        if (!Guid.TryParseExact(profileId, "N", out _)) throw new ArgumentException("Invalid Claude profile.");
        _path = Path.GetFullPath(path);
        _profileId = profileId;
    }

    public ClaudeStatusLineRead Read()
    {
        if (TryRead(_path, out var state)) return new(state);
        if (TryRead(_path + ".bak", out state)) return new(state, true);
        return new(null, File.Exists(_path) || File.Exists(_path + ".bak"));
    }

    public async Task<ClaudeStatusLineState> RecordAsync(ClaudeStatusLineResult result, DateTimeOffset receivedAt, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // Independent statusLine processes can arrive concurrently. Serialize the read/merge/
        // atomic write, with bounded locking and automatic release if a process is cancelled.
        using var lease = await AcquireAsync(token).ConfigureAwait(false);
        var previous = Read().State;
        if (previous is not null && receivedAt < previous.LastReceivedAt) return previous;
        var sample = result.Status == ClaudeInputStatus.Available
            ? new ClaudeRateLimitSample(receivedAt, result.FiveHour, result.SevenDay) : previous?.LastGood;
        var state = new ClaudeStatusLineState(1, "Claude", _profileId, receivedAt, result.Status, sample);
        if (!Valid(state)) throw new InvalidDataException("Invalid projected Claude metadata.");
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, Options, token).ConfigureAwait(false);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (TryRead(_path, out _)) File.Replace(temp, _path, _path + ".bak", true);
            else File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return state;
    }

    private async Task<FileStream> AcquireAsync(CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
    }

    private bool TryRead(string path, out ClaudeStatusLineState? state)
    {
        state = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is 0 or > 8192) return false;
            state = JsonSerializer.Deserialize<ClaudeStatusLineState>(stream, Options);
            return state is not null && Valid(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }

    private bool Valid(ClaudeStatusLineState state)
    {
        if (state.Version != 1 || state.Provider != "Claude" || state.ProfileId != _profileId
            || !Enum.IsDefined(state.LastInputStatus) || state.LastReceivedAt <= DateTimeOffset.UnixEpoch) return false;
        if (state.LastGood is not { } good) return state.LastInputStatus != ClaudeInputStatus.Available;
        return good.ReceivedAt > DateTimeOffset.UnixEpoch && good.ReceivedAt <= state.LastReceivedAt
            && (state.LastInputStatus != ClaudeInputStatus.Available || good.ReceivedAt == state.LastReceivedAt)
            && (good.FiveHour is not null || good.SevenDay is not null)
            && (good.FiveHour is null || good.FiveHour.IsValid) && (good.SevenDay is null || good.SevenDay.IsValid);
    }
}
