using System.Drawing;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2.Localization;
using Forms = System.Windows.Forms;

namespace WotLK.Launcher.UI.V2;

internal interface ILauncherTrayIconHost : IDisposable
{
    event EventHandler? RestoreRequested;

    event EventHandler? ExitRequested;

    bool IsVisible { get; set; }

    void ShowNotification(string title, string message, bool playSound);
}

internal sealed class LauncherTrayController : IDisposable, ILauncherDesktopNotificationSink
{
    private readonly Window _window;
    private readonly ILauncherTrayIconHost _trayIcon;
    private readonly Action _requestExit;
    private WindowState _restoreWindowState = WindowState.Normal;
    private bool _isHiddenInTray;
    private int _disposeState;

    internal LauncherTrayController(
        Window window,
        ILauncherTrayIconHost trayIcon,
        Action requestExit)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _trayIcon = trayIcon ?? throw new ArgumentNullException(nameof(trayIcon));
        _requestExit = requestExit ?? throw new ArgumentNullException(nameof(requestExit));
        _trayIcon.RestoreRequested += TrayIcon_RestoreRequested;
        _trayIcon.ExitRequested += TrayIcon_ExitRequested;
        _trayIcon.IsVisible = true;
    }

    internal bool IsHiddenInTray => _isHiddenInTray;

    internal void HideInTray()
    {
        RunOnDispatcher(() =>
        {
            if (Volatile.Read(ref _disposeState) != 0 || _isHiddenInTray)
            {
                return;
            }

            if (_window.WindowState != WindowState.Minimized)
            {
                _restoreWindowState = _window.WindowState;
            }

            _trayIcon.IsVisible = true;
            _window.ShowInTaskbar = false;
            _window.Hide();
            _isHiddenInTray = true;
        });
    }

    internal void RestoreWindow()
    {
        RunOnDispatcher(() =>
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            _trayIcon.IsVisible = true;
            _window.ShowInTaskbar = true;
            if (!_window.IsVisible)
            {
                _window.Show();
            }

            _window.WindowState = _restoreWindowState == WindowState.Minimized
                ? WindowState.Normal
                : _restoreWindowState;
            _isHiddenInTray = false;
            _ = _window.Activate();
            _window.Focus();
        });
    }

    internal void ShowNotification(string title, string message, bool playSound)
    {
        RunOnDispatcher(() =>
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            _trayIcon.IsVisible = true;
            _trayIcon.ShowNotification(title, message, playSound);
        });
    }

    void ILauncherDesktopNotificationSink.ShowNotification(
        string title,
        string message,
        bool playSound) => ShowNotification(title, message, playSound);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _trayIcon.RestoreRequested -= TrayIcon_RestoreRequested;
        _trayIcon.ExitRequested -= TrayIcon_ExitRequested;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }

    private void TrayIcon_RestoreRequested(object? sender, EventArgs e) => RestoreWindow();

    private void TrayIcon_ExitRequested(object? sender, EventArgs e)
    {
        RunOnDispatcher(() =>
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            _trayIcon.IsVisible = false;
            _requestExit();
        });
    }

    private void RunOnDispatcher(Action action)
    {
        if (_window.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (!_window.Dispatcher.HasShutdownStarted
            && !_window.Dispatcher.HasShutdownFinished)
        {
            _ = _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, action);
        }
    }

}

internal sealed class WindowsLauncherTrayIconHost : ILauncherTrayIconHost
{
    private readonly Icon _icon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _openItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private int _disposeState;

    internal WindowsLauncherTrayIconHost()
    {
        _icon = LoadApplicationIcon();
        _openItem = new Forms.ToolStripMenuItem("Ouvrir Atlas Launcher");
        _exitItem = new Forms.ToolStripMenuItem("Quitter");
        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false
        };
        _menu.Items.Add(_openItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_exitItem);
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Atlas Launcher",
            ContextMenuStrip = _menu,
            Visible = false
        };

        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        _openItem.Click += OpenItem_Click;
        _exitItem.Click += ExitItem_Click;
        LauncherLocalization.LocaleChanged += LauncherLocalization_LocaleChanged;
        ApplyLocalizedText();
    }

    public event EventHandler? RestoreRequested;

    public event EventHandler? ExitRequested;

    public bool IsVisible
    {
        get => _notifyIcon.Visible;
        set
        {
            if (Volatile.Read(ref _disposeState) == 0)
            {
                _notifyIcon.Visible = value;
            }
        }
    }

    public void ShowNotification(string title, string message, bool playSound)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        if (playSound)
        {
            SystemSounds.Asterisk.Play();
        }

        _notifyIcon.ShowBalloonTip(
            5000,
            title,
            message,
            Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _notifyIcon.MouseClick -= NotifyIcon_MouseClick;
        _openItem.Click -= OpenItem_Click;
        _exitItem.Click -= ExitItem_Click;
        LauncherLocalization.LocaleChanged -= LauncherLocalization_LocaleChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        RestoreRequested = null;
        ExitRequested = null;
    }

    private void NotifyIcon_MouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            RestoreRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OpenItem_Click(object? sender, EventArgs e) =>
        RestoreRequested?.Invoke(this, EventArgs.Empty);

    private void ExitItem_Click(object? sender, EventArgs e) =>
        ExitRequested?.Invoke(this, EventArgs.Empty);

    private void LauncherLocalization_LocaleChanged(object? sender, EventArgs e) =>
        ApplyLocalizedText();

    private void ApplyLocalizedText()
    {
        _openItem.Text = LauncherLocalization.Text("Ouvrir Atlas Launcher");
        _exitItem.Text = LauncherLocalization.Text("Quitter");
    }

    private static Icon LoadApplicationIcon()
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Le chemin d'Atlas Launcher est indisponible.");
        return Icon.ExtractAssociatedIcon(processPath)
            ?? throw new InvalidOperationException("L'icône d'Atlas Launcher est indisponible.");
    }
}
