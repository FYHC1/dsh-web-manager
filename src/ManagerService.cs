﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System;
using System.Threading;
using System.Collections.Generic;
using System.Windows.Forms;

namespace DshWebManager
{
    /// <summary>
    /// Orchestrates config, the instance controller and the Edge window. Runs a 1 s timer
    /// for heartbeat (service liveness), icon refresh and window-size capture.
    /// </summary>
    public sealed class ManagerService : IDisposable
    {
        private readonly ManagerConfig _config;
        private readonly List<InstanceController> _controllers = new List<InstanceController>();
        private readonly System.Threading.Timer _timer;
        private bool _hadWindow;
        private DateTime _lastSizeCapture = DateTime.MinValue;
        private bool _disposed;

        public event Action<string> StatusChanged;   // UI thread marshaling is done by the frontend
        public event Action<string, string> Balloon; // title, text

        public IList<InstanceController> Controllers { get { return _controllers; } }
        public InstanceController Controller { get { return _controllers.Count > 0 ? _controllers[0] : null; } }
        public ManagerConfig Config { get { return _config; } }
        public InstanceState State { get { return Controller == null ? InstanceState.Stopped : Controller.State; } }
        public IServiceBackend Backend { get { return Controller == null ? null : Controller.Backend; } }

        public ManagerService(ManagerConfig config)
        {
            _config = config;
            foreach (InstanceConfig inst in config.EffectiveInstances)
            {
                if (!inst.Enabled) continue;
                InstanceController c = new InstanceController(config, inst);
                c.StatusChanged += OnControllerStatus;
                _controllers.Add(c);
            }
            _timer = new System.Threading.Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Initialize(string action)
        {
            _config.MigrateLegacyWindowSize();
            _config.Save();
            foreach (InstanceController c in _controllers) c.Start();
            if (String.Equals(action, "open", StringComparison.OrdinalIgnoreCase))
                OpenWindow();
            _timer.Change(0, 1000);
        }

        private void OnControllerStatus(string text)
        {
            var h = StatusChanged;
            if (h != null) h(text);
        }

        public void OpenWindow() { OpenWindow(0); }

        public void OpenWindow(int index)
        {
            InstanceController c = GetController(index);
            if (c == null) return;
            // Make sure the service is online first; a window pointing at a dead
            // port is useless.
            if (c.State == InstanceState.Stopped || c.State == InstanceState.Error)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        c.Start(); // may block up to the startup timeout
                        OpenWindowCore(c);
                    }
                    catch (Exception ex) { FileLog.Error("OpenWindow(start) failed: " + ex.Message); }
                });
                return;
            }
            OpenWindowCore(c);
        }

        public InstanceController GetController(int index)
        {
            if (index >= 0 && index < _controllers.Count) return _controllers[index];
            return _controllers.Count > 0 ? _controllers[0] : null;
        }

        private const int WindowRetryMax = 4;   // 5 s apart -> ~20 s grace for WSL cold start

        private void OpenWindowCore(InstanceController c)
        {
            string url = WindowUrl(c);
            if (String.IsNullOrEmpty(url))
            {
                // WSL may be cold-starting: localhost forwarding can take several
                // seconds (or longer) to come up after the distro boots. Retry in
                // the background before declaring the service unreachable.
                FileLog.Info("OpenWindow: URL not ready, scheduling retries (WSL cold start?)");
                ScheduleWindowRetry(0, c);
                return;
            }
            try
            {
                EdgeWindow.EnsureVisible(_config, url, c.ActivePort);
                _hadWindow = true;
            }
            catch (Exception ex)
            {
                FileLog.Error("OpenWindow failed: " + ex.Message);
                var b = Balloon; if (b != null) b("dsh web manager", "打开窗口失败: " + ex.Message);
            }
        }

        private void ScheduleWindowRetry(int attempt, InstanceController c)
        {
            if (attempt >= WindowRetryMax)
            {
                FileLog.Error("OpenWindow: no usable URL after " + WindowRetryMax + " retries");
                var b = Balloon;
                if (b == null) return;
                bool serviceUp = false;
                try { serviceUp = c.Backend.IsServiceUp(c.ActivePort); }
                catch { }
                if (serviceUp)
                    b("dsh web manager", "WSL 服务在运行，但 localhostForwarding 关闭，Windows 无法访问；请开启 localhostForwarding 或在 WSL 内使用浏览器");
                else
                    b("dsh web manager", "WSL 服务未就绪：请检查所选发行版是否正确（配置 wslDistro 或查看托盘「后端」状态）、该发行版内是否安装 dsh");
                return;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(5000);
                try
                {
                    string url = WindowUrl(c);
                    if (!String.IsNullOrEmpty(url))
                    {
                        EdgeWindow.EnsureVisible(_config, url, c.ActivePort);
                        _hadWindow = true;
                        FileLog.Info("OpenWindow: retry succeeded after ~" + ((attempt + 1) * 5) + "s");
                        return;
                    }
                    ScheduleWindowRetry(attempt + 1, c);
                }
                catch (Exception ex)
                {
                    FileLog.Error("OpenWindow retry failed: " + ex.Message);
                    ScheduleWindowRetry(attempt + 1, c);
                }
            });
        }

        /// <summary>Window URL for the active backend/port (empty = not reachable from Windows).</summary>
        private string WindowUrl(InstanceController c)
        {
            try { return c.Backend.GetWindowUrl(c.ActivePort); }
            catch (Exception ex) { FileLog.Error("WindowUrl failed: " + ex.Message); }
            return "http://127.0.0.1:" + c.ActivePort + "/";
        }

        public void Restart()
        {
            foreach (InstanceController c in _controllers) c.Restart();
            OpenWindowAfterDelay();
        }

        /// <summary>Switches the WSL service mode (wrapper/systemd) and restarts the WSL instance.</summary>
        public void SetWslMode(string mode)
        {
            if (!String.Equals(mode, "wrapper", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(mode, "systemd", StringComparison.OrdinalIgnoreCase))
                return;
            if (!_config.IsWsl)
            {
                Balloon("dsh web manager", "请先切换到 WSL 后端");
                return;
            }
            if (String.Equals(mode, _config.WslServiceMode, StringComparison.OrdinalIgnoreCase))
            {
                Balloon("dsh web manager", "服务模式已是 " + mode);
                return;
            }
            bool hadWindow = EdgeWindow.FindAppWindow(Controller.ActivePort) != IntPtr.Zero;
            try { Controller.Stop(true); }
            catch (Exception ex) { FileLog.Error("SetWslMode stop failed: " + ex.Message); }
            _config.WslServiceMode = mode.ToLowerInvariant();
            _config.Save();
            foreach (InstanceController c in _controllers) { c.Reconfigure(); c.Start(); }
            if (hadWindow) OpenWindowAfterDelay();
            Balloon("dsh web manager", "WSL 服务模式已切换: " + mode);
        }

        /// <summary>Switches the service backend (windows/wsl) and restarts the instance.</summary>
        public void SetBackend(string type)
        {
            if (!String.Equals(type, "windows", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(type, "wsl", StringComparison.OrdinalIgnoreCase))
                return;
            if (String.Equals(type, _config.BackendType, StringComparison.OrdinalIgnoreCase))
            {
                Balloon("dsh web manager", "后端已是 " + type);
                return;
            }
            bool hadWindow = EdgeWindow.FindAppWindow(Controller.ActivePort) != IntPtr.Zero;
            try { Controller.Stop(true); }
            catch (Exception ex) { FileLog.Error("SetBackend stop failed: " + ex.Message); }
            _config.BackendType = type.ToLowerInvariant();
            _config.Save();
            foreach (InstanceController c in _controllers) { c.Reconfigure(); c.Start(); }
            if (hadWindow) OpenWindowAfterDelay();
            Balloon("dsh web manager", "已切换到 " + Controller.BackendDescribe + " 后端");
        }

        private void OpenWindowAfterDelay()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(4000);
                try { OpenWindow(); } catch { }
            });
        }

        public void ToggleAutoStart()
        {
            bool next = !_config.AutoStart;
            try
            {
                Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) { Balloon("dsh web manager", "写入注册表失败"); return; }
                if (next)
                    key.SetValue("dsh-web-manager", "\"" + AppPaths.ExePath + "\" tray");
                else
                    key.DeleteValue("dsh-web-manager", false);
                key.Close();
                _config.AutoStart = next;
                _config.Save();
                Balloon("dsh web manager", next ? "已开启开机自启（仅托盘，不弹窗）" : "已关闭开机自启");
            }
            catch (Exception ex)
            {
                FileLog.Error("ToggleAutoStart failed: " + ex.Message);
                Balloon("dsh web manager", "设置开机自启失败: " + ex.Message);
            }
        }

        public void Exit(bool stopService)
        {
            if (stopService || !_config.ExitKeepService)
            {
                foreach (InstanceController c in _controllers)
                {
                    try { c.Stop(false); }
                    catch (Exception ex) { FileLog.Error("Exit stop failed: " + ex.Message); }
                    // The service is gone: close the app window it served so no dead
                    // window is left behind after the manager exits.
                    try { EdgeWindow.CloseWindow(c.ActivePort); }
                    catch (Exception ex) { FileLog.Error("Exit close window failed: " + ex.Message); }
                }
            }
            _disposed = true;
            // Hard exit: guarantees the tray process terminates even when the UI
            // message loop is unreachable from the control-pipe thread.
            Environment.Exit(0);
        }

        private void Tick(object state)
        {
            if (_disposed) return;
            try
            {
                foreach (InstanceController c in _controllers) c.Tick();

                // Window handling for the first controller (single-window UX for now;
                // multi-instance window management can extend this per instance).
                InstanceController c0 = Controller;
                if (c0 == null) return;
                int port = c0.ActivePort;
                // Icon: re-apply continuously so transient Edge icon changes are overridden.
                if (c0.State == InstanceState.Managed || c0.State == InstanceState.Attached)
                    EdgeWindow.ApplyIconToWindow(port);

                // Window presence edge detection (close-window semantics).
                bool hasWindow = EdgeWindow.FindAppWindow(port) != IntPtr.Zero;
                if (_hadWindow && !hasWindow)
                {
                    FileLog.Info("App window closed (port " + port + ")");
                    if (_config.CloseStopsService && c0.State == InstanceState.Managed)
                    {
                        c0.Stop(false);
                        var b = Balloon; if (b != null) b("dsh web manager", "窗口已关闭，服务已停止");
                    }
                    // Default: service keeps running; tray can re-open the window anytime.
                }
                _hadWindow = hasWindow;

                // Size capture every ~2 s.
                if (DateTime.Now.Subtract(_lastSizeCapture).TotalSeconds >= 2)
                {
                    _lastSizeCapture = DateTime.Now;
                    EdgeWindow.CaptureSize(port, _config, DateTime.Now);
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("Tick failed: " + ex.ToString());
            }
        }

        public void Dispose()
        {
            _disposed = true;
            if (_timer != null) _timer.Dispose();
        }
    }
}