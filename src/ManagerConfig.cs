using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace DshWebManager
{
    /// <summary>Persisted manager configuration (shared, visible from Windows and WSL).</summary>
    public sealed class WindowConfig
    {
        public string Size { get; set; }       // "WxH"
        public string Position { get; set; }   // "X,Y"
    }

    /// <summary>One independently managed dsh web instance (v3.0 multi-instance).</summary>
    public sealed class InstanceConfig
    {
        public string Id { get; set; }            // stable id, e.g. "default" / "wsl"
        public string Profile { get; set; }       // dsh profile name
        public string BackendType { get; set; }   // "windows" | "wsl"
        public int Port { get; set; }             // windows backend port
        public int WslPort { get; set; }          // wsl backend port
        public string WslDistro { get; set; }     // pinned distro ("" = auto)
        public string WslServiceMode { get; set; }// "wrapper" | "systemd"
        public WindowConfig Window { get; set; }
        public bool Enabled { get; set; }

        public bool IsWsl { get { return String.Equals(BackendType, "wsl", StringComparison.OrdinalIgnoreCase); } }
        public int EffectivePort { get { return IsWsl ? WslPort : Port; } }
        public void SetEffectivePort(int value) { if (IsWsl) WslPort = value; else Port = value; }
    }

    public sealed class ManagerConfig
    {
        public int Port { get; set; }             // windows backend port
        public bool AutoFallback { get; set; }
        public string DataDir { get; set; }
        public bool CloseStopsService { get; set; }
        public bool ExitKeepService { get; set; }
        public bool AutoStart { get; set; }
        public WindowConfig Window { get; set; }
        public string BackendType { get; set; }   // "windows" | "wsl" (v2.1)
        public string ActiveBackend { get; set; } // active backend for open/restart; remembered across restarts (v3.0)
        public string DefaultBackend { get; set; } // backend whose window opens on manager start (v3.0)
        public int WslPort { get; set; }          // wsl backend port (v2.1, per-backend port memory)
        public string WslDistro { get; set; }     // pinned WSL distro; empty = auto (v2.1)
        public string WslServiceMode { get; set; } // "wrapper" | "systemd" (v3.0); systemd unavailable -> auto fallback to wrapper
        public string LastWslDistro { get; set; } // remembered last working distro (v3.0, auto distro selection)
        public string BridgeToken { get; set; }   // dsh runtime bridge shared secret (v3.0)
        public string LastVersionCheckUtc { get; set; } // update check throttle (v3.0)
        public string LastKnownLatest { get; set; }     // last known latest dsh version (v3.0)
        public string LastManagerCheckUtc { get; set; } // manager self-update check throttle (v3.1)
        public string LastKnownManagerLatest { get; set; } // last known latest manager version (v3.1)
        public string ManagerUpdateApi { get; set; }    // release API override ("" = GitHub official; v3.1)
        public string Profile { get; set; }        // dsh profile name (default web)
        public string Version { get; set; }

        /// <summary>v3.0 multi-instance list; empty falls back to the legacy single
        /// instance fields (migration view in EffectiveInstances).</summary>
        public List<InstanceConfig> Instances { get; set; }

        /// <summary>Instances to run: the configured list, or the legacy fields as one.</summary>
        public List<InstanceConfig> EffectiveInstances
        {
            get
            {
                if (Instances != null && Instances.Count > 0)
                {
                    foreach (InstanceConfig inst in Instances)
                    {
                        if (inst.Window == null) inst.Window = new WindowConfig();
                        if (String.IsNullOrEmpty(inst.WslServiceMode)) inst.WslServiceMode = "wrapper";
                        if (String.IsNullOrEmpty(inst.Profile)) inst.Profile = "web";
                    }
                    return Instances;
                }
                List<InstanceConfig> legacy = new List<InstanceConfig>();
                InstanceConfig one = new InstanceConfig();
                one.Id = "default";
                one.Profile = Profile;
                one.BackendType = BackendType;
                one.Port = Port;
                one.WslPort = WslPort;
                one.WslDistro = WslDistro;
                one.WslServiceMode = WslServiceMode;
                one.Window = Window;
                one.Enabled = true;
                legacy.Add(one);
                return legacy;
            }
        }

        public bool IsWsl
        {
            get { return String.Equals(BackendType, "wsl", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>The port of the active backend (per-backend port memory).</summary>
        public int EffectivePort { get { return IsWsl ? WslPort : Port; } }

        public void SetEffectivePort(int value)
        {
            if (IsWsl) WslPort = value; else Port = value;
        }

        public ManagerConfig()
        {
            Port = 3080;
            AutoFallback = true;
            DataDir = String.Empty;
            CloseStopsService = false;
            ExitKeepService = false;
            AutoStart = false;
            Window = new WindowConfig();
            BackendType = "windows";
            ActiveBackend = "windows";
            DefaultBackend = "windows";
            WslPort = 3080;
            WslDistro = String.Empty;
            WslServiceMode = "wrapper";
            LastWslDistro = String.Empty;
            BridgeToken = String.Empty;
            LastVersionCheckUtc = String.Empty;
            LastKnownLatest = String.Empty;
            LastManagerCheckUtc = String.Empty;
            LastKnownManagerLatest = String.Empty;
            ManagerUpdateApi = String.Empty;
            Profile = "web";
            Version = "3.0.0";
            Instances = null; // null = legacy single-instance mode
        }

        public static ManagerConfig Load()
        {
            ManagerConfig cfg = new ManagerConfig();
            string path = AppPaths.ConfigFile;
            if (File.Exists(path))
            {
                try
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    ManagerConfig loaded = ser.Deserialize<ManagerConfig>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        if (loaded.Window == null) loaded.Window = new WindowConfig();
                        if (String.IsNullOrEmpty(loaded.BackendType)) loaded.BackendType = "windows";
                        if (String.IsNullOrEmpty(loaded.ActiveBackend)) loaded.ActiveBackend = "windows";
                        if (String.IsNullOrEmpty(loaded.DefaultBackend)) loaded.DefaultBackend = "windows";
                        if (loaded.WslPort <= 0) loaded.WslPort = 3080;
                        if (loaded.WslDistro == null) loaded.WslDistro = String.Empty;
                        if (String.IsNullOrEmpty(loaded.WslServiceMode)) loaded.WslServiceMode = "wrapper";
                        if (loaded.LastWslDistro == null) loaded.LastWslDistro = String.Empty;
                        if (loaded.BridgeToken == null) loaded.BridgeToken = String.Empty;
                        if (loaded.LastVersionCheckUtc == null) loaded.LastVersionCheckUtc = String.Empty;
                        if (loaded.LastKnownLatest == null) loaded.LastKnownLatest = String.Empty;
                        if (loaded.LastManagerCheckUtc == null) loaded.LastManagerCheckUtc = String.Empty;
                        if (loaded.LastKnownManagerLatest == null) loaded.LastKnownManagerLatest = String.Empty;
                        if (loaded.ManagerUpdateApi == null) loaded.ManagerUpdateApi = String.Empty;
                        if (String.IsNullOrEmpty(loaded.Profile)) loaded.Profile = "web";
                        if (String.IsNullOrEmpty(loaded.Version)) loaded.Version = "3.0.0";
                        return loaded;
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Error("Failed to read config, using defaults: " + ex.Message);
                }
            }
            return cfg;
        }

        public void Save()
        {
            try
            {
                AppPaths.EnsureDirectories();
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(this);
                File.WriteAllText(AppPaths.ConfigFile, json);
            }
            catch (Exception ex)
            {
                FileLog.Error("Failed to write config: " + ex.Message);
            }
        }

        /// <summary>Generates the shared runtime-bridge token once and persists it.</summary>
        public void EnsureBridgeToken()
        {
            if (!String.IsNullOrEmpty(BridgeToken)) return;
            BridgeToken = Guid.NewGuid().ToString("N");
            Save();
            FileLog.Info("ManagerConfig: generated runtime bridge token");
        }

        /// <summary>Migrates the legacy v1.x "window-size" file ("W,H" lines) into Window.Size.</summary>
        public void MigrateLegacyWindowSize()
        {
            if (!String.IsNullOrEmpty(Window.Size)) return;
            string legacy = AppPaths.LegacyWindowSizeFile;
            if (!File.Exists(legacy)) return;
            try
            {
                string text = File.ReadAllText(legacy).Trim();
                string[] parts = text.Split(new char[] { ',', 'x', 'X', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    int w, h;
                    if (int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h) && w > 400 && h > 300)
                        Window.Size = w + "x" + h;
                    FileLog.Info("Migrated legacy window-size: " + Window.Size);
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("Legacy window-size migration failed: " + ex.Message);
            }
        }
    }
}