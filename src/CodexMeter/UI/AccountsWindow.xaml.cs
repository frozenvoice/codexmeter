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
    public Func<string, int, bool>? MoveAccount { get; set; }
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
        Introduction.Text = UiText.T("Connect accounts here to see their usage together. Choose the connection method that fits your situation.",
            "계정을 연결하면 여러 계정의 사용량을 함께 볼 수 있습니다. 아래에서 상황에 맞는 연결 방법을 선택하세요.");
        NewLoginHeading.Text = UiText.T("First time, or adding another account", "처음 사용하거나 다른 계정을 추가할 때");
        ConnectionOptions.Header = UiText.T("Add an account · Connection guide", "계정 추가 · 연결 방법");
        NewLoginHint.Text = UiText.T("Choose New account sign-in, then select your ChatGPT account in the browser. Return here after login; quota is checked automatically. Your existing Codex login stays as it is.",
            "새 계정 로그인을 누르고 브라우저에서 사용할 ChatGPT 계정을 선택하세요. 로그인 후 돌아오면 사용량을 자동으로 확인합니다. 기존 Codex 로그인은 유지됩니다.");
        LabelCaption.Text = UiText.T("Nickname in CycleArc (optional; you can change it later)", "CycleArc에서 쓸 별명 (선택 사항 · 나중에 변경 가능)");
        NewLabelExample.Text = UiText.T("e.g. Personal, Work", "예: 개인 계정, 업무용");
        AddAccountButton.Content = UiText.T("New account sign-in", "새 계정 로그인");
        ExistingHeading.Text = UiText.T("Already signed in to Codex on this PC", "이 PC의 Codex에 이미 로그인했다면");
        ExistingHint.Text = UiText.T("Find and link the Codex CLI account on this PC. Accounts already listed below are skipped. This does not import accounts signed in only on the ChatGPT website.",
            "이 PC의 Codex CLI에 로그인된 계정을 찾아 연결합니다. 아래 목록에 있는 계정은 건너뜁니다. ChatGPT 웹에만 로그인한 계정은 찾을 수 없습니다.");
        DiscoverButton.Content = UiText.T("Find accounts on this PC", "이 PC의 계정 찾기");
        AdvancedConnection.Header = UiText.T("Advanced · Connect a specific Codex folder", "고급 · Codex 폴더 직접 연결");
        ChooseHomeHint.Text = UiText.T("Use this only if you already keep a Codex login in a custom CODEX_HOME. Choose that home folder, not codex.exe or a project folder. Usually you can use one of the two options above.",
            "CODEX_HOME을 따로 지정해 사용하던 경우에만 필요합니다. 로그인 정보가 있는 Codex 홈 폴더를 고르세요. codex.exe나 작업 프로젝트 폴더가 아닙니다. 보통은 위의 두 방법으로 연결하면 됩니다.");
        ChooseHomeButton.Content = UiText.T("Choose Codex home folder…", "Codex 홈 폴더 선택…");
        ChooseHomeButton.ToolTip = ChooseHomeHint.Text;
        AccountHelp.Header = UiText.T("Nicknames, icons and account actions", "별명·아이콘과 버튼 사용 안내");
        ProfileHelp.Text = UiText.T("ChatGPT's web nickname and profile picture are not provided by the current Codex account connection. Set a nickname below for CycleArc; leaving it empty shows the email. The circular icon is made locally from the first two characters. It is not a synced ChatGPT profile image, and changing it here does not change your ChatGPT profile.",
            "현재 Codex 계정 연결에서는 ChatGPT 웹의 닉네임·프로필 사진을 제공하지 않습니다. 아래 별명을 저장하면 CycleArc에서 그 이름으로 표시하고, 비워 두면 이메일을 표시합니다. 원형 아이콘은 이름의 앞 두 글자로 이 앱에서 만듭니다. ChatGPT 프로필과 동기화되는 이미지가 아니며, 별명을 바꿔도 ChatGPT 프로필은 바뀌지 않습니다.");
        ActionsHelp.Text = UiText.T("Select a card: use this account for the detail card, tray and widget. All accounts continue to refresh.\nOrder ↑ / ↓: move the account in this list and the usage popup. The order is saved immediately; the selected account stays the same.\nSign in again: renew or change the login in a profile added here. For a linked Codex account, sign in again in Codex itself.\nRemove from list: stop showing and checking this profile. Its Codex login and saved data are kept; it is not automatically added back.",
            "카드 선택: 상세 카드·트레이·위젯에 표시할 계정을 정합니다. 다른 계정도 계속 새로고침합니다.\n순서 ↑ / ↓: 이 목록과 사용량 팝업의 계정 순서를 바꿉니다. 즉시 저장되며 선택한 계정은 유지됩니다.\n다시 로그인: 여기서 추가한 계정의 로그인을 갱신하거나 변경합니다. 기존 Codex에서 연결한 계정은 원래 Codex에서 다시 로그인하세요.\n목록에서 제거: 이 프로필의 표시와 조회를 중단합니다. Codex 로그인과 저장된 데이터는 남으며, 자동으로 다시 추가되지 않습니다.");
        SelectionHint.Text = UiText.T("Select a card for the tray and widget. Save a nickname below to make accounts easier to recognize.",
            "카드를 누르면 트레이와 위젯에 표시됩니다. 아래 별명을 저장하면 계정을 더 쉽게 구분할 수 있습니다.");
        EmptyAccountsHint.Text = UiText.T("No accounts are connected yet. Start with New account sign-in above.",
            "아직 연결된 계정이 없습니다. 위의 새 계정 로그인으로 시작하세요.");
        CancelOperationButton.Content = UiText.T("Cancel", "취소");
        DoneButton.Content = UiText.Close;
        OperationStatus.Text = "";
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
        if (!_bound) ConnectionOptions.IsExpanded = accounts.Count == 0
            || accounts.All(account => account.Snapshot.LastSuccessfulRefresh is null && account.Snapshot.Status != CodexQuotaStatus.Available);
        _bound = true;
        _rowsBusy = busy;
        _accounts = accounts;
        _selected = selected;
        AccountsHeading.Text = UiText.T($"Connected accounts · {accounts.Count}", $"연결된 계정 · {accounts.Count}");
        EmptyAccountsHint.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountRows.Items.Clear();
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            var id = account.Profile.Id;
            var content = new StackPanel();
            var summary = AccountSummary.Create(account, selected == id, () => SelectAccount?.Invoke(id));
            summary.IsEnabled = _operation is null;
            content.Children.Add(summary);
            var source = account.Profile.IsManaged ? UiText.T("Signed in through CycleArc", "CycleArc에서 로그인")
                : UiText.T("Linked from Codex on this PC", "이 PC의 기존 Codex에서 연결");
            var identity = new TextBlock { Text = (account.Email is not null && account.Email != account.DisplayName ? account.Email + " · " : "") + source,
                FontSize = 11, Margin = new Thickness(4, 0, 4, 6), TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = account.Profile.HomePath };
            identity.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            content.Children.Add(identity);
            var caption = new TextBlock { Text = UiText.T("Nickname in CycleArc", "CycleArc에서 쓸 별명"),
                FontSize = 11, Margin = new Thickness(4, 2, 4, 5) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var editHeading = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            editHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            editHeading.Children.Add(caption);
            var order = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, IsEnabled = !busy };
            var orderLabel = new TextBlock { Text = UiText.T("Order", "순서"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            orderLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            order.Children.Add(orderLabel);
            order.Children.Add(MoveButton(id, account.DisplayName, -1, index > 0));
            order.Children.Add(MoveButton(id, account.DisplayName, 1, index < accounts.Count - 1));
            Grid.SetColumn(order, 1);
            editHeading.Children.Add(order);
            content.Children.Add(editHeading);
            var actions = new DockPanel { Margin = new Thickness(0, 0, 0, 20), IsEnabled = _operation is null };
            var remove = ActionButton(UiText.T("Remove", "제거"), () =>
            {
                if (RemoveAccount?.Invoke(id) == false)
                    OperationStatus.Text = UiText.T("Wait for this account's operation to finish.", "이 계정의 작업이 끝난 뒤 다시 시도하세요.");
            });
            remove.ToolTip = UiText.T("Remove from this list. The Codex login and saved data are kept.", "목록에서 제거합니다. Codex 로그인과 저장된 데이터는 유지합니다.");
            DockPanel.SetDock(remove, Dock.Right); actions.Children.Add(remove);
            if (account.Profile.IsManaged)
            {
                var login = ActionButton(UiText.T("Sign in again", "다시 로그인"), () => StartLogin(id, account.Profile.Label));
                login.ToolTip = UiText.T("Renew or change this profile's login in your browser.", "브라우저에서 이 프로필의 로그인을 갱신하거나 변경합니다.");
                DockPanel.SetDock(login, Dock.Right); actions.Children.Add(login);
            }
            var rename = ActionButton(UiText.T("Save name", "별명 저장"), () =>
            {
                RenameAccount?.Invoke(id, _labels.GetValueOrDefault(id, account.Profile.Label));
                OperationStatus.Text = UiText.T("Nickname saved in CycleArc. Your ChatGPT profile is unchanged.", "CycleArc 별명을 저장했습니다. ChatGPT 프로필에는 영향을 주지 않습니다.");
            });
            rename.ToolTip = UiText.T("Use this nickname in CycleArc. Leave it empty to show the email.", "이 앱에서 사용할 별명입니다. 비워서 저장하면 이메일을 표시합니다.");
            DockPanel.SetDock(rename, Dock.Right); actions.Children.Add(rename);
            var label = new TextBox { Text = _labels.GetValueOrDefault(id, account.Profile.Label), MaxLength = 80,
                MinWidth = 60, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(label, caption.Text + " · " + account.DisplayName);
            label.ToolTip = rename.ToolTip;
            label.TextChanged += (_, _) => _labels[id] = label.Text;
            actions.Children.Add(label);
            content.Children.Add(actions);
            AccountRows.Items.Add(content);
        }
    }

    private Button MoveButton(string id, string name, int direction, bool enabled)
    {
        var button = ActionButton(direction < 0 ? "↑" : "↓", () =>
        {
            if (MoveAccount?.Invoke(id, direction) == true)
                OperationStatus.Text = UiText.T("Account order saved. The usage popup follows the same order.", "계정 순서를 저장했습니다. 사용량 팝업에도 같은 순서로 표시됩니다.");
        });
        button.Tag = direction < 0 ? "MoveAccountUp" : "MoveAccountDown";
        button.IsEnabled = enabled;
        button.Padding = new Thickness(8, 3, 8, 3);
        button.MinWidth = 30;
        button.MinHeight = 28;
        button.ToolTip = direction < 0 ? UiText.T($"Move {name} up", $"{name} 위로 이동") : UiText.T($"Move {name} down", $"{name} 아래로 이동");
        System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
        return button;
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
            await Dispatcher.InvokeAsync(() => OperationStatus.Text = DiscoveryMessage(result));
        }, UiText.T("Checking existing Codex sign-ins…", "기존 Codex 로그인을 확인하는 중…"));
    }

    private string DiscoveryMessage(CodexDiscoveryResult result)
    {
        if (result.Failed > 0 || result.SignedOut > 0)
            return UiText.T($"Connected {result.Added}; signed out {result.SignedOut}; could not check {result.Failed}. For a new login, use New account sign-in. If Codex cannot be checked, verify its installation/path in Settings → Connection.",
                $"{result.Added}개 연결 · {result.SignedOut}개 로그인 필요 · {result.Failed}개 확인 실패. 새로 로그인하려면 새 계정 로그인을 누르세요. Codex를 확인할 수 없다면 설정 → 연결에서 설치와 경로를 확인하세요.");
        if (result.Added > 0) return UiText.T($"Connected {result.Added} existing accounts. Their quota-check results are shown in the list.",
            $"기존 계정 {result.Added}개를 연결했습니다. 목록에서 사용량 조회 결과를 확인하세요.");
        return _accounts.Count > 0
            ? UiText.T("No additional Codex accounts found. Already connected accounts remain in the list. To add a different account, use New account sign-in.",
                "추가로 연결할 Codex 계정이 없습니다. 이미 연결된 계정은 목록에 있습니다. 다른 계정을 추가하려면 새 계정 로그인을 누르세요.")
            : UiText.T("No signed-in Codex accounts found on this PC. Use New account sign-in to connect one in your browser.",
                "이 PC에서 로그인된 Codex 계정을 찾지 못했습니다. 새 계정 로그인을 눌러 브라우저에서 연결하세요.");
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
