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
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme is not "https" and not "http")
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
}

public enum LoginNavigationAction
{
    Ignore,
    StayOnExternalAuth,
    ProbeSession
}

public sealed class LoginNavigationMachine
{
    public bool IsActive { get; private set; }

    public void Begin() => IsActive = true;

    public void Complete() => IsActive = false;

    public void Cancel() => IsActive = false;

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

    public bool MayNavigateAwayToProbe() => !IsActive;
}
