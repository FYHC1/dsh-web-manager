using System;
using System.IO;
using System.Reflection;

namespace DshWebManager
{
    /// <summary>File and directory layout for the manager.</summary>
    public static class AppPaths
    {
        // %LOCALAPPDATA%\dsh-web-manager\app   -- application files (EXE, assets)
        // %LOCALAPPDATA%\dsh-web-manager\logs  -- rolling log
        // %USERPROFILE%\.dsh-webui\            -- shared mutable state (visible from WSL as /mnt/c/...)
        public static string LocalAppData
        {
            get
            {
                string v = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return String.IsNullOrEmpty(v) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local") : v;
            }
        }

        public static string DataRoot { get { return Path.Combine(LocalAppData, "dsh-web-manager"); } }
        public static string InstallRoot { get { return Path.Combine(DataRoot, "app"); } }
        public static string LogDir { get { return Path.Combine(DataRoot, "logs"); } }
        public static string LogFile { get { return Path.Combine(LogDir, "manager.log"); } }

        /// <summary>Shared config directory, visible from both Windows and WSL.</summary>
        public static string SharedDir
        {
            get
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, ".dsh-webui");
            }
        }

        public static string ConfigFile { get { return Path.Combine(SharedDir, "config.json"); } }
        public static string LegacyWindowSizeFile { get { return Path.Combine(SharedDir, "window-size"); } }
        public static string IconFile { get { return Path.Combine(SharedDir, "dsh-webui.ico"); } }

        public static string ExePath
        {
            get
            {
                return Assembly.GetEntryAssembly() == null
                    ? Path.Combine(InstallRoot, "dsh-web-manager.exe")
                    : Assembly.GetEntryAssembly().Location;
            }
        }

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(InstallRoot);
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(SharedDir);
        }
    }
}