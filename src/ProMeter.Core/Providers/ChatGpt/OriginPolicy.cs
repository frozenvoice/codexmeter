namespace ProMeter.Providers.ChatGpt;

public enum OriginKind
{
    None,
    ChatGptApp,
    InteractiveAuth
}

public static class OriginPolicy
{
    public static readonly string[] ChatGptAppHosts = ["chatgpt.com", "www.chatgpt.com"];

    public static readonly string[] InteractiveAuthHosts =
    [
        "auth.openai.com",
        "accounts.google.com",
        "login.microsoftonline.com",
        "login.live.com",
        "appleid.apple.com",
        "id.apple.com"
    ];

    public static bool TryGetAbsoluteUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || ContainsControl(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || !parsed.IsAbsoluteUri)
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!parsed.IsDefaultPort && parsed.Port != 443)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public static OriginKind Classify(string? value)
    {
        if (!TryGetAbsoluteUri(value, out var uri))
        {
            return OriginKind.None;
        }

        if (HostEqualsAny(uri, ChatGptAppHosts))
        {
            return OriginKind.ChatGptApp;
        }

        if (HostEqualsAny(uri, InteractiveAuthHosts))
        {
            return OriginKind.InteractiveAuth;
        }

        return OriginKind.None;
    }

    public static bool IsChatGptAppOrigin(string? value) => Classify(value) == OriginKind.ChatGptApp;

    public static bool IsInteractiveAuthOrigin(string? value) => Classify(value) == OriginKind.InteractiveAuth;

    public static bool AllowsBackendFetch(string? value) => IsChatGptAppOrigin(value);

    public static bool AllowsInteractiveNavigation(string? value) =>
        Classify(value) is OriginKind.ChatGptApp or OriginKind.InteractiveAuth;

    public static string? Host(string? value) =>
        TryGetAbsoluteUri(value, out var uri) ? uri.Host : null;

    private static bool HostEqualsAny(Uri uri, IReadOnlyList<string> hosts)
    {
        foreach (var host in hosts)
        {
            if (string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ContainsControl(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }
}

public static class BackendTargetPolicy
{
    public static readonly string[] AllowedPrefixes =
    [
        ChatGptEndpoints.Session,
        "/backend-api/"
    ];

    public static bool TryValidate(string? path, out string normalized, out string error) =>
        CanonicalTargetPolicy.TryValidate(path, out normalized, out error);
}

public enum LoginNavigationAction
{
    Ignore,
    StayOnExternalAuth,
    ProbeSession
}

public sealed class LoginNavigationMachine
{
    private TaskCompletionSource<bool>? _waiters;

    public bool IsActive { get; private set; }
    public int Generation { get; private set; }

    public (Task<bool> Task, bool Started) BeginOrJoin()
    {
        if (_waiters is not null)
        {
            return (_waiters.Task, false);
        }

        Generation++;
        IsActive = true;
        _waiters = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (_waiters.Task, true);
    }

    public void Complete(bool signedIn)
    {
        IsActive = false;
        _waiters?.TrySetResult(signedIn);
        _waiters = null;
    }

    public void Cancel()
    {
        IsActive = false;
        _waiters?.TrySetResult(false);
        _waiters = null;
    }

    public LoginNavigationAction Observe(string? uri, bool navigationCompleted)
    {
        if (!IsActive)
        {
            return LoginNavigationAction.Ignore;
        }

        var kind = OriginPolicy.Classify(uri);
        if (kind == OriginKind.InteractiveAuth)
        {
            return LoginNavigationAction.StayOnExternalAuth;
        }

        if (kind == OriginKind.ChatGptApp && navigationCompleted)
        {
            return LoginNavigationAction.ProbeSession;
        }

        return LoginNavigationAction.Ignore;
    }

    public bool AcceptsProbe(int generation, string? uri, bool navigationCompleted) =>
        IsActive
        && generation == Generation
        && Observe(uri, navigationCompleted) == LoginNavigationAction.ProbeSession;

    public bool ShouldStopProbe(string? uri) => !OriginPolicy.IsChatGptAppOrigin(uri);

    public bool MayNavigateAwayToProbe() => !IsActive;
}

public static class SessionProbePolicy
{
    public static readonly TimeSpan[] Backoff = [TimeSpan.Zero, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800)];

    public static bool CanProbe(string? uri) => OriginPolicy.AllowsBackendFetch(uri);
}
