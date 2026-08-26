using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace DshWebManager
{
    /// <summary>
    /// v3.0 update mechanism: current/latest dsh version check and one-click update
    /// of the dsh package via npm (npmmirror). Both WSL-side and Windows-native dsh
    /// are supported (v3.9): the manager picks the platform of the active backend.
    /// </summary>
    public static class UpdateChecker
    {
        private const string RegistryLatest = "https://registry.npmmirror.com/@deepseek-ai/dsh/latest";
        private const string RegistryFlag = "--registry=https://registry.npmmirror.com";

        /// <summary>Current dsh version installed inside the distro (or empty).</summary>
        public static string GetCurrentWslDshVersion(string distro)
        {
            if (String.IsNullOrWhiteSpace(distro)) return String.Empty;
            // dsh is usually installed via a version manager (fnm/nvm) whose PATH is
            // only exported in a login shell; a bare `wsl -d X dsh --version` misses
            // it and reported "dsh not found" for the user's FedoraLinux.
            CommandResult r = WslTools.RunCapture(distro, "bash", new string[] { "-lc", "dsh --version" }, 10000);
            if (r.ExitCode != 0) return String.Empty;
            return FirstVersionLine(r.StandardOutput ?? String.Empty);
        }

        /// <summary>First line that looks like a version (skips login-shell banner noise).</summary>
        private static string FirstVersionLine(string text)
        {
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string t = line.Trim();
                if (t.Length > 0 && Char.IsDigit(t[0]) && t.IndexOf('.') > 0)
                    return t;
            }
            return String.Empty;
        }

        /// <summary>Current dsh version installed on Windows ("" if not found/failed).</summary>
        public static string GetCurrentWindowsDshVersion()
        {
            string dsh = DshLauncher.FindDshCommand();
            if (String.IsNullOrEmpty(dsh)) return String.Empty;
            CommandResult r = RunWindowsCommand("\"" + dsh + "\" --version", 15000);
            if (r.ExitCode != 0) return String.Empty;
            string line = (r.StandardOutput ?? String.Empty).Trim();
            int nl = line.IndexOfAny(new char[] { '\r', '\n' });
            if (nl >= 0) line = line.Substring(0, nl).Trim();
            return line;
        }

        /// <summary>Latest published dsh version from the npmmirror registry (or empty).</summary>
        public static string GetLatestDshVersion()
        {
            try
            {
                WebRequest req = WebRequest.Create(RegistryLatest);
                req.Timeout = 10000;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    int idx = json.IndexOf("\"version\"", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        int start = json.IndexOf('"', idx + 9) + 1;
                        int end = json.IndexOf('"', start);
                        if (start > 0 && end > start) return json.Substring(start, end - start);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("UpdateChecker.latest: " + ex.Message);
            }
            return String.Empty;
        }

        /// <summary>Updates the WSL-side global dsh package. Returns true on success.</summary>
        public static bool UpdateWslDsh(string distro)
        {
            if (String.IsNullOrWhiteSpace(distro)) return false;
            FileLog.Info("UpdateChecker: updating dsh in " + distro + " (npm install -g @deepseek-ai/dsh@latest)");
            // npm lives behind fnm/nvm like dsh does - run inside a login shell.
            CommandResult r = WslTools.RunCapture(distro, "bash",
                new string[] { "-lc", "npm install -g @deepseek-ai/dsh@latest " + RegistryFlag }, 180000);
            if (r.ExitCode != 0)
                FileLog.Error("UpdateChecker.update failed: " + (r.StandardOutput ?? r.StandardError ?? String.Empty));
            return r.ExitCode == 0;
        }

        /// <summary>Updates the Windows-side global dsh package. Returns:
        /// 0 = updated, 1 = failed, 2 = dsh is the offline-bundle built-in
        /// (not an npm-global install; update by re-running the bundle installer).</summary>
        public static int UpdateWindowsDsh()
        {
            if (IsBundleDsh())
            {
                string dsh = DshLauncher.FindDshCommand();
                FileLog.Info("UpdateChecker: dsh is the offline-bundle built-in (" + dsh + "); npm -g would not update it");
                return 2;
            }
            string npm = FindNpmCommand();
            if (String.IsNullOrEmpty(npm)) return 1;
            FileLog.Info("UpdateChecker: updating Windows dsh via " + npm + " (npm install -g @deepseek-ai/dsh@latest)");
            CommandResult r = RunWindowsCommand("\"" + npm + "\" install -g @deepseek-ai/dsh@latest " + RegistryFlag, 180000);
            if (r.ExitCode != 0)
                FileLog.Error("UpdateChecker.update(win) failed: " + (r.StandardOutput ?? r.StandardError ?? String.Empty));
            return r.ExitCode == 0 ? 0 : 1;
        }

        /// <summary>true when the resolved dsh.cmd is the offline-bundle shim
        /// (<root>\bin\dsh.cmd with the dsh package tree at <root>\dsh).</summary>
        private static bool IsBundleDsh()
        {
            string dsh = DshLauncher.FindDshCommand();
            if (String.IsNullOrEmpty(dsh)) return false;
            string binDir = Path.GetDirectoryName(dsh);
            if (String.IsNullOrEmpty(binDir)) return false;
            string parent = Path.GetDirectoryName(binDir);
            if (String.IsNullOrEmpty(parent)) return false;
            return File.Exists(Path.Combine(parent, "node", "node.exe"))
                && Directory.Exists(Path.Combine(parent, "dsh", "@deepseek-ai", "dsh"));
        }

        /// <summary>npm.cmd owning the dsh install: next to dsh.cmd, in the
        /// sibling bundle node dir (<root>\node\npm.cmd), else PATH.</summary>
        private static string FindNpmCommand()
        {
            string dsh = DshLauncher.FindDshCommand();
            if (!String.IsNullOrEmpty(dsh))
            {
                string dir = Path.GetDirectoryName(dsh);
                if (!String.IsNullOrEmpty(dir))
                {
                    List<string> candidates = new List<string>();
                    candidates.Add(Path.Combine(dir, "npm.cmd"));
                    candidates.Add(Path.Combine(dir, "..", "node", "npm.cmd"));
                    candidates.Add(Path.Combine(dir, "node", "npm.cmd"));
                    foreach (string c in candidates)
                        if (File.Exists(c)) return c;
                }
            }
            return "npm.cmd"; // rely on PATH
        }

        /// <summary>pnpm.cmd for `dsh plugin` (dsh forwards to a bare `pnpm`): on
        /// PATH, else the corepack pnpm shim shipped inside the bundled/nvm node.
        /// Empty when not found — dsh then reports its own "pnpm not found" error.</summary>
        public static string FindPnpmCommand()
        {
            string fromPath = WhichWin("pnpm.cmd");
            if (fromPath != null) return fromPath;
            string dsh = DshLauncher.FindDshCommand();
            if (String.IsNullOrEmpty(dsh)) return String.Empty;
            string binDir = Path.GetDirectoryName(dsh);
            if (String.IsNullOrEmpty(binDir)) return String.Empty;
            List<string> candidates = new List<string>();
            // Bundle layout: <root>\bin\dsh.cmd, shims under <root>\node\node_modules\corepack\shims,
            // and newer bundles ship a relocatable pnpm.cmd beside node.exe (<root>\node\pnpm.cmd).
            // nvm layout: <nodejs>\node_modules\corepack\shims (and <nodejs>\dsh.cmd).
            candidates.Add(Path.Combine(binDir, "pnpm.cmd"));
            candidates.Add(Path.Combine(binDir, "node_modules", "corepack", "shims", "pnpm.cmd"));
            string parent = Path.GetDirectoryName(binDir);
            if (!String.IsNullOrEmpty(parent))
            {
                candidates.Add(Path.Combine(parent, "node", "pnpm.cmd"));
                candidates.Add(Path.Combine(parent, "node", "node_modules", "corepack", "shims", "pnpm.cmd"));
                candidates.Add(Path.Combine(parent, "node_modules", "corepack", "shims", "pnpm.cmd"));
            }
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return String.Empty;
        }

        private static string WhichWin(string exe)
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

        /// <summary>Runs a Windows command line through cmd.exe (hidden) and captures
        /// output without pipe deadlock (both streams drained on background threads).
        /// `innerCommand` is the already-quoted command, e.g. "\"C:\...\dsh.cmd\" --version".</summary>
        public static CommandResult RunWindowsCommand(string innerCommand, int timeoutMs)
        {
            return RunWindowsCommand(innerCommand, timeoutMs, null);
        }

        /// <summary>Overload with `extraPathDir`: a directory prepended to the
        /// child's PATH (so `dsh plugin` can find pnpm/corepack shims) without
        /// touching the user's environment.</summary>
        public static CommandResult RunWindowsCommand(string innerCommand, int timeoutMs, string extraPathDir)
        {
            CommandResult result = new CommandResult();
            result.ExitCode = -1;
            result.StandardOutput = String.Empty;
            result.StandardError = String.Empty;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                psi.Arguments = "/d /s /c \"" + innerCommand + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                if (!String.IsNullOrEmpty(extraPathDir))
                {
                    string existing = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
                    psi.EnvironmentVariables["PATH"] = extraPathDir + ";" + existing;
                }
                using (Process process = new Process())
                {
                    process.StartInfo = psi;
                    if (!process.Start()) return result;
                    string stdout = String.Empty;
                    string stderr = String.Empty;
                    Thread tOut = new Thread(delegate() { try { stdout = process.StandardOutput.ReadToEnd(); } catch { } });
                    Thread tErr = new Thread(delegate() { try { stderr = process.StandardError.ReadToEnd(); } catch { } });
                    tOut.IsBackground = true;
                    tErr.IsBackground = true;
                    tOut.Start();
                    tErr.Start();
                    bool exited = process.WaitForExit(timeoutMs);
                    if (!exited)
                    {
                        DshLauncher.KillTree(process.Id);
                        process.WaitForExit(3000);
                        result.TimedOut = true;
                    }
                    tOut.Join(3000);
                    tErr.Join(3000);
                    result.ExitCode = exited ? process.ExitCode : -1;
                    result.StandardOutput = stdout ?? String.Empty;
                    result.StandardError = stderr ?? String.Empty;
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("UpdateChecker.RunWindowsCommand: " + ex.Message);
            }
            return result;
        }
    }
}
