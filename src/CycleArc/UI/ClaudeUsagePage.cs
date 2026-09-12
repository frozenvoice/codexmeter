using System.Diagnostics;
using CycleArc.Providers.Claude;

namespace CycleArc.UI;

internal static class ClaudeUsagePage
{
    internal static void Open(Window owner, Action<string>? openForTest = null)
    {
        try
        {
            if (openForTest is not null) openForTest(ClaudeUsagePresentation.UsagePageUrl);
            else Process.Start(new ProcessStartInfo(ClaudeUsagePresentation.UsagePageUrl) { UseShellExecute = true });
        }
        catch
        {
            System.Windows.MessageBox.Show(owner,
                UiText.T("Could not open the browser. Open Claude → Settings → Usage to check current limits.",
                    "브라우저를 열지 못했습니다. Claude → 설정 → 사용량에서 현재 한도를 확인하세요."),
                UiText.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
