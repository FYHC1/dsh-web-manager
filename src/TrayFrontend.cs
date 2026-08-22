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
        private readonly Button _btnWindows;
        private readonly Button _btnWsl;
        private readonly ToolStripMenuItem _modeMenu;
        private readonly ToolStripMenuItem _modeWrapperItem;
        private readonly ToolStripMenuItem _modeSystemdItem;
        private readonly ToolStripMenuItem _defaultWindowsItem;
        private readonly ToolStripMenuItem _defaultWslItem;
        private readonly ToolStripMenuItem _defaultMenu;
        private readonly System.Collections.Generic.List<ToolStripMenuItem> _instanceItems =
            new System.Collections.Generic.List<ToolStripMenuItem>();
        private ToolStripMenuItem _instancesMenu;
        private string _pendingStatus;
        private bool _statusFlushPending;
        private bool _closing;
        private const int MenuMinimumWidth = 220;
        private const int StatusItemHeight = 46;   // fixed two-line status height
        // 开机自启"开"状态用灰色阴影背景标识（主菜单不开勾选列，避免加宽所有项）。
        private static readonly Color AutoStartOnColor = Color.FromArgb(0xE6, 0xE6, 0xE6);

        private static string MenuOpen = "\u6253\u5f00\u7a97\u53e3";            // 打开窗口
        private static string MenuRestart = "\u91cd\u542f\u670d\u52a1";        // 重启服务
        private static string MenuAutoStart = "\u5f00\u673a\u81ea\u542f";      // 开机自启
        private static string MenuExit = "\u9000\u51fa";                       // 退出
        private static string MenuStatus = "\u72b6\u6001";                     // 状态
        private static string MenuBackendWindows = "Windows \u672c\u673a";     // Windows 本机
        private static string MenuBackendWsl = "WSL";
        private static string MenuWslMode = "WSL \u670d\u52a1\u6a21\u5f0f";       // WSL 服务模式
        private static string MenuWindowsMode = "Windows \u670d\u52a1\u6a21\u5f0f"; // Windows 服务模式（占位，保持菜单高度）
        private static string MenuWslModeWrapper = "wrapper (\u81ea\u6108\u811a\u672c)";  // wrapper (自愈脚本)
        private static string MenuWslModeSystemd = "systemd (unit)";
        private static string MenuInstances = "\u5b9e\u4f8b";                    // 实例
        private static string MenuCheckUpdate = "\u68c0\u67e5\u66f4\u65b0";    // 检查更新
        private static string MenuUpdateDsh = "\u66f4\u65b0 dsh";                // 更新 dsh
        private static string MenuAddInstance = "\u6dfb\u52a0\u5b9e\u4f8b";       // 添加实例
        private static string MenuRemoveInstance = "\u5220\u9664\u5b9e\u4f8b";    // 删除实例
        private static string MenuCloseInstance = "\u5173\u95ed\u5b9e\u4f8b";        // 关闭实例
        private static string MenuDefaultBackend = "\u9ed8\u8ba4\u542f\u52a8\u540e\u7aef";  // 默认启动后端
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
            _autoStartItem.Click += delegate { _service.ToggleAutoStart(); RefreshBackendCheck(); };

            _btnWindows = new Button();
            _btnWindows.Text = MenuBackendWindows;
            _btnWindows.FlatStyle = FlatStyle.Flat;
            _btnWindows.FlatAppearance.BorderSize = 0;
            _btnWindows.Width = 96;
            _btnWindows.Height = 30;
            _btnWindows.Cursor = Cursors.Hand;
            _btnWindows.Click += delegate { _service.ActiveBackend = "windows"; RefreshBackendCheck(); };

            _btnWsl = new Button();
            _btnWsl.Text = MenuBackendWsl;
            _btnWsl.FlatStyle = FlatStyle.Flat;
            _btnWsl.FlatAppearance.BorderSize = 0;
            _btnWsl.Width = 96;
            _btnWsl.Height = 30;
            _btnWsl.Cursor = Cursors.Hand;
            _btnWsl.Click += delegate { _service.ActiveBackend = "wsl"; RefreshBackendCheck(); };

            FlowLayoutPanel switcher = new FlowLayoutPanel();
            switcher.FlowDirection = FlowDirection.LeftToRight;
            switcher.WrapContents = false;
            switcher.AutoSize = true;
            switcher.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            switcher.Margin = new Padding(0);
            switcher.Padding = new Padding(4, 3, 4, 3);
            switcher.Controls.Add(_btnWindows);
            switcher.Controls.Add(_btnWsl);
            ToolStripControlHost switcherHost = new ToolStripControlHost(switcher);
            // Pin the host size: a self-sizing host nested in an auto-sized menu causes
            // repeated measure/layout churn (the tray felt janky). A fixed host breaks the
            // cascading AutoSize loop.
            switcherHost.AutoSize = false;
            Size swPref = switcher.GetPreferredSize(Size.Empty);
            switcherHost.Size = new Size(swPref.Width + 8, swPref.Height + 6);

            _modeWrapperItem = new ToolStripMenuItem(MenuWslModeWrapper);
            _modeSystemdItem = new ToolStripMenuItem(MenuWslModeSystemd);
            _modeWrapperItem.Click += delegate { _service.SetWslMode("wrapper"); };
            _modeSystemdItem.Click += delegate { _service.SetWslMode("systemd"); };
            _modeMenu = new ToolStripMenuItem(MenuWslMode);
            _modeMenu.DropDownItems.Add(_modeWrapperItem);
            _modeMenu.DropDownItems.Add(_modeSystemdItem);

            _defaultWindowsItem = new ToolStripMenuItem(MenuBackendWindows);
            _defaultWslItem = new ToolStripMenuItem(MenuBackendWsl);
            _defaultWindowsItem.Click += delegate { _service.DefaultBackend = "windows"; RefreshBackendCheck(); };
            _defaultWslItem.Click += delegate { _service.DefaultBackend = "wsl"; RefreshBackendCheck(); };
            _defaultMenu = new ToolStripMenuItem(MenuDefaultBackend);
            _defaultMenu.DropDownItems.Add(_defaultWindowsItem);
            _defaultMenu.DropDownItems.Add(_defaultWslItem);

            RefreshBackendCheck();

            // v3.0 multi-instance: every instance gets its own submenu, plus add/remove.
            _instancesMenu = new ToolStripMenuItem(MenuInstances);
            RebuildInstanceMenu();

            _menu = new ContextMenuStrip();
            _menu.Renderer = new TrayRenderer();
            _menu.ShowImageMargin = false;
            _menu.MinimumSize = new Size(MenuMinimumWidth, 0);
            _menu.Items.Add(switcherHost);        // 顶部: 横向 Windows/WSL 切换按钮
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem(MenuOpen, null, delegate { _service.OpenWindow(); }));
            _menu.Items.Add(new ToolStripMenuItem(MenuRestart, null, delegate { _service.Restart(); }));
            _menu.Items.Add(_autoStartItem);
            _menu.Items.Add(_modeMenu);
            _menu.Items.Add(_defaultMenu);
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

            _notify.ContextMenuStrip = _menu;   // native tray popup: system owns position, focus and dismissal
            _notify.MouseClick += OnMouseClick;

            _service.StatusChanged += s => UpdateStatus(s);
            _service.Balloon += (t, b) => ShowBalloon(t, b);
            _service.InstancesChanged += RebuildInstanceMenu;

            ApplyThemeToSubmenus();
            _statusItem.AutoSize = false;
            _statusItem.Height = StatusItemHeight;
            _statusItem.Width = MenuMinimumWidth;
            UpdateStatus(_service.Controller.StatusText);
        }

        /// <summary>Small, deterministic renderer: native item sizing, immediate
        /// hover paint, explicit arrows and separators. No gradients or animations.</summary>
        private sealed class TrayRenderer : ToolStripProfessionalRenderer
        {
            private static readonly Color Hover = Color.FromArgb(0xE8, 0xF0, 0xFE);
            private static readonly Color Border = Color.FromArgb(0x2D, 0x7F, 0xFF);

            public TrayRenderer() : base(new TrayColorTable()) { RoundedEdges = false; }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                Rectangle r = new Rectangle(Point.Empty, e.Item.Size);
                Color c = e.Item.Selected ? Hover : e.ToolStrip.BackColor;
                // 主菜单不开勾选列：像"开机自启"这类用背景色标识"开"状态的项，非悬停时按其
                // 显式设置的 BackColor（灰色阴影）绘制；悬停仍显示淡蓝高亮。
                if (!e.Item.Selected && !e.Item.BackColor.IsEmpty)
                    c = e.Item.BackColor;
                using (SolidBrush b = new SolidBrush(c)) e.Graphics.FillRectangle(b, r);
                if (e.Item.Selected)
                    using (Pen p = new Pen(Border)) e.Graphics.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Color.FromArgb(0x55, 0x55, 0x55);
                base.OnRenderArrow(e);
            }
        }

        private sealed class TrayColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Color.White; } }
            public override Color MenuItemSelected { get { return Color.FromArgb(0xE8, 0xF0, 0xFE); } }
            public override Color MenuItemBorder { get { return Color.FromArgb(0x2D, 0x7F, 0xFF); } }
            public override Color MenuBorder { get { return Color.FromArgb(0xD0, 0xD0, 0xD0); } }
            public override Color SeparatorDark { get { return Color.FromArgb(0xE0, 0xE0, 0xE0); } }
            public override Color SeparatorLight { get { return Color.FromArgb(0xE0, 0xE0, 0xE0); } }
            public override Color ImageMarginGradientBegin { get { return Color.White; } }
            public override Color ImageMarginGradientMiddle { get { return Color.White; } }
            public override Color ImageMarginGradientEnd { get { return Color.White; } }
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
            // Restarting a service can block for seconds (esp. WSL): do it off the UI thread.
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    c.Restart();
                    EdgeWindow.EnsureVisible(_service.Config, c.Backend.GetWindowUrl(c.ActivePort), c.ActivePort);
                }
                catch (Exception ex) { FileLog.Error("RestartInstance: " + ex.Message); }
            });
        }

        private void CloseInstance(int index)
        {
            InstanceController c = _service.GetController(index);
            if (c == null) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    c.Stop(false); // stop the service (managed: full stop; attached: detach)
                }
                catch (Exception ex) { FileLog.Error("CloseInstance stop: " + ex.Message); }
                try
                {
                    EdgeWindow.CloseWindow(c.ActivePort);
                }
                catch (Exception ex) { FileLog.Error("CloseInstance window: " + ex.Message); }
            });
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
                ToolStripMenuItem closeItem = new ToolStripMenuItem(MenuCloseInstance, null, delegate { CloseInstance(idx); });
                ToolStripMenuItem statusItem = new ToolStripMenuItem(MenuStatus + ": " + ic.StatusText);
                statusItem.Enabled = false;
                item.DropDownItems.Add(openItem);
                item.DropDownItems.Add(restartItem);
                item.DropDownItems.Add(closeItem);
                item.DropDownItems.Add(new ToolStripSeparator());
                item.DropDownItems.Add(statusItem);
                _instanceItems.Add(item);
                _instancesMenu.DropDownItems.Add(item);
            }
            _instancesMenu.DropDownItems.Add(new ToolStripSeparator());
            _instancesMenu.DropDownItems.Add(new ToolStripMenuItem(MenuAddInstance, null, delegate { ShowAddInstanceDialog(); }));
            _instancesMenu.DropDownItems.Add(new ToolStripMenuItem(MenuRemoveInstance, null, delegate { ShowRemoveInstanceDialog(); }));
            if (_menu != null) ApplyThemeToSubmenus();
        }

        private void ShowAddInstanceDialog()
        {
            using (InstanceDialog dlg = new InstanceDialog(null, _service.Config))
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

        /// <summary>Submenu ToolStripDropDowns do not inherit the parent menu's renderer;
        /// apply the light theme to every nested menu so hover/selection colors match.</summary>
        private void ApplyThemeToSubmenus()
        {
            try
            {
                foreach (ToolStripItem item in _menu.Items)
                {
                    ToolStripDropDownItem mi = item as ToolStripDropDownItem;
                    if (mi != null) ApplySubmenuRenderer(mi);
                }
            }
            catch { }
        }

        private void ApplySubmenuRenderer(ToolStripDropDownItem parent)
        {
            try
            {
                // Accessing DropDown on a leaf item creates an empty native popup
                // window (extra taskbar button / missing arrows). Only real menus get one.
                if (parent.DropDownItems.Count == 0) return;
                parent.DropDown.Renderer = _menu.Renderer;
                ToolStripDropDownMenu dropMenu = parent.DropDown as ToolStripDropDownMenu;
                if (dropMenu != null)
                {
                    dropMenu.ShowImageMargin = false;
                    // .NET Framework 4.8 ShowCheckMargin defaults to FALSE, and with
                    // the image margin also off the native check glyph is never
                    // painted — Checked items looked identical to unchecked ones
                    // (e.g. the 默认启动后端 / WSL 服务模式 choices). Enable the
                    // check column only on submenus that actually carry checked
                    // items, so menus without checks keep their compact layout.
                    dropMenu.ShowCheckMargin = SubmenuHasCheckedItems(parent);
                }
                foreach (ToolStripItem item in parent.DropDownItems)
                {
                    ToolStripDropDownItem sub = item as ToolStripDropDownItem;
                    if (sub != null && sub.DropDownItems.Count > 0) ApplySubmenuRenderer(sub);
                }
            }
            catch { }
        }

        /// <summary>True if any direct child can display a check mark (Checked or CheckOnClick).</summary>
        private static bool SubmenuHasCheckedItems(ToolStripDropDownItem parent)
        {
            foreach (ToolStripItem item in parent.DropDownItems)
            {
                ToolStripMenuItem mi = item as ToolStripMenuItem;
                if (mi != null && (mi.CheckOnClick || mi.Checked)) return true;
            }
            return false;
        }

        public void UpdateStatus(string text)
        {
            // StatusChanged fires from background threads (timer tick, thread pool,
            // control pipe). Coalesce: only ONE flush is queued at a time, always
            // carrying the newest text, so a burst of events cannot flood the UI
            // thread message queue (which caused tray lag / menu re-layout churn).
            if (InvokeRequired)
            {
                _pendingStatus = text;
                if (_statusFlushPending) return;
                _statusFlushPending = true;
                try { BeginInvoke(new Action(FlushStatus)); }
                catch { _statusFlushPending = false; }
                return;
            }
            UpdateStatusCore(text);
        }

        private void FlushStatus()
        {
            _statusFlushPending = false;
            UpdateStatusCore(_pendingStatus);
        }

        private void UpdateStatusCore(string text)
        {
            try
            {
                // Show the active instance's compact StatusText (with runtime summary)
                // instead of the raw event message, so the tray stays short/consistent.
                InstanceController active = _service.Controller;
                string status = active == null ? text : active.StatusText;
                string display = status;
                int sep = display.IndexOf(" · ");
                if (sep >= 0)
                    display = display.Substring(0, sep) + Environment.NewLine + "  " + display.Substring(sep + 3);
                else
                    display = display + Environment.NewLine + "  "; // always two lines so the fixed-height status item stays stable
                // Only touch controls whose text actually changed: setting Text on a
                // ToolStrip item triggers a re-measure, and doing it on every event
                // churned the layout (menu height/width jumps, hover paint lost).
                if (_statusItem != null)
                {
                    string newText = MenuStatus + ": " + display;
                    if (_statusItem.Text != newText) _statusItem.Text = newText;
                }
                string notifyText = Title + " - " + status;
                if (_notify.Text != notifyText) _notify.Text = notifyText;
                RefreshBackendCheck();
                for (int i = 0; i < _instanceItems.Count && i < _service.Controllers.Count; i++)
                {
                    ToolStripMenuItem item = _instanceItems[i];
                    InstanceController ic = _service.Controllers[i];
                    string label = InstanceLabel(ic);
                    if (item.Text != label) item.Text = label;
                    if (item.DropDownItems.Count >= 5)
                    {
                        ToolStripMenuItem si = item.DropDownItems[4] as ToolStripMenuItem;
                        if (si != null)
                        {
                            string st = MenuStatus + ": " + ic.StatusText;
                            if (si.Text != st) si.Text = st;
                        }
                    }
                }
            }
            catch { }
        }

        private void RefreshBackendCheck()
        {
            try
            {
                bool wsl = String.Equals(_service.ActiveBackend, "wsl", StringComparison.OrdinalIgnoreCase);
                Color active = Color.FromArgb(0x2D, 0x7F, 0xFF);
                Color inactive = SystemColors.Control;
                if (_btnWindows != null)
                {
                    Color bg = wsl ? inactive : active;
                    Color fg = wsl ? Color.FromArgb(0x33, 0x33, 0x33) : Color.White;
                    if (_btnWindows.BackColor != bg) _btnWindows.BackColor = bg;
                    if (_btnWindows.ForeColor != fg) _btnWindows.ForeColor = fg;
                }
                if (_btnWsl != null)
                {
                    Color bg = wsl ? active : inactive;
                    Color fg = wsl ? Color.White : Color.FromArgb(0x33, 0x33, 0x33);
                    if (_btnWsl.BackColor != bg) _btnWsl.BackColor = bg;
                    if (_btnWsl.ForeColor != fg) _btnWsl.ForeColor = fg;
                }
                // Keep this row present at all times: hiding/showing an item forced a
                // full native menu resize on every backend switch. Windows gets a
                // disabled same-height placeholder; WSL gets the real submenu.
                if (_modeMenu != null)
                {
                    string modeText = wsl ? MenuWslMode : MenuWindowsMode;
                    if (_modeMenu.Text != modeText) _modeMenu.Text = modeText;
                    if (_modeMenu.Enabled != wsl) _modeMenu.Enabled = wsl;
                }
                bool defWsl = String.Equals(_service.DefaultBackend, "wsl", StringComparison.OrdinalIgnoreCase);
                if (_defaultWslItem != null && _defaultWslItem.Checked != defWsl) _defaultWslItem.Checked = defWsl;
                if (_defaultWindowsItem != null && _defaultWindowsItem.Checked != !defWsl) _defaultWindowsItem.Checked = !defWsl;
                bool systemd = wsl
                    && String.Equals(_service.Config.WslServiceMode, "systemd", StringComparison.OrdinalIgnoreCase);
                if (_modeSystemdItem != null && _modeSystemdItem.Checked != systemd) _modeSystemdItem.Checked = systemd;
                if (_modeWrapperItem != null && _modeWrapperItem.Checked != !systemd) _modeWrapperItem.Checked = !systemd;
                // 开机自启：主菜单不开勾选列（会加宽所有项），改用灰色阴影背景标识"开"。
                // 以 config 为准同步 Checked 与 BackColor，ToggleAutoStart 失败时也能自愈。
                bool autoStart = _service.Config.AutoStart;
                if (_autoStartItem != null)
                {
                    if (_autoStartItem.Checked != autoStart) _autoStartItem.Checked = autoStart;
                    Color autoBg = autoStart ? AutoStartOnColor : Color.Empty;
                    if (_autoStartItem.BackColor != autoBg) _autoStartItem.BackColor = autoBg;
                }
            }
            catch { }
        }

        public void ShowBalloon(string title, string text)
        {
            try
            {
                // Balloon events come from background threads (update check, etc.).
                if (InvokeRequired)
                {
                    BeginInvoke(new Action<string, string>(ShowBalloon), title, text);
                    return;
                }
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
            private readonly ComboBox _wslDistro;
            private readonly ComboBox _mode;
            private readonly TableLayoutPanel _table;
            public InstanceConfig Result { get; private set; }

            /// <summary>One selectable WSL distro entry ("" = auto-detect).</summary>
            private sealed class DistroItem
            {
                public string Name;
                public override string ToString() { return String.IsNullOrEmpty(Name) ? "(自动)" : Name; }
            }

            public InstanceDialog(InstanceConfig existing, ManagerConfig config)
            {
                Text = MenuAddInstance;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;

                _id = new TextBox();
                _profile = new TextBox();
                _port = new TextBox();
                _backend = new ComboBox();
                _wslPort = new TextBox();
                _wslDistro = new ComboBox();
                _mode = new ComboBox();

                // Default ports: continue after the globally highest used port, so a
                // new instance never collides with any existing one (either backend).
                int maxUsed = 3079;
                if (config != null)
                {
                    if (config.Instances != null && config.Instances.Count > 0)
                    {
                        foreach (InstanceConfig inst in config.Instances)
                        {
                            if (inst.Port > maxUsed) maxUsed = inst.Port;
                            if (inst.WslPort > maxUsed) maxUsed = inst.WslPort;
                        }
                    }
                    else
                    {
                        if (config.Port > maxUsed) maxUsed = config.Port;
                        if (config.WslPort > maxUsed) maxUsed = config.WslPort;
                    }
                }
                int defWin = maxUsed + 1;
                int defWsl = maxUsed + 2;

                _id.Text = existing == null ? "new" : existing.Id;
                _profile.Text = existing == null ? "web" : existing.Profile;
                _port.Text = (existing == null ? defWin : existing.Port).ToString();
                _backend.Items.AddRange(new object[] { "windows", "wsl" });
                _backend.SelectedIndex = existing != null && existing.IsWsl ? 1 : 0;
                _wslPort.Text = (existing == null ? defWsl : existing.WslPort).ToString();
                _mode.Items.AddRange(new object[] { "wrapper", "systemd" });
                _mode.SelectedIndex = existing != null && String.Equals(existing.WslServiceMode, "systemd", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

                // WSL distro: drop-down of real user distros (+ "(自动)" = auto-detect).
                _wslDistro.DropDownStyle = ComboBoxStyle.DropDownList;
                _wslDistro.Items.Add(new DistroItem());
                int distroSelect = 0;
                int distroIdx = 1;
                try
                {
                    foreach (WslDistroState s in WslTools.DetectDistroStates())
                    {
                        if (!WslTools.IsUserWslDistro(s.Name)) continue;
                        _wslDistro.Items.Add(new DistroItem { Name = s.Name });
                        if (existing != null && String.Equals(existing.WslDistro, s.Name, StringComparison.OrdinalIgnoreCase))
                            distroSelect = distroIdx;
                        distroIdx++;
                    }
                }
                catch { }
                _wslDistro.SelectedIndex = distroSelect;

                // Buttons: fixed size in a bottom panel.
                Button ok = new Button();
                ok.Text = "确定";
                ok.DialogResult = DialogResult.OK;
                ok.Size = new Size(90, 32);
                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Size = new Size(90, 32);
                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.Dock = DockStyle.Bottom;
                buttons.Height = 48;
                buttons.Padding = new Padding(0, 8, 12, 8);
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);

                _table = new TableLayoutPanel();
                _table.ColumnCount = 2;
                _table.RowCount = 7;
                _table.Dock = DockStyle.Fill;
                _table.Padding = new Padding(12);
                _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
                _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
                for (int i = 0; i < 7; i++)
                    _table.RowStyles.Add(new RowStyle(SizeType.AutoSize, 0));

                AddRow(_table, 0, "实例 Id", _id);
                AddRow(_table, 1, "后端", _backend);
                AddRow(_table, 2, "Profile", _profile);
                AddRow(_table, 3, "端口 (Windows)", _port);
                AddRow(_table, 4, "端口 (WSL)", _wslPort);
                AddRow(_table, 5, "WSL 发行版", _wslDistro);
                AddRow(_table, 6, "WSL 模式", _mode);

                Controls.Add(buttons);   // dock Bottom first
                Controls.Add(_table);    // Fill takes the remaining space

                _backend.SelectedIndexChanged += delegate { UpdateVisibility(); };
                UpdateVisibility();

                AcceptButton = ok;
                CancelButton = cancel;
            }

            /// <summary>Show only the fields relevant to the selected backend.</summary>
            private void UpdateVisibility()
            {
                bool wsl = _backend.SelectedIndex == 1;
                SetRowVisible(3, !wsl); // Windows port
                SetRowVisible(4, wsl);  // WSL port
                SetRowVisible(5, wsl);  // WSL distro
                SetRowVisible(6, wsl);  // WSL mode
                ClientSize = new Size(400, wsl ? 340 : 230);
            }

            private void SetRowVisible(int row, bool visible)
            {
                if (visible)
                {
                    _table.RowStyles[row].SizeType = SizeType.AutoSize;
                    _table.RowStyles[row].Height = 0;
                }
                else
                {
                    _table.RowStyles[row].SizeType = SizeType.Absolute;
                    _table.RowStyles[row].Height = 0;
                }
                Control l = _table.GetControlFromPosition(0, row);
                Control i = _table.GetControlFromPosition(1, row);
                if (l != null) l.Visible = visible;
                if (i != null) i.Visible = visible;
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
                    bool wsl = _backend.SelectedIndex == 1;
                    if (String.IsNullOrWhiteSpace(_id.Text))
                    {
                        MessageBox.Show("实例 Id 不能为空");
                        e.Cancel = true;
                        return;
                    }
                    int winPort, wslPort;
                    if (!int.TryParse(_port.Text, out winPort) || winPort <= 0) winPort = 3081;
                    if (!int.TryParse(_wslPort.Text, out wslPort) || wslPort <= 0) wslPort = 3080;
                    // Validate the active backend's port specifically.
                    if (wsl && wslPort <= 0)
                    {
                        MessageBox.Show("端口 (WSL) 必须是正整数");
                        e.Cancel = true;
                        return;
                    }
                    if (!wsl && winPort <= 0)
                    {
                        MessageBox.Show("端口 (Windows) 必须是正整数");
                        e.Cancel = true;
                        return;
                    }
                    Result = new InstanceConfig();
                    Result.Id = _id.Text.Trim();
                    Result.Profile = _profile.Text.Trim();
                    Result.BackendType = wsl ? "wsl" : "windows";
                    Result.Port = winPort;
                    Result.WslPort = wslPort;
                    DistroItem chosen = _wslDistro.SelectedItem as DistroItem;
                    Result.WslDistro = chosen == null ? String.Empty : chosen.Name;
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