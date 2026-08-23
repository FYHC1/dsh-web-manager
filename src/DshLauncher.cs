using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace DshWebManager
{
    /// <summary>Resolves and launches the `dsh` CLI (hidden) and kills process trees.</summary>
    public static class DshLauncher
    {
        /// <summary>Absolute path to the dsh CLI entry (dsh.cmd/dsh.exe), set from
        /// config.DshCommand by ManagerService. Empty = resolve via PATH / known
        /// layouts. Lets the offline bundle pin the bundled dsh instead of a
        /// global install.</summary>
        public static string DshCommandOverride { get; set; }

        public static string FindDshCommand()
        {
            List<string> candidates = new List<string>();
            // 0. Config override (offline bundle): exact file, highest priority.
            string ov = DshCommandOverride;
            if (!String.IsNullOrEmpty(ov)) candidates.Add(ov);
            // 1. Resolve via PATH ("dsh.cmd" from nvm-windows / npm global).
            string fromPath = Which("dsh.cmd");
            if (fromPath != null) candidates.Add(fromPath);
            // 2. Common nvm-windows layouts.
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!String.IsNullOrEmpty(programFiles))
                candidates.Add(Path.Combine(programFiles, "nvm4w", "nodejs", "dsh.cmd"));
            if (!String.IsNullOrEmpty(programFiles))
                candidates.Add(Path.Combine(programFiles, "nvm", "nodejs", "dsh.cmd"));
            if (!String.IsNullOrEmpty(home))
                candidates.Add(Path.Combine(home, "AppData", "Roaming", "npm", "dsh.cmd"));
            foreach (string c in candidates)
                if (!String.IsNullOrEmpty(c) && File.Exists(c)) return c;
            return null;
        }

        private static string Which(string exe)
        {
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dir in pathEnv.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string full = Path.Combine(dir.Trim(), exe);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Starts `dsh --profile &lt;profile&gt; --host 127.0.0.1 --port N` hidden.
        /// Output is captured via async stream reading into the manager log dir.
        /// Returns the cmd.exe wrapper process (the real node process is a child).
        /// </summary>
        public static Process StartDshWeb(int port, string profile, string bridgeToken)
        {
            string dsh = FindDshCommand();
            if (dsh == null) throw new InvalidOperationException("dsh command not found. Install dsh and update PATH.");
            FileLog.Info("Starting dsh web with: " + dsh + " --profile " + profile + " --host 127.0.0.1 --port " + port);

            string logOut = Path.Combine(AppPaths.LogDir, "dsh-web.out.log");
            string logErr = Path.Combine(AppPaths.LogDir, "dsh-web.err.log");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            // Classic cmd quoting:  cmd /d /s /c ""<path>\dsh.cmd" --profile ... --port N --no-open"
            // --no-open: dsh web opens the default browser by default; the manager opens
            // its own standalone --app window, so suppress dsh's browser launch.
            psi.Arguments = "/d /s /c \"\"" + dsh + "\" --profile " + profile
                + " --host 127.0.0.1 --port " + port + " --no-open\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.WorkingDirectory = AppPaths.InstallRoot;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            // v3.0 runtime bridge: the in-dsh plugin listens on port+100 and reports
            // authoritative status (dsh version, node, uptime) + graceful shutdown.
            if (!String.IsNullOrEmpty(bridgeToken))
            {
                psi.EnvironmentVariables["DSH_BRIDGE_PORT"] = (port + 100).ToString();
                psi.EnvironmentVariables["DSH_BRIDGE_TOKEN"] = bridgeToken;
            }
            psi.EnvironmentVariables["DSH_PROFILE"] = profile;
            psi.EnvironmentVariables["DSH_WEB_PORT"] = port.ToString();
            psi.EnvironmentVariables["DSH_WEB_HOST"] = "127.0.0.1";

            Process p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += delegate(object sender, System.Diagnostics.DataReceivedEventArgs e)
            {
                if (e.Data != null) FileLog.AppendLine(logOut, e.Data);
            };
            p.ErrorDataReceived += delegate(object sender, System.Diagnostics.DataReceivedEventArgs e)
            {
                if (e.Data != null) FileLog.AppendLine(logErr, e.Data);
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            FileLog.Info("dsh web launched, wrapper pid=" + p.Id + ", port=" + port);
            return p;
        }

        /// <summary>Kills a process tree via taskkill /T /F.</summary>
        public static void KillTree(int pid)
        {
            if (pid <= 0) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        p.WaitForExit(5000);
                        FileLog.Info("taskkill /PID " + pid + " /T /F exit=" + p.ExitCode);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("KillTree failed for pid=" + pid + ": " + ex.Message);
            }
        }
    }
}