using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Usage;
using CycleArc.Providers.Claude;
using System.Threading;
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
            var connection = new FakeConnection(accounts[1].Profile.Id);
            var guide = new ClaudeConnectionWindow(accounts[1].Profile, @"C:\Synthetic CycleArc\CycleArc.exe", connection);
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
                guide.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                AccountUiChecks.PumpUntil(guide.ActiveOperation);
                Check(((Button)guide.FindName("ConnectExistingButton")).IsEnabled, "Existing signed-in account cannot be connected.");
                ((Button)guide.FindName("ConnectExistingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AccountUiChecks.PumpUntil(guide.ActiveOperation);
                Check(!connection.LastLogin && connection.Calls == 1, "Existing-login action unexpectedly started interactive login.");
                Check(((Button)guide.FindName("OpenClaudeButton")).Visibility == Visibility.Visible, "Successful connection has no Claude launch action.");
                Check(((TextBlock)guide.FindName("AccountIdentity")).Text.Contains("person@example.invalid"), "Verified identity is missing.");
                foreach (var size in new[] { new Size(610, 580), new Size(470, 400) })
                {
                    AccountUiChecks.Render(guide, size.Width, size.Height, directory is not null && size.Width == 610
                        ? Path.Combine(directory, $"claude-setup-{language}-{theme}.png") : null);
                    CheckBadges(guide, [UsageProviderId.Claude]);
                    Check(!((Expander)guide.FindName("AdvancedDetails")).IsExpanded, "Technical connection details dominate the default UI.");
                    Check(!AccountUiChecks.Descendants<TextBox>((FrameworkElement)guide.Content).Any(), "The main connection flow still requires copying JSON.");
                    count++;
                }
                connection.DelayLogin = true;
                ((Button)guide.FindName("LoginButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var active = guide.ActiveOperation;
                ((Button)guide.FindName("LoginButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(ReferenceEquals(active, guide.ActiveOperation), "Duplicate Claude login replaced the busy operation.");
                Check(((ProgressBar)guide.FindName("OperationProgress")).Visibility == Visibility.Visible
                    && !((Button)guide.FindName("ConnectExistingButton")).IsEnabled, "Claude login lacks visible single-flight feedback.");
                guide.CancelOperation();
                AccountUiChecks.PumpUntil(active);
                Check(((ProgressBar)guide.FindName("OperationProgress")).Visibility == Visibility.Collapsed, "Cancel left Claude connection busy.");
                CheckPendingAccounts(flyout, manager, accounts, directory, language, theme);
                count += 4;
            }
            finally { flyout.Close(); widget.Close(); manager.Close(); guide.Close(); }
        }
        Console.WriteLine($"PASS: {count} mixed Codex/Claude WPF renders; account/selection/widget badges, aliases, stale state, provider-scoped credits and compact connection guide in both languages/all themes.");
    }

    private static void CheckPendingAccounts(FlyoutWindow flyout, AccountsWindow manager, CodexAccountView[] fixtures,
        string? directory, UiLanguage language, AppTheme theme)
    {
        flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = 100 });
        var first = fixtures[0];
        var second = first with { Profile = first.Profile with { Id = "33333333333333333333333333333333", Label = "Work" } };
        var pending = fixtures[1] with { Snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable) with { Provider = UsageProviderId.Claude } };
        CodexAccountView[] all = [first, pending, second];
        flyout.BindAccounts(all, second.Profile.Id, false);
        AccountUiChecks.Render(flyout, 440, null, directory is null ? null : Path.Combine(directory, $"connected-only-{language}-{theme}.png"));
        var rows = (ItemsControl)flyout.FindName("AccountOverview");
        Check(rows.Items.Count == 2 && rows.Items.Cast<Button>().All(button => (string)button.Tag != pending.Profile.Id), "Pending Claude profile is visible in the main account overview.");
        Check(((TextBlock)flyout.FindName("AccountsHeading")).Text == UiText.T("Accounts · 2", "계정 · 2"), "Pending profile inflated the displayed account count.");
        Check(((TextBlock)flyout.FindName("StatusText")).Text == UiText.T("All updated", "전체 최신"), "Pending profile incorrectly requires attention.");
        Check(flyout.SelectedProfileId == second.Profile.Id, "Filtering changed a valid account selection.");
        manager.Bind(all, second.Profile.Id);
        var managed = (ItemsControl)manager.FindName("AccountRows");
        Check(managed.Items.Count == 3, "Pending profile was lost from account management.");
        Check(!((Button)((StackPanel)managed.Items[1]).Children[0]).IsEnabled, "A profile without usage can be selected from management.");

        flyout.BindAccounts(all, pending.Profile.Id, false);
        AccountUiChecks.Render(flyout, 440, null, null);
        Check(flyout.SelectedProfileId == first.Profile.Id, "Hidden selected profile did not fall back to a usable account.");

        flyout.BindAccounts([pending], pending.Profile.Id, false);
        AccountUiChecks.Render(flyout, 440, null, directory is null ? null : Path.Combine(directory, $"no-connected-accounts-{language}-{theme}.png"));
        Check(flyout.SelectedProfileId is null && rows.Items.Count == 0, "Empty overview retained a pending selection.");
        Check(((Border)flyout.FindName("CodexCard")).Visibility == Visibility.Collapsed
            && ((Border)flyout.FindName("ResetCreditsCard")).Visibility == Visibility.Collapsed
            && ((UsageProviderBadge)flyout.FindName("SelectedProviderBadge")).Visibility == Visibility.Collapsed, "Empty overview displays an unconnected provider or quota card.");

        flyout.BindAccounts([first, fixtures[1], second], pending.Profile.Id, false);
        AccountUiChecks.Render(flyout, 440, null, null);
        Check(rows.Items.Count == 3 && flyout.SelectedProfileId == pending.Profile.Id
            && ((Border)flyout.FindName("CodexCard")).Visibility == Visibility.Visible, "First valid usage did not restore the account and detail card.");
    }

    private sealed class FakeConnection(string profileId) : IClaudeConnectionActions
    {
        private readonly ClaudeAuthentication _auth = new(ClaudeAuthStatus.SignedIn, "person@example.invalid", "Pro", new string('A', 64));
        private ClaudeConnectionBinding? _binding;
        public bool LastLogin { get; private set; }
        public int Calls { get; private set; }
        public bool DelayLogin { get; set; }
        public Task<ClaudeConnectionOverview> InspectAsync(string id, CancellationToken token) =>
            Task.FromResult(new ClaudeConnectionOverview(_binding, _auth, _binding is not null, @"C:\Synthetic Claude"));
        public async Task<ClaudeConnectionResult> ConnectAsync(string id, string executable, bool login, string? directory, CancellationToken token)
        {
            Calls++; LastLogin = login;
            if (DelayLogin) await Task.Delay(Timeout.Infinite, token);
            _binding = new(1, profileId, @"C:\Synthetic Claude", @"C:\Synthetic Claude\claude.cmd", false, _auth.Fingerprint!, DateTimeOffset.UtcNow);
            return new(true, _auth, _binding);
        }
        public Task DisconnectAsync(string id, CancellationToken token) { _binding = null; return Task.CompletedTask; }
        public void OpenClaude(string id, string workingDirectory) => throw new InvalidOperationException("Offline tests cannot open a live session.");
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
