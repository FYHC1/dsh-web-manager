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
        /// (the caller then runs UpdateBundleDsh for the in-place update).</summary>
        public static int UpdateWindowsDsh()
        {
            if (IsBundleDsh())
            {
                string dsh = DshLauncher.FindDshCommand();
                FileLog.Info("UpdateChecker: dsh is the offline-bundle built-in (" + dsh + "); in-place update required");
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

        /// <summary>In-place update of the offline-bundle dsh tree with the
        /// BUNDLED npm (npmmirror) — no global npm and no bundle re-install
        /// needed. Mirrors Build-Bundle.ps1: npm-install the latest dsh into a
        /// staging node_modules (--global-style), swap it over <root>\dsh, then
        /// rewrite the bin\dsh.cmd shim (the package's bin entry path can change
        /// between versions). The caller must stop managed dsh instances first:
        /// the swap fails while the tree is locked by a running dsh process
        /// (rollback restores the old tree). Returns 0 = updated, 1 = failed,
        /// 2 = bundle layout not recognized.</summary>
        public static int UpdateBundleDsh()
        {
            string dsh = DshLauncher.FindDshCommand();
            if (String.IsNullOrEmpty(dsh)) return 2;
            string binDir = Path.GetDirectoryName(dsh);
            string root = String.IsNullOrEmpty(binDir) ? null : Path.GetDirectoryName(binDir);
            if (String.IsNullOrEmpty(root)) return 2;
            string nodeExe = Path.Combine(root, "node", "node.exe");
            string npmCli = Path.Combine(root, "node", "node_modules", "npm", "bin", "npm-cli.js");
            string dshDir = Path.Combine(root, "dsh");
            if (!File.Exists(nodeExe) || !File.Exists(npmCli) || !Directory.Exists(dshDir)) return 2;

            // 1. Stage install — never touch the live tree until the new tree
            //    is complete (a failed download must leave dsh runnable).
            string stage = Path.Combine(root, "dsh-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(stage, "package.json"),
                    "{\"name\":\"dsh-bundle-stage\",\"private\":true}");
            }
            catch (Exception ex)
            {
                FileLog.Error("UpdateChecker.bundle-update: staging failed: " + ex.Message);
                TryDeleteDir(stage);
                return 1;
            }
            FileLog.Info("UpdateChecker: bundle in-place update via " + npmCli + " (npm install @deepseek-ai/dsh@latest, npmmirror, global-style)");
            CommandResult r = RunWindowsCommand(
                "cd /d \"" + stage + "\" && \"" + nodeExe + "\" \"" + npmCli + "\" install @deepseek-ai/dsh@latest --global-style --omit=dev --no-audit --no-fund --loglevel=error " + RegistryFlag,
                600000);
            if (r.ExitCode != 0 || !Directory.Exists(Path.Combine(stage, "node_modules", "@deepseek-ai", "dsh")))
            {
                FileLog.Error("UpdateChecker.bundle-update: npm install failed: " + (r.StandardError ?? r.StandardOutput ?? String.Empty));
                TryDeleteDir(stage);
                return 1;
            }

            // 2. Swap the trees. Directory.Move throws while any process holds
            //    handles inside the tree (a dsh instance the caller could not
            //    stop) — roll back and report.
            string oldDir = Path.Combine(root, "dsh.old");
            TryDeleteDir(oldDir);
            bool movedOld = false;
            try
            {
                Directory.Move(dshDir, oldDir);
                movedOld = true;
                Directory.Move(Path.Combine(stage, "node_modules"), dshDir);
            }
            catch (Exception ex)
            {
                FileLog.Error("UpdateChecker.bundle-update: swap failed (a dsh instance may still be running): " + ex.Message);
                if (movedOld && !Directory.Exists(dshDir)) { try { Directory.Move(oldDir, dshDir); } catch (Exception) { } }
                TryDeleteDir(stage);
                return 1;
            }
            TryDeleteDir(stage);
            TryDeleteDir(oldDir);

            // 3. Rewrite the shim from the NEW package.json bin field (the entry
            //    path can change between dsh versions). On any failure keep the
            //    existing shim — its target usually still exists.
            try
            {
                string rel = ReadDshBinEntry(Path.Combine(dshDir, "@deepseek-ai", "dsh", "package.json"));
                if (!String.IsNullOrEmpty(rel))
                {
                    string entry = Path.Combine(dshDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(entry))
                    {
                        File.WriteAllText(Path.Combine(binDir, "dsh.cmd"),
                            "@echo off\r\nsetlocal\r\n\"" + nodeExe + "\" \"" + entry + "\" %*\r\n",
                            new UTF8Encoding(false));
                        FileLog.Info("UpdateChecker.bundle-update: shim rewritten -> " + entry);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("UpdateChecker.bundle-update: shim rewrite failed (dsh.cmd kept): " + ex.Message);
            }
            FileLog.Info("UpdateChecker.bundle-update: OK");
            return 0;
        }

        /// <summary>dsh bin entry from a package.json (bin as string or {dsh:…}),
        /// via the same JavaScriptSerializer ManagerConfig uses.</summary>
        private static string ReadDshBinEntry(string packageJsonPath)
        {
            if (!File.Exists(packageJsonPath)) return String.Empty;
            object pkg = new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<object>(File.ReadAllText(packageJsonPath));
            Dictionary<string, object> rootObj = pkg as Dictionary<string, object>;
            if (rootObj == null) return String.Empty;
            object bin;
            if (!rootObj.TryGetValue("bin", out bin)) return String.Empty;
            string single = bin as string;
            if (single != null) return single;
            Dictionary<string, object> binMap = bin as Dictionary<string, object>;
            if (binMap == null) return String.Empty;
            object entry;
            return binMap.TryGetValue("dsh", out entry) ? entry as string : String.Empty;
        }

        private static void TryDeleteDir(string dir)
        {
            try
            {
                if (!String.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch (Exception ex) { FileLog.Error("UpdateChecker: delete " + dir + " failed: " + ex.Message); }
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
