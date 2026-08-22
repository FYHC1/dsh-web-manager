using System;
using System.IO;
using System.Text;

namespace DshWebManager
{
    /// <summary>Small rolling file log (approx. 512 KB max, one backup). The log
    /// file keeps one persistent StreamWriter (AutoFlush) instead of opening and
    /// closing the file on every call - the heartbeat tick and WinEvent hook can
    /// log several lines per second on a busy system.</summary>
    public static class FileLog
    {
        private const long MaxBytes = 512L * 1024;
        private static readonly object Sync = new object();
        private static StreamWriter _writer;
        private static string _writerPath = String.Empty;

        public static void Info(string message) { Write("INFO", message); }
        public static void Error(string message) { Write("ERROR", message); }
        public static void Error(Exception ex) { Write("ERROR", ex == null ? "null exception" : ex.ToString()); }

        /// <summary>Appends a raw line to an arbitrary file (used for captured process output).</summary>
        public static void AppendLine(string path, string text)
        {
            try
            {
                if (String.IsNullOrEmpty(text)) return;
                lock (Sync)
                {
                    AppPaths.EnsureDirectories();
                    File.AppendAllText(path, text + "\r\n", Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    AppPaths.EnsureDirectories();
                    EnsureWriter();
                    string line = String.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}\r\n", DateTime.Now, level, message);
                    _writer.Write(line);
                    _writer.Flush();
                    // Rollover check (approx: the file can exceed MaxBytes by one write).
                    FileInfo fi = new FileInfo(AppPaths.LogFile);
                    if (fi.Length > MaxBytes)
                    {
                        CloseWriter();
                        string backup = AppPaths.LogFile + ".1";
                        File.Copy(AppPaths.LogFile, backup, true);
                        File.WriteAllText(AppPaths.LogFile, String.Empty, Encoding.UTF8);
                        EnsureWriter();
                    }
                }
            }
            catch
            {
                // Logging must never take the app down. A failed write also
                // invalidates the cached writer so the next call re-opens.
                CloseWriter();
            }
        }

        private static void EnsureWriter()
        {
            if (_writer != null && String.Equals(_writerPath, AppPaths.LogFile, StringComparison.OrdinalIgnoreCase))
                return;
            CloseWriter();
            _writer = new StreamWriter(AppPaths.LogFile, true, Encoding.UTF8);
            _writer.AutoFlush = true;
            _writerPath = AppPaths.LogFile;
        }

        private static void CloseWriter()
        {
            try
            {
                if (_writer != null) _writer.Dispose();
            }
            catch { }
            _writer = null;
            _writerPath = String.Empty;
        }
    }
}