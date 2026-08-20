using System;
using System.Diagnostics;

namespace DshWebManager
{
    public enum InstanceState
    {
        Stopped,
        Starting,
        Managed,   // this manager launched the dsh process
        Attached,  // external dsh process already serving; we only manage the window
        Error
    }

    /// <summary>
    /// Lifecycle state machine for one dsh web service on one port.
    /// </summary>
    public sealed class InstanceController
    {
        private const int MissingThreshold = 3;      // ticks before declaring the service dead
        private const int CrashWindowMs = 60 * 1000; // crash counting window
        private const int CrashLimit = 3;            // restarts allowed inside the window
        private const int WaitReadyMs = 60 * 1000;   // startup timeout

        private readonly object _sync = new object();
        private readonly ManagerConfig _config;

        public int ActivePort { get; private set; }
        public int ManagedPid { get; private set; }
        public InstanceState State { get; private set; }
        public string LastError { get; private set; }
        public DateTime? LastStartedUtc { get; private set; }

        private int _missingCount;
        private DateTime _windowStart = DateTime.MinValue;
        private int _crashCount;

        public event Action<string> StatusChanged;

        public InstanceController(ManagerConfig config)
        {
            _config = config;
            State = InstanceState.Stopped;
            ActivePort = config.Port;
        }

        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case InstanceState.Managed: return "运行中 (managed, port " + ActivePort + ")";
                    case InstanceState.Attached: return "外部服务 (attached, port " + ActivePort + ")";
                    case InstanceState.Starting: return "启动中…";
                    case InstanceState.Stopped: return "未运行";
                    case InstanceState.Error: return "错误: " + LastError;
                    default: return State.ToString();
                }
            }
        }

        /// <summary>Starts (or attaches to) dsh web on the configured/fallback port.</summary>
        public void Start()
        {
            lock (_sync)
            {
                if (State == InstanceState.Starting || State == InstanceState.Managed) return;
                int preferred = _config.Port;
                int pid = PortInspector.GetListenerPid(preferred);

                if (pid > 0 && PortInspector.IsDshProcess(pid))
                {
                    // Existing dsh already serving the preferred port: attach, never kill it.
                    ActivePort = preferred;
                    State = InstanceState.Attached;
                    LastStartedUtc = null;
                    FireStatus("已附着现有 dsh 服务 (port " + preferred + ")");
                    return;
                }

                if (PortInspector.IsListening(preferred))
                {
                    int chosen = PortInspector.ChoosePort(preferred, _config.AutoFallback, true);
                    if (chosen <= 0)
                    {
                        State = InstanceState.Error;
                        LastError = "port " + preferred + " 被其它程序占用，且没有可用顺延端口";
                        FireStatus(LastError);
                        return;
                    }
                    ActivePort = chosen;
                    FireStatus("端口 " + preferred + " 被占用，顺延至 " + chosen);
                }
                else
                {
                    ActivePort = preferred;
                }

                State = InstanceState.Starting;
                try
                {
                    Process p = DshLauncher.StartDshWeb(ActivePort, _config.Profile);
                    ManagedPid = p.Id;
                    if (!PortInspector.WaitReady(ActivePort, WaitReadyMs))
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
                    FireStatus("dsh web 已启动 (port " + ActivePort + ")");
                }
                catch (Exception ex)
                {
                    State = InstanceState.Error;
                    LastError = "启动失败: " + ex.Message;
                    FireStatus(LastError);
                }
            }
        }

        /// <summary>Stops the managed dsh process (attached services are left alone).</summary>
        public void Stop(bool force)
        {
            lock (_sync)
            {
                if (State == InstanceState.Managed && ManagedPid > 0)
                {
                    FileLog.Info("Stopping managed dsh (pid=" + ManagedPid + ")");
                    DshLauncher.KillTree(ManagedPid);
                    ManagedPid = 0;
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

        /// <summary>Restarts: stop (if managed) then start again.</summary>
        public void Restart()
        {
            lock (_sync)
            {
                if (State == InstanceState.Managed && ManagedPid > 0)
                    DshLauncher.KillTree(ManagedPid);
                ManagedPid = 0;
                State = InstanceState.Stopped;
            }
            Start();
        }

        /// <summary>Periodic heart-beat: detect crash of a managed service and restart with back-off.</summary>
        public void Tick()
        {
            lock (_sync)
            {
                if (State != InstanceState.Managed && State != InstanceState.Attached) return;
                bool up = PortInspector.IsListening(ActivePort);
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

                // Managed service crashed: restart with back-off.
                _missingCount = 0;
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
                ManagedPid = 0;
                try
                {
                    Process p = DshLauncher.StartDshWeb(ActivePort, _config.Profile);
                    ManagedPid = p.Id;
                    if (PortInspector.WaitReady(ActivePort, WaitReadyMs))
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
    }
}