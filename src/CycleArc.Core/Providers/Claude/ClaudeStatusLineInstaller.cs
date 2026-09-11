using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CycleArc.Codex;

namespace CycleArc.Providers.Claude;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClaudeBridgeOptions(int Version, string ProfileId, string ConfigDirectory,
    string CycleArcExecutable, string DataRoot, bool HadStatusLine, JsonObject? PreviousStatusLine);

public enum ClaudeSetupFailure { InvalidSettings, SettingsChanged, AlreadyLinked, ConnectionUnavailable }
public sealed class ClaudeSetupException(ClaudeSetupFailure failure) : Exception("Claude connection settings could not be updated.")
{
    public ClaudeSetupFailure Failure { get; } = failure;
}

public static class ClaudeStatusLineInstaller
{
    private const string Prefix = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ";
    private const string Marker = "# CycleArc automatic statusLine v1\n# ";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public const int MaxSettingsBytes = 1024 * 1024;

    public static string Payload(ClaudeBridgeOptions options) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(options));
    public static ClaudeBridgeOptions Decode(string payload)
    {
        try
        {
            if (payload.Length > 12000) throw new InvalidDataException();
            var options = JsonSerializer.Deserialize<ClaudeBridgeOptions>(Convert.FromBase64String(payload));
            if (options is null || !Valid(options)) throw new InvalidDataException();
            return options;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException or ArgumentException)
        { throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings); }
    }

    public static string Command(ClaudeBridgeOptions options)
    {
        if (!Valid(options)) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        var payload = Payload(options);
        var path = options.CycleArcExecutable.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var script = Marker + payload + "\n"
            + "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::InputEncoding; "
            + "$input | & '" + path + "' '" + ClaudeStatusLineBridge.Argument + "' '" + payload
            + "' | ForEach-Object { $_ }; exit $LASTEXITCODE";
        var command = Prefix + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        if (command.Length > 28000) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        return command;
    }

    public static bool TryRead(string command, out ClaudeBridgeOptions? options)
    {
        options = null;
        try
        {
            if (!command.StartsWith(Prefix, StringComparison.Ordinal) || command.Length > 28000) return false;
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(command[Prefix.Length..]));
            if (!script.StartsWith(Marker, StringComparison.Ordinal)) return false;
            var end = script.IndexOf('\n', Marker.Length);
            if (end < 0) return false;
            var parsed = Decode(script[Marker.Length..end]);
            if (!string.Equals(Command(parsed), command, StringComparison.Ordinal)) return false;
            options = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ClaudeSetupException or ArgumentException) { return false; }
    }

    // Recognize only the exact command emitted by the previous manual setup window.
    private static string? LegacyTarget(string command)
    {
        try
        {
            if (!command.StartsWith(Prefix, StringComparison.Ordinal) || command.Length > 28000) return null;
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(command[Prefix.Length..]));
            const string start = "$input | & '";
            const string middle = "' '--claude-statusline' '";
            var from = script.IndexOf(start, StringComparison.Ordinal);
            var divider = script.IndexOf(middle, from < 0 ? 0 : from + start.Length, StringComparison.Ordinal);
            if (from < 0 || divider < 0) return null;
            var path = script[(from + start.Length)..divider].Replace("''", "'", StringComparison.Ordinal);
            var idFrom = divider + middle.Length;
            if (script.Length < idFrom + 33 || script[idFrom + 32] != '\'') return null;
            var id = script.Substring(idFrom, 32);
            string? dataRoot = null;
            const string rootPrefix = " '--data-root' '";
            var tail = script[(idFrom + 33)..];
            if (tail.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                var rootEnd = tail.LastIndexOf("' | ForEach-Object", StringComparison.Ordinal);
                if (rootEnd < rootPrefix.Length) return null;
                dataRoot = tail[rootPrefix.Length..rootEnd].Replace("''", "'", StringComparison.Ordinal);
            }
            var generated = JsonNode.Parse(ClaudeStatusLineCommand.SettingsJson(path, id, dataRoot))!["statusLine"]!["command"]!.GetValue<string>();
            return generated == command ? id : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException) { return null; }
    }

    public static async Task<ClaudeBridgeOptions> InstallAsync(CodexAccountStore accounts, string profileId,
        string directory, string executable, CancellationToken token, Action? beforeCommit = null)
    {
        directory = RequireDirectory(directory);
        Directory.CreateDirectory(directory);
        using var lease = await LeaseAsync(directory, token).ConfigureAwait(false);
        var file = Path.Combine(directory, "settings.json");
        var original = ReadBytes(file);
        var settings = ParseSettings(original);
        var had = settings.ContainsKey("statusLine");
        JsonObject? previous = null;
        if (settings["statusLine"] is { } status)
        {
            if (status is not JsonObject obj || !ValidStatusLine(obj)) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
            var command = obj["command"]!.GetValue<string>();
            if (TryRead(command, out var installed))
            {
                if (installed!.ProfileId != profileId && accounts.ContainsClaude(installed.ProfileId))
                    throw new ClaudeSetupException(ClaudeSetupFailure.AlreadyLinked);
                had = installed.HadStatusLine;
                previous = installed.PreviousStatusLine?.DeepClone().AsObject();
            }
            else if (LegacyTarget(command) is { } legacyId)
            {
                if (legacyId != profileId && accounts.ContainsClaude(legacyId)) throw new ClaudeSetupException(ClaudeSetupFailure.AlreadyLinked);
                had = false;
            }
            else previous = obj.DeepClone().AsObject();
        }
        var options = new ClaudeBridgeOptions(1, profileId, directory, Path.GetFullPath(executable), accounts.RootDirectory, had, previous);
        var replacement = (settings["statusLine"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject { ["type"] = "command" };
        replacement["command"] = Command(options);
        settings["statusLine"] = replacement;
        beforeCommit?.Invoke();
        WriteIfChanged(file, original, settings, token);
        return options;
    }

    public static bool IsInstalled(string directory, string profileId)
    {
        try
        {
            var settings = ParseSettings(ReadBytes(Path.Combine(RequireDirectory(directory), "settings.json")));
            return settings["statusLine"] is JsonObject obj && ValidStatusLine(obj)
                && TryRead(obj["command"]!.GetValue<string>(), out var options) && options!.ProfileId == profileId
                && string.Equals(options.ConfigDirectory, directory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ClaudeSetupException or IOException or UnauthorizedAccessException) { return false; }
    }

    public static async Task RestoreAsync(string directory, string profileId, CancellationToken token)
    {
        directory = RequireDirectory(directory);
        if (!Directory.Exists(directory)) return;
        using var lease = await LeaseAsync(directory, token).ConfigureAwait(false);
        var file = Path.Combine(directory, "settings.json");
        var original = ReadBytes(file);
        var settings = ParseSettings(original);
        if (settings["statusLine"] is not JsonObject obj || !ValidStatusLine(obj)
            || !TryRead(obj["command"]!.GetValue<string>(), out var installed) || installed!.ProfileId != profileId) return;
        if (installed.HadStatusLine) settings["statusLine"] = installed.PreviousStatusLine?.DeepClone();
        else settings.Remove("statusLine");
        WriteIfChanged(file, original, settings, token);
    }

    private static bool Valid(ClaudeBridgeOptions o) => o.Version == 1 && Guid.TryParseExact(o.ProfileId, "N", out _)
        && ClaudeConnectionPaths.Normalize(o.ConfigDirectory) is { } directory && directory == o.ConfigDirectory
        && ClaudeConnectionPaths.Normalize(o.CycleArcExecutable) is { } executable && executable == o.CycleArcExecutable
        && Path.GetFileName(o.CycleArcExecutable).Equals("CycleArc.exe", StringComparison.OrdinalIgnoreCase)
        && ClaudeConnectionPaths.Normalize(o.DataRoot) is { } root && root == o.DataRoot
        && (o.PreviousStatusLine is null || o.HadStatusLine && ValidStatusLine(o.PreviousStatusLine, 8192));

    private static bool ValidStatusLine(JsonObject obj, int maxLength = 28000) => obj["type"] is JsonValue type && type.TryGetValue<string>(out var value)
        && value == "command" && obj["command"] is JsonValue command && command.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text) && text.Length <= maxLength && !text.Contains('\0');

    private static string RequireDirectory(string directory)
    {
        var normalized = ClaudeConnectionPaths.Normalize(directory) ?? throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        if (Directory.Exists(normalized) && (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
            throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        return normalized;
    }
    private static byte[]? ReadBytes(string file)
    {
        if (!File.Exists(file)) return null;
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxSettingsBytes) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        if (buffer.Length > MaxSettingsBytes) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        return buffer.ToArray();
    }
    private static JsonObject ParseSettings(byte[]? bytes)
    {
        if (bytes is null) return new();
        try
        {
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            using var doc = JsonDocument.Parse(text);
            CheckUnique(doc.RootElement);
            return JsonNode.Parse(text) as JsonObject ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings); }
    }
    private static void CheckUnique(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                CheckUnique(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) CheckUnique(item);
    }
    private static void WriteIfChanged(string file, byte[]? original, JsonObject settings, CancellationToken token)
    {
        if (original is not null && JsonNode.DeepEquals(ParseSettings(original), settings)) return;
        var output = Encoding.UTF8.GetBytes(settings.ToJsonString(JsonOptions) + Environment.NewLine);
        if (output.Length > MaxSettingsBytes) throw new ClaudeSetupException(ClaudeSetupFailure.InvalidSettings);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(output); stream.Flush(true); }
            token.ThrowIfCancellationRequested();
            var current = ReadBytes(file);
            if (original is null ? current is not null : current is null || !original.AsSpan().SequenceEqual(current))
                throw new ClaudeSetupException(ClaudeSetupFailure.SettingsChanged);
            if (original is null) File.Move(temporary, file, false);
            else File.Replace(temporary, file, file + ".cyclearc.bak", true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<FileStream> LeaseAsync(string directory, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(directory, ".cyclearc-settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(2)) { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }
}
