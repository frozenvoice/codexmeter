namespace CycleArc.Services;

/// <summary>
/// Tracks a single realized WebView2 host Window. Session checking and
/// interactive login must reuse this host; a hidden/unrealized window is not
/// a valid first-initialization surface.
/// </summary>
public sealed class WebViewHostSession
{
    public int HostInstanceId { get; private set; }
    public bool IsRealized { get; private set; }
    public bool IsVisible { get; private set; }
    public bool CheckingOverlayVisible { get; private set; }
    public bool LoginSurfaceVisible { get; private set; }

    public void RealizeForDiagnostic()
    {
        if (HostInstanceId == 0)
        {
            HostInstanceId = 1;
        }

        IsRealized = true;
        IsVisible = true;
        CheckingOverlayVisible = true;
        LoginSurfaceVisible = false;
    }

    public void HideAfterSuccessfulSession()
    {
        IsVisible = false;
        CheckingOverlayVisible = false;
        LoginSurfaceVisible = false;
    }

    public void ShowLoginOnSameHost()
    {
        if (!IsRealized)
        {
            throw new InvalidOperationException("WebView2 host must be realized before login navigation.");
        }

        IsVisible = true;
        CheckingOverlayVisible = false;
        LoginSurfaceVisible = true;
    }

    public void Cancel()
    {
        IsVisible = false;
        CheckingOverlayVisible = false;
        LoginSurfaceVisible = false;
    }
}

public enum WebViewInteractiveLoginResult
{
    SignedIn,
    Cancelled,
    Unsupported
}

public interface IWebViewInteractiveLogin
{
    Task<WebViewInteractiveLoginResult> ShowInteractiveLoginAsync(CancellationToken cancellationToken = default);
}
