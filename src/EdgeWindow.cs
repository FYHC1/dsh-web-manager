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
        private static readonly System.Collections.Generic.Dictionary<int, IntPtr> _aumidHwnds =
            new System.Collections.Generic.Dictionary<int, IntPtr>(); // port -> last handled hwnd
        private static readonly Guid PkeyAppUserModelFmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private const uint PidAppUserModelId = 5;
        private const uint PidRelaunchIcon = 2;
        private static List<EdgeProcInfo> _procCache;                 // cached msedge pid->cmdline snapshot
        private static DateTime _procCacheAt = DateTime.MinValue;
        private static readonly TimeSpan ProcCacheTtl = TimeSpan.FromSeconds(2);

        /// <summary>Resolves the Chromium-family browser used for the standalone
        /// app window: Microsoft Edge first, then Google Chrome, then the
        /// open-source Chromium build. All three speak the same --app/
        /// --user-data-dir window protocol and use the Chrome_WidgetWin_1 frame
        /// class, so launch, lookup, icon and geometry handling are identical.
        /// Returns null when none is installed.</summary>
        public static string FindBrowserExe()
        {
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string lAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            List<string> candidates = new List<string>();
            // 1. Microsoft Edge
            if (!String.IsNullOrEmpty(pf86)) candidates.Add(Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"));
            if (!String.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"));
            // 2. Google Chrome (per-machine and per-user installs)
            if (!String.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"));
            if (!String.IsNullOrEmpty(pf86)) candidates.Add(Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"));
            if (!String.IsNullOrEmpty(lAppData)) candidates.Add(Path.Combine(lAppData, "Google", "Chrome", "Application", "chrome.exe"));
            // 3. Open-source Chromium build
            if (!String.IsNullOrEmpty(lAppData)) candidates.Add(Path.Combine(lAppData, "Chromium", "Application", "chrome.exe"));
            if (!String.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "Chromium", "Application", "chrome.exe"));
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>Launches the browser app window for the given URL with the
        /// instance's remembered size/position (the per-instance WindowConfig,
        /// not the manager-level one: multi-instance windows each keep their own).
        /// Uses Edge when present, otherwise falls back to Chrome/Chromium.
        /// Returns the started browser process (for fast window detection).</summary>
        public static Process Launch(string url, int port, string dataDir, WindowConfig window)
        {
            string browser = FindBrowserExe();
            if (browser == null)
                throw new InvalidOperationException("未找到可用的浏览器：需要 Microsoft Edge、Google Chrome 或 Chromium 之一");

            // A dedicated, isolated browser profile is REQUIRED: without it Edge
            // merges the --app request into the default profile's running instance
            // (single-instance semantics), the app window never becomes a standalone
            // window, and the manager can neither find it nor set its taskbar icon.
            // Each instance gets its own profile dir (suffixed by port) so multiple
            // app windows never merge into one browser window.
            if (String.IsNullOrEmpty(dataDir))
                dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-web-manager-browser");
            dataDir = dataDir + "-" + port;
            string args = "--app=" + url + " --user-data-dir=\"" + dataDir + "\"";
            // Edge 150 IGNORES --window-size (verified: fresh profile with
            // --window-size=1500x800 still opens 945x1020) and always opens --app
            // windows at its own saved/default placement. We therefore launch
            // NORMALLY (visible immediately - no minimize-then-restore delay) and
            // apply the remembered geometry via SetWindowPos the moment the
            // window materializes (fast MainWindowHandle polling, ~150ms).
            // Position flags still apply; size flags are kept for other engines.
            if (window != null)
            {
                if (!String.IsNullOrEmpty(window.Size))
                    args += " --window-size=" + window.Size;
                if (!String.IsNullOrEmpty(window.Position))
                    args += " --window-position=" + window.Position;
            }

            FileLog.Info("Launching browser app window [" + Path.GetFileName(browser) + "]: " + args);
            // Do NOT kill lingering browser background processes: after a window
            // close, startup boost keeps a warm process for the profile.
            // Forwarding to it makes the next open nearly instant (a cold start
            // with this profile's extensions takes ~1s+). The size no longer
            // depends on the command line (the geometry hook sizes the hidden
            // window before it is shown), so a forwarded launch is fine - the
            // WMI fallback in the enforcement poll finds the window created by
            // the existing process.
            lock (_launchAt) { _launchAt[port] = DateTime.Now; } // hold CaptureSize off for a while
            ProcessStartInfo psi = new ProcessStartInfo(browser, args);
            psi.UseShellExecute = false;
            return Process.Start(psi);
        }

        /// <summary>Starts an idle, window-less browser process for the profile so the
        /// NEXT open forwards to a warm browser instead of a slower reload of the
        /// profile state (extensions/session - measured ~600ms extra on reopen).
        /// No-op when a process for the profile already exists. Called when an app
        /// window closes, giving the warm process time to settle before the user
        /// reopens. The relaxed FindAppWindow matches the forwarded window.</summary>
        public static void Preheat(int port, string dataDir)
        {
            try
            {
                if (String.IsNullOrEmpty(dataDir))
                    dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-web-manager-browser");
                dataDir = dataDir + "-" + port;
                string needle = "-" + port + "\"";
                _procCache = null;
                foreach (EdgeProcInfo info in GetEdgeProcesses())
                {
                    if (info.CommandLine.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (info.CommandLine.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        return; // already warm (visible window or preheated)
                }
                string browser = FindBrowserExe();
                if (browser == null) return;
                ProcessStartInfo psi = new ProcessStartInfo(browser,
                    "--user-data-dir=\"" + dataDir + "\" --no-startup-window");
                psi.UseShellExecute = false;
                Process.Start(psi);
                FileLog.Info("Preheat: started warm browser [" + Path.GetFileName(browser) + "] for " + dataDir);
            }
            catch (Exception ex) { FileLog.Error("Preheat: " + ex.Message); }
        }

        /// <summary>Per-port launch timestamps; CaptureSize is held off for a few
        /// seconds after a launch so a window that opened at the wrong size is
        /// never captured (that used to clobber the remembered geometry before
        /// the enforcement pass could correct it).</summary>
        private static readonly System.Collections.Generic.Dictionary<int, DateTime> _launchAt =
            new System.Collections.Generic.Dictionary<int, DateTime>();
        private static readonly TimeSpan CaptureHoldoff = TimeSpan.FromSeconds(6);

        /// <summary>One msedge/chrome process snapshot (pid + command line).</summary>
        private sealed class EdgeProcInfo
        {
            public uint Pid;
            public string CommandLine;
        }

        /// <summary>
        /// Cached snapshot of Edge/Chrome processes (pid + command line). The command
        /// line is read via WMI, which is slow and can occasionally hang or throw when
        /// the WMI service is busy; caching the snapshot for 2 s collapses the per-tick
        /// lookups into one query, and the EnumerationOptions timeout bounds a stuck call.
        /// </summary>
        private static List<EdgeProcInfo> GetEdgeProcesses()
        {
            if (_procCache != null && DateTime.Now.Subtract(_procCacheAt) < ProcCacheTtl)
                return _procCache;
            List<EdgeProcInfo> result = new List<EdgeProcInfo>();
            try
            {
                EnumerationOptions opts = new EnumerationOptions();
                opts.Timeout = TimeSpan.FromSeconds(3);
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\cimv2",
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe' OR Name = 'chrome.exe'",
                    opts))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object cv = obj["CommandLine"];
                        object pv = obj["ProcessId"];
                        if (cv == null || pv == null) continue;
                        uint pidv;
                        if (!uint.TryParse(pv.ToString(), out pidv)) continue;
                        EdgeProcInfo info = new EdgeProcInfo();
                        info.Pid = pidv;
                        info.CommandLine = cv.ToString();
                        result.Add(info);
                    }
                }
                _procCache = result;
                _procCacheAt = DateTime.Now;
            }
            catch (Exception ex)
            {
                FileLog.Error("GetEdgeProcesses failed: " + ex.Message);
                if (_procCache != null) return _procCache; // degrade to the last snapshot
            }
            return result;
        }

        /// <summary>
        /// Finds the Edge/Chrome app window serving exactly this port. The owning
        /// process is matched by the dedicated per-port user-data-dir suffix
        /// ("...-3081\""); command line must NOT contain "--type=" (renderer/GPU/
        /// utility helpers). The profile dir is exclusively ours, so no --app=
        /// /:port requirement is needed - that also matches a forwarded window
        /// created by a preheated (--no-startup-window) warm process.
        /// </summary>
        public static IntPtr FindAppWindow(int port)
        {
            try
            {
                // Only match windows whose user-data-dir carries the per-port suffix
                // ("...-3081\""). Stale windows from the old shared profile would
                // otherwise be "found" and restored instead of launching a real
                // standalone app window.
                string dataNeedle = "-" + port + "\"";
                List<uint> pids = new List<uint>();
                foreach (EdgeProcInfo info in GetEdgeProcesses())
                {
                    if (info.CommandLine.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (info.CommandLine.IndexOf(dataNeedle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    pids.Add(info.Pid);
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
            catch (Exception ex)
            {
                FileLog.Error("FindAppWindow failed: " + ex.Message);
            }
            return IntPtr.Zero;
        }

        /// <summary>Brings the app window to front, or launches a new one when absent.
        /// Returns true when a new window was launched. Edge 150 IGNORES every size
        /// channel for --app windows (--window-size, --start-minimized, the
        /// Preferences window_placement/app_window_placement keys - all verified
        /// empirically) and always shows the window at its own 945x1020 default
        /// first, so the remembered geometry is applied by a WinEvent hook that
        /// sizes the window WHILE STILL HIDDEN (EVENT_OBJECT_CREATE fires ~80ms
        /// before the SHOW), with a polling enforcement pass as fallback.</summary>
        public static bool EnsureVisible(WindowConfig window, string dataDir, string url, int port)
        {
            IntPtr h = FindAppWindow(port);
            if (h != IntPtr.Zero)
            {
                Win32.ShowWindow(h, Win32.SW_RESTORE);
                Win32.SetForegroundWindow(h);
                return false;
            }
            // Snapshot the remembered geometry NOW: the periodic CaptureSize can
            // overwrite the shared WindowConfig with the (possibly wrong) actual
            // size within seconds of launch, which would make the enforcement pass
            // compare against the clobbered value and no-op.
            WindowConfig snap = null;
            if (window != null)
            {
                snap = new WindowConfig();
                snap.Size = window.Size;
                snap.Position = window.Position;
            }
            // Arm the geometry hook BEFORE the launch so the very first window
            // creation event is caught (the window is created hidden and shown
            // ~80ms later; sizing it at creation means it is never visible at
            // the wrong size). The launched pid is added right after start.
            if (snap != null) ArmGeometryHook(port, snap);
            Process launched = Launch(url, port, dataDir, window);
            NoteLaunchedPid(launched == null ? 0 : launched.Id);
            ScheduleGeometryEnforce(port, snap, launched);
            return true;
        }

        // ------------------------------------------------------------------
        // Geometry hook: sizes the app window while it is still hidden.
        // An out-of-process WinEvent hook (no DLL injection) watches
        // EVENT_OBJECT_CREATE..EVENT_OBJECT_SHOW on its own message-pump
        // thread; when a target Edge process creates the real browser frame
        // (Chrome_WidgetWin_1, unowned, captioned, resizable - Edge's little
        // popup bubbles are owned WS_POPUP windows and are skipped), the
        // remembered geometry is applied via SetWindowPos before Chromium
        // shows the window, so the user never sees the default 945x1020.
        // ------------------------------------------------------------------
        private static readonly object _hookSync = new object();
        private static Win32.WinEventDelegate _hookDelegate;   // strong ref: a collected delegate kills the hook
        private static readonly System.Collections.Generic.List<GeometryJob> _hookJobs =
            new System.Collections.Generic.List<GeometryJob>();
        private static readonly TimeSpan HookMaxArmed = TimeSpan.FromSeconds(20);

        /// <summary>One armed launch: the port, its remembered geometry and the set
        /// of browser processes that may create the window (profile processes +
        /// the launched forwarder). Multiple launches (e.g. Windows and WSL opening
        /// almost simultaneously) each get their own job - the hook stays armed
        /// until every job has been applied or expired.</summary>
        private sealed class GeometryJob
        {
            public int Port;
            public DateTime ArmedAt = DateTime.Now;
            public WindowConfig Geom;
            public System.Collections.Generic.HashSet<uint> Pids = new System.Collections.Generic.HashSet<uint>();
        }

        /// <summary>Starts the hook for one launch. Pid set = every CURRENT
        /// browser process of the per-port profile (covers the preheated warm
        /// process that receives a forwarded launch); the freshly launched
        /// forwarder pid is added via NoteLaunchedPid right after start.</summary>
        private static void ArmGeometryHook(int port, WindowConfig geom)
        {
            try
            {
                bool startThread = false;
                lock (_hookSync)
                {
                    GeometryJob job = new GeometryJob();
                    job.Port = port;
                    job.Geom = geom;
                    string needle = "-" + port + "\"";
                    foreach (EdgeProcInfo info in GetEdgeProcesses())
                    {
                        if (info.CommandLine.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (info.CommandLine.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        job.Pids.Add(info.Pid);
                    }
                    if (_hookDelegate == null) _hookDelegate = OnGeometryWinEvent;
                    _hookJobs.Add(job);
                    if (_hookJobs.Count == 1) startThread = true;
                }
                if (startThread)
                {
                    System.Threading.Thread t = new System.Threading.Thread(HookMessageLoop);
                    t.IsBackground = true;
                    t.Name = "EdgeGeometryHook";
                    t.Start();
                }
                FileLog.Info("GeometryHook: armed for port " + port);
            }
            catch (Exception ex) { FileLog.Error("GeometryHook arm: " + ex.Message); }
        }

        /// <summary>Adds the just-launched forwarder process to the watched set
        /// (the most recent job - it belongs to the launch that just happened).</summary>
        public static void NoteLaunchedPid(int pid)
        {
            if (pid <= 0) return;
            lock (_hookSync)
            {
                if (_hookJobs.Count > 0) _hookJobs[_hookJobs.Count - 1].Pids.Add((uint)pid);
            }
        }

        /// <summary>Removes one port's armed job; the pump thread notices within
        /// a second and unhooks when no job is left. Idempotent/thread-safe.</summary>
        public static void DisarmGeometryHook(int port)
        {
            lock (_hookSync)
            {
                for (int i = _hookJobs.Count - 1; i >= 0; i--)
                    if (_hookJobs[i].Port == port) _hookJobs.RemoveAt(i);
            }
        }

        private static void HookMessageLoop()
        {
            IntPtr hook = IntPtr.Zero;
            try
            {
                Win32.WinEventDelegate cb;
                lock (_hookSync) { cb = _hookDelegate; }
                if (cb == null) return;
                hook = Win32.SetWinEventHook(Win32.EVENT_OBJECT_CREATE, Win32.EVENT_OBJECT_SHOW,
                    IntPtr.Zero, cb, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
                if (hook == IntPtr.Zero) return;
                while (true)
                {
                    bool armed = false;
                    lock (_hookSync)
                    {
                        for (int i = _hookJobs.Count - 1; i >= 0; i--)
                        {
                            if (DateTime.Now.Subtract(_hookJobs[i].ArmedAt) > HookMaxArmed)
                                _hookJobs.RemoveAt(i); // give up on this launch
                            else
                                armed = true;
                        }
                    }
                    if (!armed) break;
                    Win32.MsgWaitForMultipleObjects(0, null, false, 1000, Win32.QS_ALLINPUT);
                    Win32.MSG msg;
                    while (Win32.PeekMessage(out msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
                    {
                        Win32.TranslateMessage(ref msg);
                        Win32.DispatchMessage(ref msg);
                    }
                }
            }
            catch (Exception ex) { FileLog.Error("GeometryHook loop: " + ex.Message); }
            finally
            {
                if (hook != IntPtr.Zero) Win32.UnhookWinEvent(hook);
                lock (_hookSync) { _hookJobs.Clear(); }
            }
        }

        private static void OnGeometryWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (hwnd == IntPtr.Zero || idObject != Win32.OBJID_WINDOW) return;
                uint pid;
                Win32.GetWindowThreadProcessId(hwnd, out pid);
                GeometryJob job = null;
                lock (_hookSync)
                {
                    if (_hookJobs.Count == 0) return;
                    foreach (GeometryJob j in _hookJobs)
                        if (j.Pids.Contains(pid)) { job = j; break; }
                }
                if (job == null) return;
                WindowConfig geom = job.Geom;
                if (geom == null) return;
                if (Win32.GetAncestor(hwnd, Win32.GA_ROOT) != hwnd) return;
                System.Text.StringBuilder cls = new System.Text.StringBuilder(64);
                Win32.GetClassName(hwnd, cls, 64);
                if (!cls.ToString().StartsWith("Chrome_WidgetWin_1", StringComparison.Ordinal)) return;
                // The real app frame is an unowned captioned resizable window; Edge's
                // companion bubbles are owned WS_POPUP windows - never touch those.
                if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero) return;
                int style = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
                if ((style & Win32.WS_POPUP) != 0) return;
                if ((style & Win32.WS_CAPTION) != Win32.WS_CAPTION) return;
                if ((style & Win32.WS_THICKFRAME) == 0) return;

                if (eventType == Win32.EVENT_OBJECT_CREATE)
                {
                    // Chromium creates the frame hidden and shows it ~80ms later:
                    // apply the geometry now and the SHOW reveals the right size.
                    SizeHiddenWindow(hwnd, geom);
                    FileLog.Info("GeometryHook: sized window at CREATE (hidden)");
                }
                else // EVENT_OBJECT_SHOW: verify, correct if the size was missed
                {
                    Win32.RECT r;
                    if (!Win32.GetWindowRect(hwnd, out r)) return;
                    int mw, mh, mx, my;
                    bool hasSize = TryParseSize(geom.Size, out mw, out mh);
                    bool hasPos = TryParsePosition(geom.Position, out mx, out my);
                    bool sizeOk = !hasSize || (r.Width == mw && r.Height == mh);
                    bool posOk = !hasPos || (r.Left == mx && r.Top == my);
                    if (sizeOk && posOk)
                    {
                        FileLog.Info("GeometryHook: window shown at remembered size " + r.Width + "x" + r.Height);
                        return;
                    }
                    Win32.ShowWindow(hwnd, Win32.SW_HIDE);
                    SizeHiddenWindow(hwnd, geom);
                    Win32.ShowWindow(hwnd, Win32.SW_SHOW);
                    FileLog.Info("GeometryHook: corrected size at SHOW");
                }
            }
            catch (Exception ex) { FileLog.Error("GeometryHook event: " + ex.Message); }
        }

        /// <summary>SetWindowPos with the remembered geometry (visibility untouched).</summary>
        private static void SizeHiddenWindow(IntPtr h, WindowConfig window)
        {
            int mw, mh, mx, my;
            bool hasSize = TryParseSize(window.Size, out mw, out mh);
            bool hasPos = TryParsePosition(window.Position, out mx, out my);
            if (!hasSize && !hasPos) return;
            Win32.RECT r;
            if (!Win32.GetWindowRect(h, out r)) return;
            int px = hasPos ? mx : r.Left;
            int py = hasPos ? my : r.Top;
            int pw = hasSize ? mw : r.Width;
            int ph = hasSize ? mh : r.Height;
            if (pw < 100 || ph < 100) return;
            Win32.SetWindowPos(h, IntPtr.Zero, px, py, pw, ph, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
        }

        /// <summary>Background pass right after a launch: poll for the window to
        /// appear, then restore it at the remembered geometry (the window was
        /// <summary>Background pass right after a launch: poll for the window to
        /// appear, then apply the remembered geometry as soon as it does. The
        /// launched process may be a short-lived forwarder (Edge startup boost
        /// holds a warm background process for the profile, which creates the
        /// window), so each iteration checks BOTH the launched process's
        /// MainWindowHandle (fast) and a fresh-WMI FindAppWindow (catches the
        /// forwarded window). The snapshot keeps working even if CaptureSize
        /// later overwrites the shared WindowConfig with the wrong size.</summary>
        private static void ScheduleGeometryEnforce(int port, WindowConfig snapshot, Process launched)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    for (int i = 0; i < 80; i++) // up to ~12 s
                    {
                        System.Threading.Thread.Sleep(150);
                        IntPtr h = IntPtr.Zero;
                        if (launched != null)
                        {
                            try
                            {
                                using (Process p = Process.GetProcessById(launched.Id))
                                    h = p.MainWindowHandle;
                            }
                            catch { } // process exited / re-exec'd / forwarded
                        }
                        if (h == IntPtr.Zero)
                        {
                            // Fresh WMI snapshot: catches the window when the
                            // launch was forwarded to a warm Edge process.
                            _procCache = null;
                            h = FindAppWindow(port);
                        }
                        if (h != IntPtr.Zero)
                        {
                            RestoreGeometry(h, snapshot);
                            DisarmGeometryHook(port); // fallback pass took over / hook succeeded
                            return;
                        }
                    }
                    DisarmGeometryHook(port); // gave up: stop this port's pump job
                }
                catch (Exception ex) { FileLog.Error("RestoreGeometry pass: " + ex.Message); }
            });
        }

        /// <summary>Applies the remembered geometry to a window that just appeared.
        /// The window is launched normally (visible immediately - no minimize
        /// delay) at Edge's own default size; we SetWindowPos it to the remembered
        /// size/position within ~150 ms of creation, so the correction is barely
        /// perceived. If the window somehow came up minimized, un-minimize it via
        /// the hide/resize/show sequence so the wrong size is never displayed.</summary>
        public static void RestoreGeometry(IntPtr h, WindowConfig window)
        {
            if (h == IntPtr.Zero) return;
            int memW = 0, memH = 0, memX = 0, memY = 0;
            bool hasSize = window != null && TryParseSize(window.Size, out memW, out memH);
            bool hasPos = window != null && TryParsePosition(window.Position, out memX, out memY);
            if (!hasSize && !hasPos)
            {
                // No remembered geometry: just make sure it is not stuck minimized.
                if (Win32.IsIconic(h)) Win32.ShowWindow(h, Win32.SW_RESTORE);
                return;
            }
            if (Win32.IsIconic(h))
            {
                // Un-minimize without showing, resize, then show (never displays
                // the wrong size). Priming via GetWindowPlacement is required:
                // Chromium rejects zeroed WINDOWPLACEMENT structs (error 87).
                Win32.WINDOWPLACEMENT wp = new Win32.WINDOWPLACEMENT();
                wp.length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.WINDOWPLACEMENT));
                if (!Win32.GetWindowPlacement(h, ref wp))
                {
                    Win32.ShowWindow(h, Win32.SW_RESTORE);
                    return;
                }
                wp.showCmd = Win32.SW_HIDE;
                Win32.SetWindowPlacement(h, ref wp);
                int x = hasPos ? memX : wp.normalPosition.Left;
                int y = hasPos ? memY : wp.normalPosition.Top;
                int w = hasSize ? memW : (wp.normalPosition.Right - wp.normalPosition.Left);
                int ht = hasSize ? memH : (wp.normalPosition.Bottom - wp.normalPosition.Top);
                if (w > 100 && ht > 100)
                    Win32.SetWindowPos(h, IntPtr.Zero, x, y, w, ht, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
                Win32.ShowWindow(h, Win32.SW_SHOW);
                FileLog.Info("RestoreGeometry: restored " + w + "x" + ht + " @" + x + "," + y);
                return;
            }
            // Normal (visible) window: direct SetWindowPos - fast, no flicker.
            Win32.RECT r = new Win32.RECT();
            if (!Win32.GetWindowRect(h, out r)) return;
            int px = hasPos ? memX : r.Left;
            int py = hasPos ? memY : r.Top;
            int pw = hasSize ? memW : r.Width;
            int ph = hasSize ? memH : r.Height;
            if (pw < 100 || ph < 100) return;
            // The geometry hook usually already sized the hidden window before the
            // SHOW; skip the redundant SetWindowPos when nothing changed.
            if (pw == r.Width && ph == r.Height && px == r.Left && py == r.Top) return;
            Win32.SetWindowPos(h, IntPtr.Zero, px, py, pw, ph, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
            FileLog.Info("RestoreGeometry: applied " + pw + "x" + ph + " @" + px + "," + py);
        }

        private static bool TryParseSize(string size, out int w, out int h)
        {
            w = h = 0;
            if (String.IsNullOrEmpty(size)) return false;
            string[] parts = size.Split('x');
            return parts.Length == 2
                && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h)
                && w > 100 && h > 100;
        }

        private static bool TryParsePosition(string pos, out int x, out int y)
        {
            x = y = 0;
            if (String.IsNullOrEmpty(pos)) return false;
            string[] parts = pos.Split(',');
            return parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y);
        }

        /// <summary>Closes the app window for the port (WM_CLOSE), if present.</summary>
        public static void CloseWindow(int port)
        {
            try
            {
                IntPtr h = FindAppWindow(port);
                if (h == IntPtr.Zero) return;
                Win32.PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                FileLog.Info("Closing app window (port " + port + ")");
            }
            catch (Exception ex)
            {
                FileLog.Error("CloseWindow failed: " + ex.Message);
            }
        }

        /// <summary>Applies the DSH icon (32px big / 16px small) + AUMID to the app window.
        /// Done once per window handle: the icon/AUMID persist, so re-sending every
        /// tick was pure overhead (and the cross-process SendMessage could stall).
        /// The "already handled" guard is PER PORT: with several instances there are
        /// several windows, and a single remembered hwnd would ping-pong between
        /// them and re-apply every tick.</summary>
        public static void ApplyIconToWindow(int port)
        {
            IntPtr h = FindAppWindow(port);
            if (h == IntPtr.Zero) return;
            IntPtr last;
            if (_aumidHwnds.TryGetValue(port, out last) && last == h) return; // already applied
            _aumidHwnds[port] = h;
            EnsureIcons();
            if (_bigIcon != IntPtr.Zero)
                Win32.SendMessageW(h, Win32.WM_SETICON, new IntPtr(Win32.ICON_BIG), _bigIcon);
            if (_smallIcon != IntPtr.Zero)
                Win32.SendMessageW(h, Win32.WM_SETICON, new IntPtr(Win32.ICON_SMALL), _smallIcon);
            ApplyAumidToWindow(h, "DeepSeekHarness.WebUI", AppPaths.IconFile);
        }

        /// <summary>
        /// Sets PKEY_AppUserModel_ID and PKEY_AppUserModel_RelaunchIcon on the
        /// window's property store so the taskbar shows the DSH whale icon
        /// instead of the Edge default (WM_SETICON alone does not drive the
        /// taskbar button for Chromium app windows).
        /// </summary>
        public static void ApplyAumidToWindow(IntPtr hwnd, string aumid, string relaunchIcon)
        {
            if (hwnd == IntPtr.Zero || String.IsNullOrEmpty(aumid) || String.IsNullOrEmpty(relaunchIcon)) return;
            if (!System.IO.File.Exists(relaunchIcon)) return;
            try
            {
                Guid iid = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
                IntPtr storePtr;
                int hr = Win32.SHGetPropertyStoreForWindow(hwnd, ref iid, out storePtr);
                if (hr != 0 || storePtr == IntPtr.Zero) return;
                Win32.IPropertyStore store = (Win32.IPropertyStore)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(storePtr);
                try
                {
                    // PKEY_AppUserModel_ID
                    Win32.PropertyKey idKey = new Win32.PropertyKey { fmtid = PkeyAppUserModelFmtid, pid = PidAppUserModelId };
                    Win32.PropVariant pvId = new Win32.PropVariant { vt = Win32.VT_LPWSTR, pwszVal = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(aumid) };
                    store.SetValue(ref idKey, ref pvId);
                    // PKEY_AppUserModel_RelaunchIcon
                    Win32.PropertyKey iconKey = new Win32.PropertyKey { fmtid = PkeyAppUserModelFmtid, pid = PidRelaunchIcon };
                    Win32.PropVariant pvIcon = new Win32.PropVariant { vt = Win32.VT_LPWSTR, pwszVal = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(relaunchIcon) };
                    store.SetValue(ref iconKey, ref pvIcon);
                    int commitHr = store.Commit();
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pvId.pwszVal);
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pvIcon.pwszVal);
                    FileLog.Info("AUMID applied to window 0x" + hwnd.ToInt64().ToString("X") + " (aumid=" + aumid + ", hr=" + commitHr + ")");
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.Release(storePtr);
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(store);
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("AUMID apply failed: " + ex.Message);
            }
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
        /// <summary>
        /// Captures window size/position into the given WindowConfig (throttled by the
        /// caller) and invokes onChanged when the geometry actually changed.
        /// </summary>
        public static void CaptureSize(int port, WindowConfig window, Action onChanged, DateTime now)
        {
            if (window == null) return;
            // Right after a launch the window may still be materializing at the
            // wrong size (Edge saved placement beats the flags); capturing it now
            // would clobber the remembered geometry. Hold off for a few seconds so
            // the enforcement pass can correct the window first.
            lock (_launchAt)
            {
                DateTime launched;
                if (_launchAt.TryGetValue(port, out launched)
                    && now.Subtract(launched) < CaptureHoldoff)
                    return;
                // Holdoff over: prune the entry so the map does not grow with
                // every launch over the manager's lifetime.
                _launchAt.Remove(port);
            }
            IntPtr h = FindAppWindow(port);
            if (h == IntPtr.Zero) return;
            Win32.RECT r;
            if (!Win32.GetWindowRect(h, out r)) return;
            if (Win32.IsIconic(h)) return;
            if (r.Width < 200 || r.Height < 150) return; // ignore minimized/emerging windows
            string size = r.Width + "x" + r.Height;
            string pos = r.Left + "," + r.Top;
            if (window.Size != size || window.Position != pos)
            {
                window.Size = size;
                window.Position = pos;
                if (onChanged != null) onChanged();
                FileLog.Info("Window geometry saved: size=" + size + " pos=" + pos);
            }
        }
    }
}