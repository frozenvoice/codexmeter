namespace ProMeter.Services;

public sealed class AppLog
{
    private readonly object _gate = new();
    private readonly string _directory;
    private const long MaxBytes = 512 * 1024;

    public AppLog(string? directory = null)
    {
        _directory = directory ?? AppPaths.Logs;
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;
    public string CurrentFile => Path.Combine(_directory, $"prometer-{DateTime.UtcNow:yyyyMMdd}.log");

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null)
    {
        var detail = ex is null ? message : $"{message} :: {ex.GetType().Name}: {Sanitize(ex.Message)}";
        Write("ERROR", detail);
    }

    public void Http(string operation, int status, string? extra = null)
    {
        var suffix = string.IsNullOrWhiteSpace(extra) ? "" : " " + Sanitize(extra);
        Info($"http {operation} status={status}{suffix}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {Sanitize(message)}";
        lock (_gate)
        {
            var file = CurrentFile;
            File.AppendAllText(file, line + Environment.NewLine);
            RotateIfNeeded(file);
        }
    }

    private static void RotateIfNeeded(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Exists && info.Length > MaxBytes)
            {
                var archive = file + ".1";
                if (File.Exists(archive))
                {
                    File.Delete(archive);
                }

                File.Move(file, archive);
            }
        }
        catch
        {
            // logging must never throw
        }
    }

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var text = value;
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            "(?i)(authorization|cookie|session[-_ ]?token|access[-_ ]?token)\\s*[:=]\\s*.+",
            "$1=[redacted]");
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            "Bearer\\s+[A-Za-z0-9._\\-]+",
            "Bearer [redacted]");
        if (text.Length > 2000)
        {
            text = text[..2000] + "…";
        }

        return text;
    }
}
