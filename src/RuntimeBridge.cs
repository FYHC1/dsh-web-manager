using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace DshWebManager
{
    /// <summary>
    /// Parsed runtime-bridge payload: the authoritative state of a running dsh web
    /// service (its dsh version, node runtime, uptime), as reported by the in-dsh
    /// runtime bridge (plugins/dsh-runtime-bridge) over the versioned JSON protocol.
    /// </summary>
    public sealed class BridgeInfo
    {
        public bool Reachable { get; set; }   // both getStatus and getRuntimeInfo answered
        public int Pid { get; set; }          // dsh process id inside the distro
        public long UptimeMs { get; set; }    // ms since dsh started
        public string Profile { get; set; }   // DSH_PROFILE
        public int WebPort { get; set; }      // DSH_WEB_PORT
        public string DshVersion { get; set; }// dsh --version inside the distro
        public string Node { get; set; }      // process.version
        public string Hostname { get; set; }

        public BridgeInfo()
        {
            Profile = String.Empty;
            DshVersion = String.Empty;
            Node = String.Empty;
            Hostname = String.Empty;
        }

        /// <summary>Human-friendly one-line summary, e.g. "dsh 0.1.0-rc.7 · node v24.19.0 · 运行 12m".</summary>
        public string Summary
        {
            get
            {
                if (!Reachable) return String.Empty;
                StringBuilder sb = new StringBuilder();
                if (!String.IsNullOrEmpty(DshVersion)) sb.Append("dsh ").Append(DshVersion);
                if (!String.IsNullOrEmpty(Node)) { if (sb.Length > 0) sb.Append(" · "); sb.Append("node ").Append(Node); }
                if (UptimeMs > 0) { if (sb.Length > 0) sb.Append(" · "); sb.Append("运行 ").Append(BridgeInfoParser.FormatUptime(UptimeMs)); }
                return sb.ToString();
            }
        }
    }

    /// <summary>Parses runtime-bridge JSON responses and formats uptime.</summary>
    public static class BridgeInfoParser
    {
        /// <summary>Merges getStatus and getRuntimeInfo responses into one BridgeInfo.</summary>
        public static BridgeInfo FromJson(string statusJson, string runtimeJson)
        {
            BridgeInfo info = new BridgeInfo();
            Dictionary<string, object> root;

            if (!String.IsNullOrEmpty(statusJson))
            {
                root = Deserialize(statusJson);
                Dictionary<string, object> status = GetDict(root, "status");
                if (status != null)
                {
                    info.Pid = GetInt(status, "pid");
                    info.UptimeMs = GetLong(status, "uptimeMs");
                    info.Profile = GetString(status, "profile");
                    info.WebPort = GetInt(status, "webPort");
                }
            }

            if (!String.IsNullOrEmpty(runtimeJson))
            {
                root = Deserialize(runtimeJson);
                Dictionary<string, object> rt = GetDict(root, "info");
                if (rt != null)
                {
                    info.Node = GetString(rt, "node");
                    info.DshVersion = GetString(rt, "dshVersion");
                    info.Hostname = GetString(rt, "hostname");
                }
            }

            return info;
        }

        /// <summary>Human friendly uptime: "刚启动" / "12m" / "2h5m".</summary>
        public static string FormatUptime(long ms)
        {
            if (ms < 0) ms = 0;
            long sec = ms / 1000;
            if (sec < 60) return "\u521a\u542f\u52a8"; // 刚启动
            long min = sec / 60;
            if (min < 60) return min + "m";
            long h = min / 60;
            long remMin = min % 60;
            return h + "h" + remMin + "m";
        }

        private static Dictionary<string, object> Deserialize(string json)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                object obj = ser.DeserializeObject(json);
                return obj as Dictionary<string, object>;
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> root, string key)
        {
            object v;
            if (root == null || !root.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return String.Empty;
            return v.ToString();
        }

        private static long GetLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v); }
            catch { return 0; }
        }

        private static int GetInt(Dictionary<string, object> d, string key)
        {
            long l = GetLong(d, key);
            return l > int.MaxValue ? int.MaxValue : (l < int.MinValue ? int.MinValue : (int)l);
        }
    }

    /// <summary>
    /// Shared runtime-bridge client for both Windows and WSL backends. The bridge
    /// listens on 127.0.0.1:&lt;webPort + 100&gt;; from Windows the WSL bridge is reached
    /// through localhost forwarding, so the TCP address is identical either way.
    /// Returns null when the bridge is not reachable or the token is unset.
    /// </summary>
    public static class RuntimeBridgeClient
    {
        public static BridgeInfo Query(int port, string token)
        {
            if (port <= 0 || String.IsNullOrEmpty(token)) return null;
            int bridgePort = port + 100;
            string statusJson = WslTools.BridgeQuery(bridgePort, token, "getStatus", 2500);
            string runtimeJson = WslTools.BridgeQuery(bridgePort, token, "getRuntimeInfo", 2500);
            if (statusJson == null || runtimeJson == null) return null;
            BridgeInfo info = BridgeInfoParser.FromJson(statusJson, runtimeJson);
            info.Reachable = true;
            return info;
        }

        /// <summary>Asks the in-dsh bridge to shut down gracefully; returns the raw response or null.</summary>
        public static string Shutdown(int port, string token)
        {
            if (port <= 0 || String.IsNullOrEmpty(token)) return null;
            return WslTools.BridgeQuery(port + 100, token, "shutdown", 2000);
        }
    }
}
