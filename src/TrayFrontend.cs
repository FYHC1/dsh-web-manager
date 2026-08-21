using System;
using System.Drawing;
using System.Windows.Forms;

namespace DshWebManager
{
    /// <summary>System-tray frontend: icon, context menu, balloon notifications.</summary>
    public sealed class TrayFrontend : Form
    {
        private readonly ManagerService _service;
        private readonly NotifyIcon _notify;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly ToolStripMenuItem _backendWindowsItem;
        private readonly ToolStripMenuItem _backendWslItem;
        private bool _closing;

        private static string MenuOpen = "\u6253\u5f00\u7a97\u53e3";            // 打开窗口
        private static string MenuRestart = "\u91cd\u542f\u670d\u52a1";        // 重启服务
        private static string MenuAutoStart = "\u5f00\u673a\u81ea\u542f";      // 开机自启
        private static string MenuExit = "\u9000\u51fa";                       // 退出
        private static string MenuStatus = "\u72b6\u6001";                     // 状态
        private static string MenuBackend = "\u540e\u7aef";                    // 后端
        private static string MenuBackendWindows = "Windows \u672c\u673a";     // Windows 本机
        private static string MenuBackendWsl = "WSL";
        private static string Title = "dsh web manager";

        public TrayFrontend(ManagerService service)
        {
            _service = service;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;

            _notify = new NotifyIcon();
            _notify.Text = Title;
            _notify.Icon = LoadTrayIcon();
            _notify.Visible = true;

            _statusItem = new ToolStripMenuItem(MenuStatus + ": \u542f\u52a8\u4e2d…"); // 启动中…
            _statusItem.Enabled = false;
            _autoStartItem = new ToolStripMenuItem(MenuAutoStart);
            _autoStartItem.CheckOnClick = true;
            _autoStartItem.Checked = service.Config.AutoStart;
            _autoStartItem.Click += delegate { _service.ToggleAutoStart(); };

            _backendWindowsItem = new ToolStripMenuItem(MenuBackendWindows);
            _backendWslItem = new ToolStripMenuItem(MenuBackendWsl);
            _backendWindowsItem.Click += delegate { _service.SetBackend("windows"); };
            _backendWslItem.Click += delegate { _service.SetBackend("wsl"); };
            ToolStripMenuItem backendMenu = new ToolStripMenuItem(MenuBackend);
            backendMenu.DropDownItems.Add(_backendWindowsItem);
            backendMenu.DropDownItems.Add(_backendWslItem);
            RefreshBackendCheck();

            _menu = new ContextMenuStrip();
            _menu.Items.Add(new ToolStripMenuItem(MenuOpen, null, delegate { _service.OpenWindow(); }));
            _menu.Items.Add(new ToolStripMenuItem(MenuRestart, null, delegate { _service.Restart(); }));
            _menu.Items.Add(_autoStartItem);
            _menu.Items.Add(backendMenu);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_statusItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem(MenuExit, null, delegate { _closing = true; _service.Exit(true); }));
            _notify.ContextMenuStrip = _menu;

            _notify.MouseClick += OnMouseClick;

            _service.StatusChanged += s => UpdateStatus(s);
            _service.Balloon += (t, b) => ShowBalloon(t, b);

            UpdateStatus(_service.Controller.StatusText);
        }

        private Icon LoadTrayIcon()
        {
            // Prefer the official multi-size icon file; fall back to the EXE resource.
            try
            {
                if (System.IO.File.Exists(AppPaths.IconFile))
                    return new Icon(AppPaths.IconFile, SystemInformation.SmallIconSize);
            }
            catch { }
            try
            {
                return Icon.ExtractAssociatedIcon(AppPaths.ExePath);
            }
            catch { }
            return SystemIcons.Application;
        }

        private void OnMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                _service.OpenWindow();
        }

        public void UpdateStatus(string text)
        {
            try
            {
                if (_statusItem != null) _statusItem.Text = MenuStatus + ": " + text;
                _notify.Text = Title + " - " + text;
                RefreshBackendCheck();
            }
            catch { }
        }

        private void RefreshBackendCheck()
        {
            try
            {
                bool wsl = _service.Config.IsWsl;
                if (_backendWslItem != null) _backendWslItem.Checked = wsl;
                if (_backendWindowsItem != null) _backendWindowsItem.Checked = !wsl;
            }
            catch { }
        }

        public void ShowBalloon(string title, string text)
        {
            try
            {
                _notify.BalloonTipTitle = title;
                _notify.BalloonTipText = text;
                _notify.ShowBalloonTip(3000);
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closing)
            {
                // Closing the hidden form must not kill the tray app; hide instead.
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _notify.Visible = false;
                _notify.Dispose();
                _menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}