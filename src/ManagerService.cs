using System;
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
        private readonly Dictionary<InstanceController, bool> _hadWindows = new Dictionary<InstanceController, bool>();
        private readonly object _sync = new object();
        private DateTime _lastSizeCapture = DateTime.MinValue;
        private DateTime _lastRuntimeRefresh = DateTime.MinValue;
        private bool _disposed;

        public event Action<string> StatusChanged;   // UI thread marshaling is done by the frontend
        public event Action<string, string> Balloon; // title, text
        public event Action InstancesChanged;        // raised after add/remove instance (v3.0 P2-2)

        public IList<InstanceController> Controllers { get { return _controllers; } }

        /// <summary>Active backend ("windows" / "wsl"); remembered across restarts.</summary>
        public string ActiveBackend
        {
            get { return _config.ActiveBackend; }
            set
            {
                if (String.Equals(value, _config.ActiveBackend, StringComparison.OrdinalIgnoreCase)) return;
                _config.ActiveBackend = value;
                _config.Save();
                FileLog.Info("ActiveBackend -> " + value);
            }
        }

        /// <summary>Backend whose window opens when the manager starts ("windows" / "wsl").</summary>
        public string DefaultBackend
        {
            get { return _config.DefaultBackend; }
            set
            {
                if (String.Equals(value, _config.DefaultBackend, StringComparison.OrdinalIgnoreCase)) return;
                _config.DefaultBackend = value;
                _config.Save();
                FileLog.Info("DefaultBackend -> " + value);
            }
        }

        /// <summary>Controller for the active backend (fallback: first instance).</summary>
        public InstanceController ActiveController
        {
            get
            {
                foreach (InstanceController c in _controllers)
                    if (String.Equals(c.Backend.BackendType, _config.ActiveBackend, StringComparison.OrdinalIgnoreCase))
                        return c;
                return _controllers.Count > 0 ? _controllers[0] : null;
            }
        }

        public InstanceController Controller { get { return ActiveController; } }
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
                OpenDefaultBackendWindow();
            _timer.Change(0, 1000);
            // v3.0: throttled dsh update check in the background (24 h).
            ThreadPool.QueueUserWorkItem(_ => CheckForUpdates());
        }

        private void OnControllerStatus(string text)
        {
            var h = StatusChanged;
            if (h != null) h(text);
        }

        /// <summary>Snapshot of the current controllers (the list is mutated by add/remove).</summary>
        private List<InstanceController> Snapshot()
        {
            lock (_sync) { return new List<InstanceController>(_controllers); }
        }

        /// <summary>Adds a new instance at runtime and starts it (v3.0 P2-2).</summary>
        public void AddInstance(InstanceConfig inst)
        {
            if (inst == null) return;
            if (String.IsNullOrEmpty(inst.Id))
            {
                Balloon("dsh web manager", "实例 Id 不能为空");
                return;
            }
            // Ensure an explicit instance list exists (legacy single-instance migration).
            if (_config.Instances == null)
            {
                _config.Instances = new List<InstanceConfig>();
                foreach (InstanceConfig e in _config.EffectiveInstances) _config.Instances.Add(e);
            }
            foreach (InstanceConfig e in _config.Instances)
                if (String.Equals(e.Id, inst.Id, StringComparison.OrdinalIgnoreCase))
                {
                    Balloon("dsh web manager", "实例 Id 已存在: " + inst.Id);
                    return;
                }
            // A port is exclusive across backends: no other instance may reuse it.
            int newPort = inst.EffectivePort;
            foreach (InstanceConfig e in _config.Instances)
                if (e.EffectivePort == newPort)
                {
                    Balloon("dsh web manager", "端口 " + newPort + " 已被实例 " + e.Id + " 占用，请换一个端口");
                    return;
                }
            if (inst.Window == null) inst.Window = new WindowConfig();
            if (String.IsNullOrEmpty(inst.Profile)) inst.Profile = "web";
            if (String.IsNullOrEmpty(inst.WslServiceMode)) inst.WslServiceMode = "wrapper";
            inst.Enabled = true;
            _config.Instances.Add(inst);
            _config.Save();
            InstanceController c = new InstanceController(_config, inst);
            c.StatusChanged += OnControllerStatus;
            lock (_sync) { _controllers.Add(c); }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { c.Start(); }
                catch (Exception ex) { FileLog.Error("AddInstance start: " + ex.Message); }
            });
            var h = InstancesChanged; if (h != null) h();
        }

        /// <summary>Removes the instance at the given controller index (v3.0 P2-2).</summary>
        public void RemoveInstance(int index)
        {
            InstanceController c = null;
            lock (_sync)
            {
                if (index < 0 || index >= _controllers.Count) return;
                c = _controllers[index];
            }
            if (_config.Instances == null)
            {
                _config.Instances = new List<InstanceConfig>();
                foreach (InstanceConfig e in _config.EffectiveInstances) _config.Instances.Add(e);
            }
            if (_config.Instances.Count <= 1)
            {
                Balloon("dsh web manager", "至少保留一个实例");
                return;
            }
            _config.Instances.Remove(c.Instance);
            _config.Save();
            lock (_sync)
            {
                _controllers.Remove(c);
                _hadWindows.Remove(c);
            }
            // Stop off the UI thread (WSL stop can take seconds).
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { c.Stop(false); } catch (Exception ex) { FileLog.Error("RemoveInstance stop: " + ex.Message); }
                try { EdgeWindow.CloseWindow(c.ActivePort); } catch { }
            });
            var h = InstancesChanged; if (h != null) h();
        }

        /// <summary>Opens the window of the configured default-start backend.</summary>
        public void OpenDefaultBackendWindow()
        {
            InstanceController c = GetControllerForBackend(_config.DefaultBackend);
            if (c == null) return;
            int index = _controllers.IndexOf(c);
            OpenWindow(index >= 0 ? index : 0);
            // Only the default-start backend's window should remain: close stale
            // app windows of the other instances so a restart does not resurrect them.
            foreach (InstanceController other in _controllers)
            {
                if (other == c) continue;
                try { EdgeWindow.CloseWindow(other.ActivePort); }
                catch (Exception ex) { FileLog.Error("OpenDefaultBackendWindow close stale: " + ex.Message); }
            }
        }

        private InstanceController GetControllerForBackend(string backend)
        {
            foreach (InstanceController c in _controllers)
                if (String.Equals(c.Backend.BackendType, backend, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        public void OpenWindow()
        {
            InstanceController c = ActiveController;
            if (c == null) return;
            int index = _controllers.IndexOf(c);
            OpenWindow(index >= 0 ? index : 0);
        }

        public void OpenWindow(int index)
        {
            InstanceController c = GetController(index);
            if (c == null) return;
            // Window lookup/launch touches WMI and processes: run off the UI thread so
            // the tray never freezes while opening a window.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Make sure the service is online first; a window pointing at a
                    // dead port is useless.
                    if (c.State == InstanceState.Stopped || c.State == InstanceState.Error)
                        c.Start(); // may block up to the startup timeout
                    OpenWindowCore(c);
                }
                catch (Exception ex) { FileLog.Error("OpenWindow failed: " + ex.Message); }
            });
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
                _hadWindows[c] = true;
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
                        _hadWindows[c] = true;
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

        /// <summary>Throttled dsh update check; balloons when a newer version exists.</summary>
        public void CheckForUpdates()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string distro = String.Empty;
                    if (_config.IsWsl || _config.EffectiveInstances.Count > 0)
                    {
                        foreach (InstanceConfig inst in _config.EffectiveInstances)
                        {
                            if (inst.IsWsl && !String.IsNullOrWhiteSpace(inst.WslDistro)) { distro = inst.WslDistro; break; }
                        }
                        if (String.IsNullOrEmpty(distro))
                        {
                            string resolved;
                            if (WslTools.ResolveDistro(_config.WslDistro, _config.LastWslDistro, out resolved)) distro = resolved;
                        }
                    }
                    if (String.IsNullOrEmpty(distro))
                    {
                        FileLog.Info("CheckForUpdates: no WSL distro to check; skipping");
                        return;
                    }
                    string current = TryGetBridgeDshVersion();
                    string latest = UpdateChecker.CheckThrottled(_config, distro, current);
                    if (String.IsNullOrEmpty(latest)) return;
                    var b = Balloon;
                    if (b != null)
                    {
                        string cur = String.IsNullOrEmpty(current) ? UpdateChecker.GetCurrentWslDshVersion(distro) : current;
                        b("dsh web manager", "发现 dsh 新版本 " + latest + "（当前 " + cur + "）。可在托盘菜单「更新 dsh」一键更新。");
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Error("CheckForUpdates: " + ex.Message);
                }
            });
        }

        /// <summary>Current dsh version from the first reachable runtime bridge ("" if none).</summary>
        private string TryGetBridgeDshVersion()
        {
            foreach (InstanceController c in _controllers)
            {
                if (c.Backend == null) continue;
                try
                {
                    BridgeInfo info = c.Backend.QueryBridgeInfo(c.ActivePort);
                    if (info != null && info.Reachable && !String.IsNullOrEmpty(info.DshVersion))
                        return info.DshVersion;
                }
                catch (Exception ex) { FileLog.Error("TryGetBridgeDshVersion: " + ex.Message); }
            }
            return String.Empty;
        }

        /// <summary>One-click update of the WSL-side global dsh package.</summary>
        public void ApplyDshUpdate()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string distro = String.Empty;
                    string resolved;
                    if (WslTools.ResolveDistro(_config.WslDistro, _config.LastWslDistro, out resolved)) distro = resolved;
                    if (String.IsNullOrEmpty(distro)) { Balloon("dsh web manager", "未找到 WSL 发行版，无法更新"); return; }
                    var b = Balloon;
                    if (b != null) b("dsh web manager", "正在更新 WSL dsh（npmmirror）…");
                    bool ok = UpdateChecker.UpdateWslDsh(distro);
                    if (b != null)
                        b("dsh web manager", ok ? "dsh 更新完成：" + UpdateChecker.GetCurrentWslDshVersion(distro) : "dsh 更新失败，请查看日志");
                    _config.LastVersionCheckUtc = String.Empty; // allow an immediate re-check
                    _config.Save();
                }
                catch (Exception ex)
                {
                    FileLog.Error("ApplyDshUpdate: " + ex.Message);
                }
            });
        }

        public void Restart()
        {
            InstanceController c = ActiveController;
            if (c == null) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { c.Restart(); }
                catch (Exception ex) { FileLog.Error("Restart: " + ex.Message); }
                OpenWindowAfterDelay();
            });
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
            List<InstanceController> snapshot = Snapshot();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    bool hadWindow = false;
                    foreach (InstanceController c in snapshot)
                        if (EdgeWindow.FindAppWindow(c.ActivePort) != IntPtr.Zero) { hadWindow = true; break; }
                    foreach (InstanceController c in snapshot)
                    {
                        try { c.Stop(true); }
                        catch (Exception ex) { FileLog.Error("SetWslMode stop: " + ex.Message); }
                    }
                    _config.WslServiceMode = mode.ToLowerInvariant();
                    _config.Save();
                    foreach (InstanceController c in snapshot) { c.Reconfigure(); c.Start(); }
                    if (hadWindow) OpenWindowAfterDelay();
                }
                catch (Exception ex) { FileLog.Error("SetWslMode: " + ex.Message); }
            });
            Balloon("dsh web manager", "WSL 服务模式切换中: " + mode);
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
            List<InstanceController> snapshot = Snapshot();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    bool hadWindow = false;
                    foreach (InstanceController c in snapshot)
                        if (EdgeWindow.FindAppWindow(c.ActivePort) != IntPtr.Zero) { hadWindow = true; break; }
                    foreach (InstanceController c in snapshot)
                    {
                        try { c.Stop(true); }
                        catch (Exception ex) { FileLog.Error("SetBackend stop: " + ex.Message); }
                    }
                    _config.BackendType = type.ToLowerInvariant();
                    _config.Save();
                    foreach (InstanceController c in snapshot) { c.Reconfigure(); c.Start(); }
                    if (hadWindow) OpenWindowAfterDelay();
                }
                catch (Exception ex) { FileLog.Error("SetBackend: " + ex.Message); }
            });
            Balloon("dsh web manager", "后端切换中: " + type);
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

        /// <summary>Per-instance window icon application and close-window detection.</summary>
        private void HandleInstanceWindow(InstanceController c)
        {
            int port = c.ActivePort;
            if (c.State == InstanceState.Managed || c.State == InstanceState.Attached)
                EdgeWindow.ApplyIconToWindow(port);

            bool hasWindow = EdgeWindow.FindAppWindow(port) != IntPtr.Zero;
            bool had;
            _hadWindows.TryGetValue(c, out had);
            if (had && !hasWindow)
            {
                FileLog.Info("App window closed (port " + port + ")");
                if (_config.CloseStopsService && c.State == InstanceState.Managed)
                {
                    c.Stop(false);
                    var b = Balloon; if (b != null) b("dsh web manager", "窗口已关闭，服务已停止");
                }
                // Default: service keeps running; tray can re-open the window anytime.
            }
            _hadWindows[c] = hasWindow;
        }

        private void Tick(object state)
        {
            if (_disposed) return;
            try
            {
                foreach (InstanceController c in Snapshot())
                {
                    c.Tick();
                    HandleInstanceWindow(c);
                }

                // Size capture every ~2 s for every instance.
                if (DateTime.Now.Subtract(_lastSizeCapture).TotalSeconds >= 2)
                {
                    _lastSizeCapture = DateTime.Now;
                    foreach (InstanceController c in Snapshot())
                        EdgeWindow.CaptureSize(c.ActivePort, c.Instance.Window, delegate { _config.Save(); }, DateTime.Now);
                }

                // Runtime-bridge status every ~10 s; re-publish when the summary text
                // actually changed (uptime ticks up ~once a minute) to keep tray fresh.
                if (DateTime.Now.Subtract(_lastRuntimeRefresh).TotalSeconds >= 10)
                {
                    _lastRuntimeRefresh = DateTime.Now;
                    foreach (InstanceController c in Snapshot())
                    {
                        string before = c.RuntimeSummary;
                        c.RefreshRuntime();
                        string after = c.RuntimeSummary;
                        if (!String.Equals(before, after))
                            c.RefreshStatusDisplay();
                    }
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