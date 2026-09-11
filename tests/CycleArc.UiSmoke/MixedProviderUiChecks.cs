using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class MixedProviderUiChecks
{
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        foreach (var language in Enum.GetValues<UiLanguage>())
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            var accounts = Fixtures();
            var flyout = new FlyoutWindow();
            var widget = new FloatingWidget();
            var manager = new AccountsWindow();
            var guide = new ClaudeConnectionWindow(accounts[1].Profile, @"C:\Synthetic CycleArc\CycleArc.exe");
            try
            {
                foreach (var selected in accounts)
                {
                    foreach (var zoom in new[] { 80, 100, 150 })
                    {
                        flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                        flyout.BindAccounts(accounts, selected.Profile.Id, false);
                        AccountUiChecks.Render(flyout, 440 * zoom / 100d, null,
                            directory is not null && selected == accounts[2] && zoom == 100
                                ? Path.Combine(directory, $"mixed-{language}-{theme}.png") : null);
                        CheckBadges(flyout, accounts.Select(a => a.Profile.Provider).Append(selected.Profile.Provider).ToArray());
                        Check(((Border)flyout.FindName("ResetCreditsCard")).Visibility ==
                            (selected.Profile.Provider == UsageProviderId.Codex ? Visibility.Visible : Visibility.Collapsed),
                            "Reset credit actions crossed providers.");
                        Check(((TextBlock)flyout.FindName("CodexRingValueText")).Text ==
                            CodexRingPresentation.From(selected.Snapshot).CenterValueText, "Selected provider values differ from its snapshot.");
                        var overview = (ItemsControl)flyout.FindName("AccountOverview");
                        foreach (var button in overview.Items.Cast<Button>())
                        {
                            var account = accounts.Single(a => a.Profile.Id == (string)button.Tag);
                            if (account.Profile.Provider == UsageProviderId.Claude)
                                Check(!AccountUiChecks.Descendants<TextBlock>(button).Any(text => text.Text.Contains("Codex", StringComparison.Ordinal)),
                                    "Claude account rows contain a Codex label.");
                        }
                        count++;
                    }
                    widget.BindAccount(selected, true);
                    AccountUiChecks.Render(widget, 245, null, directory is not null && selected == accounts[2]
                        ? Path.Combine(directory, $"claude-widget-{language}-{theme}.png") : null);
                    CheckBadges(widget, [selected.Profile.Provider]);
                    Check(((TextBlock)widget.FindName("AccountName")).Text == selected.DisplayName, "Widget lost the selected alias.");
                    if (selected.Profile.Provider == UsageProviderId.Claude)
                        Check(!widget.ToolTip.ToString()!.Contains("Codex", StringComparison.Ordinal), "Claude widget tooltip names Codex.");
                    if (selected.Snapshot.Status == CodexQuotaStatus.Stale)
                        Check(((TextBlock)widget.FindName("HistoryValue")).Visibility == Visibility.Visible
                            && ((TextBlock)widget.FindName("CodexValue")).Text.Contains('~'), "Claude widget hides stale state.");
                    count++;
                }
                foreach (var status in new[] { CodexQuotaStatus.Unavailable, CodexQuotaStatus.ProtocolMismatch })
                {
                    flyout.Bind(accounts[1].Snapshot with { Status = status, Windows = [], LastSuccessfulRefresh = null });
                    AccountUiChecks.Render(flyout, 440, null, null);
                    var notice = (TextBlock)flyout.FindName("CodexStatusText");
                    Check(notice.Visibility == Visibility.Visible && notice.Text.Contains("Claude", StringComparison.Ordinal)
                        && !notice.Text.Contains("Codex", StringComparison.Ordinal), "Missing Claude data has wrong connection guidance.");
                    Check(((ItemsControl)flyout.FindName("CodexRows")).Items.Count == 0, "Missing usage was shown as zero.");
                    count++;
                }
                manager.Bind(accounts, accounts[1].Profile.Id);
                foreach (var size in new[] { new Size(700, 800), new Size(470, 400) })
                {
                    AccountUiChecks.Render(manager, size.Width, size.Height, null);
                    CheckBadges(manager, accounts.Select(a => a.Profile.Provider).ToArray());
                    var buttons = AccountUiChecks.Descendants<Button>((FrameworkElement)manager.Content).ToArray();
                    Check(buttons.Count(button => button.Tag as string == "ConfigureClaude") == 2, "Claude connection action is missing.");
                    count++;
                }
                string? configured = null;
                manager.AddClaudeAccount = label => accounts[1].Profile with { Label = label };
                manager.ConfigureClaude = id => configured = id;
                ((TextBox)manager.FindName("ClaudeAccountLabel")).Text = "New synthetic";
                ((Button)manager.FindName("AddClaudeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(configured == accounts[1].Profile.Id, "Adding Claude did not open the matching connection guide.");
                foreach (var size in new[] { new Size(670, 700), new Size(470, 400) })
                {
                    AccountUiChecks.Render(guide, size.Width, size.Height, directory is not null && size.Width == 670
                        ? Path.Combine(directory, $"claude-setup-{language}-{theme}.png") : null);
                    CheckBadges(guide, [UsageProviderId.Claude]);
                    Check(((TextBox)guide.FindName("ConnectionJson")).Text.Contains("statusLine", StringComparison.Ordinal), "Claude guide has no usable configuration.");
                    count++;
                }
            }
            finally { flyout.Close(); widget.Close(); manager.Close(); guide.Close(); }
        }
        Console.WriteLine($"PASS: {count} mixed Codex/Claude WPF renders; account/selection/widget badges, aliases, stale state, provider-scoped credits and compact connection guide in both languages/all themes.");
    }

    private static CodexAccountView[] Fixtures()
    {
        var now = DateTimeOffset.Now;
        var codex = new CodexAccountView(new CodexAccountProfile("default", @"C:\synthetic\codex", "Work · Codex"),
            new(CodexQuotaStatus.Available, "pro", now, now, null, null, 2,
                [new("codex", 32, 10080, now.AddDays(6), CodexWindowKind.Weekly)], null), "work@example.invalid");
        CodexAccountView Claude(string id, string name, CodexQuotaStatus status, double percentage) =>
            new(new CodexAccountProfile(id, "", name) { Provider = UsageProviderId.Claude },
                new CodexQuotaSnapshot(status, null, now.AddMinutes(status == CodexQuotaStatus.Stale ? -12 : -1), now, null, null, null,
                    [new("five_hour", percentage, 300, now.AddHours(3), CodexWindowKind.FiveHour),
                     new("seven_day", 47.2, 10080, now.AddDays(4), CodexWindowKind.Weekly)], null) { Provider = UsageProviderId.Claude });
        return [codex, Claude("11111111111111111111111111111111", UiText.T("Personal · Claude", "개인 계정 · Claude"), CodexQuotaStatus.Available, 23.5),
            Claude("22222222222222222222222222222222", UiText.T("Research · Claude", "연구용 계정 · Claude"), CodexQuotaStatus.Stale, 78.2)];
    }

    private static void CheckBadges(Window window, UsageProviderId[] expected)
    {
        var badges = AccountUiChecks.Descendants<UsageProviderBadge>((FrameworkElement)window.Content).ToArray();
        Check(badges.Length == expected.Length, "Mixed provider badge count is incorrect.");
        for (var i = 0; i < badges.Length; i++)
        {
            var badge = badges[i];
            var label = (TextBlock)badge.Child;
            Check(badge.Provider == expected[i] && label.Text == expected[i].Name(), "Provider badge is bound to another account.");
            Check(label.ActualWidth >= label.DesiredSize.Width - 1, "Provider badge text is clipped.");
            var parent = (FrameworkElement)VisualTreeHelper.GetParent(badge);
            var left = badge.TranslatePoint(new Point(), parent).X;
            Check(left >= -1 && left + badge.ActualWidth <= parent.ActualWidth + 1, "Provider badge overflows its account row.");
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
