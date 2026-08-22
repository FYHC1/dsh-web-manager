using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DshWebManager
{
    public enum WslServiceModeKind
    {
        Wrapper,   // v2.1: wsl-start.sh self-heal loop via wsl.exe
        Systemd    // v3.0: systemd --user unit running dsh in the foreground
    }

    /// <summary>
    /// Hosts dsh web inside a WSL distro, in one of two service modes:
    ///   Wrapper (v2.1): wsl.exe -d &lt;distro&gt; -- bash -lc '~/.dsh-webui/wsl-start.sh ...'
    ///     - the wsl.exe client tracks the script; script self-heals + writes a pidfile
    ///   Systemd (v3.0): systemd --user unit dsh-web-&lt;port&gt;.service
    ///     - dsh runs in the foreground, systemd Restart=on-failure heals it, journald logs
    /// The requested mode falls back to Wrapper when systemd is not available (no wsl --shutdown yet).
    /// Ownership: manager-launched = managed; an external dsh already serving the port = attached.
    /// </summary>
    public sealed class WslBackend : IServiceBackend
    {
        private readonly ManagerConfig _config;    // shared config (LastWslDistro / BridgeToken)
        private readonly InstanceConfig _instance; // per-instance config (distro / service mode)
        private Process _proc;
        private Process _keepalive;   // persistent wsl.exe client holding the WSL VM alive (systemd mode)
        private string _distro = String.Empty;
        private WslServiceModeKind _mode = WslServiceModeKind.Wrapper;
        private int _lastPort;
        private BridgeInfo _bridgeInfo;                 // cached runtime-bridge payload
        private DateTime _bridgeInfoAt = DateTime.MinValue;
        private static readonly TimeSpan BridgeInfoTtl = TimeSpan.FromSeconds(10);
        // Failure-path probe throttles: both IsServiceUp (WSL-side ss) and
        // IsWrapperAlive (systemd is-active) spawn wsl.exe processes; while a
        // service is down the heartbeat would otherwise call them every second.
        private DateTime _wslProbeAt = DateTime.MinValue;
        private bool _wslProbeResult;
        private static readonly TimeSpan WslProbeTtl = TimeSpan.FromSeconds(5);
        private DateTime _unitCheckAt = DateTime.MinValue;
        private bool _unitCheckResult;
        private static readonly TimeSpan UnitCheckTtl = TimeSpan.FromSeconds(3);

        public WslBackend(ManagerConfig config)
            : this(config, config == null || config.EffectiveInstances == null || config.EffectiveInstances.Count == 0 ? null : config.EffectiveInstances[0])
        {
        }

        public WslBackend(ManagerConfig shared, InstanceConfig instance)
        {
            _config = shared;
            _instance = instance;
        }

        public string BackendType { get { return "wsl"; } }

        public string Distro { get { return _distro; } }

        public WslServiceModeKind Mode { get { return _mode; } }

        /// <summary>Persists the working distro so auto selection survives a WSL restart.</summary>
        public void RememberDistro()
        {
            if (String.IsNullOrEmpty(_distro)) return;
            if (String.Equals(_config.LastWslDistro, _distro, StringComparison.OrdinalIgnoreCase)) return;
            _config.LastWslDistro = _distro;
            _config.Save();
            FileLog.Info("WslBackend: remembered working distro " + _distro);
        }

        public string Describe()
        {
            if (String.IsNullOrEmpty(_distro)) return "WSL";
            return _mode == WslServiceModeKind.Systemd
                ? "WSL (" + _distro + ", systemd)"
                : "WSL (" + _distro + ")";
        }

        public bool IsAvailable(out string error)
        {
            error = String.Empty;
            try
            {
                if (!File.Exists(WslTools.FindWslExe()))
                {
                    error = "未找到 wsl.exe（需要启用 WSL）";
                    return false;
                }
            }
            catch { }
            string distro;
            if (!WslTools.ResolveDistro(_instance.WslDistro, _config.LastWslDistro, out distro))
            {
                error = "未找到可用的 WSL 发行版（可在配置 wslDistro 中指定）";
                return false;
            }
            _distro = distro;
            ResolveMode();
            return true;
        }

        /// <summary>
        /// Picks the service mode: requested "systemd" when the distro is actually
        /// booted with systemd, otherwise falls back to the wrapper (never breaks).
        /// </summary>
        private void ResolveMode()
        {
            bool wantSystemd = String.Equals(_instance.WslServiceMode, "systemd", StringComparison.OrdinalIgnoreCase);
            if (wantSystemd && WslTools.SystemdAvailable(_distro))
            {
                _mode = WslServiceModeKind.Systemd;
                return;
            }
            if (wantSystemd)
                FileLog.Error("WslBackend: systemd requested but not available in " + _distro
                    + " (enable [boot] systemd in /etc/wsl.conf, then wsl --shutdown); falling back to wrapper");
            _mode = WslServiceModeKind.Wrapper;
        }

        public int ManagedPid
        {
            get
            {
                if (_mode == WslServiceModeKind.Systemd) return 0; // systemd owns the dsh process
                try { return _proc != null && !_proc.HasExited ? _proc.Id : 0; }
                catch { return 0; }
            }
        }

        public PortProbeResult ProbePort(int port)
        {
            if (!PortInspector.IsListening(port))
            {
                // Windows cannot see the port (localhost forwarding off). Still check
                // inside the distro: a running WSL dsh must ATTACH, not be treated as a
                // port conflict (which would shift the port and relaunch a second dsh).
                if (!String.IsNullOrEmpty(_distro) && WslTools.WslPortOwnerPid(_distro, port) > 0)
                    return WslTools.WslPortHasDsh(_distro, port)
                        ? PortProbeResult.DshServing
                        : PortProbeResult.Occupied;
                return PortProbeResult.Free;
            }
            if (IsWrapperAlive()) return PortProbeResult.DshServing;        // our own launch
            if (WslTools.WslPortHasDsh(_distro, port)) return PortProbeResult.DshServing; // external WSL dsh
            return PortProbeResult.Occupied;
        }

        public bool Start(int port, string profile)
        {
            if (String.IsNullOrEmpty(_distro))
            {
                string error;
                if (!IsAvailable(out error))
                {
                    FileLog.Error("WslBackend.Start: " + error);
                    return false;
                }
            }
            if (profile.IndexOfAny(new char[] { ' ', '\t' }) >= 0)
            {
                FileLog.Error("WslBackend.Start: profile with spaces is not supported in WSL mode: " + profile);
                return false;
            }
            _lastPort = port;
            EnsureBridgeToken();
            bool ok = _mode == WslServiceModeKind.Systemd
                ? StartSystemd(port, profile)
                : StartWrapper(port, profile);
            if (ok)
            {
                if (!String.Equals(_config.LastWslDistro, _distro, StringComparison.OrdinalIgnoreCase))
                {
                    // Remember the working distro so auto selection survives a WSL restart
                    // (a stopped default distro must not win over the real one).
                    _config.LastWslDistro = _distro;
                    _config.Save();
                }
                string ping = WslTools.BridgeQuery(BridgePort(port), _config.BridgeToken, "ping", 2500);
                if (ping != null)
                    FileLog.Info("WslBackend: runtime bridge ping ok (" + ping + ")");
                else
                    FileLog.Info("WslBackend: runtime bridge not reachable on port " + BridgePort(port) + " yet (plugin loads with dsh)");
            }
            return ok;
        }

        private int BridgePort(int port) { return port + 100; }

        private void EnsureBridgeToken()
        {
            _config.EnsureBridgeToken();
        }

        private bool StartSystemd(int port, string profile)
        {
            if (!WslTools.EnsureSystemdFiles(_distro, profile, port, BridgePort(port), _config.BridgeToken))
            {
                FileLog.Error("WslBackend.StartSystemd: failed to write unit files");
                return false;
            }
            string unit = "dsh-web-" + port + ".service";
            if (!WslTools.WaitSystemdUserReady(_distro, 30000))
            {
                FileLog.Error("WslBackend.StartSystemd: systemd user session not ready in " + _distro);
                return false;
            }
            WslTools.Systemctl(_distro, "daemon-reload", String.Empty);
            FileLog.Info("WslBackend: systemctl --user start " + unit + " (" + Describe() + ")");
            if (!WslTools.Systemctl(_distro, "start", unit))
            {
                FileLog.Error("WslBackend.StartSystemd: systemctl start failed (user manager running? try: loginctl enable-linger)");
                return false;
            }
            // WSL2 shuts the VM down when no wsl.exe client stays connected. systemd
            // mode only makes short-lived calls, so without a persistent client the VM
            // would cycle off/on and take the unit down with it. Keep one alive.
            StartKeepalive();
            return true;
        }

        private void StartKeepalive()
        {
            try
            {
                if (_keepalive != null && !_keepalive.HasExited) return;
                string logOut = Path.Combine(AppPaths.LogDir, "wsl-keepalive.out.log");
                string logErr = Path.Combine(AppPaths.LogDir, "wsl-keepalive.err.log");
                _keepalive = WslTools.StartWsl(_distro, "sleep", new string[] { "infinity" }, logOut, logErr);
                if (_keepalive != null)
                    FileLog.Info("WslBackend: keepalive wsl.exe client pid=" + _keepalive.Id + " holds the WSL VM alive");
            }
            catch (Exception ex)
            {
                FileLog.Error("WslBackend.StartKeepalive: " + ex.Message);
            }
        }

        private void StopKeepalive()
        {
            int pid = 0;
            try { if (_keepalive != null && !_keepalive.HasExited) pid = _keepalive.Id; }
            catch { }
            if (pid > 0) DshLauncher.KillTree(pid);
            _keepalive = null;
        }

        private bool StartWrapper(int port, string profile)
        {
            if (!WslTools.EnsureWslScript(_distro))
            {
                FileLog.Error("WslBackend.Start: failed to materialize wsl-start.sh in " + _distro);
                return false;
            }
            string cmd = "~/.dsh-webui/wsl-start.sh " + WslTools.BashQuote(profile) + " " + port
                + " " + BridgePort(port) + " " + WslTools.BashQuote(_config.BridgeToken);
            string logOut = Path.Combine(AppPaths.LogDir, "wsl-dsh.out.log");
            string logErr = Path.Combine(AppPaths.LogDir, "wsl-dsh.err.log");
            FileLog.Info("WslBackend: launching " + Describe() + " dsh on port " + port + " (profile " + profile + ")");
            _proc = WslTools.StartWsl(_distro, "bash", new string[] { "-lc", cmd }, logOut, logErr);
            if (_proc == null)
            {
                FileLog.Error("WslBackend.Start: wsl.exe did not start");
                return false;
            }
            FileLog.Info("WslBackend: wsl.exe wrapper pid=" + _proc.Id);
            return true;
        }

        public bool IsWrapperAlive()
        {
            if (_mode == WslServiceModeKind.Systemd)
            {
                // The unit is "the wrapper": while it stays active systemd is healing
                // the service; if the unit itself went away (e.g. the user systemd
                // manager restarted and dropped it), report dead so the controller
                // restarts it instead of waiting forever.
                // Throttled: this runs every heartbeat while the service is down
                // and each call spawns two wsl.exe processes (id -u + systemctl).
                DateTime now = DateTime.UtcNow;
                if (now.Subtract(_unitCheckAt) < UnitCheckTtl) return _unitCheckResult;
                bool alive = WslTools.SystemctlIsActive(_distro, _lastPort);
                _unitCheckAt = now;
                _unitCheckResult = alive;
                return alive;
            }
            try { return _proc != null && !_proc.HasExited; }
            catch { return false; }
        }

        public void Stop(int port)
        {
            if (_mode == WslServiceModeKind.Systemd)
            {
                string unit = "dsh-web-" + port + ".service";
                if (WslTools.SystemctlIsActive(_distro, port))
                {
                    FileLog.Info("WslBackend: systemctl --user stop " + unit);
                    WslTools.Systemctl(_distro, "stop", unit);
                }
                // Covers BOTH the unit we just stopped (wait for the port to
                // release) and an attached dsh that runs OUTSIDE our unit
                // (e.g. started manually or by an older manager): bridge
                // shutdown, then a targeted kill of the port owner - only
                // ever a verified dsh process.
                StopDshInsideDistro(port);
                StopKeepalive();
                _lastPort = 0;
                return;
            }
            // Graceful shutdown first: ask the in-dsh runtime bridge to terminate
            // cleanly (only a dsh carrying our token answers), then fall back to
            // the hard kill if it is not answering.
            if (port > 0 && !String.IsNullOrEmpty(_config.BridgeToken))
            {
                string resp = WslTools.BridgeQuery(BridgePort(port), _config.BridgeToken, "shutdown", 2000);
                if (resp != null)
                {
                    FileLog.Info("WslBackend: bridge shutdown requested (" + resp + ")");
                    System.Threading.Thread.Sleep(1200); // give dsh a moment to exit
                }
            }
            if (!String.IsNullOrEmpty(_distro))
            {
                // Ask the distro to stop ONLY this instance's launcher script
                // (TERM -> cleanup -> exit). Multi-instance safe: the pattern
                // anchors on the script name AND the per-instance port token, so
                // another instance's wrapper on a different port is never hit.
                // The [.] bracket trick prevents pkill from matching this very
                // command; the pidfile is per-port too (wsl-dsh-<port>.pid).
                string script = "pkill -TERM -f 'wsl-start[.]sh.* " + port + " ' 2>/dev/null; "
                    + "kill -TERM $(cat ~/.dsh-webui/wsl-dsh-" + port + ".pid 2>/dev/null) 2>/dev/null; true";
                CommandResult r = WslTools.RunCapture(_distro, "bash", new string[] { "-lc", script }, 8000);
                if (r.ExitCode != 0)
                    FileLog.Error("WslBackend.Stop: distro-side kill returned " + r.ExitCode);
                // Give the client a moment to exit on its own.
                System.Threading.Thread.Sleep(500);
            }
            StopDshInsideDistro(port);
            int pid = ManagedPid;
            if (pid > 0) DshLauncher.KillTree(pid);
            _proc = null;
        }

        /// <summary>Waits for the port inside the distro to release; if a dsh
        /// still owns it (attached service that ignored the graceful paths),
        /// kills exactly that process - and nothing else.</summary>
        private void StopDshInsideDistro(int port)
        {
            try
            {
                if (String.IsNullOrEmpty(_distro) || port <= 0) return;
                for (int i = 0; i < 10 && WslTools.WslPortOwnerPid(_distro, port) > 0; i++)
                    System.Threading.Thread.Sleep(300);
                int owner = WslTools.WslPortOwnerPid(_distro, port);
                if (owner <= 0) return;
                if (!WslTools.WslPortHasDsh(_distro, port))
                {
                    FileLog.Info("WslBackend.Stop: port " + port + " owner pid=" + owner + " is not dsh; leaving it alone");
                    return;
                }
                FileLog.Info("WslBackend.Stop: killing dsh pid=" + owner + " on port " + port + " in " + _distro);
                WslTools.RunCapture(_distro, "bash", new string[] { "-lc", "kill " + owner + " 2>/dev/null; true" }, 5000);
            }
            catch (Exception ex) { FileLog.Error("StopDshInsideDistro: " + ex.Message); }
        }

        /// <summary>
        /// Backend-aware liveness: the Windows probe works through localhost forwarding;
        /// when forwarding is off, fall back to the WSL-side socket table. The WSL-side
        /// probe spawns a wsl.exe process, so its result is cached for a few seconds
        /// (the heartbeat calls this every second while the service is down).
        /// </summary>
        public bool IsServiceUp(int port)
        {
            if (PortInspector.IsListening(port)) return true;
            if (String.IsNullOrEmpty(_distro)) return false;
            DateTime now = DateTime.UtcNow;
            if (now.Subtract(_wslProbeAt) < WslProbeTtl) return _wslProbeResult;
            bool up = WslTools.WslPortOwnerPid(_distro, port) > 0;
            _wslProbeAt = now;
            _wslProbeResult = up;
            return up;
        }

        /// <summary>
        /// Window URL for the WSL service. dsh only binds 127.0.0.1 inside WSL
        /// (0.0.0.0 is rejected for safety), so Windows can reach it only through
        /// localhost forwarding; when that is off, no URL is usable from Windows
        /// and an empty string is returned (the caller shows a hint instead of
        /// opening a window that cannot load).
        /// </summary>
        public string GetWindowUrl(int port)
        {
            if (PortInspector.IsListening(port))
                return "http://127.0.0.1:" + port + "/";
            FileLog.Info("WslBackend: port " + port + " not reachable from Windows (localhost forwarding off?)");
            return String.Empty;
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

        /// <summary>Throttled bridge refresh for the periodic heart-beat tick.</summary>
        public void RefreshRuntime(int port)
        {
            if (port <= 0 || String.IsNullOrEmpty(_config.BridgeToken)) return;
            if (DateTime.UtcNow.Subtract(_bridgeInfoAt) < BridgeInfoTtl) return;
            QueryBridgeInfo(port);
        }

        /// <summary>Rich runtime status from the cached bridge payload ("" when unknown).</summary>
        public string GetRuntimeSummary(int port)
        {
            return _bridgeInfo == null ? String.Empty : _bridgeInfo.Summary;
        }
    }
}
