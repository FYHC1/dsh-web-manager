using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace DshWebManager
{
    /// <summary>
    /// dsh web manager self-update: queries GitHub Releases (FYHC1/dsh-web-manager),
    /// compares versions, downloads the manager exe release asset (v3.9: the asset
    /// carries a version suffix, "dsh-web-manager-&lt;ver&gt;.exe") and verifies the
    /// binary before the manager hands off to the detached updater.
    /// </summary>
    public static class ManagerUpdater
    {
        private const string ReleasesApi = "https://api.github.com/repos/FYHC1/dsh-web-manager/releases/latest";
        private const string ExeAssetName = "dsh-web-manager.exe";
        private const long MinExeBytes = 50000;

        public sealed class ReleaseInfo
        {
            public string Tag = String.Empty;         // e.g. "v3.0.1"
            public string DownloadUrl = String.Empty; // exe asset URL ("" = asset missing)
            public bool HasRelease;                   // false = 404 (nothing published yet)
        }

        /// <summary>Current manager version from the assembly ("3.0.0").</summary>
        public static string CurrentVersion()
        {
            try
            {
                Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (v != null) return v.ToString(3);
            }
            catch { }
            return "0.0.0";
        }

        /// <summary>True when dotted version a is newer than b (leading "v"/prefix and
        /// prerelease suffixes like "-rc.1" are ignored).</summary>
        public static bool IsNewer(string a, string b)
        {
            int[] av = ParseVersion(a), bv = ParseVersion(b);
            for (int i = 0; i < 3; i++)
                if (av[i] != bv[i]) return av[i] > bv[i];
            return false;
        }

        private static int[] ParseVersion(string v)
        {
            int[] parts = new int[] { 0, 0, 0 };
            if (String.IsNullOrEmpty(v)) return parts;
            string s = v.Trim();
            int dash = s.IndexOf('-');
            if (dash >= 0) s = s.Substring(0, dash);
            int digitStart = 0;
            while (digitStart < s.Length && !Char.IsDigit(s[digitStart])) digitStart++;
            if (digitStart > 0) s = s.Substring(digitStart);
            string[] seg = s.Split('.');
            for (int i = 0; i < 3 && i < seg.Length; i++)
            {
                int n;
                if (int.TryParse(seg[i], out n) && n >= 0) parts[i] = n;
            }
            return parts;
        }

        /// <summary>Matches the manager exe release asset: the legacy exact name
        /// "dsh-web-manager.exe" or the version-suffixed "dsh-web-manager-&lt;ver&gt;.exe".</summary>
        private static bool IsExeAsset(string name)
        {
            if (String.Equals(name, ExeAssetName, StringComparison.OrdinalIgnoreCase)) return true;
            return name.StartsWith("dsh-web-manager-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Queries the latest release. apiOverride lets tests/mirrors point
        /// elsewhere ("" = official GitHub endpoint). Returns null on network errors.</summary>
        public static ReleaseInfo GetLatestRelease(string apiOverride)
        {
            ReleaseInfo info = new ReleaseInfo();
            try
            {
                string url = String.IsNullOrWhiteSpace(apiOverride) ? ReleasesApi : apiOverride;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.UserAgent = "dsh-web-manager-updater";
                using (WebResponse resp = req.GetResponse())
                using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(json);
                    if (root == null) return info;
                    object tag;
                    if (root.TryGetValue("tag_name", out tag) && tag != null) info.Tag = tag.ToString();
                    object pre;
                    if (root.TryGetValue("prerelease", out pre) && pre is bool && (bool)pre) return info;
                    object assets;
                    if (root.TryGetValue("assets", out assets) && assets != null)
                    {
                        // JavaScriptSerializer materializes JSON arrays as ArrayList
                        // (not object[]), so accept both shapes.
                        object[] arr = assets as object[];
                        if (arr == null)
                        {
                            System.Collections.ArrayList list = assets as System.Collections.ArrayList;
                            if (list != null) arr = list.ToArray();
                        }
                        if (arr != null)
                        {
                            foreach (object o in arr)
                            {
                                Dictionary<string, object> asset = o as Dictionary<string, object>;
                                if (asset == null) continue;
                                object name;
                                if (!asset.TryGetValue("name", out name) || name == null) continue;
                                if (!IsExeAsset(name.ToString())) continue;
                                object urlObj;
                                if (asset.TryGetValue("browser_download_url", out urlObj) && urlObj != null)
                                    info.DownloadUrl = urlObj.ToString();
                                break;
                            }
                        }
                    }
                    info.HasRelease = true;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse hr = ex.Response as HttpWebResponse;
                if (hr != null && (int)hr.StatusCode == 404)
                {
                    FileLog.Info("ManagerUpdater: no release published yet (404)");
                    return info; // HasRelease=false, empty tag
                }
                FileLog.Error("ManagerUpdater.release: " + ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                FileLog.Error("ManagerUpdater.release: " + ex.Message);
                return null;
            }
            return info;
        }

        /// <summary>Downloads the release exe to destPath; returns false on failure.</summary>
        public static bool Download(string url, string destPath)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 60000;
                req.ReadWriteTimeout = 120000;
                req.UserAgent = "dsh-web-manager-updater";
                using (WebResponse resp = req.GetResponse())
                using (Stream src = resp.GetResponseStream())
                using (FileStream dst = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[65536];
                    long total = 0;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n);
                        total += n;
                    }
                    if (total < MinExeBytes) return false;
                }
                byte[] head = new byte[2];
                using (FileStream fs = new FileStream(destPath, FileMode.Open, FileAccess.Read))
                    if (fs.Read(head, 0, 2) < 2) return false;
                return head[0] == (byte)'M' && head[1] == (byte)'Z';
            }
            catch (Exception ex)
            {
                FileLog.Error("ManagerUpdater.download: " + ex.Message);
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                return false;
            }
        }

        /// <summary>File version of a downloaded exe ("" when unreadable).</summary>
        public static string DownloadedVersion(string path)
        {
            try
            {
                FileVersionInfo fv = FileVersionInfo.GetVersionInfo(path);
                if (fv != null && !String.IsNullOrEmpty(fv.FileVersion)) return fv.FileVersion;
            }
            catch { }
            return String.Empty;
        }
    }
}
