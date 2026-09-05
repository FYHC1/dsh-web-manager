using System;
using System.Collections.Concurrent;

namespace DshWebManager
{
    /// <summary>
    /// Tracks the dsh web launch token per port. dsh &gt;= 0.1.2 serves the UI
    /// only after authentication: the URL printed on stdout at startup carries
    /// ?token=&lt;launchToken&gt; (random per dsh process); opening it mints a
    /// persistent signed cookie, after which even a stale/absent token is
    /// accepted while the cookie lives (the signing secret persists in the
    /// profile's credentials). The manager captures that URL from the managed
    /// process's stdout (Windows + WSL) and opens its windows with it, so the
    /// embedded window authenticates like the user's own browser would.
    /// Externally started (attached) dsh processes are not captured — their
    /// windows rely on the cookie minted by an earlier tokened visit.
    /// </summary>
    public static class DshWebAuth
    {
        private static readonly ConcurrentDictionary<int, string> Tokens =
            new ConcurrentDictionary<int, string>();

        /// <summary>Feeds one stdout line of a managed dsh process. Captures
        /// "… http://127.0.0.1:&lt;port&gt;/?token=&lt;token&gt;" (printed at every
        /// dsh web startup; the newest line wins).</summary>
        public static void ObserveLine(int port, string line)
        {
            if (port <= 0 || String.IsNullOrEmpty(line)) return;
            string marker = "http://127.0.0.1:" + port.ToString() + "/?token=";
            int idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return;
            string rest = line.Substring(idx + marker.Length);
            int end = 0;
            while (end < rest.Length && IsTokenChar(rest[end])) end++;
            if (end == 0) return;
            Tokens[port] = rest.Substring(0, end);
            FileLog.Info("DshWebAuth: captured launch token for port " + port.ToString());
        }

        /// <summary>Drops the stored token (called when a dsh process for the
        /// port is being (re)started — the old process's token is void).</summary>
        public static void Forget(int port)
        {
            if (port <= 0) return;
            string removed;
            Tokens.TryRemove(port, out removed);
        }

        /// <summary>True when a launch token has been captured for the port.</summary>
        public static bool HasToken(int port)
        {
            if (port <= 0) return false;
            string token;
            return Tokens.TryGetValue(port, out token) && !String.IsNullOrEmpty(token);
        }

        /// <summary>Window URL for the port: carries the captured launch token
        /// when available, plain URL otherwise (older dsh / attached instance —
        /// those rely on the persistent cookie).</summary>
        public static string WindowUrl(int port)
        {
            string plain = "http://127.0.0.1:" + port.ToString() + "/";
            if (port <= 0) return plain;
            string token;
            if (Tokens.TryGetValue(port, out token) && !String.IsNullOrEmpty(token))
                return plain + "?token=" + token;
            return plain;
        }

        /// <summary>dsh tokens are base64url (randomBytes → base64url): A-Za-z0-9-_.
        /// Stops at the first foreign char (whitespace, quote, "&", …).</summary>
        private static bool IsTokenChar(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '-' || c == '_';
        }
    }
}
