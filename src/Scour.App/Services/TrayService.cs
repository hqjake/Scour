using System.Windows;
using Forms = System.Windows.Forms;

namespace Scour.App.Services;

/// <summary>Notification-area icon with quick actions and balloon notifications.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private Action? _balloonClick;

    public TrayService(Action open, Action quickClean, Action checkHealth, Action analyzeDisk, Action exit)
    {
        _icon = new Forms.NotifyIcon
        {
            Text = "Scour",
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        var menu = _icon.ContextMenuStrip;
        menu.Items.Add("Open Scour", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quick clean", null, (_, _) => quickClean());
        menu.Items.Add("Check PC health", null, (_, _) => checkHealth());
        menu.Items.Add("Analyze disk space", null, (_, _) => analyzeDisk());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
    }

    public static System.Drawing.Icon LoadIcon()
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/Scour;component/Assets/scour.ico"));
        return res is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(res.Stream, 32, 32);
    }

    public void Notify(string title, string text, Action? onClick = null, bool warning = false)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(6000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>One-off notification without a lasting tray icon (used by the scheduled clean).</summary>
    public static async Task NotifyOnceAsync(string title, string text)
    {
        using var icon = new Forms.NotifyIcon { Icon = LoadIcon(), Visible = true, Text = "Scour" };
        icon.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.Info);
        await Task.Delay(8000);
        icon.Visible = false;
    }
}
