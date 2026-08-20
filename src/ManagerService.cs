using System;
using System.Threading;
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
        private readonly InstanceController _controller;
        private readonly System.Threading.Timer _timer;
        private bool _hadWindow;
        private DateTime _lastSizeCapture = DateTime.MinValue;
        private bool _disposed;

        public event Action<string> StatusChanged;   // UI thread marshaling is done by the frontend
        public event Action<string, string> Balloon; // title, text

        public InstanceController Controller { get { return _controller; } }
        public ManagerConfig Config { get { return _config; } }
        public InstanceState State { get { return _controller.State; } }

        public ManagerService(ManagerConfig config)
        {
            _config = config;
            _controller = new InstanceController(config);
            _controller.StatusChanged += OnControllerStatus;
            _timer = new System.Threading.Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Initialize(string action)
        {
            _config.MigrateLegacyWindowSize();
            _config.Save();
            _controller.Start();
            if (String.Equals(action, "open", StringComparison.OrdinalIgnoreCase))
                OpenWindow();
            _timer.Change(0, 1000);
        }

        private void OnControllerStatus(string text)
        {
            var h = StatusChanged;
            if (h != null) h(text);
        }

        public void OpenWindow()
        {
            // Make sure the service is online first; a window pointing at a dead
            // port is useless.
            if (_controller.State == InstanceState.Stopped || _controller.State == InstanceState.Error)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        _controller.Start(); // may block up to the startup timeout
                        EdgeWindow.EnsureVisible(_config, _controller.ActivePort);
                    }
                    catch (Exception ex) { FileLog.Error("OpenWindow(start) failed: " + ex.Message); }
                });
                return;
            }
            try
            {
                EdgeWindow.EnsureVisible(_config, _controller.ActivePort);
                _hadWindow = true;
            }
            catch (Exception ex)
            {
                FileLog.Error("OpenWindow failed: " + ex.Message);
                var b = Balloon; if (b != null) b("dsh web manager", "打开窗口失败: " + ex.Message);
            }
        }

        public void Restart()
        {
            _controller.Restart();
            OpenWindowAfterDelay();
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
                try { _controller.Stop(false); }
                catch (Exception ex) { FileLog.Error("Exit stop failed: " + ex.Message); }
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
                _controller.Tick();

                int port = _controller.ActivePort;
                // Icon: re-apply continuously so transient Edge icon changes are overridden.
                if (_controller.State == InstanceState.Managed || _controller.State == InstanceState.Attached)
                    EdgeWindow.ApplyIconToWindow(port);

                // Window presence edge detection (close-window semantics).
                bool hasWindow = EdgeWindow.FindAppWindow(port) != IntPtr.Zero;
                if (_hadWindow && !hasWindow)
                {
                    FileLog.Info("App window closed (port " + port + ")");
                    if (_config.CloseStopsService && _controller.State == InstanceState.Managed)
                    {
                        _controller.Stop(false);
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