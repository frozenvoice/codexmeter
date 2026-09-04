namespace ProMeter.Providers.ChatGpt;

public static class CanonicalTargetPolicy
{
    public const string ExpectedOrigin = "https://chatgpt.com";

    public static bool TryValidate(string? candidate, out string canonicalPathAndQuery, out string error)
    {
        canonicalPathAndQuery = "";
        error = "";
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "empty target";
            return false;
        }

        if (candidate.Contains('\\') || OriginPolicy.ContainsControl(candidate))
        {
            error = "illegal target characters";
            return false;
        }

        if (candidate.Contains("://", StringComparison.Ordinal))
        {
            error = "absolute or scheme-relative target rejected";
            return false;
        }

        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            error = "absolute or scheme-relative target rejected";
            return false;
        }

        if (!candidate.StartsWith('/'))
        {
            error = "target must be a relative same-origin path";
            return false;
        }

        if (candidate.Contains('#', StringComparison.Ordinal))
        {
            error = "fragments rejected";
            return false;
        }

        if (!PercentEncoding.IsWellFormed(candidate, out error))
        {
            return false;
        }

        if (PercentEncoding.PathContainsEncodedTraversal(candidate))
        {
            error = "encoded path traversal rejected";
            return false;
        }

        Uri uri;
        try
        {
            uri = new Uri(new Uri(ExpectedOrigin + "/", UriKind.Absolute), candidate);
        }
        catch
        {
            error = "target is not a valid URL";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.IdnHost, "chatgpt.com", StringComparison.OrdinalIgnoreCase)
            || (!uri.IsDefaultPort && uri.Port != 443)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "canonical origin is not https://chatgpt.com";
            return false;
        }

        var pathname = uri.AbsolutePath;
        if (pathname.Contains("..", StringComparison.Ordinal)
            || pathname.Contains('\\')
            || pathname.Contains("//", StringComparison.Ordinal))
        {
            error = "path traversal rejected";
            return false;
        }

        if (!TryRejectDotSegments(pathname, out error))
        {
            return false;
        }

        if (!IsApprovedPathname(pathname))
        {
            error = "target prefix is not approved";
            return false;
        }

        canonicalPathAndQuery = pathname + uri.Query;
        return true;
    }

    public static bool IsApprovedPathname(string pathname)
    {
        if (string.Equals(pathname, ChatGptEndpoints.Session, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return pathname.StartsWith("/backend-api/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRejectDotSegments(string pathname, out string error)
    {
        error = "";
        var parts = pathname.Split('/', StringSplitOptions.None);
        if (parts.Length == 0 || parts[0].Length != 0)
        {
            error = "path traversal rejected";
            return false;
        }

        foreach (var part in parts.Skip(1))
        {
            if (part is "." or "..")
            {
                error = "path traversal rejected";
                return false;
            }
        }

        return true;
    }
}

public static class PercentEncoding
{
    public static bool IsWellFormed(string value, out string error)
    {
        error = "";
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
            {
                continue;
            }

            if (i + 2 >= value.Length || !IsHex(value[i + 1]) || !IsHex(value[i + 2]))
            {
                error = "malformed percent encoding";
                return false;
            }
        }

        return true;
    }

    public static bool PathContainsEncodedTraversal(string candidate)
    {
        var cut = candidate.IndexOf('?', StringComparison.Ordinal);
        var path = cut >= 0 ? candidate[..cut] : candidate;
        var lower = path.ToLowerInvariant();
        return lower.Contains("%2e")
               || lower.Contains("%2f")
               || lower.Contains("%5c")
               || path.Contains("..", StringComparison.Ordinal);
    }

    private static bool IsHex(char ch) =>
        ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
