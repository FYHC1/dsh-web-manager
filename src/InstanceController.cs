using System;
using System.Diagnostics;

namespace DshWebManager
{
    public enum InstanceState
    {
        Stopped,
        Starting,
        Managed,   // this manager launched the service (Windows or WSL)
        Attached,  // external dsh service already serving; we only manage the window
        Error
    }

    /// <summary>
    /// Lifecycle state machine for one dsh web service on one port, on top of an
    /// IServiceBackend (Windows or WSL). Managed services are stopped/restarted by
    /// the manager; attached services are only monitored.
    /// </summary>
    public sealed class InstanceController
    {
        private const int MissingThreshold = 3;      // ticks before declaring the service dead
        private const int CrashWindowMs = 60 * 1000; // crash counting window
        private const int CrashLimit = 3;            // restarts allowed inside the window
        private const int WaitReadyMs = 60 * 1000;   // startup timeout

        private readonly object _sync = new object();
        private readonly ManagerConfig _config;
        private readonly InstanceConfig _instance;
        private IServiceBackend _backend;

        public int ActivePort { get; private set; }
        public InstanceState State { get; private set; }
        public string LastError { get; private set; }
        public DateTime? LastStartedUtc { get; private set; }

        public int ManagedPid { get { return _backend == null ? 0 : _backend.ManagedPid; } }
        public string BackendDescribe { get { return _backend == null ? String.Empty : _backend.Describe(); } }
        public IServiceBackend Backend { get { return _backend; } }

        private int _missingCount;
        private DateTime _windowStart = DateTime.MinValue;
        private int _crashCount;

        public event Action<string> StatusChanged;

        public InstanceController(ManagerConfig config, InstanceConfig instance)
        {
            _config = config;
            _instance = instance;
            State = InstanceState.Stopped;
            ActivePort = instance.EffectivePort;
            _backend = BackendFactory.Create(config, instance);
        }

        public InstanceConfig Instance { get { return _instance; } }

        public string StatusText
        {
            get
            {
                string where = ShortBackend();
                switch (State)
                {
                    case InstanceState.Managed: return "运行中 (" + where + ", " + ActivePort + ")" + RuntimeSuffix();
                    case InstanceState.Attached: return "外部服务 (" + ActivePort + ")" + RuntimeSuffix();
                    case InstanceState.Starting: return "启动中…";
                    case InstanceState.Stopped: return "未启动 · 未知版本";
                    case InstanceState.Error: return "错误: " + LastError;
                    default: return State.ToString();
                }
            }
        }

        /// <summary>Short backend label for the tray status ("Windows" / "WSL").</summary>
        private string ShortBackend()
        {
            if (_backend == null) return "?";
            return String.Equals(_backend.BackendType, "wsl", StringComparison.OrdinalIgnoreCase) ? "WSL" : "Windows";
        }

        /// <summary>Rich runtime summary from the backend (empty when unavailable).</summary>
        public string RuntimeSummary { get { return _backend == null ? String.Empty : _backend.GetRuntimeSummary(ActivePort); } }

        /// <summary>Periodic bridge refresh, throttled inside the backend.</summary>
        public void RefreshRuntime()
        {
            lock (_sync)
            {
                if (_backend == null) return;
                if (State != InstanceState.Managed && State != InstanceState.Attached) return;
                try { _backend.RefreshRuntime(ActivePort); }
                catch (Exception ex) { FileLog.Error("RefreshRuntime failed: " + ex.Message); }
            }
        }

        /// <summary>Re-publishes StatusText without writing a log line (periodic refresh).</summary>
        public void RefreshStatusDisplay()
        {
            var h = StatusChanged;
            if (h != null) h(StatusText);
        }

        private string RuntimeSuffix()
        {
            string s = _backend == null ? String.Empty : _backend.GetRuntimeSummary(ActivePort);
            return String.IsNullOrEmpty(s) ? String.Empty : " · " + s;
        }

        /// <summary>Rebuilds the backend after a backend-type config change.</summary>
        public void Reconfigure()
        {
            lock (_sync)
            {
                _backend = BackendFactory.Create(_config, _instance);
                ActivePort = _instance.EffectivePort;
                State = InstanceState.Stopped;
                LastError = null;
                _missingCount = 0;
                _crashCount = 0;
                FireStatus("已切换后端: " + BackendDescribe);
            }
        }

        /// <summary>Starts (or attaches to) dsh web on the configured/fallback port.</summary>
        public void Start()
        {
            lock (_sync)
            {
                if (State == InstanceState.Starting || State == InstanceState.Managed) return;
                string error;
                if (!_backend.IsAvailable(out error))
                {
                    State = InstanceState.Error;
                    LastError = error;
                    FireStatus(LastError);
                    return;
                }

                int preferred = _instance.EffectivePort;
                PortProbeResult probe = _backend.ProbePort(preferred);

                if (probe == PortProbeResult.DshServing)
                {
                    // Existing dsh already serving the preferred port: attach, never kill it.
                    ActivePort = preferred;
                    State = InstanceState.Attached;
                    LastStartedUtc = null;
                    RememberBackendDistro();
                    FireStatus("已附着现有 dsh 服务 (" + _backend.Describe() + ", port " + preferred + ")");
                    return;
                }

                if (probe == PortProbeResult.Occupied)
                {
                    int chosen = FindFreePort(preferred);
                    if (chosen <= 0)
                    {
                        State = InstanceState.Error;
                        LastError = "port " + preferred + " 被其它程序占用，且没有可用顺延端口";
                        FireStatus(LastError);
                        return;
                    }
                    ActivePort = chosen;
                    _instance.SetEffectivePort(chosen);
                    _config.Save();
                    FireStatus("端口 " + preferred + " 被占用，顺延至 " + chosen);
                }
                else
                {
                    ActivePort = preferred;
                }

                State = InstanceState.Starting;
                try
                {
                    if (!_backend.Start(ActivePort, _instance.Profile))
                    {
                        State = InstanceState.Error;
                        LastError = "后端启动失败 (" + _backend.Describe() + ")";
                        FireStatus(LastError);
                        return;
                    }
                    if (!WaitReadyBackend(ActivePort, WaitReadyMs))
                    {
                        State = InstanceState.Error;
                        LastError = "dsh web 启动超时 (port " + ActivePort + ")";
                        FireStatus(LastError);
                        return;
                    }
                    State = InstanceState.Managed;
                    LastStartedUtc = DateTime.UtcNow;
                    _crashCount = 0;
                    _windowStart = DateTime.UtcNow;
                    FireStatus("dsh web 已启动 (" + _backend.Describe() + ", port " + ActivePort + ")");
                }
                catch (Exception ex)
                {
                    State = InstanceState.Error;
                    LastError = "启动失败: " + ex.Message;
                    FireStatus(LastError);
                }
            }
        }

        private int FindFreePort(int preferred)
        {
            if (!_config.AutoFallback) return -1;
            for (int p = preferred + 1; p < preferred + 100; p++)
            {
                if (_backend.ProbePort(p) == PortProbeResult.Free) return p;
            }
            return -1;
        }

        /// <summary>Stops the managed service (attached services are left alone).</summary>
        public void Stop(bool force)
        {
            lock (_sync)
            {
                bool owned = State == InstanceState.Managed
                    || State == InstanceState.Starting
                    || State == InstanceState.Error;
                if (owned && (_backend.IsWrapperAlive() || _backend.ManagedPid > 0))
                {
                    // Also cleans up a backend whose start failed part-way (Error/Starting):
                    // a spawned wsl.exe / script must not outlive the manager.
                    FileLog.Info("Stopping managed dsh (" + _backend.Describe() + ")");
                    _backend.Stop();
                    State = InstanceState.Stopped;
                    FireStatus("服务已停止");
                }
                else if (State == InstanceState.Attached)
                {
                    // We do not own the attached process; just detach.
                    State = InstanceState.Stopped;
                    FireStatus("已解除对外部服务的附着");
                }
                else
                {
                    State = InstanceState.Stopped;
                }
                _missingCount = 0;
            }
        }

        /// <summary>Restarts: stop (if owned) then start again.</summary>
        public void Restart()
        {
            lock (_sync)
            {
                Stop(true); // also cleans up a half-started backend (Error/Starting)
            }
            Start();
        }

        /// <summary>Periodic heart-beat: detect crash of a managed service and restart with back-off.</summary>
        public void Tick()
        {
            lock (_sync)
            {
                if (State != InstanceState.Managed && State != InstanceState.Attached) return;
                bool up = _backend.IsServiceUp(ActivePort);
                if (up)
                {
                    _missingCount = 0;
                    return;
                }
                _missingCount++;
                if (_missingCount < MissingThreshold) return;

                if (State == InstanceState.Attached)
                {
                    // External service vanished; do not resurrect it automatically.
                    FileLog.Info("Attached external service disappeared on port " + ActivePort);
                    State = InstanceState.Stopped;
                    FireStatus("外部服务已停止 (port " + ActivePort + ")，未自动重启");
                    return;
                }

                _missingCount = 0;

                // WSL: the launcher script self-heals (restart loop). While its wrapper
                // is alive the port may be briefly down; wait instead of fighting it.
                if (_backend.IsWrapperAlive())
                {
                    FileLog.Info("Managed service down on port " + ActivePort + " but wrapper alive; waiting for self-heal");
                    return;
                }

                // Managed service crashed (wrapper gone): restart with back-off.
                DateTime now = DateTime.UtcNow;
                if (now.Subtract(_windowStart) > TimeSpan.FromMilliseconds(CrashWindowMs))
                {
                    _crashCount = 0;
                    _windowStart = now;
                }
                _crashCount++;
                FileLog.Info("Managed dsh down on port " + ActivePort + " (crash #" + _crashCount + ")");
                if (_crashCount > CrashLimit)
                {
                    State = InstanceState.Error;
                    LastError = "dsh web 在 " + CrashLimit + " 次崩溃后停止尝试 (port " + ActivePort + ")";
                    FireStatus(LastError);
                    return;
                }
                FireStatus("检测到服务停止，自动重启 (" + _crashCount + "/" + CrashLimit + ")");
                try
                {
                    if (!_backend.Start(ActivePort, _instance.Profile))
                    {
                        State = InstanceState.Error;
                        LastError = "自动重启失败 (" + _backend.Describe() + ")";
                        FireStatus(LastError);
                        return;
                    }
                    if (WaitReadyBackend(ActivePort, WaitReadyMs))
                    {
                        State = InstanceState.Managed;
                        LastStartedUtc = DateTime.UtcNow;
                        FireStatus("已自动重启 (port " + ActivePort + ")");
                    }
                    else
                    {
                        State = InstanceState.Error;
                        LastError = "自动重启后服务仍未就绪 (port " + ActivePort + ")";
                        FireStatus(LastError);
                    }
                }
                catch (Exception ex)
                {
                    State = InstanceState.Error;
                    LastError = "自动重启失败: " + ex.Message;
                    FireStatus(LastError);
                }
            }
        }

        private void FireStatus(string text)
        {
            FileLog.Info("Status: " + text);
            var h = StatusChanged;
            if (h != null) h(text);
        }

        /// <summary>WSL backend: persist the working distro for auto selection across restarts.</summary>
        private void RememberBackendDistro()
        {
            WslBackend wsl = _backend as WslBackend;
            if (wsl != null) wsl.RememberDistro();
        }

        /// <summary>Backend-aware readiness wait (WSL falls back to the distro socket table).</summary>
        private bool WaitReadyBackend(int port, int timeoutMs)
        {
            // Wall-clock deadline: a backend probe may take >1 s (wsl.exe spawn),
            // so an iteration counter would stretch the timeout far beyond intent.
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (_backend.IsServiceUp(port)) return true;
                System.Threading.Thread.Sleep(500);
            }
            return false;
        }
    }
}
