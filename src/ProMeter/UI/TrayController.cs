using System.Windows.Forms;

namespace ProMeter.UI;

public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private Icon? _current;

    public event Action? LeftClick;
    public event Action? OpenRequested;
    public event Action? SyncRequested;
    public event Action? LoginRequested;
    public event Action? SettingsRequested;
    public event Action? OpenLogsRequested;
    public event Action? StatisticsRequested;
    public event Action<bool>? StartupToggled;
    public event Action? AboutRequested;
    public event Action? ExitRequested;

    public TrayController()
    {
        _icon = new NotifyIcon
        {
            Visible = true,
            Text = "ProMeter"
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
        menu.Items.Add("Open ProMeter", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Sync now", null, (_, _) => SyncRequested?.Invoke());
        menu.Items.Add("Open Login", null, (_, _) => LoginRequested?.Invoke());
        menu.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add("Open logs", null, (_, _) => OpenLogsRequested?.Invoke());
        menu.Items.Add("View statistics", null, (_, _) => StatisticsRequested?.Invoke());
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = startWithWindows, CheckOnClick = true };
        startup.CheckedChanged += (_, _) => StartupToggled?.Invoke(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add("About", null, (_, _) => AboutRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
    }

    public void Update(QuotaSnapshot snapshot, TrayIconStyle style)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _icon.Text = DisplayFormatting.Tooltip(snapshot);
            var next = TrayIconRenderer.Render(snapshot, style, 32);
            _icon.Icon = next;
            _current?.Dispose();
            _current = next;
        });
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
