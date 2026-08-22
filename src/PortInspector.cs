using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DshWebManager
{
    /// <summary>Port listening checks and listener-process identification.</summary>
    public static class PortInspector
    {
        /// <summary>True when something listens on 127.0.0.1:port (or 0.0.0.0/::),
        /// also trying the IPv6 loopback so a [::1]-only (or dual-stack) listener
        /// is not missed. A plain connect probe has no side effects on an HTTP
        /// server; WSL ports without localhost forwarding are covered by the
        /// backend's WslPortOwnerPid fallback.</summary>
        public static bool IsListening(int port)
        {
            if (IsListeningOn("127.0.0.1", port)) return true;
            return IsListeningOn("::1", port);
        }

        private static bool IsListeningOn(string address, int port)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(address, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(400)) return false;
                    if (!client.Connected) return false;
                    client.EndConnect(ar);
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>PID that owns the listener on the given port, or 0.</summary>
        public static int GetListenerPid(int port)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.ASCII;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    foreach (string line in output.Split('\n'))
                    {
                        string t = line.Trim();
                        if (!t.Contains("LISTENING")) continue;
                        Match m = Regex.Match(t, @"^\s*TCP\s+(\S+):(\d+)\s+\S+\s+LISTENING\s+(\d+)");
                        if (!m.Success) continue;
                        int pnum;
                        if (!int.TryParse(m.Groups[2].Value, out pnum)) continue;
                        if (pnum != port) continue;
                        int pid;
                        if (int.TryParse(m.Groups[3].Value, out pid)) return pid;
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>True when the given PID looks like a dsh (node) process serving `dsh web`.</summary>
        public static bool IsDshProcess(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                string cmd = GetCommandLine(pid);
                if (String.IsNullOrEmpty(cmd)) return false;
                return cmd.IndexOf("dsh", StringComparison.OrdinalIgnoreCase) >= 0
                    && cmd.IndexOf("web", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        public static string GetCommandLine(int pid)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        object v = obj["CommandLine"];
                        if (v != null) return v.ToString();
                    }
                }
            }
            catch { }
            return String.Empty;
        }

        /// <summary>
        /// Picks a working port starting at preferred: the preferred port if free or already served
        /// by dsh; otherwise the next free port (when autoFallback is on) or -1.
        /// </summary>
        public static int ChoosePort(int preferred, bool autoFallback, bool requireFree)
        {
            if (!IsListening(preferred)) return preferred;
            if (!requireFree)
            {
                int pid = GetListenerPid(preferred);
                if (pid > 0 && IsDshProcess(pid)) return preferred;
            }
            if (!autoFallback) return -1;
            for (int p = preferred + 1; p < preferred + 100; p++)
                if (!IsListening(p)) return p;
            return -1;
        }

        /// <summary>Waits until the port responds (or timeout).</summary>
        public static bool WaitReady(int port, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (IsListening(port)) return true;
                Thread.Sleep(500);
                waited += 500;
            }
            return false;
        }
    }
}