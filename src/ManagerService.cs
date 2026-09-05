using System;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
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
        private int _tickRunning;                       // reentrancy guard (Interlocked)
        private DateTime _lastSizeCapture = DateTime.MinValue;
        private DateTime _lastRuntimeRefresh = DateTime.MinValue;
        private bool _disposed;

        public event Action<string> StatusChanged;   // UI thread marshaling is done by the frontend
        public event Action<string, string> Balloon; // title, text
        public event Action InstancesChanged;        // raised after add/remove instance (v3.0 P2-2)
        public event Action Exiting;                 // raised right before process exit (hide the tray icon)

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
            // Offline-bundle pin: launch the bundled dsh directly instead of a
            // PATH lookup (no-op when DshCommand is empty).
            DshLauncher.DshCommandOverride = _config.DshCommand;
            ResolveWindowBackend();
            // Start ONLY what this launch needs. The old blanket
            // foreach c.Start() made ANY shortcut (e.g. open wsl) also boot every
            // other instance - the user saw the Windows dsh start when they only
            // asked for the WSL side. Services now come up when their window is
            // opened (OpenWindow starts a Stopped instance); already-running ones
            // attach via the heartbeat. A plain "tray" start (login/autostart)
            // launches nothing by itself.
            if (String.Equals(action, "open windows", StringComparison.OrdinalIgnoreCase))
                OpenBackendWindow("windows");
            else if (String.Equals(action, "open wsl", StringComparison.OrdinalIgnoreCase))
                OpenBackendWindow("wsl");
            else if (String.Equals(action, "open", StringComparison.OrdinalIgnoreCase))
                OpenDefaultBackendWindow();
            _timer.Change(0, 1000);
            // v3.0: throttled dsh update check in the background (24 h).
            ThreadPool.QueueUserWorkItem(_ => CheckForUpdatesThrottled());
        }

        /// <summary>Resolves config.WindowBackend into EdgeWindow.Mode:
        /// "edge" / "webview2" explicitly, "auto" probes the WebView2 Runtime and
        /// falls back to edge when absent (or when the managed DLLs are missing).</summary>
        private void ResolveWindowBackend()
        {
            string requested = String.IsNullOrWhiteSpace(_config.WindowBackend)
                ? "auto" : _config.WindowBackend.Trim().ToLowerInvariant();
            if (requested == "edge")
            {
                EdgeWindow.Mode = "edge";
                FileLog.Info("WindowBackend: edge (explicit)");
                return;
            }
            bool available = WebViewWindow.IsRuntimeAvailable();
            if (requested == "webview2")
            {
                if (available)
                {
                    EdgeWindow.Mode = "webview2";
                    FileLog.Info("WindowBackend: webview2 (explicit)");
                }
                else
                {
                    EdgeWindow.Mode = "edge";
                    FileLog.Info("WindowBackend: webview2 requested but runtime missing; falling back to edge");
                    var b = Balloon; if (b != null) b("dsh web manager", "WebView2 Runtime 不可用，已回退 Edge 窗口模式");
                }
                return;
            }
            // auto
            EdgeWindow.Mode = available ? "webview2" : "edge";
            FileLog.Info("WindowBackend: " + EdgeWindow.Mode + " (auto)");
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
            InstanceController c = GetOrCreateControllerForBackend(_config.DefaultBackend);
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

        /// <summary>Controller for a backend; when the backend has NO instance at
        /// all (removed, or the manager was never configured for it), provisions a
        /// default instance on the fly so the desktop shortcuts work on first use -
        /// no manual 添加实例 step required. The window opening still does the
        /// lazy service start (OpenWindow -> c.Start()).</summary>
        private InstanceController GetOrCreateControllerForBackend(string backend)
        {
            InstanceController c = GetControllerForBackend(backend);
            if (c != null) return c;
            InstanceConfig inst = BuildDefaultInstance(backend);
            if (inst == null)
            {
                FileLog.Error("Could not auto-provision an instance for backend " + backend);
                return null;
            }
            AddInstance(inst); // persists, creates the controller, starts async
            return GetControllerForBackend(backend);
        }

        /// <summary>A sensible default instance for a backend that has none:
        /// remembered backend port (next free one), profile/mode/distro from the
        /// shared config, a unique id. Returned config is NOT yet persisted.</summary>
        private InstanceConfig BuildDefaultInstance(string backend)
        {
            bool wsl = String.Equals(backend, "wsl", StringComparison.OrdinalIgnoreCase);
            InstanceConfig inst = new InstanceConfig();
            inst.Id = UniqueInstanceId(wsl ? "wsl" : "windows");
            if (inst.Id == null) return null;
            inst.Profile = String.IsNullOrWhiteSpace(_config.Profile) ? "web" : _config.Profile;
            inst.BackendType = backend;
            inst.Enabled = true;
            inst.Window = new WindowConfig();
            int basePort = wsl ? _config.WslPort : _config.Port;
            if (basePort <= 0) basePort = 3080;
            int port = NextFreePort(basePort);
            if (port <= 0) return null;
            inst.Port = port;
            inst.WslPort = port;
            inst.WslDistro = _config.WslDistro;
            inst.WslServiceMode = String.IsNullOrWhiteSpace(_config.WslServiceMode) ? "wrapper" : _config.WslServiceMode;
            FileLog.Info("Auto-provisioned " + backend + " instance: id=" + inst.Id + ", port=" + port);
            return inst;
        }

        private string UniqueInstanceId(string baseId)
        {
            if (_config.Instances == null) return baseId;
            if (!IdExists(baseId)) return baseId;
            for (int n = 2; n < 20; n++)
            {
                string candidate = baseId + "-" + n;
                if (!IdExists(candidate)) return candidate;
            }
            return null;
        }

        private bool IdExists(string id)
        {
            if (_config.Instances == null) return false;
            foreach (InstanceConfig e in _config.Instances)
                if (String.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Next port >= basePort that no existing instance uses.</summary>
        private int NextFreePort(int basePort)
        {
            for (int p = basePort; p < basePort + 100; p++)
            {
                bool used = false;
                foreach (InstanceConfig e in _config.EffectiveInstances)
                    if (e.EffectivePort == p) { used = true; break; }
                if (!used) return p;
            }
            return -1;
        }

        /// <summary>Opens the window of one specific backend ("windows" / "wsl").</summary>
        public void OpenBackendWindow(string backend)
        {
            InstanceController c = GetOrCreateControllerForBackend(backend);
            if (c == null)
            {
                Balloon("dsh web manager", "未找到后端: " + backend);
                return;
            }
            int index = _controllers.IndexOf(c);
            OpenWindow(index >= 0 ? index : 0);
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

        private void OpenWindowCore(InstanceController c)
        {
            // Launch the window IMMEDIATELY with the deterministic URL. Previously
            // the launch was gated on Windows-side reachability (WindowUrl returned
            // empty whenever WSL localhost forwarding hiccuped), which turned a
            // flaky forwarding into 5-second retry delays - the reported "mostly
            // slow" window pop. The window now opens at once and loads the page as
            // soon as the URL becomes reachable; a background check reports the
            // genuinely-unreachable case instead of delaying the window.
            string url = DshWebAuth.WindowUrl(c.ActivePort);
            try
            {
                EdgeWindow.EnsureVisible(c.Instance.Window, _config.DataDir, url, c.ActivePort);
                // Do NOT mark the window as present yet: it still has to materialize
                // (up to ~1s on cold start). Setting true here made the next Tick
                // log a spurious "App window closed" (and run a needless Preheat).
                // The Tick sets it true once FindAppWindow actually sees the window.
                _hadWindows[c] = false;
            }
            catch (Exception ex)
            {
                FileLog.Error("OpenWindow failed: " + ex.Message);
                var b = Balloon; if (b != null) b("dsh web manager", "打开窗口失败: " + ex.Message);
            }
            ScheduleReachabilityCheck(c);
        }

        /// <summary>Background diagnostic after opening a window. A cold dsh
        /// start (first run after an install, profile init) can take tens of
        /// seconds, so this POLLS up to ~45s before reporting anything — the
        /// previous fixed 10s wait fired mid-startup and reported "未检测到
        /// dsh"/"启动超时" for services that came up seconds later. When the
        /// service turns reachable, the window is re-navigated to the current
        /// (token-authenticated) URL: windows that opened too early show a
        /// browser error or dsh's 401 body and would otherwise keep doing so
        /// until the user manually reopened them.</summary>
        private void ScheduleReachabilityCheck(InstanceController c)
        {
            int port = c.ActivePort;
            bool isWsl = c.Instance != null && c.Instance.IsWsl;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    bool ready = false;
                    for (int i = 0; i < 22; i++) // 22 x 2s = ~44s (start timeout is 60s)
                    {
                        Thread.Sleep(2000);
                        if (PortInspector.IsListening(port)) { ready = true; break; }
                        if (c.State == InstanceState.Error) break; // hard failure: report now
                    }
                    if (ready)
                    {
                        // The launch token is printed on stdout around the same
                        // moment the port opens; give the capture a moment so
                        // the recovery navigation authenticates on first go.
                        for (int i = 0; i < 5 && !DshWebAuth.HasToken(port); i++)
                            Thread.Sleep(1000);
                        EdgeWindow.Renavigate(c.Instance.Window, _config.DataDir, port, DshWebAuth.WindowUrl(port));
                        return;
                    }
                    bool serviceUp = false;
                    try { serviceUp = c.Backend.IsServiceUp(port); }
                    catch { }
                    var b = Balloon;
                    if (b == null) return;
                    if (serviceUp)
                    {
                        // Service answers natively but the window cannot reach the port yet.
                        if (isWsl)
                            b("dsh web manager", "服务在运行，但 Windows 暂时无法访问 (localhostForwarding 关闭？)；窗口稍后会自动加载");
                        else
                            b("dsh web manager", "服务在运行，但窗口暂时无法访问；请稍候或重新打开窗口");
                    }
                    else if (isWsl)
                    {
                        b("dsh web manager", "WSL 服务未就绪：请检查发行版配置（wslDistro）或该发行版内是否安装 dsh");
                    }
                    else
                    {
                        // Windows 实例不可达：优先报告启动失败的真实原因（典型场景：dsh 被
                        // 卸载后 PATH 中已无 dsh.cmd，LastError="未找到 dsh 命令（请安装 dsh
                        // 并更新 PATH）"），而不是沿用 WSL 的文案。
                        string why = (c.State == InstanceState.Error && !String.IsNullOrEmpty(c.LastError)) ? c.LastError : null;
                        if (String.IsNullOrEmpty(why))
                            b("dsh web manager", "Windows 端未检测到 dsh，请先安装 dsh（dsh web manager 桌面版 / 离线安装包）");
                        else
                            b("dsh web manager", "Windows dsh 服务未就绪：" + why);
                    }
                }
                catch (Exception ex) { FileLog.Error("ReachabilityCheck: " + ex.Message); }
            });
        }

        /// <summary>Manual dsh update check (tray 检查 dsh 更新): unthrottled and
        /// always reports the outcome (latest / already-latest / failure). A silent
        /// return here is exactly what the user experienced as "no feedback".</summary>
        public void CheckForUpdates()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { CheckManagerUpdateThrottled(); }
                catch (Exception ex) { FileLog.Error("CheckManagerUpdateThrottled: " + ex.Message); }
                try { CheckDshUpdate(true); }
                catch (Exception ex) { FileLog.Error("CheckForUpdates: " + ex.Message); }
            });
        }

        /// <summary>Startup dsh update check: throttled to 24 h, balloons only when a
        /// newer version exists (keeps a fresh install quiet).</summary>
        public void CheckForUpdatesThrottled()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { CheckManagerUpdateThrottled(); }
                catch (Exception ex) { FileLog.Error("CheckManagerUpdateThrottled: " + ex.Message); }
                try { CheckDshUpdate(false); }
                catch (Exception ex) { FileLog.Error("CheckForUpdatesThrottled: " + ex.Message); }
            });
        }

        /// <summary>Core dsh update check for the active backend (runtime bridge
        /// version preferred, else that platform's `dsh --version`). `manual`
        /// bypasses the 24 h throttle and always balloons a result.</summary>
        private void CheckDshUpdate(bool manual)
        {
            DateTime last;
            DateTime.TryParse(_config.LastVersionCheckUtc, out last);
            if (!manual && DateTime.UtcNow.Subtract(last) < TimeSpan.FromHours(24))
                return; // startup check within throttle: keep quiet

            string current = TryGetBridgeDshVersion();
            if (String.IsNullOrEmpty(current))
            {
                if (IsWslActive())
                {
                    string distro = ResolveDshDistro();
                    if (!String.IsNullOrEmpty(distro)) current = UpdateChecker.GetCurrentWslDshVersion(distro);
                }
                else
                {
                    current = UpdateChecker.GetCurrentWindowsDshVersion();
                }
            }

            string latest = UpdateChecker.GetLatestDshVersion();
            if (String.IsNullOrEmpty(latest))
            {
                if (manual) Balloon("dsh web manager", "检查 dsh 更新失败（网络或 npmmirror 镜像不可达）");
                return;
            }
            _config.LastVersionCheckUtc = DateTime.UtcNow.ToString("o");
            _config.LastKnownLatest = latest;
            _config.Save();

            if (String.IsNullOrEmpty(current))
            {
                if (manual) Balloon("dsh web manager", "未获取到当前 dsh 版本，请确认 dsh 已安装并配置 PATH");
                return;
            }
            if (String.Equals(current.Trim(), latest.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                if (manual) Balloon("dsh web manager", "已是最新版本 (v" + current + ")");
                return;
            }
            Balloon("dsh web manager", "发现 dsh 新版本 " + latest + "（当前 " + current + "）。可在托盘菜单「更新 dsh」一键更新。");
        }

        /// <summary>WSL distro for the update flows ("" when none usable).</summary>
        private string ResolveDshDistro()
        {
            foreach (InstanceConfig inst in _config.EffectiveInstances)
            {
                if (inst.IsWsl && !String.IsNullOrWhiteSpace(inst.WslDistro)) return inst.WslDistro;
            }
            string resolved;
            if (WslTools.ResolveDistro(_config.WslDistro, _config.LastWslDistro, out resolved)) return resolved;
            return String.Empty;
        }

        /// <summary>true when the active backend is WSL (update flows target it).</summary>
        private bool IsWslActive()
        {
            return String.Equals(_config.ActiveBackend, "wsl", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Current dsh version from the runtime bridge of the active
        /// backend's controller first, then any reachable controller ("" if none).</summary>
        private string TryGetBridgeDshVersion()
        {
            InstanceController preferred = ActiveController;
            string v = ReadControllerBridgeVersion(preferred);
            if (!String.IsNullOrEmpty(v)) return v;
            foreach (InstanceController c in _controllers)
            {
                if (c == preferred) continue;
                v = ReadControllerBridgeVersion(c);
                if (!String.IsNullOrEmpty(v)) return v;
            }
            return String.Empty;
        }

        private static string ReadControllerBridgeVersion(InstanceController c)
        {
            if (c == null || c.Backend == null) return String.Empty;
            try
            {
                BridgeInfo info = c.Backend.QueryBridgeInfo(c.ActivePort);
                if (info != null && info.Reachable && !String.IsNullOrEmpty(info.DshVersion))
                    return info.DshVersion;
            }
            catch (Exception ex) { FileLog.Error("TryGetBridgeDshVersion: " + ex.Message); }
            return String.Empty;
        }

        /// <summary>One-click update of the dsh package for the active backend
        /// (Windows npm install -g, or the WSL-side global package).</summary>
        public void ApplyDshUpdate()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var b = Balloon;
                    if (IsWslActive())
                    {
                        string distro = ResolveDshDistro();
                        if (String.IsNullOrEmpty(distro)) { if (b != null) b("dsh web manager", "未找到 WSL 发行版，无法更新"); return; }
                        if (b != null) b("dsh web manager", "正在更新 dsh（WSL " + distro + "，npmmirror）…");
                        bool ok = UpdateChecker.UpdateWslDsh(distro);
                        if (b != null)
                            b("dsh web manager", ok ? "dsh 更新完成：" + UpdateChecker.GetCurrentWslDshVersion(distro) : "dsh 更新失败，请查看日志");
                    }
                    else
                    {
                        if (String.IsNullOrEmpty(DshLauncher.FindDshCommand()))
                        { if (b != null) b("dsh web manager", "未找到 dsh 命令（请安装 dsh 并更新 PATH）"); return; }
                        if (b != null) b("dsh web manager", "正在更新 dsh（Windows，npmmirror）…");
                        int rc = UpdateChecker.UpdateWindowsDsh();
                        if (rc == 2)
                        {
                            // Offline-bundle built-in dsh: update IN PLACE with the
                            // bundled npm (no bundle re-install needed). The tree
                            // swap requires the dsh processes gone — stop managed
                            // instances first and restart the ones that ran.
                            if (b != null) b("dsh web manager", "dsh 为离线包内置版本，正在就地更新（捆绑 npm，npmmirror）…");
                            List<InstanceController> running = new List<InstanceController>();
                            foreach (InstanceController c in _controllers)
                            {
                                if (c.State == InstanceState.Managed)
                                {
                                    try { c.Stop(false); running.Add(c); }
                                    catch (Exception ex) { FileLog.Error("ApplyDshUpdate stop: " + ex.Message); }
                                }
                            }
                            rc = UpdateChecker.UpdateBundleDsh();
                            foreach (InstanceController c in running)
                            {
                                try { c.Start(); }
                                catch (Exception ex) { FileLog.Error("ApplyDshUpdate restart: " + ex.Message); }
                            }
                            if (b != null)
                            {
                                if (rc == 0)
                                    b("dsh web manager", "dsh 就地更新完成：" + UpdateChecker.GetCurrentWindowsDshVersion());
                                else if (rc == 1)
                                    b("dsh web manager", "dsh 就地更新失败（可能仍有 dsh 实例占用文件），请查看日志后重试");
                                else
                                    b("dsh web manager", "当前 dsh 为离线包内置版本，但 bundle 布局无法识别；请重新运行离线包安装器 (Install-Offline.ps1) 升级");
                            }
                        }
                        else
                        {
                            string newVer = UpdateChecker.GetCurrentWindowsDshVersion();
                            if (b != null)
                                b("dsh web manager", rc == 0 ? "dsh 更新完成：" + newVer : "dsh 更新失败，请查看日志");
                        }
                    }
                    _config.LastVersionCheckUtc = String.Empty; // allow an immediate re-check
                    _config.Save();
                }
                catch (Exception ex)
                {
                    FileLog.Error("ApplyDshUpdate: " + ex.Message);
                }
            });
        }

        /// <summary>On-demand manager update check (menu 检查管理器更新).</summary>
        public void CheckForManagerUpdate()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ManagerUpdater.ReleaseInfo rel = ManagerUpdater.GetLatestRelease(_config.ManagerUpdateApi);
                    string cur = ManagerUpdater.CurrentVersion();
                    if (rel == null)
                    {
                        Balloon("dsh web manager", "检查管理器更新失败（网络或 GitHub 不可达）");
                        return;
                    }
                    if (!rel.HasRelease || String.IsNullOrEmpty(rel.Tag))
                    {
                        Balloon("dsh web manager", "GitHub 上还没有发布版本，当前为 v" + cur);
                        return;
                    }
                    _config.LastManagerCheckUtc = DateTime.UtcNow.ToString("o");
                    _config.LastKnownManagerLatest = rel.Tag;
                    _config.Save();
                    if (ManagerUpdater.IsNewer(rel.Tag, cur))
                        Balloon("dsh web manager", "发现新版本 " + rel.Tag + "（当前 v" + cur + "），可在「更新 dsh web manager」一键更新。");
                    else
                        Balloon("dsh web manager", "已是最新版本 (v" + cur + ")");
                }
                catch (Exception ex) { FileLog.Error("CheckForManagerUpdate: " + ex.Message); }
            });
        }

        /// <summary>Startup update check, throttled to 24 h; only balloons when newer.</summary>
        private void CheckManagerUpdateThrottled()
        {
            DateTime last;
            DateTime.TryParse(_config.LastManagerCheckUtc, out last);
            if (DateTime.UtcNow.Subtract(last) < TimeSpan.FromHours(24)) return;
            ManagerUpdater.ReleaseInfo rel = ManagerUpdater.GetLatestRelease(_config.ManagerUpdateApi);
            if (rel == null || !rel.HasRelease || String.IsNullOrEmpty(rel.Tag)) return;
            _config.LastManagerCheckUtc = DateTime.UtcNow.ToString("o");
            _config.LastKnownManagerLatest = rel.Tag;
            _config.Save();
            string cur = ManagerUpdater.CurrentVersion();
            if (ManagerUpdater.IsNewer(rel.Tag, cur))
                Balloon("dsh web manager", "发现 dsh web manager 新版本 " + rel.Tag + "（当前 v" + cur + "），可在托盘菜单「更新 dsh web manager」中更新。");
        }

        /// <summary>One-click manager self-update: fetch the release exe, verify it,
        /// hand off to a detached updater that swaps the binary once we exit, then
        /// restart the tray without stopping any dsh service.</summary>
        public void ApplyManagerUpdate()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ManagerUpdater.ReleaseInfo rel = ManagerUpdater.GetLatestRelease(_config.ManagerUpdateApi);
                    string cur = ManagerUpdater.CurrentVersion();
                    if (rel == null)
                    {
                        Balloon("dsh web manager", "检查管理器更新失败（网络或 GitHub 不可达）");
                        return;
                    }
                    if (!rel.HasRelease || String.IsNullOrEmpty(rel.Tag))
                    {
                        Balloon("dsh web manager", "GitHub 上还没有发布版本，当前为 v" + cur);
                        return;
                    }
                    if (!ManagerUpdater.IsNewer(rel.Tag, cur))
                    {
                        Balloon("dsh web manager", "已是最新版本 (v" + cur + ")");
                        return;
                    }
                    if (String.IsNullOrEmpty(rel.DownloadUrl))
                    {
                        FileLog.Error("ApplyManagerUpdate: release " + rel.Tag + " has no dsh-web-manager exe asset");
                        Balloon("dsh web manager", "发布 " + rel.Tag + " 未附带 dsh-web-manager 安装包，无法自动更新");
                        return;
                    }
                    Balloon("dsh web manager", "正在下载 dsh web manager " + rel.Tag + " …");
                    string updateDir = Path.Combine(AppPaths.DataRoot, "update");
                    Directory.CreateDirectory(updateDir);
                    string newExe = Path.Combine(updateDir, "dsh-web-manager.new.exe");
                    try { if (File.Exists(newExe)) File.Delete(newExe); } catch { }
                    if (!ManagerUpdater.Download(rel.DownloadUrl, newExe))
                    {
                        Balloon("dsh web manager", "下载失败，请稍后重试");
                        return;
                    }
                    string dlVersion = ManagerUpdater.DownloadedVersion(newExe);
                    if (!String.IsNullOrEmpty(dlVersion) && !ManagerUpdater.IsNewer(dlVersion, cur))
                    {
                        FileLog.Error("ApplyManagerUpdate: downloaded exe version " + dlVersion + " not newer than " + cur);
                        Balloon("dsh web manager", "下载的文件版本异常 (" + dlVersion + ")，已取消更新");
                        return;
                    }
                    FileLog.Info("ApplyManagerUpdate: v" + cur + " -> " + rel.Tag + " (file " + dlVersion + ")");
                    string ps1 = Path.Combine(updateDir, "apply-update.ps1");
                    WriteUpdaterScript(ps1, newExe);
                    System.Diagnostics.Process.Start("powershell.exe",
                        "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + ps1 + "\"");
                    FileLog.Info("ApplyManagerUpdate: updater spawned, exiting");
                    Balloon("dsh web manager", "正在更新 dsh web manager（v" + cur + " → " + rel.Tag + "），数秒后自动重启");
                    // Let the balloon paint, then exit without stopping dsh services.
                    Thread.Sleep(1500);
                    ExitForUpdate();
                }
                catch (Exception ex)
                {
                    FileLog.Error("ApplyManagerUpdate: " + ex.ToString());
                    try { Balloon("dsh web manager", "更新管理器失败: " + ex.Message); } catch { }
                }
            });
        }

        /// <summary>One-click refresh of the dsh-web-manager plugin bundle in the
        /// ACTIVE backend's dsh profile: `dsh plugin remove` + `add` with the recorded
        /// spec (auto-detected from that platform's profile package.json, or
        /// PluginUpdateSpec). Windows (dsh runs natively) and WSL are both supported —
        /// previously this was WSL-only, so a Windows profile reported
        /// "未找到插件的安装来源".</summary>
        public void UpdatePluginBundle()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string profile = String.IsNullOrWhiteSpace(_config.Profile) ? "web" : _config.Profile;
                    // Same guard as WslBackend.Start: the profile is interpolated
                    // into node -e / bash / cmd command strings, so spaces/tabs (which
                    // would break quoting) must be rejected up front.
                    if (profile.IndexOfAny(new char[] { ' ', '\t' }) >= 0)
                    {
                        FileLog.Error("UpdatePluginBundle: profile with spaces is not supported: " + profile);
                        Balloon("dsh web manager", "Profile 不能包含空格，插件包更新已取消");
                        return;
                    }
                    if (IsWslActive())
                        UpdatePluginInWsl(profile);
                    else
                        UpdatePluginOnWindows(profile);
                }
                catch (Exception ex)
                {
                    FileLog.Error("UpdatePluginBundle: " + ex.ToString());
                    try { Balloon("dsh web manager", "更新插件包失败: " + ex.Message); } catch { }
                }
            });
        }

        private void UpdatePluginInWsl(string profile)
        {
            string distro = ResolveDshDistro();
            if (String.IsNullOrEmpty(distro))
            {
                Balloon("dsh web manager", "未找到 WSL 发行版，无法更新插件包");
                return;
            }
            // PluginUpdateSpec > the spec recorded in the WSL profile > the Windows
            // profile's spec (converted to /mnt/c) > the npm package name. Never
            // empty, so a profile without the plugin gets it INSTALLED instead of
            // reporting the old confusing "未找到插件的安装来源".
            string spec = ResolveWslPluginSpec(distro, profile);
            FileLog.Info("UpdatePluginInWsl: refreshing " + profile + " with spec " + spec);
            Balloon("dsh web manager", "正在更新 dsh 插件包（WSL " + distro + " / " + profile + "）…");
            // Remove is best-effort (the package may not be installed yet).
            WslTools.RunCapture(distro, "bash", new string[] { "-lc",
                "dsh plugin --profile " + WslTools.BashQuote(profile) + " remove dsh-web-manager" }, 120000);
            CommandResult add = WslTools.RunCapture(distro, "bash", new string[] { "-lc",
                "dsh plugin --profile " + WslTools.BashQuote(profile) + " add " + WslTools.BashQuote(spec) }, 180000);
            ReportPluginUpdate(add, profile, spec, ReadPluginVersion(distro, profile));
        }

        private void UpdatePluginOnWindows(string profile)
        {
            string dsh = DshLauncher.FindDshCommand();
            if (String.IsNullOrEmpty(dsh))
            {
                Balloon("dsh web manager", "未找到 dsh 命令（请安装 dsh 并更新 PATH），无法更新插件包");
                return;
            }
            // PluginUpdateSpec > the spec recorded in the Windows profile > npm name.
            string spec = ResolveWindowsPluginSpec(profile);
            if (spec.IndexOf(' ') >= 0)
            {
                FileLog.Error("UpdatePluginOnWindows: spec with spaces is not supported: " + spec);
                Balloon("dsh web manager", "插件来源路径包含空格，暂不支持，请在 config 中设置 PluginUpdateSpec");
                return;
            }
            FileLog.Info("UpdatePluginOnWindows: refreshing " + profile + " with spec " + spec);
            Balloon("dsh web manager", "正在更新 dsh 插件包（Windows / " + profile + "）…");
            // `dsh plugin` forwards to a bare `pnpm`; the bundled node ships only a
            // corepack shim, so prepend its dir to the child PATH when pnpm is not
            // globally installed (scoped to this process only).
            string pnpm = UpdateChecker.FindPnpmCommand();
            string pnpmDir = String.IsNullOrEmpty(pnpm) ? null : Path.GetDirectoryName(pnpm);
            // Remove is best-effort (the package may not be installed yet).
            UpdateChecker.RunWindowsCommand("\"" + dsh + "\" plugin --profile " + profile + " remove dsh-web-manager", 120000, pnpmDir);
            CommandResult add = UpdateChecker.RunWindowsCommand("\"" + dsh + "\" plugin --profile " + profile + " add " + spec, 180000, pnpmDir);
            ReportPluginUpdate(add, profile, spec, ReadWindowsPluginVersion(profile));
        }

        /// <summary>Common success/failure balloon for a plugin remove+add round.</summary>
        private void ReportPluginUpdate(CommandResult add, string profile, string spec, string ver)
        {
            if (add.ExitCode == 0)
            {
                // Report exactly which package version was installed and from where.
                string detail = String.IsNullOrEmpty(ver) ? "dsh-web-manager" : "dsh-web-manager@" + ver;
                FileLog.Info("UpdatePluginBundle: updated " + detail + " in " + profile + " from " + spec);
                Balloon("dsh web manager", "dsh 插件包已更新（" + profile + "）：" + detail
                    + "（来源 " + spec + "），重启 dsh 后生效");
            }
            else
            {
                // Surface the actual reason (e.g. "[ERR_PNPM_FETCH_404] ...
                // not in the npm registry") instead of a generic failure or pnpm's
                // informational banner — the user reported a confusing message here.
                string output = ((add.StandardOutput ?? String.Empty) + (add.StandardError ?? String.Empty)).Trim();
                string first = FirstErrorLine(output);
                FileLog.Error("UpdatePluginBundle add failed: " + output);
                Balloon("dsh web manager", "插件包更新失败"
                    + (String.IsNullOrEmpty(first)
                        ? "，退出码 " + add.ExitCode
                        : "：" + Truncate(first, 110))
                    + "，请查看日志");
            }
        }

        /// <summary>First line that actually explains a failure (e.g. an ERR_/not
        /// found/"pnpm not found" line), skipping pnpm's informational banners.</summary>
        private static string FirstErrorLine(string text)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string fallback = String.Empty;
            foreach (string line in lines)
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (fallback.Length == 0 && !t.StartsWith("\u2713") && !t.StartsWith("Progress:"))
                    fallback = t;
                string low = t.ToLowerInvariant();
                if (low.Contains("err_") || low.Contains("err!") || low.Contains("error")
                    || low.Contains("not in the npm registry") || low.Contains("not found")
                    || low.Contains("pnpm not found") || low.Contains("enoent")
                    || low.Contains("could not") || low.Contains("failed"))
                    return t;
            }
            return fallback;
        }

        private static string Truncate(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        /// <summary>Reads the recorded install spec of dsh-web-manager from the
        /// profile's package.json (e.g. "file:/home/.../dsh-web-manager").
        /// Empty when the package is not a direct dependency of the profile.</summary>
        private static string ReadPluginSpec(string distro, string profile)
        {
            string script = "node -e 'const base=process.env.DSH_HOME||(process.env.HOME+\"/.dsh\");"
                + "const p=base+\"/profiles/" + profile + "/package.json\";"
                + "let d;try{d=require(p)}catch(e){process.exit(1)}"
                + "const s=(d.dependencies&&d.dependencies[\"dsh-web-manager\"])"
                + "||(d.optionalDependencies&&d.optionalDependencies[\"dsh-web-manager\"])||\"\";"
                + "console.log(s)'";
            CommandResult r = WslTools.RunCapture(distro, "bash", new string[] { "-lc", script }, 30000);
            if (r.ExitCode != 0) return String.Empty;
            return (r.StandardOutput ?? String.Empty).Trim();
        }

        /// <summary>Installed dsh-web-manager version inside the profile ("" if unreadable).</summary>
        private static string ReadPluginVersion(string distro, string profile)
        {
            string script = "node -e 'const base=process.env.DSH_HOME||(process.env.HOME+\"/.dsh\");"
                + "const p=base+\"/profiles/" + profile + "/node_modules/dsh-web-manager/package.json\";"
                + "try{console.log(require(p).version)}catch(e){process.exit(1)}'";
            CommandResult r = WslTools.RunCapture(distro, "bash", new string[] { "-lc", script }, 30000);
            if (r.ExitCode != 0) return String.Empty;
            return (r.StandardOutput ?? String.Empty).Trim();
        }

        /// <summary>Reads the recorded install spec of dsh-web-manager from the
        /// Windows profile's package.json (e.g. "file:C:/.../dsh-web-manager").
        /// Empty when the package is not a direct dependency of the profile.</summary>
        private static string ReadWindowsPluginSpec(string profile)
        {
            string pkg = WindowsProfilePackageJson(profile);
            if (String.IsNullOrEmpty(pkg) || !File.Exists(pkg)) return String.Empty;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> package = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(pkg));
                if (package == null) return String.Empty;
                string spec = ReadDependencySpec(package, "dependencies");
                if (!String.IsNullOrEmpty(spec)) return spec;
                return ReadDependencySpec(package, "optionalDependencies");
            }
            catch (Exception ex) { FileLog.Error("ReadWindowsPluginSpec: " + ex.Message); }
            return String.Empty;
        }

        /// <summary>Installed dsh-web-manager version in the Windows profile ("" if unreadable).</summary>
        private static string ReadWindowsPluginVersion(string profile)
        {
            string baseDir = WindowsDshHome();
            if (String.IsNullOrEmpty(baseDir)) return String.Empty;
            string pkg = Path.Combine(baseDir, "profiles", profile, "node_modules", "dsh-web-manager", "package.json");
            try
            {
                if (!File.Exists(pkg)) return String.Empty;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> package = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(pkg));
                object v;
                if (package != null && package.TryGetValue("version", out v) && v != null) return v.ToString();
            }
            catch { }
            return String.Empty;
        }

        private static string WindowsDshHome()
        {
            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!String.IsNullOrWhiteSpace(home)) return home.Trim().TrimEnd('\\');
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (String.IsNullOrEmpty(userProfile)) return String.Empty;
            return Path.Combine(userProfile, ".dsh");
        }

        private static string WindowsProfilePackageJson(string profile)
        {
            string baseDir = WindowsDshHome();
            if (String.IsNullOrEmpty(baseDir)) return String.Empty;
            return Path.Combine(baseDir, "profiles", profile, "package.json");
        }

        private static string ReadDependencySpec(Dictionary<string, object> package, string section)
        {
            object o;
            if (package.TryGetValue(section, out o) && o is Dictionary<string, object>)
            {
                object v;
                if (((Dictionary<string, object>)o).TryGetValue("dsh-web-manager", out v) && v != null)
                    return v.ToString();
            }
            return String.Empty;
        }

        /// <summary>Plugin install spec for the Windows profile:
        /// PluginUpdateSpec &gt; recorded spec in the Windows profile &gt; the GitHub
        /// repo (the package is not published to the npm registry - pnpm 404s).</summary>
        private string ResolveWindowsPluginSpec(string profile)
        {
            if (!String.IsNullOrWhiteSpace(_config.PluginUpdateSpec)) return _config.PluginUpdateSpec;
            string recorded = ReadWindowsPluginSpec(profile);
            return String.IsNullOrWhiteSpace(recorded) ? DefaultPluginSpec : recorded;
        }

        /// <summary>Plugin install spec for the WSL profile: PluginUpdateSpec &gt;
        /// recorded spec in the WSL profile &gt; recorded Windows spec converted to a
        /// /mnt/c path &gt; the GitHub repo. Never empty, so an uninstalled plugin is
        /// installed rather than reported as a missing install source.</summary>
        private string ResolveWslPluginSpec(string distro, string profile)
        {
            if (!String.IsNullOrWhiteSpace(_config.PluginUpdateSpec)) return _config.PluginUpdateSpec;
            string wsl = ReadPluginSpec(distro, profile);
            if (!String.IsNullOrWhiteSpace(wsl)) return wsl;
            string win = ReadWindowsPluginSpec(profile);
            if (!String.IsNullOrWhiteSpace(win))
            {
                string converted = ToWslInstallSpec(win);
                if (!String.IsNullOrWhiteSpace(converted)) return converted;
            }
            return DefaultPluginSpec;
        }

        /// <summary>Last-resort install source (repo root; the package is not on npm).</summary>
        private const string DefaultPluginSpec = "github:FYHC1/dsh-web-manager#main";

        /// <summary>Converts a Windows file: spec ("file:C:/x/y") to the
        /// WSL-equivalent "file:/mnt/c/x/y"; other specs pass through unchanged.</summary>
        private static string ToWslInstallSpec(string spec)
        {
            string s = (spec ?? String.Empty).Trim();
            if (!s.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return s;
            string body = s.Substring(5);
            if (body.Length >= 2 && Char.IsLetter(body[0]) && body[1] == ':')
            {
                char drive = Char.ToLowerInvariant(body[0]);
                string rest = body.Substring(2).Replace('\\', '/').TrimStart('/');
                return "file:/mnt/" + drive + "/" + rest;
            }
            return s;
        }

        /// <summary>Exits the tray without stopping dsh services so the detached
        /// updater can replace this EXE; on restart the manager re-attaches to the
        /// still-running dsh instances.</summary>
        public void ExitForUpdate()
        {
            _disposed = true;
            var h = Exiting;
            if (h != null) h();
            Environment.Exit(0);
        }

        /// <summary>Writes the detached self-update script (pure ASCII, PS 5.1 safe):
        /// waits for this EXE to unlock, swaps in the downloaded binary and restarts
        /// the tray. Paths are single-quoted so PowerShell never mangles them.</summary>
        private static void WriteUpdaterScript(string path, string newExe)
        {
            string target = AppPaths.ExePath;
            string log = Path.Combine(AppPaths.LogDir, "manager-update.log");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("$target = '" + target + "'");
            sb.AppendLine("$new = '" + newExe + "'");
            sb.AppendLine("$log = '" + log + "'");
            sb.AppendLine("function Log($m) { try { Add-Content -Path $log -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + ' ' + $m) } catch {} }");
            sb.AppendLine("Log 'updater started'");
            sb.AppendLine("$unlocked = $false");
            sb.AppendLine("for ($i = 0; $i -lt 120; $i++) {");
            sb.AppendLine("  Start-Sleep -Milliseconds 500");
            sb.AppendLine("  try { $fs = [System.IO.File]::Open($target, 'Open', 'ReadWrite', 'None'); $fs.Close(); $unlocked = $true; break } catch {}");
            sb.AppendLine("}");
            sb.AppendLine("if (-not $unlocked) { Log 'FAILED: manager exe stayed locked for 60s'; exit 1 }");
            sb.AppendLine("$copied = $false");
            sb.AppendLine("for ($i = 0; $i -lt 10; $i++) {");
            sb.AppendLine("  try { Copy-Item -Force -LiteralPath $new -Destination $target; $copied = $true; break } catch { Start-Sleep -Milliseconds 500 }");
            sb.AppendLine("}");
            sb.AppendLine("if (-not $copied) { Log 'FAILED: copy failed after retries'; exit 1 }");
            sb.AppendLine("Log 'replaced exe'");
            sb.AppendLine("try { Start-Process -FilePath $target -ArgumentList 'tray'; Log 'restarted' }");
            sb.AppendLine("catch { Log ('FAILED: ' + $_.Exception.Message) }");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
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
            // Hide the tray icon FIRST: Environment.Exit below never runs the
            // Form's Dispose, so without an explicit NIM_DELETE the icon lingers
            // as a ghost until hover/Explorer refresh (and a second click seemed
            // to be required to make it vanish).
            var h = Exiting;
            if (h != null) h();
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
                // Keep the browser warm ONLY while the service still runs: after
                // 关闭实例 there is nothing to reopen and a warm window-less Edge
                // would just idle ~150MB for nothing.
                if (c.State == InstanceState.Managed || c.State == InstanceState.Attached)
                    EdgeWindow.Preheat(port, _config.DataDir);
            }
            _hadWindows[c] = hasWindow;
        }

        /// <summary>Stops one instance's service and closes its window (menu 关闭实例;
        /// also reachable headlessly via the control action "closeinstance &lt;backend&gt;").
        /// Runs off the UI thread: WSL stops can take seconds.</summary>
        public void CloseInstance(int index)
        {
            InstanceController c = GetController(index);
            if (c == null) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { c.Stop(false); }
                catch (Exception ex) { FileLog.Error("CloseInstance stop: " + ex.Message); }
                try { EdgeWindow.CloseWindow(c.ActivePort); }
                catch (Exception ex) { FileLog.Error("CloseInstance window: " + ex.Message); }
            });
        }

        /// <summary>CloseInstance by backend name ("windows" / "wsl").</summary>
        public void CloseInstanceBackend(string backend)
        {
            InstanceController c = GetControllerForBackend(backend);
            if (c == null)
            {
                Balloon("dsh web manager", "未找到后端: " + backend);
                return;
            }
            int index = _controllers.IndexOf(c);
            CloseInstance(index >= 0 ? index : 0);
        }

        private void Tick(object state)
        {
            if (_disposed) return;
            // Reentrancy guard: the timer fires every 1s, but a single Tick can
            // take longer (WMI queries have a 3s timeout), so the framework may
            // invoke the callback concurrently on another pool thread. Skipping
            // the overlapping run keeps the heartbeat, close-detection and size
            // capture strictly serialized (double Stop/Start of a crashed
            // service is already prevented by the controller lock, but double
            // work is pointless and WMI pressure only makes the stall worse).
            if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0) return;
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
            finally
            {
                Interlocked.Exchange(ref _tickRunning, 0);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            if (_timer != null) _timer.Dispose();
        }
    }
}