using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexMeter.Codex;
using CodexMeter.Models;
using CodexMeter.Services;
using CodexMeter.UI;

namespace CodexMeter.UiSmoke;

internal static class AccountUiChecks
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
            foreach (var size in new[] { 0, 1, 3, 8 })
            {
                var accounts = Fixtures(size);
                var id = accounts.FirstOrDefault()?.Profile.Id ?? "";
                var flyout = new FlyoutWindow();
                var window = new AccountsWindow();
                var widget = new FloatingWidget();
                try
                {
                    var selected = "";
                    var refreshes = 0;
                    flyout.AccountSelected += value => selected = value;
                    flyout.SyncRequested += () => refreshes++;
                    flyout.BindAccounts(accounts, id, false);
                    var overview = (ItemsControl)flyout.FindName("AccountOverview");
                    if (overview.Items.Count != (size > 1 ? size : 0)) throw new InvalidOperationException("Account overview is incomplete.");
                    if (size > 1)
                    {
                        ((Button)overview.Items[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (selected != accounts[1].Profile.Id || refreshes != 0) throw new InvalidOperationException("Account inspection started refresh or chose wrong account.");
                        var expected = CodexRingPresentation.From(accounts[1].Snapshot).CenterValueText;
                        flyout.BindAccounts(accounts, selected, false);
                        if (((TextBlock)flyout.FindName("SelectedAccountText")).Text != accounts[1].DisplayName)
                            throw new InvalidOperationException("Selected identity is not visible.");
                        flyout.BindAccounts(Enumerable.Reverse(accounts).ToArray(), selected, false);
                        if (!overview.Items.Cast<Button>().Select(button => button.Tag as string)
                            .SequenceEqual(Enumerable.Reverse(accounts).Select(account => account.Profile.Id)))
                            throw new InvalidOperationException("Usage popup did not follow saved account order.");
                    }
                    foreach (var zoom in new[] { 80, 100, 150 })
                    {
                        flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                        flyout.BindAccounts(accounts, id, false);
                        Render(flyout, 440 * zoom / 100d, null, directory is not null && size == 3 && zoom == 100
                            ? Path.Combine(directory, $"accounts-{language}-{theme}.png") : null);
                        CheckSummaryRows(flyout);
                        flyout.BindAccounts(accounts, id, true);
                        if (((Button)flyout.FindName("RefreshAllButton")).IsEnabled)
                            throw new InvalidOperationException("Batch refresh enabled early.");
                        count++;
                    }
                    window.Bind(accounts, id);
                    Render(window, 700, 800, directory is not null && size == 3
                        ? Path.Combine(directory, $"manage-{language}-{theme}.png") : null);
                    if (((ItemsControl)window.FindName("AccountRows")).Items.Count != size)
                        throw new InvalidOperationException("Account management list lost accounts.");
                    if (size > 0) CheckRenameSurvivesDisplayTick(window, accounts, id);
                    CheckGuidanceAndOrder(window, accounts, directory, language, theme);
                    widget.BindAccount(accounts.FirstOrDefault(), size > 1);
                    Render(widget, 245, null, null);
                    if (((TextBlock)widget.FindName("AccountName")).Visibility != (size > 1 ? Visibility.Visible : Visibility.Collapsed))
                        throw new InvalidOperationException("Widget does not identify selected account.");
                    count += 2;
                }
                finally { flyout.Close(); window.Close(); widget.Close(); }
            }
        }
        CheckLoginCancellation();
        CheckCreditAccountCapture();
        CheckLocalIcons();
        Console.WriteLine($"PASS: {count} multi-account WPF renders; guidance/compact scrolling, local icons, ordering, rename continuity, refresh, login cancellation and credit-account routing.");
    }

    private static CodexAccountView[] Fixtures(int count)
    {
        var now = DateTimeOffset.Now;
        return Enumerable.Range(0, count).Select(i => new CodexAccountView(
            new CodexAccountProfile(i == 0 ? "default" : i.ToString("D32"), @"C:\synthetic\account" + i,
                UiText.T(i == 0 ? "Personal" : i == 1 ? "Work" : "Account " + (i + 1),
                    i == 0 ? "개인 계정" : i == 1 ? "업무 계정" : "추가 계정 " + (i + 1)), i > 0),
            new CodexQuotaSnapshot(i == 2 ? CodexQuotaStatus.Stale : i == 3 ? CodexQuotaStatus.SignedOut : CodexQuotaStatus.Available,
                "pro", now.AddMinutes(-3), now.AddMinutes(-1), null, null, 2,
                [new("codex", 18 + i * 9, 10080, now.AddDays(4), CodexWindowKind.Weekly)], null,
                [now.AddDays(28), now.AddDays(54)]), $"account{i}@example.invalid")).ToArray();
    }

    private static void CheckSummaryRows(FlyoutWindow window)
    {
        foreach (Button button in ((ItemsControl)window.FindName("AccountOverview")).Items)
        foreach (var row in ((StackPanel)button.Content).Children.OfType<Grid>())
        {
            var first = (FrameworkElement)row.Children[0];
            var second = (FrameworkElement)row.Children[1];
            var right = first.TranslatePoint(new Point(first.ActualWidth, 0), row).X;
            var left = second.TranslatePoint(new Point(), row).X;
            if (right > left + 1 || left + second.ActualWidth > row.ActualWidth + 1)
                throw new InvalidOperationException("Account name/status or quota row overlaps.");
        }
    }

    private static void CheckGuidanceAndOrder(AccountsWindow window, CodexAccountView[] accounts,
        string? directory, UiLanguage language, AppTheme theme)
    {
        foreach (var name in new[] { "NewLoginHint", "ExistingHint", "ChooseHomeHint", "ProfileHelp", "ActionsHelp", "SelectionHint" })
            if (string.IsNullOrWhiteSpace(((TextBlock)window.FindName(name)).Text))
                throw new InvalidOperationException("Connection guidance is missing.");
        if (((TextBlock)window.FindName("EmptyAccountsHint")).Visibility != (accounts.Length == 0 ? Visibility.Visible : Visibility.Collapsed))
            throw new InvalidOperationException("First-use guidance is not visible.");
        var rows = (ItemsControl)window.FindName("AccountRows");
        var movedId = "";
        var movedDirection = 0;
        window.MoveAccount = (id, direction) => { movedId = id; movedDirection = direction; return true; };
        for (var i = 0; i < rows.Items.Count; i++)
        {
            var order = ((StackPanel)rows.Items[i]).Children.OfType<Grid>().Single().Children.OfType<StackPanel>().Single();
            var up = order.Children.OfType<Button>().Single(b => b.Tag as string == "MoveAccountUp");
            var down = order.Children.OfType<Button>().Single(b => b.Tag as string == "MoveAccountDown");
            if (up.IsEnabled != (i > 0) || down.IsEnabled != (i < accounts.Length - 1))
                throw new InvalidOperationException("Account order boundary buttons are incorrect.");
            if (i == 1)
            {
                up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (movedId != accounts[i].Profile.Id || movedDirection != -1)
                    throw new InvalidOperationException("Reordering targeted the wrong account.");
            }
        }
        var advanced = (Expander)window.FindName("AdvancedConnection");
        var help = (Expander)window.FindName("AccountHelp");
        var connection = (Expander)window.FindName("ConnectionOptions");
        if (connection.IsExpanded != (accounts.Length == 0))
            throw new InvalidOperationException("First-use connection guide has the wrong initial state.");
        if (advanced.IsExpanded || help.IsExpanded) throw new InvalidOperationException("Detailed guidance should start collapsed.");
        connection.IsExpanded = advanced.IsExpanded = help.IsExpanded = true;
        Render(window, 470, 400, null);
        var scroll = (ScrollViewer)window.FindName("AccountsScroll");
        if (scroll.ViewportHeight <= 30 || scroll.ScrollableHeight <= 0)
            throw new InvalidOperationException("Guidance expansion hid the compact window's scrolling content.");
        scroll.ScrollToBottom();
        ((FrameworkElement)window.Content).UpdateLayout();
        var target = accounts.Length > 0 ? (FrameworkElement)rows.Items[^1] : (FrameworkElement)window.FindName("EmptyAccountsHint");
        var bottom = target.TransformToAncestor(scroll).Transform(new Point(0, target.ActualHeight)).Y;
        if (bottom > scroll.ViewportHeight + 2 || bottom < 0)
            throw new InvalidOperationException("Last account cannot be reached after opening guidance.");
        scroll.ScrollToTop();
        Render(window, 700, 800, null);
        if (directory is not null && accounts.Length == 3)
            Render(window, 700, 800, Path.Combine(directory, $"guide-{language}-{theme}.png"));
    }

    private static void CheckLocalIcons()
    {
        var accounts = Fixtures(2);
        var window = new AccountsWindow();
        Border Icon()
        {
            var row = (StackPanel)((ItemsControl)window.FindName("AccountRows")).Items[0];
            var content = (StackPanel)row.Children.OfType<Button>().Single().Content;
            return ((DockPanel)content.Children.OfType<Grid>().First().Children[0]).Children.OfType<Border>().Single();
        }
        try
        {
            Color? color = null;
            foreach (var pair in new[] { ("frozenvoice", "FR"), ("D", "D"), ("개인 계정", "개인"), ("👩‍💻work", "👩‍💻W") })
            {
                accounts[0] = accounts[0] with { Profile = accounts[0].Profile with { Label = pair.Item1 } };
                window.Bind(accounts, accounts[0].Profile.Id);
                var icon = Icon();
                if (((TextBlock)icon.Child).Text != pair.Item2)
                    throw new InvalidOperationException("Local avatar initials are incorrect.");
                var background = ((SolidColorBrush)icon.Background).Color;
                if (color is not null && color != background)
                    throw new InvalidOperationException("Renaming changed the account's identifying color.");
                color = background;
                static double Linear(byte value) { var c = value / 255d; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
                var luminance = 0.2126 * Linear(background.R) + 0.7152 * Linear(background.G) + 0.0722 * Linear(background.B);
                if (1.05 / (luminance + 0.05) < 4.5) throw new InvalidOperationException("Account icon text lacks contrast.");
                // Supply a new collection, as the manager does, before the next changed profile.
                accounts = accounts.ToArray();
            }
        }
        finally { window.Close(); }
    }

    private static void CheckLoginCancellation()
    {
        var window = new AccountsWindow();
        var release = new TaskCompletionSource<CodexLoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken token = default;
        window.SignIn = (_, _, ct) => { calls++; token = ct; return release.Task; };
        try
        {
            var add = (Button)window.FindName("AddAccountButton");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (calls != 1 || add.IsEnabled || ((ProgressBar)window.FindName("OperationProgress")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("Login must visibly remain single-flight.");
            ((Button)window.FindName("CancelOperationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!token.IsCancellationRequested || add.IsEnabled) throw new InvalidOperationException("Cancel did not wait for login cleanup.");
            release.TrySetResult(new(CodexQuotaStatus.Cancelled));
            PumpUntil(window.ActiveOperation);
            if (!add.IsEnabled || ((ProgressBar)window.FindName("OperationProgress")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Login busy state was not released.");
        }
        finally { release.TrySetResult(new(CodexQuotaStatus.Cancelled)); window.Close(); }
    }

    private static void CheckRenameSurvivesDisplayTick(AccountsWindow window, CodexAccountView[] accounts, string selected)
    {
        TextBox Editor()
        {
            var row = (StackPanel)((ItemsControl)window.FindName("AccountRows")).Items[0];
            return row.Children.OfType<DockPanel>().Single().Children.OfType<TextBox>().Single();
        }
        var editor = Editor();
        editor.Text = "In-progress name";
        editor.Select(3, 4);
        // The local age/countdown timer supplies new view records with unchanged quota state.
        window.Bind(accounts.Select(account => account with { }).ToArray(), selected);
        if (!ReferenceEquals(editor, Editor()) || editor.Text != "In-progress name"
            || editor.SelectionStart != 3 || editor.SelectionLength != 4)
            throw new InvalidOperationException("Display-only update interrupted account-name editing.");
    }

    private static void CheckCreditAccountCapture()
    {
        var accounts = Fixtures(2);
        var credit = new CodexResetCredit("synthetic", DateTimeOffset.Now.AddDays(2));
        accounts[0] = accounts[0] with { Snapshot = accounts[0].Snapshot with { RedeemableCredits = [credit], ResetCreditsAvailable = 1 } };
        var flyout = new FlyoutWindow();
        try
        {
            flyout.BindAccounts(accounts, accounts[0].Profile.Id, false);
            var captured = "";
            flyout.RedeemAccountCredit = (profile, _) => { captured = profile; return Task.FromResult(CreditRedemptionOutcome.Reset); };
            var confirmation = typeof(FlyoutWindow).GetProperty("ConfirmCreditForTest", BindingFlags.NonPublic | BindingFlags.Instance)!;
            confirmation.SetValue(flyout, (Func<string, bool>)(prompt =>
            {
                if (!prompt.Contains(accounts[0].DisplayName)) throw new InvalidOperationException("Credit confirmation omits account.");
                flyout.BindAccounts(accounts, accounts[1].Profile.Id, false); // simulate a nested-dispatcher account change
                return true;
            }));
            var use = typeof(FlyoutWindow).GetMethod("UseCreditAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var row = CodexCreditCard.From(accounts[0].Snapshot, DateTimeOffset.Now).Rows.Single();
            PumpUntil((Task)use.Invoke(flyout, [row])!);
            if (captured != accounts[0].Profile.Id) throw new InvalidOperationException("Credit redemption changed account during confirmation.");
        }
        finally { flyout.Close(); }
    }

    private static void PumpUntil(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        if (!task.IsCompleted) throw new TimeoutException("Offline UI operation did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static void Render(Window window, double width, double? height, string? path)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height ?? double.PositiveInfinity));
        var size = new Size(width, height ?? content.DesiredSize.Height);
        content.Arrange(new Rect(new Point(), size));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        if (path is null) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
