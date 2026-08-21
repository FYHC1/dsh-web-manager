﻿﻿﻿using System;
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
        void Stop();                                 // stop the managed service (attached never touched)
        bool IsServiceUp(int port);                  // backend-aware liveness (wait-ready + heartbeat)
        string GetWindowUrl(int port);               // URL the Edge window opens (WSL IP when forwarding is off)
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
            _proc = DshLauncher.StartDshWeb(port, profile);
            return _proc != null;
        }

        public bool IsWrapperAlive()
        {
            try { return _proc != null && !_proc.HasExited; }
            catch { return false; }
        }

        public void Stop()
        {
            int pid = ManagedPid;
            if (pid > 0) DshLauncher.KillTree(pid);
            _proc = null;
        }

        public bool IsServiceUp(int port)
        {
            return PortInspector.IsListening(port);
        }

        public string GetWindowUrl(int port)
        {
            return "http://127.0.0.1:" + port + "/";
        }
    }
}
