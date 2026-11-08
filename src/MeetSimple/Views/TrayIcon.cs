using Forms = System.Windows.Forms;

namespace MeetSimple.Views;

/// <summary>
/// The tray icon that keeps Meet Simple reachable while its window is closed and it is watching for calls.
/// Click to open; right-click for "Watch for calls" and "Quit". Uses the app's own icon.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.ToolStripMenuItem watchItem;
    private readonly System.Drawing.Icon icon;

    public TrayIcon()
    {
        icon = LoadAppIcon();
        menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Open Meet Simple");
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        openItem.Click += (_, _) => OpenRequested?.Invoke();
        watchItem = new Forms.ToolStripMenuItem("Watch for calls") { CheckOnClick = true };
        watchItem.Click += (_, _) => WatchToggled?.Invoke(watchItem.Checked);
        var quitItem = new Forms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => QuitRequested?.Invoke();
        menu.Items.Add(openItem);
        menu.Items.Add(watchItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quitItem);

        notifyIcon = new Forms.NotifyIcon { Icon = icon, Text = "Meet Simple", ContextMenuStrip = menu, Visible = false };
        notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    public event Action? OpenRequested;
    public event Action? QuitRequested;
    public event Action<bool>? WatchToggled;

    public bool Visible
    {
        get => notifyIcon.Visible;
        set => notifyIcon.Visible = value;
    }

    public void SetState(bool watching, bool recording)
    {
        watchItem.Checked = watching;
        notifyIcon.Text = recording ? "Meet Simple · recording" : watching ? "Meet Simple · watching for calls" : "Meet Simple";
    }

    public void ShowNotice(string title, string message) =>
        notifyIcon.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        menu.Dispose();
        icon.Dispose();
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } executablePath
                && System.Drawing.Icon.ExtractAssociatedIcon(executablePath) is { } appIcon)
            {
                return appIcon;
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException)
        {
            // Fall through to the generic icon.
        }
        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }
}
