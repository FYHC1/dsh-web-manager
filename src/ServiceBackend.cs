using System;
using System.Diagnostics;

namespace DshWebManager
{
    /// <summary>Result of probing a port from the manager's point of view.</summary>
    public enum PortProbeResult
    {
        Free,        // nothing serves this port (reachable from Windows)
        DshServing,  // a dsh web service already serves it (attach candidate, never kill)
        Occupied     // occupied by a foreign program -> port fallback
    }

    /// <summary>
    /// Abstraction over where the dsh web service runs.
    /// WindowsBackend (v2.0) runs dsh on Windows; WslBackend (v2.1) runs it inside a
    /// WSL distro through wsl.exe. The instance controller only talks to this interface.
    /// </summary>
    public interface IServiceBackend
    {
        string BackendType { get; }                  // "windows" | "wsl"
        string Describe();                           // human readable, e.g. "WSL (FedoraLinux)"
        bool IsAvailable(out string error);          // prerequisites (dsh cmd / wsl.exe + distro)
        int ManagedPid { get; }                      // wrapper process id this manager launched (0 = none)
        PortProbeResult ProbePort(int port);         // attach / fallback decision
        bool Start(int port, string profile);        // launch the service, returns success
        bool IsWrapperAlive();                       // the launched wrapper process still alive
        void Stop(int port);                         // stop the instance's service (managed AND attached)
        bool IsServiceUp(int port);                  // backend-aware liveness (wait-ready + heartbeat)
        string GetWindowUrl(int port);               // URL the Edge window opens (WSL IP when forwarding is off)
        void RefreshRuntime(int port);               // throttled runtime-bridge refresh (no-op if none; v3.0)
        string GetRuntimeSummary(int port);          // optional rich runtime status ("" = none; v3.0 bridge)
        BridgeInfo QueryBridgeInfo(int port);        // forced bridge query + cache (null = unreachable; v3.0)
    }

    public static class BackendFactory
    {
        public static IServiceBackend Create(ManagerConfig config)
        {
            if (config == null || config.EffectiveInstances == null || config.EffectiveInstances.Count == 0)
                return new WindowsBackend(config, null);
            return Create(config, config.EffectiveInstances[0]);
        }

        public static IServiceBackend Create(ManagerConfig shared, InstanceConfig instance)
        {
            if (instance != null && instance.IsWsl)
                return new WslBackend(shared, instance);
            return new WindowsBackend(shared, instance);
        }
    }

    /// <summary>dsh web running directly on Windows (v2.0 behaviour).</summary>
    public sealed class WindowsBackend : IServiceBackend
    {
        private readonly ManagerConfig _config;
        private Process _proc;
        private int _lastPort;
        private BridgeInfo _bridgeInfo;                 // cached runtime-bridge payload
        private DateTime _bridgeInfoAt = DateTime.MinValue;
        private static readonly TimeSpan BridgeInfoTtl = TimeSpan.FromSeconds(10);
        // Fallback: when the runtime-bridge plugin is not loaded, show a cached
        // "dsh <version>" line. Probed on a background thread, 60s TTL.
        private string _fallbackSummary = String.Empty;
        private DateTime _fallbackProbeAt = DateTime.MinValue;
        private int _fallbackProbeRunning;
        private static readonly TimeSpan FallbackProbeTtl = TimeSpan.FromSeconds(60);

        public WindowsBackend(ManagerConfig config) { _config = config; }

        public WindowsBackend(ManagerConfig shared, InstanceConfig instance)
        {
            _config = shared;
        }

        public string BackendType { get { return "windows"; } }
        public string Describe() { return "Windows 本机"; }

        public bool IsAvailable(out string error)
        {
            error = String.Empty;
            if (DshLauncher.FindDshCommand() == null)
            {
                error = "未找到 dsh 命令（请安装 dsh 并更新 PATH）";
                return false;
            }
            return true;
        }

        public int ManagedPid
        {
            get
            {
                try { return _proc != null && !_proc.HasExited ? _proc.Id : 0; }
                catch { return 0; }
            }
        }

        public PortProbeResult ProbePort(int port)
        {
            int pid = PortInspector.GetListenerPid(port);
            if (pid > 0 && PortInspector.IsDshProcess(pid)) return PortProbeResult.DshServing;
            if (PortInspector.IsListening(port)) return PortProbeResult.Occupied;
            return PortProbeResult.Free;
        }

        public bool Start(int port, string profile)
        {
            _config.EnsureBridgeToken();
            _lastPort = port;
            _proc = DshLauncher.StartDshWeb(port, profile, _config.BridgeToken);
            return _proc != null;
        }

        public bool IsWrapperAlive()
        {
            try { return _proc != null && !_proc.HasExited; }
            catch { return false; }
        }

        public void Stop(int port)
        {
            // Graceful shutdown first: ask the in-dsh runtime bridge to terminate
            // cleanly. The bridge only answers on a dsh configured with OUR token,
            // so this is safe for attached services too (a foreign dsh simply does
            // not respond) - attached means "this manager did not spawn it in the
            // CURRENT run" (a manager restart loses process ownership), it is still
            // the instance's own service and 关闭实例/退出 must actually stop it.
            if (port > 0 && !String.IsNullOrEmpty(_config.BridgeToken))
            {
                string resp = RuntimeBridgeClient.Shutdown(port, _config.BridgeToken);
                if (resp != null)
                {
                    FileLog.Info("WindowsBackend: bridge shutdown requested (" + resp + ")");
                    System.Threading.Thread.Sleep(1200); // give dsh a moment to exit
                }
            }
            int pid = ManagedPid;
            if (pid == 0 && port > 0 && PortInspector.IsListening(port))
            {
                // Still serving: stop the listener of THIS instance's port, but
                // only ever a verified dsh process (never a third-party program).
                int listener = PortInspector.GetListenerPid(port);
                if (listener > 0 && PortInspector.IsDshProcess(listener)) pid = listener;
            }
            if (pid > 0) DshLauncher.KillTree(pid);
            _proc = null;
        }

        public bool IsServiceUp(int port)
        {
            return PortInspector.IsListening(port);
        }

        public string GetWindowUrl(int port)
        {
            return DshWebAuth.WindowUrl(port);
        }

        public void RefreshRuntime(int port)
        {
            if (port <= 0 || String.IsNullOrEmpty(_config.BridgeToken)) return;
            if (DateTime.UtcNow.Subtract(_bridgeInfoAt) < BridgeInfoTtl) return;
            QueryBridgeInfo(port);
            if (_bridgeInfo == null)
                ScheduleFallbackProbe();
        }

        public string GetRuntimeSummary(int port)
        {
            if (_bridgeInfo != null) return _bridgeInfo.Summary;
            return _fallbackSummary;
        }

        /// <summary>Background fallback probe: `dsh --version` on Windows, cached
        /// 60 s so the 10 s heartbeat never blocks on a process spawn.</summary>
        private void ScheduleFallbackProbe()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _fallbackProbeRunning, 1, 0) != 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (DateTime.UtcNow.Subtract(_fallbackProbeAt) < FallbackProbeTtl) return;
                    string v = UpdateChecker.GetCurrentWindowsDshVersion();
                    _fallbackSummary = String.IsNullOrEmpty(v) ? String.Empty : "dsh " + v;
                    _fallbackProbeAt = DateTime.UtcNow;
                }
                catch (Exception ex) { FileLog.Error("WindowsBackend fallback probe: " + ex.Message); }
                finally { System.Threading.Interlocked.Exchange(ref _fallbackProbeRunning, 0); }
            });
        }

        /// <summary>
        /// Queries the runtime bridge once (no throttle) and caches the result.
        /// Returns null when the bridge is not reachable or the token is unset.
        /// </summary>
        public BridgeInfo QueryBridgeInfo(int port)
        {
            BridgeInfo info = RuntimeBridgeClient.Query(port, _config.BridgeToken);
            if (info == null) return null;
            _bridgeInfo = info;
            _bridgeInfoAt = DateTime.UtcNow;
            return info;
        }
    }
}
