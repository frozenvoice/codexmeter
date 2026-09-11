using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using CodexMeter.Codex;
using CancellationToken = System.Threading.CancellationToken;
using CancellationTokenSource = System.Threading.CancellationTokenSource;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace CodexMeter.UI;

public partial class AccountsWindow : Window
{
    public Func<string?, string, CancellationToken, Task<CodexLoginResult>>? SignIn { get; set; }
    public Func<string?, CancellationToken, Task<CodexDiscoveryResult>>? Discover { get; set; }
    public Action<string>? SelectAccount { get; set; }
    public Action<string, string>? RenameAccount { get; set; }
    public Func<string, bool>? RemoveAccount { get; set; }
    public Action<string>? LogFailure { get; set; }
    private CancellationTokenSource? _operation;
    private Task _active = Task.CompletedTask;
    private bool _closing;
    private bool _closed;
    private IReadOnlyList<CodexAccountView> _accounts = [];
    private string _selected = "";
    private bool _bound;
    private bool _rowsBusy;
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
    public Task ActiveOperation => _active;

    public AccountsWindow()
    {
        InitializeComponent();
        Title = UiText.ProductName + " · " + UiText.T("Accounts", "계정");
        Heading.Text = UiText.T("Codex accounts", "Codex 계정");
        Introduction.Text = UiText.T("Sign in with your browser to add an account. Each new account has its own Codex sign-in.",
            "브라우저에서 로그인해 계정을 추가하세요. 새 계정은 각각 독립된 Codex 로그인으로 유지됩니다.");
        LabelCaption.Text = UiText.T("Account name (optional)", "계정 이름 (선택 사항)");
        AddAccountButton.Content = UiText.T("Add account · Sign in", "계정 추가 · 로그인");
        DiscoverButton.Content = UiText.T("Find existing accounts", "기존 계정 찾기");
        ChooseHomeButton.Content = UiText.T("Choose Codex folder", "Codex 폴더 선택");
        ChooseHomeButton.ToolTip = UiText.T("Connect a custom CODEX_HOME folder using the official account protocol.",
            "별도로 사용하는 CODEX_HOME 폴더를 공식 계정 프로토콜로 확인해 연결합니다.");
        CancelOperationButton.Content = UiText.T("Cancel", "취소");
        DoneButton.Content = UiText.Close;
        OperationStatus.Text = UiText.T("Existing sign-ins are linked in place. Select an account for the tray and widget.",
            "기존 로그인은 원래 위치에서 연결합니다. 트레이와 위젯에 표시할 계정을 선택하세요.");
        PrivacyHint.Text = UiText.T("Codex manages authentication. Removing an account only removes it from this list; its Codex login is retained.",
            "인증은 Codex가 관리합니다. 계정 제거는 목록에서만 제거하며 Codex 로그인은 보존합니다.");
        System.Windows.Automation.AutomationProperties.SetName(NewAccountLabel, LabelCaption.Text);
        SourceInitialized += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            MaxHeight = Math.Max(400, work.Height - 24);
            Height = Math.Min(Height, MaxHeight);
            MaxWidth = Math.Max(470, work.Width - 24);
            Width = Math.Min(Width, MaxWidth);
        };
        Closing += OnClosing;
        Closed += (_, _) => _closed = true;
    }

    public void Bind(IReadOnlyList<CodexAccountView> accounts, string selected)
    {
        if (_closed) return;
        var busy = _operation is not null;
        // Countdown/age ticks must not replace an editor and discard its focus or selection.
        if (_bound && _rowsBusy == busy && _selected == selected && _accounts.SequenceEqual(accounts)) return;
        _bound = true;
        _rowsBusy = busy;
        _accounts = accounts;
        _selected = selected;
        AccountRows.Items.Clear();
        foreach (var account in accounts)
        {
            var id = account.Profile.Id;
            var content = new StackPanel();
            var summary = AccountSummary.Create(account, selected == id, () => SelectAccount?.Invoke(id));
            summary.IsEnabled = _operation is null;
            content.Children.Add(summary);
            var identity = new TextBlock { Text = account.Email ?? (account.Profile.IsManaged
                    ? UiText.T("Separate Codex sign-in", "독립 Codex 로그인") : UiText.T("Linked Codex sign-in", "기존 Codex 로그인 연결")),
                FontSize = 11, Margin = new Thickness(4, 0, 4, 6), TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = account.Profile.HomePath };
            identity.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            content.Children.Add(identity);
            var actions = new DockPanel { Margin = new Thickness(0, 0, 0, 17), IsEnabled = _operation is null };
            var remove = ActionButton(UiText.T("Remove", "제거"), () =>
            {
                if (RemoveAccount?.Invoke(id) == false)
                    OperationStatus.Text = UiText.T("Wait for this account's operation to finish.", "이 계정의 작업이 끝난 뒤 다시 시도하세요.");
            });
            DockPanel.SetDock(remove, Dock.Right); actions.Children.Add(remove);
            if (account.Profile.IsManaged)
            {
                var login = ActionButton(UiText.T("Sign in", "로그인"), () => StartLogin(id, account.Profile.Label));
                DockPanel.SetDock(login, Dock.Right); actions.Children.Add(login);
            }
            var rename = ActionButton(UiText.T("Rename", "이름 저장"), () => RenameAccount?.Invoke(id, _labels.GetValueOrDefault(id, account.Profile.Label)));
            DockPanel.SetDock(rename, Dock.Right); actions.Children.Add(rename);
            var label = new TextBox { Text = _labels.GetValueOrDefault(id, account.Profile.Label), MaxLength = 80,
                MinWidth = 60, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(label, UiText.T("Account name", "계정 이름") + " · " + account.DisplayName);
            label.TextChanged += (_, _) => _labels[id] = label.Text;
            actions.Children.Add(label);
            content.Children.Add(actions);
            AccountRows.Items.Add(content);
        }
    }

    public void BrowserOpened()
    {
        if (!_closed) OperationStatus.Text = UiText.T("Complete sign-in in the browser. Choose a different account there when adding another account.",
            "브라우저에서 로그인을 완료하세요. 다른 계정을 추가하려면 브라우저에서 해당 계정을 선택하세요.");
    }

    private Button ActionButton(string label, Action action)
    {
        var button = new Button { Content = label, Style = (Style)FindResource("CreditUseButton"),
            Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(6, 0, 0, 0), FontSize = 12 };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch { LogFailure?.Invoke("account-list-save-failed"); OperationStatus.Text = UiText.T("Could not save the account list.", "계정 목록을 저장하지 못했습니다."); }
        };
        return button;
    }

    private void OnAdd(object sender, RoutedEventArgs e) => StartLogin(null, NewAccountLabel.Text);
    private void StartLogin(string? id, string label)
    {
        if (SignIn is null) return;
        StartOperation(async token =>
        {
            var result = await SignIn(id, label, token);
            var message = result.Status switch
            {
                CodexQuotaStatus.Available => UiText.T("Signed in. Each account's card shows its quota-check result.", "로그인했습니다. 사용량 조회 결과는 각 계정 카드에서 확인하세요."),
                CodexQuotaStatus.Cancelled => UiText.T("Sign-in cancelled. You can try again.", "로그인을 취소했습니다. 다시 시도할 수 있습니다."),
                CodexQuotaStatus.TimedOut => UiText.T("Sign-in timed out. Try again.", "로그인 시간이 초과됐습니다. 다시 시도하세요."),
                CodexQuotaStatus.CodexNotFound => UiText.T("Install Codex CLI, or select its executable in Settings → Connection.", "Codex CLI를 설치하거나 설정 → 연결에서 실행 파일을 지정하세요."),
                _ => UiText.T("Sign-in could not be verified. Update Codex and try again.", "로그인 상태를 확인하지 못했습니다. Codex를 업데이트하고 다시 시도하세요.")
            };
            await Dispatcher.InvokeAsync(() =>
            {
                OperationStatus.Text = message;
                if (result.Status != CodexQuotaStatus.Available) LogFailure?.Invoke("account-login-" + result.Status);
            });
        }, UiText.T("Preparing Codex sign-in…", "Codex 로그인을 준비하는 중…"));
    }

    private void OnDiscover(object sender, RoutedEventArgs e) => StartDiscovery(null);
    private void OnChooseHome(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = UiText.T("Choose your Codex home folder", "Codex 홈 폴더 선택"), Multiselect = false };
        if (dialog.ShowDialog(this) == true) StartDiscovery(dialog.FolderName);
    }
    private void StartDiscovery(string? home)
    {
        if (Discover is null) return;
        StartOperation(async token =>
        {
            var result = await Discover(home, token);
            await Dispatcher.InvokeAsync(() => OperationStatus.Text = UiText.T($"Connected {result.Added}; signed out {result.SignedOut}; could not check {result.Failed}. Already connected folders are skipped.",
                $"{result.Added}개 연결 · {result.SignedOut}개 로그인 필요 · {result.Failed}개 확인 실패. 이미 연결한 폴더는 건너뜁니다."));
        }, UiText.T("Checking existing Codex sign-ins…", "기존 Codex 로그인을 확인하는 중…"));
    }

    private void StartOperation(Func<CancellationToken, Task> action, string message)
    {
        if (_operation is not null) return;
        var owner = new CancellationTokenSource();
        _operation = owner;
        OperationStatus.Text = message;
        SetBusy(true);
        _active = RunAsync();
        async Task RunAsync()
        {
            try { await action(owner.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { await Dispatcher.InvokeAsync(() => OperationStatus.Text = UiText.T("Cancelled.", "취소했습니다.")); }
            catch { await Dispatcher.InvokeAsync(() => { LogFailure?.Invoke("account-operation-failed"); OperationStatus.Text = UiText.T("Could not complete the operation. Try again.", "작업을 완료하지 못했습니다. 다시 시도하세요."); }); }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _operation = null;
                    owner.Dispose();
                    SetBusy(false);
                    if (_closing) Close();
                });
            }
        }
    }
    private void SetBusy(bool busy)
    {
        AddAccountButton.IsEnabled = DiscoverButton.IsEnabled = ChooseHomeButton.IsEnabled = NewAccountLabel.IsEnabled = !busy;
        OperationProgress.Visibility = CancelOperationButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Bind(_accounts, _selected);
    }
    private void OnCancelOperation(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        OperationStatus.Text = UiText.T("Cancelling…", "취소 중…");
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_operation is null) return;
        _closing = true;
        e.Cancel = true;
        _operation.Cancel();
        Hide();
    }
    public void CancelOperation() => _operation?.Cancel();
    private void OnDone(object sender, RoutedEventArgs e) => Close();
    private void OnHeadingDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
}
