using ProMeter.Codex;
using System.Windows.Forms;

namespace ProMeter.UI;

public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private Icon? _current;

    public event Action? LeftClick;
    public event Action? OpenRequested;
    public event Action? SyncRequested;
    public event Action? SettingsRequested;
    public event Action? OpenLogsRequested;
    public event Action<bool>? StartupToggled;
    public event Action? AboutRequested;
    public event Action? ExitRequested;

    public TrayController()
    {
        _icon = new NotifyIcon
        {
            Visible = true,
            Text = NotifyIconText.Safe(UiText.ProductName)
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                LeftClick?.Invoke();
            }
        };
        RebuildMenu(startWithWindows: true);
    }

    public void RebuildMenu(bool startWithWindows)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(UiText.OpenProMeter, null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(UiText.RefreshAll, null, (_, _) => SyncRequested?.Invoke());
        menu.Items.Add(UiText.Settings, null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(UiText.OpenLogs, null, (_, _) => OpenLogsRequested?.Invoke());
        var startup = new ToolStripMenuItem(UiText.StartWithWindows) { Checked = startWithWindows, CheckOnClick = true };
        startup.CheckedChanged += (_, _) => StartupToggled?.Invoke(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(UiText.About, null, (_, _) => AboutRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(UiText.Exit, null, (_, _) => ExitRequested?.Invoke());
        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    public void Update(CodexQuotaSnapshot snapshot, TrayIconStyle style)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _icon.Text = NotifyIconText.Safe(CodexMeterPresentation.Tooltip(snapshot));
            var next = TrayIconRenderer.Render(snapshot, style, 32);
            _icon.Icon = next;
            _current?.Dispose();
            _current = next;
        });
    }

    public void ShowContextMenu()
    {
        var menu = _icon.ContextMenuStrip;
        if (menu is null)
        {
            return;
        }

        menu.Show(System.Windows.Forms.Control.MousePosition);
    }

    public void Balloon(string title, string body)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
