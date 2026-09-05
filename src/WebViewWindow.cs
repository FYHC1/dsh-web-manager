using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshWebManager
{
    /// <summary>
    /// In-process WebView2 window backend. Each instance gets its own Form on its
    /// own STA message-pump thread; the window belongs to THIS process, so the
    /// taskbar button carries our whale icon and a per-port AppUserModelID keeps
    /// multi-instance windows from ever merging into one Edge button (the
    /// taskbar "merge buttons" setting no longer matters).
    ///
    /// The static API mirrors EdgeWindow's port-keyed contract; the manager
    /// reaches it through EdgeWindow.Mode dispatch, so ManagerService and
    /// TrayFrontend need no changes.
    /// </summary>
    public static class WebViewWindow
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<int, WebViewForm> Forms = new Dictionary<int, WebViewForm>();
        private static bool _runtimeProbed;
        private static bool _runtimeAvailable;

        /// <summary>true when a WebView2 Runtime is installed (probed once, cached).
        /// Requires WebView2Loader.dll next to the EXE (or under x64/x86).</summary>
        public static bool IsRuntimeAvailable()
        {
            if (_runtimeProbed) return _runtimeAvailable;
            try
            {
                string v = CoreWebView2Environment.GetAvailableBrowserVersionString();
                _runtimeAvailable = !String.IsNullOrEmpty(v);
                FileLog.Info("WebView2 runtime available: " + v);
            }
            catch (Exception ex)
            {
                FileLog.Info("WebView2 runtime not available, edge fallback: " + ex.Message);
                _runtimeAvailable = false;
            }
            _runtimeProbed = true;
            return _runtimeAvailable;
        }

        public static bool EnsureVisible(WindowConfig window, string dataDir, string url, int port)
        {
            WebViewForm existing = GetForm(port);
            if (existing != null && !existing.IsDisposed)
            {
                try { existing.BeginInvoke(new Action(existing.RestoreAndFocus)); }
                catch { }
                return false;
            }
            if (existing != null) Unregister(existing);

            // Create the form INSIDE the pump thread: a WinForms window belongs to
            // the thread that creates it, and EnsureCoreWebView2Async's continuations
            // post back through that thread's WindowsFormsSynchronizationContext.
            Thread t = new Thread(delegate()
            {
                WebViewForm form = new WebViewForm(url, port, dataDir, window);
                Register(port, form);
                Application.Run(form);
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Name = "WebViewWindow-" + port;
            t.Start();
            return true;
        }

        public static IntPtr FindAppWindow(int port)
        {
            WebViewForm form = GetForm(port);
            if (form == null || form.IsDisposed) return IntPtr.Zero;
            return form.WindowHandle;
        }

        /// <summary>Re-navigates the running window to `url` (no-op when the
        /// window is gone). Called once the dsh service turns reachable to
        /// recover windows that opened too early.</summary>
        public static void Renavigate(int port, string url)
        {
            WebViewForm form = GetForm(port);
            if (form == null || form.IsDisposed) return;
            try { form.BeginInvoke(new Action(delegate() { form.Renavigate(url); })); }
            catch (Exception) { } // pump thread gone: the window is closing anyway
        }

        public static void CloseWindow(int port)
        {
            WebViewForm form = GetForm(port);
            if (form == null || form.IsDisposed) return;
            IntPtr h = form.WindowHandle;
            if (h == IntPtr.Zero) return;
            try { Win32.PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception ex) { FileLog.Error("WebViewWindow.CloseWindow: " + ex.Message); }
        }

        /// <summary>No-op: the form carries the whale icon and per-port AUMID from creation.</summary>
        public static void ApplyIconToWindow(int port) { }

        /// <summary>No-op: the in-process WebView2 environment stays warm, reopening is fast.</summary>
        public static void Preheat(int port, string dataDir) { }

        public static void CaptureSize(int port, WindowConfig window, Action onChanged, DateTime now)
        {
            if (window == null) return;
            WebViewForm form = GetForm(port);
            if (form == null || form.IsDisposed) return;
            IntPtr h = form.WindowHandle;
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
                FileLog.Info("Window geometry saved (webview2): size=" + size + " pos=" + pos);
            }
        }

        /// <summary>Applies remembered geometry to a window handle (parity with EdgeWindow).</summary>
        public static void RestoreGeometry(IntPtr h, WindowConfig window)
        {
            if (h == IntPtr.Zero || window == null) return;
            int w, ht, x, y;
            bool hasSize = EdgeWindow.TryParseSize(window.Size, out w, out ht);
            bool hasPos = EdgeWindow.TryParsePosition(window.Position, out x, out y);
            if (!hasSize && !hasPos) return;
            if (Win32.IsIconic(h)) Win32.ShowWindow(h, Win32.SW_RESTORE);
            Win32.RECT r;
            if (!Win32.GetWindowRect(h, out r)) return;
            int px = hasPos ? x : r.Left;
            int py = hasPos ? y : r.Top;
            int pw = hasSize ? w : r.Width;
            int ph = hasSize ? ht : r.Height;
            if (pw < 100 || ph < 100) return;
            Win32.SetWindowPos(h, IntPtr.Zero, px, py, pw, ph, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
        }

        private static WebViewForm GetForm(int port)
        {
            lock (Sync)
            {
                WebViewForm form;
                if (Forms.TryGetValue(port, out form)) return form;
                return null;
            }
        }

        private static void Register(int port, WebViewForm form)
        {
            lock (Sync) { Forms[port] = form; }
        }

        internal static void Unregister(WebViewForm form)
        {
            if (form == null) return;
            lock (Sync)
            {
                foreach (KeyValuePair<int, WebViewForm> pair in Forms)
                {
                    if (pair.Value == form) { Forms.Remove(pair.Key); break; }
                }
            }
        }
    }

    /// <summary>One embedded WebView2 window. Owns its own STA message loop
    /// (Application.Run on a dedicated thread) so the manager may create and
    /// tear windows down from any thread.</summary>
    public sealed class WebViewForm : Form
    {
        private string _url;
        private readonly int _port;
        private readonly string _userDataFolder;
        private readonly WebView2 _webView;
        private IntPtr _handle;
        private readonly WindowConfig _window;

        public WebViewForm(string url, int port, string dataDir, WindowConfig window)
        {
            _url = url;
            _port = port;
            _window = window;
            string folder = String.IsNullOrEmpty(dataDir)
                ? Path.Combine(AppPaths.LocalAppData, "dsh-web-manager-browser")
                : dataDir;
            // Same per-port profile convention as EdgeWindow.Launch: one isolated
            // user-data folder per instance so windows never share state.
            _userDataFolder = folder + "-" + port;

            Text = "DeepSeek Harness WebUI";
            Icon = LoadWhaleIcon();
            MinimumSize = new Size(400, 300);
            StartPosition = FormStartPosition.Manual;
            RestoreRememberedGeometry();

            _webView = new WebView2();
            _webView.Dock = DockStyle.Fill;
            // Dark preset: avoids a white flash while the (dark) dsh WebUI loads.
            _webView.DefaultBackgroundColor = Color.FromArgb(11, 22, 34);
            Controls.Add(_webView);

            HandleCreated += delegate(object s, EventArgs e) { _handle = Handle; ApplyWindowIdentity(); ApplyTitleBarTheme(Color.FromArgb(11, 22, 34)); };
            Load += OnLoad;
            FormClosed += OnFormClosed;
        }

        /// <summary>The window handle, published by HandleCreated on the form thread.</summary>
        public IntPtr WindowHandle { get { return _handle; } }

        /// <summary>Navigates the running window to a new URL (used to recover a
        /// window that opened before the dsh service became reachable: the page
        /// it initially showed is a browser error or dsh's 401 body). If the
        /// WebView2 core is not initialized yet, the URL simply replaces the
        /// one OnLoad will navigate to.</summary>
        public void Renavigate(string url)
        {
            if (IsDisposed || String.IsNullOrEmpty(url)) return;
            _url = url;
            if (_webView.CoreWebView2 != null)
                _webView.CoreWebView2.Navigate(url);
        }

        public void RestoreAndFocus()
        {
            if (IsDisposed) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }

        private void RestoreRememberedGeometry()
        {
            int w, h, x, y;
            bool hasSize = EdgeWindow.TryParseSize(_window == null ? null : _window.Size, out w, out h);
            bool hasPos = EdgeWindow.TryParsePosition(_window == null ? null : _window.Position, out x, out y);
            if (hasSize) Size = new Size(w, h);
            else Size = new Size(1280, 800);
            if (hasPos) Location = new Point(x, y);
            else StartPosition = FormStartPosition.CenterScreen;
        }

        /// <summary>Per-port AppUserModelID: our own window, so the taskbar shows
        /// the whale icon and separate dsh windows never merge into one button.</summary>
        private void ApplyWindowIdentity()
        {
            try { EdgeWindow.ApplyAumidToWindow(_handle, "DeepSeekHarness.WebUI." + _port, AppPaths.IconFile); }
            catch (Exception ex) { FileLog.Error("WebViewForm AUMID: " + ex.Message); }
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, null);
                await _webView.EnsureCoreWebView2Async(env);
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                // Title bar follows the page theme: sampled after every navigation
                // so the frame blends with the dsh WebUI (dark by default).
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                _webView.CoreWebView2.Navigate(_url);
                FileLog.Info("WebView2 window navigated: " + _url + " (profile " + _userDataFolder + ")");
            }
            catch (Exception ex)
            {
                FileLog.Error("WebView2 init failed for port " + _port + ": " + ex.ToString());
            }
        }

        private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                CoreWebView2 core = _webView.CoreWebView2;
                if (core == null) return;
                string script = "(function(){function bg(el){var v=getComputedStyle(el).backgroundColor;"
                    + "if(v&&v!=='rgba(0, 0, 0, 0)'&&v!=='transparent')return v;return null;}"
                    + "return bg(document.body)||bg(document.documentElement)||'rgb(255, 255, 255)';})()";
                string json = await core.ExecuteScriptAsync(script);
                Match m = Regex.Match(json == null ? "" : json, @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
                if (!m.Success) return;
                ApplyTitleBarTheme(Color.FromArgb(
                    int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)));
            }
            catch (Exception ex)
            {
                FileLog.Error("WebView2 theme sample: " + ex.Message);
            }
        }

        /// <summary>Makes the title bar match the page background: immersive dark
        /// mode (Win10) plus exact caption/text colors (Win11 22000+; older DWMs
        /// reject the color attributes and keep the immersive look).</summary>
        private void ApplyTitleBarTheme(Color pageBg)
        {
            if (_handle == IntPtr.Zero || IsDisposed) return;
            try
            {
                bool dark = (pageBg.R * 299 + pageBg.G * 587 + pageBg.B * 114) / 1000 < 128;
                int flag = dark ? 1 : 0;
                bool flip = _lastTitleDark != flag;
                bool wasVisible = Visible;
                // Win10 does not repaint an already-shown frame when the immersive
                // flag flips at runtime (verified on 19045: hr=S_OK, no visual
                // change). Hiding before and re-showing after forces DWM to
                // rebuild the frame; imperceptible at ~100ms.
                if (flip && wasVisible)
                {
                    Win32.ShowWindow(_handle, Win32.SW_HIDE);
                    Thread.Sleep(60);
                }
                if (Win32.DwmSetWindowAttribute(_handle, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref flag, 4) != 0)
                    Win32.DwmSetWindowAttribute(_handle, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref flag, 4);
                int caption = pageBg.R | (pageBg.G << 8) | (pageBg.B << 16); // COLORREF 0x00BBGGRR
                Win32.DwmSetWindowAttribute(_handle, Win32.DWMWA_CAPTION_COLOR, ref caption, 4);
                int text = dark ? 0x00F0F0F0 : 0x00101010;
                Win32.DwmSetWindowAttribute(_handle, Win32.DWMWA_TEXT_COLOR, ref text, 4);
                _lastTitleDark = flag;
                if (flip && wasVisible)
                {
                    Win32.ShowWindow(_handle, Win32.SW_SHOW);
                    Win32.SetForegroundWindow(_handle);
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("ApplyTitleBarTheme: " + ex.Message);
            }
        }

        private int _lastTitleDark = -1;

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            try { _webView.Dispose(); } catch { }
            WebViewWindow.Unregister(this);
            FileLog.Info("WebView2 window closed (port " + _port + ")");
        }

        private static Icon LoadWhaleIcon()
        {
            try
            {
                if (File.Exists(AppPaths.IconFile)) return new Icon(AppPaths.IconFile);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(AppPaths.ExePath); }
            catch { return SystemIcons.Application; }
        }
    }
}
