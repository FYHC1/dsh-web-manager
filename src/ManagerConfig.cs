﻿﻿using System;
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
        public int WslPort { get; set; }          // wsl backend port (v2.1, per-backend port memory)
        public string WslDistro { get; set; }     // pinned WSL distro; empty = auto (v2.1)
        public string WslServiceMode { get; set; } // "wrapper" | "systemd" (v3.0); systemd unavailable -> auto fallback to wrapper
        public string LastWslDistro { get; set; } // remembered last working distro (v3.0, auto distro selection)
        public string Profile { get; set; }        // dsh profile name (default web)
        public string Version { get; set; }

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
            WslPort = 3080;
            WslDistro = String.Empty;
            WslServiceMode = "wrapper";
            LastWslDistro = String.Empty;
            Profile = "web";
            Version = "2.1.0";
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
                        if (loaded.WslPort <= 0) loaded.WslPort = 3080;
                        if (loaded.WslDistro == null) loaded.WslDistro = String.Empty;
                        if (String.IsNullOrEmpty(loaded.WslServiceMode)) loaded.WslServiceMode = "wrapper";
                        if (loaded.LastWslDistro == null) loaded.LastWslDistro = String.Empty;
                        if (String.IsNullOrEmpty(loaded.Profile)) loaded.Profile = "web";
                        if (String.IsNullOrEmpty(loaded.Version)) loaded.Version = "2.1.0";
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