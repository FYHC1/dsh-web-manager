using System;
using System.IO;
using System.Net;
using System.Text;

namespace DshWebManager
{
    /// <summary>
    /// v3.0 update mechanism: current/latest dsh version check (throttled to 24 h)
    /// and one-click update of the WSL-side global dsh package via npm (npmmirror).
    /// </summary>
    public static class UpdateChecker
    {
        private const string RegistryLatest = "https://registry.npmmirror.com/@deepseek-ai/dsh/latest";
        private const string RegistryFlag = "--registry=https://registry.npmmirror.com";
        private static readonly TimeSpan Throttle = TimeSpan.FromHours(24);

        /// <summary>Current dsh version installed inside the distro (or empty).</summary>
        public static string GetCurrentWslDshVersion(string distro)
        {
            if (String.IsNullOrWhiteSpace(distro)) return String.Empty;
            CommandResult r = WslTools.RunCapture(distro, "dsh", new string[] { "--version" }, 10000);
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
            CommandResult r = WslTools.RunCapture(distro, "npm",
                new string[] { "install", "-g", "@deepseek-ai/dsh@latest", RegistryFlag }, 180000);
            if (r.ExitCode != 0)
                FileLog.Error("UpdateChecker.update failed: " + (r.StandardOutput ?? r.StandardError ?? String.Empty));
            return r.ExitCode == 0;
        }

        /// <summary>
        /// Throttled check: returns an update payload when a newer version exists and
        /// the 24 h throttle elapsed; also refreshes LastKnownLatest in the config.
        /// </summary>
        public static string CheckThrottled(ManagerConfig config, string distro)
        {
            DateTime last;
            DateTime.TryParse(config.LastVersionCheckUtc, out last);
            if (DateTime.UtcNow.Subtract(last) < Throttle)
                return String.Empty; // still throttled; keep the last known answer

            config.LastVersionCheckUtc = DateTime.UtcNow.ToString("o");
            string current = GetCurrentWslDshVersion(distro);
            string latest = GetLatestDshVersion();
            config.LastKnownLatest = latest;
            config.Save();
            if (String.IsNullOrEmpty(current) || String.IsNullOrEmpty(latest)) return String.Empty;
            if (String.Equals(current.Trim(), latest.Trim(), StringComparison.OrdinalIgnoreCase))
                return String.Empty;
            FileLog.Info("UpdateChecker: dsh " + current + " -> " + latest);
            return latest;
        }
    }
}
