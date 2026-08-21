using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DshWebManager
{
    /// <summary>
    /// v2.1: hosts dsh web inside a WSL distro through wsl.exe.
    ///   launch : wsl.exe -d &lt;distro&gt; -- bash -lc '~/.dsh-webui/wsl-start.sh &lt;profile&gt; &lt;port&gt;'
    /// The wsl.exe client process tracks the Linux command while it runs; the script
    /// itself self-heals (restart loop) and writes a pidfile. Stop() first asks the
    /// distro to kill the script (TERM), then force-kills the wsl.exe tree.
    /// Ownership: manager-launched = managed; a dsh already serving the port = attached
    /// (monitored, never killed).
    /// </summary>
    public sealed class WslBackend : IServiceBackend
    {
        private readonly ManagerConfig _config;
        private Process _proc;
        private string _distro = String.Empty;

        public WslBackend(ManagerConfig config)
        {
            _config = config;
        }

        public string BackendType { get { return "wsl"; } }

        public string Distro { get { return _distro; } }

        public string Describe()
        {
            return String.IsNullOrEmpty(_distro) ? "WSL" : "WSL (" + _distro + ")";
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
            if (!WslTools.ResolveDistro(_config.WslDistro, out distro))
            {
                error = "未找到可用的 WSL 发行版（可在配置 wslDistro 中指定）";
                return false;
            }
            _distro = distro;
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
            if (!PortInspector.IsListening(port))
            {
                // Windows cannot see the port; still make sure nothing serves it
                // inside the distro (localhost forwarding may be off).
                if (!String.IsNullOrEmpty(_distro) && WslTools.WslPortOwnerPid(_distro, port) > 0)
                    return PortProbeResult.Occupied;
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
            if (!WslTools.EnsureWslScript(_distro))
            {
                FileLog.Error("WslBackend.Start: failed to materialize wsl-start.sh in " + _distro);
                return false;
            }

            string cmd = "~/.dsh-webui/wsl-start.sh " + WslTools.BashQuote(profile) + " " + port;
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
            try { return _proc != null && !_proc.HasExited; }
            catch { return false; }
        }

        public void Stop()
        {
            if (!String.IsNullOrEmpty(_distro))
            {
                // Ask the distro to stop the launcher script (TERM -> cleanup -> exit).
                // The [.] bracket trick prevents pkill from matching this very command.
                string script = "pkill -TERM -f 'wsl-start[.]sh' 2>/dev/null; "
                    + "kill -TERM $(cat ~/.dsh-webui/wsl-dsh.pid 2>/dev/null) 2>/dev/null; true";
                CommandResult r = WslTools.RunCapture(_distro, "bash", new string[] { "-lc", script }, 8000);
                if (r.ExitCode != 0)
                    FileLog.Error("WslBackend.Stop: distro-side kill returned " + r.ExitCode);
                // Give the client a moment to exit on its own.
                System.Threading.Thread.Sleep(500);
            }
            int pid = ManagedPid;
            if (pid > 0) DshLauncher.KillTree(pid);
            _proc = null;
        }

        /// <summary>
        /// Backend-aware liveness: the Windows probe works through localhost forwarding;
        /// when forwarding is off, fall back to the WSL-side socket table.
        /// </summary>
        public bool IsServiceUp(int port)
        {
            if (PortInspector.IsListening(port)) return true;
            if (String.IsNullOrEmpty(_distro)) return false;
            return WslTools.WslPortOwnerPid(_distro, port) > 0;
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
    }
}
