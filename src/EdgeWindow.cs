using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;

namespace DshWebManager
{
    /// <summary>
    /// Owns the Edge "--app" window: launch, port-scoped lookup, icon application (WM_SETICON)
    /// and size/position capture. HICONs are loaded once and reused for the process lifetime.
    /// </summary>
    public static class EdgeWindow
    {
        private static IntPtr _bigIcon;
        private static IntPtr _smallIcon;
        private static string _iconSource = String.Empty;

        public static string FindEdgeExe()
        {
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            List<string> candidates = new List<string>();
            if (!String.IsNullOrEmpty(pf86)) candidates.Add(Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"));
            if (!String.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"));
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>Launches the Edge app window for the given port with remembered size/position.</summary>
        public static void Launch(ManagerConfig config, int port)
        {
            string edge = FindEdgeExe();
            if (edge == null) throw new InvalidOperationException("Microsoft Edge was not found.");

            string url = "http://127.0.0.1:" + port + "/";
            string args = "--app=" + url;
            if (!String.IsNullOrEmpty(config.DataDir))
                args += " --user-data-dir=\"" + config.DataDir + "\"";
            if (!String.IsNullOrEmpty(config.Window.Size))
                args += " --window-size=" + config.Window.Size;
            if (!String.IsNullOrEmpty(config.Window.Position))
                args += " --window-position=" + config.Window.Position;

            FileLog.Info("Launching Edge app window: " + args);
            ProcessStartInfo psi = new ProcessStartInfo(edge, args);
            psi.UseShellExecute = false;
            Process.Start(psi);
        }

        /// <summary>
        /// Finds the Edge/Chrome app window serving exactly this port. Strictly scoped:
        /// command line must contain "--app=" and ":PORT" and must NOT contain "--type="
        /// (which marks internal renderer/helper processes), so other Edge windows are never touched.
        /// </summary>
        public static IntPtr FindAppWindow(int port)
        {
            try
            {
                string needle = ":" + port;
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe' OR Name = 'chrome.exe'"))
                {
                    List<uint> pids = new List<uint>();
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object cv = obj["CommandLine"];
                        if (cv == null) continue;
                        string cmd = cv.ToString();
                        if (cmd.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (cmd.IndexOf("--app=", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (cmd.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        object pv = obj["ProcessId"];
                        uint pidv;
                        if (pv != null && uint.TryParse(pv.ToString(), out pidv)) pids.Add(pidv);
                    }
                    foreach (uint pid in pids)
                    {
                        try
                        {
                            using (Process proc = Process.GetProcessById((int)pid))
                            {
                                if (proc.MainWindowHandle != IntPtr.Zero)
                                    return proc.MainWindowHandle;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("FindAppWindow failed: " + ex.Message);
            }
            return IntPtr.Zero;
        }

        /// <summary>Brings the app window to front, or launches a new one when absent.</summary>
        public static void EnsureVisible(ManagerConfig config, int port)
        {
            IntPtr h = FindAppWindow(port);
            if (h != IntPtr.Zero)
            {
                Win32.ShowWindow(h, Win32.SW_RESTORE);
                Win32.SetForegroundWindow(h);
                return;
            }
            Launch(config, port);
        }

        /// <summary>Applies the DSH icon (32px taskbar big / 16px small) to the app window.</summary>
        public static void ApplyIconToWindow(int port)
        {
            IntPtr h = FindAppWindow(port);
            if (h == IntPtr.Zero) return;
            EnsureIcons();
            if (_bigIcon != IntPtr.Zero)
                Win32.SendMessageW(h, Win32.WM_SETICON, new IntPtr(Win32.ICON_BIG), _bigIcon);
            if (_smallIcon != IntPtr.Zero)
                Win32.SendMessageW(h, Win32.WM_SETICON, new IntPtr(Win32.ICON_SMALL), _smallIcon);
        }

        private static void EnsureIcons()
        {
            string ico = AppPaths.IconFile;
            if (_bigIcon != IntPtr.Zero && _smallIcon != IntPtr.Zero && _iconSource == ico) return;
            // (Re)load once per source file; icons are session-global USER objects and stay valid
            // while this long-running process is alive.
            if (_bigIcon != IntPtr.Zero) Win32.DestroyIcon(_bigIcon);
            if (_smallIcon != IntPtr.Zero) Win32.DestroyIcon(_smallIcon);
            _bigIcon = Win32.LoadAppIcon(ico, false);
            _smallIcon = Win32.LoadAppIcon(ico, true);
            _iconSource = ico;
            if (_bigIcon == IntPtr.Zero) FileLog.Error("Could not load big icon from: " + ico);
            if (_smallIcon == IntPtr.Zero) FileLog.Error("Could not load small icon from: " + ico);
        }

        /// <summary>Captures window size/position into config (throttled) and persists on change.</summary>
        public static void CaptureSize(int port, ManagerConfig config, DateTime now)
        {
            IntPtr h = FindAppWindow(port);
            if (h == IntPtr.Zero) return;
            Win32.RECT r;
            if (!Win32.GetWindowRect(h, out r)) return;
            if (Win32.IsIconic(h)) return;
            if (r.Width < 200 || r.Height < 150) return; // ignore minimized/emerging windows
            string size = r.Width + "x" + r.Height;
            string pos = r.Left + "," + r.Top;
            if (config.Window.Size != size || config.Window.Position != pos)
            {
                config.Window.Size = size;
                config.Window.Position = pos;
                config.Save();
                FileLog.Info("Window geometry saved: size=" + size + " pos=" + pos);
            }
        }
    }
}