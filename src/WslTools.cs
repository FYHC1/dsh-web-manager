﻿﻿﻿﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace DshWebManager
{
    public sealed class CommandResult
    {
        public int ExitCode;
        public bool TimedOut;
        public string StandardOutput;
        public string StandardError;
    }

    public sealed class WslDistroState
    {
        public string Name { get; set; }
        public string State { get; set; }
        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// wsl.exe interop: distro discovery/selection, short command capture and
    /// long-running process launch. All Linux commands are invoked as
    ///   wsl.exe -d &lt;distro&gt; -- bash -lc '&lt;command&gt;'
    /// with every argument individually quoted (CommandLineToArgvW compatible).
    /// </summary>
    public static class WslTools
    {
        public static string FindWslExe()
        {
            try
            {
                string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string path = Path.Combine(system32, "wsl.exe");
                if (File.Exists(path)) return path;
            }
            catch { }
            return "wsl.exe";
        }

        // ---------------------------------------------------------------- distros

        public static List<string> DetectDistros()
        {
            List<string> distros = new List<string>();
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = FindWslExe();
                    process.StartInfo.Arguments = "--list --quiet";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.StandardOutputEncoding = Encoding.Unicode; // wsl.exe emits UTF-16LE
                    if (!process.Start()) return distros;
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(10000);
                    if (process.ExitCode != 0) return distros;
                    foreach (string value in output.Replace("\0", String.Empty).Split(
                        new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string distro = value.Trim();
                        if (distro.Length > 0 && !distros.Contains(distro)) distros.Add(distro);
                    }
                }
            }
            catch { }
            return distros;
        }

        public static List<WslDistroState> DetectDistroStates()
        {
            List<WslDistroState> states = new List<WslDistroState>();
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = FindWslExe();
                    process.StartInfo.Arguments = "--list --verbose";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.StandardOutputEncoding = Encoding.Unicode;
                    if (!process.Start()) return states;
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(10000);
                    if (process.ExitCode != 0) return states;
                    foreach (string value in output.Replace("\0", String.Empty).Split(
                        new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = value.Trim();
                        if (line.Length == 0) continue;
                        bool isDefault = false;
                        if (line[0] == '*')
                        {
                            isDefault = true;
                            line = line.Substring(1).Trim();
                        }
                        string[] columns = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (columns.Length < 3) continue;
                        bool header = (String.Equals(columns[0], "NAME", StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(columns[0], "名称", StringComparison.OrdinalIgnoreCase)) &&
                            (String.Equals(columns[columns.Length - 2], "STATE", StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(columns[columns.Length - 2], "状态", StringComparison.OrdinalIgnoreCase)) &&
                            (String.Equals(columns[columns.Length - 1], "VERSION", StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(columns[columns.Length - 1], "版本", StringComparison.OrdinalIgnoreCase));
                        if (header) continue;
                        WslDistroState state = new WslDistroState();
                        state.Name = String.Join(" ", columns, 0, columns.Length - 2);
                        state.State = columns[columns.Length - 2];
                        state.IsDefault = isDefault;
                        states.Add(state);
                    }
                }
            }
            catch { }
            return states;
        }

        /// <summary>Docker Desktop / Rancher / Podman helper distros cannot host dsh.</summary>
        public static bool IsUserWslDistro(string distro)
        {
            if (String.IsNullOrWhiteSpace(distro)) return false;
            string name = distro.Trim().ToLowerInvariant();
            if (name == "docker-desktop" || name == "docker-desktop-data") return false;
            if (name.StartsWith("docker-desktop-", StringComparison.Ordinal)) return false;
            if (name == "rancher-desktop" || name == "rancher-desktop-data") return false;
            if (name.StartsWith("rancher-desktop-", StringComparison.Ordinal)) return false;
            if (name.StartsWith("podman-machine-", StringComparison.Ordinal)) return false;
            return true;
        }

        public static int ScoreUserWslDistro(string distro)
        {
            if (String.IsNullOrWhiteSpace(distro)) return 0;
            string name = distro.Trim().ToLowerInvariant();
            if (name.StartsWith("ubuntu", StringComparison.Ordinal)) return 100;
            if (name == "debian" || name.StartsWith("debian-", StringComparison.Ordinal)) return 90;
            if (name.StartsWith("kali", StringComparison.Ordinal)) return 85;
            if (name.IndexOf("suse", StringComparison.Ordinal) >= 0) return 80;
            if (name.IndexOf("fedora", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("rocky", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("alma", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("centos", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("rhel", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("oracle", StringComparison.Ordinal) >= 0) return 75;
            if (name.StartsWith("arch", StringComparison.Ordinal) ||
                name.IndexOf("manjaro", StringComparison.Ordinal) >= 0 ||
                name.IndexOf("endeavouros", StringComparison.Ordinal) >= 0) return 70;
            if (name.IndexOf("alpine", StringComparison.Ordinal) >= 0) return 60;
            return 0;
        }

        /// <summary>
        /// Selection priority (with running-state preference so a stopped default
        /// distro is never picked over a warm one):
        ///   configured &gt; unique candidate &gt; running (single) &gt; default &gt; name score.
        /// </summary>
        public static string SelectPreferredDistro(string configured, List<string> detected, List<WslDistroState> states)
        {
            if (detected == null || detected.Count == 0) return null;

            if (!String.IsNullOrWhiteSpace(configured))
            {
                string configuredName = configured.Trim();
                foreach (string item in detected)
                    if (String.Equals(item, configuredName, StringComparison.OrdinalIgnoreCase)) return item;
            }

            List<string> candidates = new List<string>();
            foreach (string item in detected)
            {
                if (IsUserWslDistro(item) && !ContainsDistro(candidates, item)) candidates.Add(item);
            }
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            // Prefer a distro that is currently Running (warm, has the user's env).
            List<string> running = new List<string>();
            if (states != null)
            {
                foreach (WslDistroState state in states)
                {
                    if (state == null || String.IsNullOrWhiteSpace(state.Name)) continue;
                    if (!String.Equals(state.State, "Running", StringComparison.OrdinalIgnoreCase)) continue;
                    string match = FindDistro(candidates, state.Name);
                    if (match != null && !ContainsDistro(running, match)) running.Add(match);
                }
                if (running.Count == 1) return running[0];

                // Multiple running: prefer the default among them.
                foreach (WslDistroState state in states)
                {
                    if (state == null || !state.IsDefault || String.IsNullOrWhiteSpace(state.Name)) continue;
                    string match = FindDistro(running, state.Name);
                    if (match != null) return match;
                }

                // Fall back to the default distro if it is a user distro.
                foreach (WslDistroState state in states)
                {
                    if (state == null || !state.IsDefault || String.IsNullOrWhiteSpace(state.Name)) continue;
                    string match = FindDistro(candidates, state.Name);
                    if (match != null) return match;
                }
            }

            string best = null;
            int bestScore = -1;
            bool tie = false;
            foreach (string candidate in candidates)
            {
                int score = ScoreUserWslDistro(candidate);
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                    tie = false;
                }
                else if (score == bestScore)
                {
                    tie = true;
                }
            }
            if (best != null && !tie) return best;
            return null;
        }

        /// <summary>
        /// Resolves the distro to use, in priority order:
        /// configured &gt; last-used (remembered working distro) &gt; running &gt; unique &gt; default &gt; score.
        /// </summary>
        public static bool ResolveDistro(string configured, string lastUsed, out string distro)
        {
            distro = null;
            try
            {
                List<string> detected = DetectDistros();
                if (detected == null || detected.Count == 0) return false;

                if (!String.IsNullOrWhiteSpace(configured))
                {
                    foreach (string item in detected)
                    {
                        if (String.Equals(item, configured.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            distro = item;
                            return true;
                        }
                    }
                }
                if (!String.IsNullOrWhiteSpace(lastUsed))
                {
                    foreach (string item in detected)
                    {
                        if (String.Equals(item, lastUsed.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            distro = item;
                            return true;
                        }
                    }
                }
                List<WslDistroState> states = DetectDistroStates();
                distro = SelectPreferredDistro(null, detected, states);
                return distro != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsDistro(List<string> distros, string distro)
        {
            foreach (string item in distros)
                if (String.Equals(item, distro, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string FindDistro(List<string> distros, string distro)
        {
            foreach (string item in distros)
                if (String.Equals(item, distro, StringComparison.OrdinalIgnoreCase)) return item;
            return null;
        }

        // ---------------------------------------------------------------- runners

        /// <summary>
        /// Runs a short Linux command inside the distro and captures its output.
        /// </summary>
        public static CommandResult RunCapture(string distro, string command, IList<string> args, int timeoutMs)
        {
            CommandResult result = new CommandResult();
            result.ExitCode = -1;
            result.StandardOutput = String.Empty;
            result.StandardError = String.Empty;
            try
            {
                List<string> wslArgs = BuildWslArgs(distro, command, args);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = FindWslExe();
                psi.Arguments = String.Join(" ", ToArgv(wslArgs));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
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
                        DshLauncher.KillTree(SafePid(process));
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
                FileLog.Error("WslTools.RunCapture: " + ex.Message);
            }
            return result;
        }

        /// <summary>
        /// Starts a long-running Linux command (the dsh service) with output captured
        /// asynchronously into log files. Returns the wsl.exe client process; its
        /// lifetime tracks the Linux command while it runs.
        /// </summary>
        public static Process StartWsl(string distro, string command, IList<string> args, string outLog, string errLog)
        {
            List<string> wslArgs = BuildWslArgs(distro, command, args);
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = FindWslExe();
            psi.Arguments = String.Join(" ", ToArgv(wslArgs));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            Process p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) FileLog.AppendLine(outLog, e.Data);
            };
            p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) FileLog.AppendLine(errLog, e.Data);
            };
            if (!p.Start()) return null;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        private static List<string> BuildWslArgs(string distro, string command, IList<string> args)
        {
            List<string> wslArgs = new List<string>();
            wslArgs.Add("-d");
            wslArgs.Add(distro);
            wslArgs.Add("--");
            wslArgs.Add(command);
            if (args != null)
            {
                foreach (string a in args) wslArgs.Add(a);
            }
            return wslArgs;
        }

        private static int SafePid(Process process)
        {
            try { return process.HasExited ? 0 : process.Id; }
            catch { return 0; }
        }

        /// <summary>
        /// Queries the dsh runtime bridge (inside WSL, reached via localhost forwarding)
        /// with a versioned JSON request. Returns the raw response line or null on failure.
        /// </summary>
        public static string BridgeQuery(int bridgePort, string token, string method, int timeoutMs)
        {
            if (bridgePort <= 0 || String.IsNullOrEmpty(token)) return null;
            try
            {
                using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", bridgePort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1500)) return null;
                    client.EndConnect(ar);
                    System.Net.Sockets.NetworkStream stream = client.GetStream();
                    string req = "{\"v\":1,\"method\":\"" + method + "\",\"token\":\"" + token + "\"}\n";
                    byte[] data = Encoding.UTF8.GetBytes(req);
                    stream.Write(data, 0, data.Length);
                    stream.Flush();
                    StringBuilder sb = new StringBuilder();
                    byte[] buf = new byte[4096];
                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (stream.DataAvailable)
                        {
                            int read = stream.Read(buf, 0, buf.Length);
                            if (read <= 0) break;
                            sb.Append(Encoding.UTF8.GetString(buf, 0, read));
                            if (sb.ToString().IndexOf("\n") >= 0) break;
                        }
                        else
                        {
                            System.Threading.Thread.Sleep(100);
                        }
                    }
                    return sb.ToString().Trim();
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("WslTools.BridgeQuery: " + ex.Message);
                return null;
            }
        }

        /// <summary>Quotes one argument for the Windows command line (CommandLineToArgvW).</summary>
        public static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";
            if (value.Length > 0 && value.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0) return value;
            StringBuilder result = new StringBuilder();
            result.Append('"');
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') backslashes++;
                else if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                }
                else
                {
                    result.Append('\\', backslashes);
                    backslashes = 0;
                    result.Append(character);
                }
            }
            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }

        private static List<string> ToArgv(List<string> args)
        {
            List<string> argv = new List<string>();
            foreach (string a in args) argv.Add(QuoteArgument(a));
            return argv;
        }

        /// <summary>Single-quotes a value for a bash -lc command string.</summary>
        public static string BashQuote(string value)
        {
            return "'" + (value ?? String.Empty).Replace("'", "'\\''") + "'";
        }

        // ---------------------------------------------------------------- paths

        /// <summary>Converts a Windows path to a WSL path (wslpath -u, cached, with fallback).</summary>
        public static string ConvertToWslPath(string distro, string windowsPath)
        {
            if (String.IsNullOrWhiteSpace(windowsPath)) return "~";
            try
            {
                CommandResult r = RunCapture(distro, "wslpath", new string[] { "-u", windowsPath.Replace('\\', '/') }, 30000);
                string value = (r.StandardOutput ?? String.Empty).Trim();
                if (r.ExitCode == 0 && !String.IsNullOrWhiteSpace(value)) return value;
            }
            catch { }
            // Fallback: /mnt/<drive>/<rest> for a plain C:\ path.
            try
            {
                string normalized = windowsPath.Replace('\\', '/');
                if (normalized.Length > 1 && normalized[1] == ':')
                    return "/mnt/" + Char.ToLowerInvariant(normalized[0]) + "/" + normalized.Substring(2).TrimStart('/');
            }
            catch { }
            return "~";
        }

        // ---------------------------------------------------------------- WSL-side service helpers

        /// <summary>
        /// PID of the Linux process listening on the port inside the distro (0 = none).
        /// Parses `ss -tlnp` output in C# because inline shell scripts with $(...),
        /// $VAR or sed back-references get mangled by wsl.exe's argument pass-through.
        /// </summary>
        public static int WslPortOwnerPid(string distro, int port)
        {
            if (String.IsNullOrWhiteSpace(distro)) return 0;
            CommandResult r = RunCapture(distro, "ss", new string[] { "-tlnp" }, 15000);
            if (r.ExitCode != 0) return 0;
            string needle = ":" + port;
            foreach (string raw in (r.StandardOutput ?? String.Empty).Split('\n'))
            {
                string line = raw.Trim();
                if (line.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int idx = line.IndexOf("pid=", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                int start = idx + 4;
                int end = start;
                while (end < line.Length && Char.IsDigit(line[end])) end++;
                int pid;
                if (end > start && int.TryParse(line.Substring(start, end - start), out pid)) return pid;
            }
            return 0;
        }

        /// <summary>True when the Linux process on the port looks like a dsh (node) service.</summary>
        public static bool WslPortHasDsh(string distro, int port)
        {
            int pid = WslPortOwnerPid(distro, port);
            if (pid <= 0) return false;
            CommandResult ps = RunCapture(distro, "bash",
                new string[] { "-lc", "ps -p " + pid + " -o args= 2>/dev/null | head -1" }, 8000);
            if (ps.ExitCode != 0) return false;
            string cmdline = ps.StandardOutput ?? String.Empty;
            return cmdline.IndexOf("dsh", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Materializes wsl-start.sh (the WSL-side launcher with first-line self-heal)
        /// into the shared dir (LF) and copies it into ~/.dsh-webui/ inside the distro.
        /// </summary>
        public static bool EnsureWslScript(string distro)
        {
            try
            {
                string shared = Path.Combine(AppPaths.SharedDir, "wsl-start.sh");
                File.WriteAllText(shared, WslStartScript.Replace("\r\n", "\n"), new UTF8Encoding(false));
                string wslPath = ConvertToWslPath(distro, shared);
                string cmd = "mkdir -p ~/.dsh-webui && cp -f " + BashQuote(wslPath)
                    + " ~/.dsh-webui/wsl-start.sh && chmod +x ~/.dsh-webui/wsl-start.sh";
                CommandResult r = RunCapture(distro, "bash", new string[] { "-lc", cmd }, 30000);
                if (r.ExitCode != 0)
                    FileLog.Error("EnsureWslScript failed: " + (r.StandardError ?? String.Empty).Trim());
                return r.ExitCode == 0;
            }
            catch (Exception ex)
            {
                FileLog.Error("EnsureWslScript: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Waits until the systemd --user manager socket is up (WSL cold start takes
        /// a while before the user session is ready).
        /// </summary>
        public static bool WaitSystemdUserReady(string distro, int timeoutMs)
        {
            if (String.IsNullOrWhiteSpace(distro)) return false;
            string uid = String.Empty;
            CommandResult id = RunCapture(distro, "id", new string[] { "-u" }, 8000);
            if (id.ExitCode == 0) uid = (id.StandardOutput ?? String.Empty).Trim();
            if (String.IsNullOrEmpty(uid)) return false;
            string socket = "/run/user/" + uid + "/systemd/private";
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                CommandResult r = RunCapture(distro, "test", new string[] { "-S", socket }, 8000);
                if (r.ExitCode == 0) return true;
                System.Threading.Thread.Sleep(1000);
            }
            FileLog.Error("WaitSystemdUserReady: user manager socket not ready in " + distro);
            return false;
        }

        /// <summary>True when the distro was booted with systemd as init (v3.0).</summary>
        public static bool SystemdAvailable(string distro)
        {
            if (String.IsNullOrWhiteSpace(distro)) return false;
            // /run/systemd/system exists only when systemd is PID 1 (no shell metacharacters).
            CommandResult r = RunCapture(distro, "bash", new string[] { "-lc", "test -d /run/systemd/system && echo YES || echo NO" }, 10000);
            return r.ExitCode == 0 && (r.StandardOutput ?? String.Empty).Trim().Contains("YES");
        }

        /// <summary>True when the systemd user unit is currently active.</summary>
        public static bool SystemctlIsActive(string distro, int port)
        {
            if (String.IsNullOrWhiteSpace(distro) || port <= 0) return false;
            string uid = String.Empty;
            CommandResult id = RunCapture(distro, "id", new string[] { "-u" }, 8000);
            if (id.ExitCode == 0) uid = (id.StandardOutput ?? String.Empty).Trim();
            string prefix = String.IsNullOrEmpty(uid) ? String.Empty : "XDG_RUNTIME_DIR=/run/user/" + uid + " ";
            CommandResult r = RunCapture(distro, "bash",
                new string[] { "-lc", prefix + "systemctl --user is-active dsh-web-" + port + ".service 2>/dev/null" }, 10000);
            return r.ExitCode == 0 && (r.StandardOutput ?? String.Empty).Trim().Equals("active", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Runs a systemctl --user action. XDG_RUNTIME_DIR is resolved via id -u and
        /// prefixed as a plain assignment (no shell metacharacters -> safe through wsl.exe).
        /// </summary>
        public static bool Systemctl(string distro, string action, string unitName)
        {
            if (String.IsNullOrWhiteSpace(distro) || String.IsNullOrWhiteSpace(unitName)) return false;
            string uid = String.Empty;
            CommandResult id = RunCapture(distro, "id", new string[] { "-u" }, 8000);
            if (id.ExitCode == 0) uid = (id.StandardOutput ?? String.Empty).Trim();
            string prefix = String.IsNullOrEmpty(uid) ? String.Empty : "XDG_RUNTIME_DIR=/run/user/" + uid + " ";
            CommandResult r = RunCapture(distro, "bash",
                new string[] { "-lc", prefix + "systemctl --user " + action + " " + unitName + " 2>&1" }, 15000);
            if (r.ExitCode != 0)
                FileLog.Error("systemctl --user " + action + " " + unitName + " failed: " + (r.StandardOutput ?? r.StandardError ?? String.Empty).Trim());
            return r.ExitCode == 0;
        }

        /// <summary>
        /// v3.0: materializes wsl-systemd-start.sh and the systemd user unit for
        /// profile+port into ~/.config/systemd/user/. Writing files does not require
        /// systemd to be running, so this works even before /etc/wsl.conf is enabled.
        /// </summary>
        public static bool EnsureSystemdFiles(string distro, string profile, int port, int bridgePort, string bridgeToken)
        {
            try
            {
                string shared = Path.Combine(AppPaths.SharedDir, "wsl-systemd-start.sh");
                File.WriteAllText(shared, WslSystemdStartScript.Replace("\r\n", "\n"), new UTF8Encoding(false));
                string wslPath = ConvertToWslPath(distro, shared);

                string unitName = "dsh-web-" + port + ".service";
                string unitContent = "[Unit]\n"
                    + "Description=DeepSeek Harness WebUI (dsh web manager) - profile " + profile + " port " + port + "\n"
                    + "After=network.target\n\n"
                    + "[Service]\n"
                    + "Type=simple\n"
                    + "ExecStart=%h/.dsh-webui/wsl-systemd-start.sh " + profile + " " + port
                    + " " + bridgePort + " " + BashQuote(bridgeToken) + "\n"
                    + "Restart=on-failure\n"
                    + "RestartSec=3\n"
                    + "Environment=HOME=%h\n\n"
                    + "[Install]\n"
                    + "WantedBy=default.target\n";
                string unitShared = Path.Combine(AppPaths.SharedDir, "systemd", unitName);
                Directory.CreateDirectory(Path.GetDirectoryName(unitShared));
                File.WriteAllText(unitShared, unitContent.Replace("\r\n", "\n"), new UTF8Encoding(false));
                string unitWsl = ConvertToWslPath(distro, unitShared);

                string cmd = "mkdir -p ~/.dsh-webui ~/.config/systemd/user && cp -f " + BashQuote(wslPath)
                    + " ~/.dsh-webui/wsl-systemd-start.sh && chmod +x ~/.dsh-webui/wsl-systemd-start.sh && cp -f "
                    + BashQuote(unitWsl) + " ~/.config/systemd/user/" + unitName;
                CommandResult r = RunCapture(distro, "bash", new string[] { "-lc", cmd }, 30000);
                if (r.ExitCode != 0)
                    FileLog.Error("EnsureSystemdFiles failed: " + (r.StandardError ?? String.Empty).Trim());
                return r.ExitCode == 0;
            }
            catch (Exception ex)
            {
                FileLog.Error("EnsureSystemdFiles: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// WSL-side dsh launcher: self-healing loop + pidfile + TERM handling.
        /// Kept as a C# constant so the manager can (re)materialize it at will;
        /// also shipped as scripts/wsl/wsl-start.sh for the installer.
        /// </summary>
        public const string WslStartScript =
@"#!/usr/bin/env bash
# dsh web manager v2.2 - WSL-side dsh web launcher (first-line self-heal).
# Usage: wsl-start.sh <profile> <port>
# dsh intentionally rejects --host 0.0.0.0 (RCE safety), so the service binds
# 127.0.0.1 inside WSL; Windows reaches it through localhost forwarding.
set -u

PROFILE=""${1:-web}""
PORT=""${2:-3080}""
BRIDGE_PORT=""${3:-0}""
BRIDGE_TOKEN=""${4:-}""
HOST=""127.0.0.1""
DWM_DIR=""$HOME/.dsh-webui""
PIDFILE=""$DWM_DIR/wsl-dsh.pid""
LOG=""$DWM_DIR/wsl-dsh.log""
mkdir -p ""$DWM_DIR"" || exit 1

# --- toolchain bootstrap (best effort) ---
if ! command -v dsh >/dev/null 2>&1; then
  export PATH=""$HOME/.local/bin:$HOME/bin:$PATH""
  if command -v fnm >/dev/null 2>&1; then
    eval ""$(fnm env --use-on-cd 2>/dev/null)"" || true
  fi
  if ! command -v dsh >/dev/null 2>&1; then
    FNM_ROOT=""$HOME/.local/share/fnm/node-versions""
    LATEST=""$(ls -1 ""$FNM_ROOT"" 2>/dev/null | sort -V | tail -1)""
    [ -n ""$LATEST"" ] && export PATH=""$FNM_ROOT/$LATEST/installation/bin:$PATH""
  fi
fi

log() { echo ""[$(date '+%F %T')] $*"" >> ""$LOG""; }

DSH_PID=0
cleanup() {
  log ""received TERM, stopping dsh pid=$DSH_PID""
  if [ ""$DSH_PID"" -gt 0 ]; then
    kill -TERM ""$DSH_PID"" 2>/dev/null
    wait ""$DSH_PID"" 2>/dev/null
  fi
  rm -f ""$PIDFILE""
  exit 0
}
trap cleanup TERM INT

# Interruptible sleep: bash defers a trap until the current foreground command
# completes, so `sleep 60` would delay TERM handling by up to 60 s. Running sleep
# in the background and `wait`-ing makes the trap fire immediately.
sleep_int() { sleep ""$1"" & wait ""$!"" 2>/dev/null; }

log ""wsl-start.sh starting profile=$PROFILE port=$PORT (pid=$$)""

if ! command -v dsh >/dev/null 2>&1; then
  log ""ERROR: dsh not found in distro""
  exit 2
fi

CRASHES=0
while true; do
  log ""launching dsh --profile $PROFILE --host $HOST --port $PORT (bridge=$BRIDGE_PORT)""
  DSH_BRIDGE_PORT=""$BRIDGE_PORT"" DSH_BRIDGE_TOKEN=""$BRIDGE_TOKEN"" \
  DSH_PROFILE=""$PROFILE"" DSH_WEB_PORT=""$PORT"" DSH_WEB_HOST=""$HOST"" \
  dsh --profile ""$PROFILE"" --host ""$HOST"" --port ""$PORT"" >> ""$LOG"" 2>&1 &
  DSH_PID=$!
  echo ""$DSH_PID"" > ""$PIDFILE""
  wait ""$DSH_PID""
  CODE=$?
  DSH_PID=0
  log ""dsh exited code=$CODE""
  if [ ""$CODE"" -ne 0 ]; then
    CRASHES=$((CRASHES+1))
    if [ ""$CRASHES"" -ge 10 ]; then
      log ""10 consecutive failures, sleeping 60s""
      sleep_int 60
      CRASHES=0
    else
      sleep_int 3
    fi
  else
    CRASHES=0
    sleep_int 2
  fi
done
";

        /// <summary>
        /// WSL-side systemd ExecStart wrapper (foreground dsh; systemd tracks/heals it).
        /// Also shipped as scripts/wsl/wsl-systemd-start.sh for the installer.
        /// </summary>
        public const string WslSystemdStartScript =
@"#!/usr/bin/env bash
# dsh web manager v3.0 - systemd ExecStart wrapper (foreground).
# systemd tracks the dsh process directly and Restart=on-failure heals it;
# logs go to journald (journalctl --user -u dsh-web-<port>).
# Usage: wsl-systemd-start.sh <profile> <port>
set -u

PROFILE=""${1:-web}""
PORT=""${2:-3080}""
BRIDGE_PORT=""${3:-0}""
BRIDGE_TOKEN=""${4:-}""
HOST=""127.0.0.1""

# --- toolchain bootstrap (best effort, same as wsl-start.sh) ---
if ! command -v dsh >/dev/null 2>&1; then
  export PATH=""$HOME/.local/bin:$HOME/bin:$PATH""
  if command -v fnm >/dev/null 2>&1; then
    eval ""$(fnm env --use-on-cd 2>/dev/null)"" || true
  fi
  if ! command -v dsh >/dev/null 2>&1; then
    FNM_ROOT=""$HOME/.local/share/fnm/node-versions""
    LATEST=""$(ls -1 ""$FNM_ROOT"" 2>/dev/null | sort -V | tail -1)""
    [ -n ""$LATEST"" ] && export PATH=""$FNM_ROOT/$LATEST/installation/bin:$PATH""
  fi
fi
if ! command -v dsh >/dev/null 2>&1; then
  echo ""ERROR: dsh not found in distro"" >&2
  exit 2
fi

export DSH_BRIDGE_PORT=""$BRIDGE_PORT""
export DSH_BRIDGE_TOKEN=""$BRIDGE_TOKEN""
export DSH_PROFILE=""$PROFILE""
export DSH_WEB_PORT=""$PORT""
export DSH_WEB_HOST=""$HOST""
exec dsh --profile ""$PROFILE"" --host ""$HOST"" --port ""$PORT""
";



    }
}
