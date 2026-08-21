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
        private readonly ToolStripMenuItem _modeWrapperItem;
        private readonly ToolStripMenuItem _modeSystemdItem;
        private readonly System.Collections.Generic.List<ToolStripMenuItem> _instanceItems =
            new System.Collections.Generic.List<ToolStripMenuItem>();
        private ToolStripMenuItem _instancesMenu;
        private bool _closing;

        private static string MenuOpen = "\u6253\u5f00\u7a97\u53e3";            // 打开窗口
        private static string MenuRestart = "\u91cd\u542f\u670d\u52a1";        // 重启服务
        private static string MenuAutoStart = "\u5f00\u673a\u81ea\u542f";      // 开机自启
        private static string MenuExit = "\u9000\u51fa";                       // 退出
        private static string MenuStatus = "\u72b6\u6001";                     // 状态
        private static string MenuBackend = "\u540e\u7aef";                    // 后端
        private static string MenuBackendWindows = "Windows \u672c\u673a";     // Windows 本机
        private static string MenuBackendWsl = "WSL";
        private static string MenuWslMode = "WSL \u670d\u52a1\u6a21\u5f0f";       // WSL 服务模式
        private static string MenuWslModeWrapper = "wrapper (\u81ea\u6108\u811a\u672c)";  // wrapper (自愈脚本)
        private static string MenuWslModeSystemd = "systemd (unit)";
        private static string MenuInstances = "\u5b9e\u4f8b";                    // 实例
        private static string MenuCheckUpdate = "\u68c0\u67e5\u66f4\u65b0";    // 检查更新
        private static string MenuUpdateDsh = "\u66f4\u65b0 dsh";                // 更新 dsh
        private static string MenuAddInstance = "\u6dfb\u52a0\u5b9e\u4f8b";       // 添加实例
        private static string MenuRemoveInstance = "\u5220\u9664\u5b9e\u4f8b";    // 删除实例
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

            _modeWrapperItem = new ToolStripMenuItem(MenuWslModeWrapper);
            _modeSystemdItem = new ToolStripMenuItem(MenuWslModeSystemd);
            _modeWrapperItem.Click += delegate { _service.SetWslMode("wrapper"); };
            _modeSystemdItem.Click += delegate { _service.SetWslMode("systemd"); };
            ToolStripMenuItem modeMenu = new ToolStripMenuItem(MenuWslMode);
            modeMenu.DropDownItems.Add(_modeWrapperItem);
            modeMenu.DropDownItems.Add(_modeSystemdItem);

            RefreshBackendCheck();

            // v3.0 multi-instance: every instance gets its own submenu, plus add/remove.
            _instancesMenu = new ToolStripMenuItem(MenuInstances);
            RebuildInstanceMenu();

            _menu = new ContextMenuStrip();
            _menu.Items.Add(new ToolStripMenuItem(MenuOpen, null, delegate { _service.OpenWindow(); }));
            _menu.Items.Add(new ToolStripMenuItem(MenuRestart, null, delegate { _service.Restart(); }));
            _menu.Items.Add(_autoStartItem);
            _menu.Items.Add(backendMenu);
            _menu.Items.Add(modeMenu);
            _menu.Items.Add(_instancesMenu);
            ToolStripMenuItem checkItem = new ToolStripMenuItem(MenuCheckUpdate, null, delegate { _service.CheckForUpdates(); });
            ToolStripMenuItem updateItem = new ToolStripMenuItem(MenuUpdateDsh, null, delegate { _service.ApplyDshUpdate(); });
            ToolStripMenuItem updatesMenu = new ToolStripMenuItem("\u66f4\u65b0"); // 更新
            updatesMenu.DropDownItems.Add(checkItem);
            updatesMenu.DropDownItems.Add(updateItem);
            _menu.Items.Add(updatesMenu);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_statusItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem(MenuExit, null, delegate { _closing = true; _service.Exit(true); }));
            _notify.ContextMenuStrip = _menu;

            _notify.MouseClick += OnMouseClick;

            _service.StatusChanged += s => UpdateStatus(s);
            _service.Balloon += (t, b) => ShowBalloon(t, b);
            _service.InstancesChanged += RebuildInstanceMenu;

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

        private static string InstanceLabel(InstanceController c)
        {
            string where = c.BackendDescribe;
            if (String.IsNullOrEmpty(where)) where = c.Instance == null ? "?" : c.Instance.Id;
            return where + " :" + c.ActivePort;
        }

        private void RestartInstance(int index)
        {
            InstanceController c = _service.GetController(index);
            if (c == null) return;
            c.Restart();
            try
            {
                EdgeWindow.EnsureVisible(_service.Config, c.Backend.GetWindowUrl(c.ActivePort), c.ActivePort);
            }
            catch (Exception ex) { FileLog.Error("RestartInstance window: " + ex.Message); }
        }

        /// <summary>Rebuilds the instance submenu from the live controller list (v3.0 P2-2).</summary>
        private void RebuildInstanceMenu()
        {
            if (_instancesMenu == null) return;
            _instanceItems.Clear();
            _instancesMenu.DropDownItems.Clear();
            for (int i = 0; i < _service.Controllers.Count; i++)
            {
                int idx = i;
                InstanceController ic = _service.Controllers[idx];
                ToolStripMenuItem item = new ToolStripMenuItem(InstanceLabel(ic));
                ToolStripMenuItem openItem = new ToolStripMenuItem(MenuOpen, null, delegate { _service.OpenWindow(idx); });
                ToolStripMenuItem restartItem = new ToolStripMenuItem(MenuRestart, null, delegate { RestartInstance(idx); });
                ToolStripMenuItem statusItem = new ToolStripMenuItem(MenuStatus + ": " + ic.StatusText);
                statusItem.Enabled = false;
                item.DropDownItems.Add(openItem);
                item.DropDownItems.Add(restartItem);
                item.DropDownItems.Add(new ToolStripSeparator());
                item.DropDownItems.Add(statusItem);
                _instanceItems.Add(item);
                _instancesMenu.DropDownItems.Add(item);
            }
            _instancesMenu.DropDownItems.Add(new ToolStripSeparator());
            _instancesMenu.DropDownItems.Add(new ToolStripMenuItem(MenuAddInstance, null, delegate { ShowAddInstanceDialog(); }));
            _instancesMenu.DropDownItems.Add(new ToolStripMenuItem(MenuRemoveInstance, null, delegate { ShowRemoveInstanceDialog(); }));
        }

        private void ShowAddInstanceDialog()
        {
            using (InstanceDialog dlg = new InstanceDialog(null))
            {
                if (dlg.ShowDialog() == DialogResult.OK && dlg.Result != null)
                    _service.AddInstance(dlg.Result);
            }
        }

        private void ShowRemoveInstanceDialog()
        {
            InstanceController target = null;
            using (RemoveInstanceDialog dlg = new RemoveInstanceDialog(_service))
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                    target = dlg.Selected;
            }
            if (target == null) return;
            int index = -1;
            for (int i = 0; i < _service.Controllers.Count; i++)
                if (ReferenceEquals(_service.Controllers[i], target)) { index = i; break; }
            if (index >= 0) _service.RemoveInstance(index);
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
                for (int i = 0; i < _instanceItems.Count && i < _service.Controllers.Count; i++)
                {
                    ToolStripMenuItem item = _instanceItems[i];
                    InstanceController ic = _service.Controllers[i];
                    item.Text = InstanceLabel(ic);
                    if (item.DropDownItems.Count >= 4)
                        item.DropDownItems[3].Text = MenuStatus + ": " + ic.StatusText;
                }
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
                bool systemd = _service.Config.IsWsl
                    && String.Equals(_service.Config.WslServiceMode, "systemd", StringComparison.OrdinalIgnoreCase);
                if (_modeSystemdItem != null) _modeSystemdItem.Checked = systemd;
                if (_modeWrapperItem != null) _modeWrapperItem.Checked = !systemd;
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

        /// <summary>Modal dialog for entering a new instance's parameters (v3.0 P2-2).</summary>
        private sealed class InstanceDialog : Form
        {
            private readonly TextBox _id;
            private readonly TextBox _profile;
            private readonly TextBox _port;
            private readonly ComboBox _backend;
            private readonly TextBox _wslPort;
            private readonly TextBox _wslDistro;
            private readonly ComboBox _mode;
            public InstanceConfig Result { get; private set; }

            public InstanceDialog(InstanceConfig existing)
            {
                Text = MenuAddInstance;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(380, 320);

                _id = new TextBox();
                _profile = new TextBox();
                _port = new TextBox();
                _backend = new ComboBox();
                _wslPort = new TextBox();
                _wslDistro = new TextBox();
                _mode = new ComboBox();

                _id.Text = existing == null ? "wsl2" : existing.Id;
                _profile.Text = existing == null ? "web" : existing.Profile;
                _port.Text = existing == null ? "3082" : existing.Port.ToString();
                _backend.Items.AddRange(new object[] { "windows", "wsl" });
                _backend.SelectedIndex = existing != null && existing.IsWsl ? 1 : 0;
                _wslPort.Text = existing == null ? "3080" : existing.WslPort.ToString();
                _wslDistro.Text = existing == null ? String.Empty : existing.WslDistro;
                _mode.Items.AddRange(new object[] { "wrapper", "systemd" });
                _mode.SelectedIndex = existing != null && String.Equals(existing.WslServiceMode, "systemd", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

                TableLayoutPanel table = new TableLayoutPanel();
                table.ColumnCount = 2;
                table.RowCount = 8;
                table.Dock = DockStyle.Fill;
                table.Padding = new Padding(12);
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

                AddRow(table, 0, "实例 Id", _id);
                AddRow(table, 1, "后端", _backend);
                AddRow(table, 2, "Profile", _profile);
                AddRow(table, 3, "端口 (Windows)", _port);
                AddRow(table, 4, "端口 (WSL)", _wslPort);
                AddRow(table, 5, "WSL 发行版", _wslDistro);
                AddRow(table, 6, "WSL 模式", _mode);

                Button ok = new Button();
                ok.Text = "确定";
                ok.DialogResult = DialogResult.OK;
                ok.Dock = DockStyle.Fill;
                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Dock = DockStyle.Fill;
                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.Dock = DockStyle.Fill;
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                table.Controls.Add(buttons, 1, 7);

                Controls.Add(table);
                AcceptButton = ok;
                CancelButton = cancel;
            }

            private static void AddRow(TableLayoutPanel table, int row, string label, Control input)
            {
                Label l = new Label();
                l.Text = label;
                l.Dock = DockStyle.Fill;
                l.TextAlign = ContentAlignment.MiddleLeft;
                input.Dock = DockStyle.Fill;
                table.Controls.Add(l, 0, row);
                table.Controls.Add(input, 1, row);
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (DialogResult == DialogResult.OK)
                {
                    int p, wp;
                    if (String.IsNullOrWhiteSpace(_id.Text))
                    {
                        MessageBox.Show("实例 Id 不能为空");
                        e.Cancel = true;
                        return;
                    }
                    if (!int.TryParse(_port.Text, out p) || p <= 0)
                    {
                        MessageBox.Show("端口 (Windows) 必须是正整数");
                        e.Cancel = true;
                        return;
                    }
                    if (!int.TryParse(_wslPort.Text, out wp) || wp <= 0)
                    {
                        MessageBox.Show("端口 (WSL) 必须是正整数");
                        e.Cancel = true;
                        return;
                    }
                    Result = new InstanceConfig();
                    Result.Id = _id.Text.Trim();
                    Result.Profile = _profile.Text.Trim();
                    Result.BackendType = _backend.SelectedItem == null ? "windows" : _backend.SelectedItem.ToString();
                    Result.Port = p;
                    Result.WslPort = wp;
                    Result.WslDistro = _wslDistro.Text.Trim();
                    Result.WslServiceMode = _mode.SelectedItem == null ? "wrapper" : _mode.SelectedItem.ToString();
                }
                base.OnFormClosing(e);
            }
        }

        /// <summary>Modal dialog for choosing an instance to remove (v3.0 P2-2).</summary>
        private sealed class RemoveInstanceDialog : Form
        {
            private readonly ComboBox _pick;
            private readonly System.Collections.Generic.List<InstanceController> _list =
                new System.Collections.Generic.List<InstanceController>();
            public InstanceController Selected { get; private set; }

            public RemoveInstanceDialog(ManagerService service)
            {
                Text = MenuRemoveInstance;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(340, 150);

                _pick = new ComboBox();
                _pick.DropDownStyle = ComboBoxStyle.DropDownList;
                foreach (InstanceController c in service.Controllers)
                {
                    _pick.Items.Add(InstanceLabel(c));
                    _list.Add(c);
                }
                if (_pick.Items.Count > 0) _pick.SelectedIndex = 0;

                Label hint = new Label();
                hint.Text = "选择要删除的实例（删除会停止并移除它）：";
                hint.Dock = DockStyle.Top;
                hint.AutoSize = false;
                hint.Height = 40;
                hint.Padding = new Padding(12, 10, 12, 0);

                _pick.Dock = DockStyle.Top;
                _pick.Height = 30;
                _pick.Margin = new Padding(12, 0, 12, 0);

                Button ok = new Button();
                ok.Text = "删除";
                ok.DialogResult = DialogResult.OK;
                ok.Width = 90;
                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Width = 90;
                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.Dock = DockStyle.Bottom;
                buttons.Height = 44;
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);

                Controls.Add(_pick);
                Controls.Add(hint);
                Controls.Add(buttons);
                AcceptButton = ok;
                CancelButton = cancel;
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (DialogResult == DialogResult.OK && _pick.SelectedIndex >= 0 && _pick.SelectedIndex < _list.Count)
                    Selected = _list[_pick.SelectedIndex];
                base.OnFormClosing(e);
            }
        }
    }
}