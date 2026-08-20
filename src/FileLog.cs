using System;
using System.IO;
using System.Text;

namespace DshWebManager
{
    /// <summary>Small rolling file log (approx. 512 KB max, one backup).</summary>
    public static class FileLog
    {
        private const long MaxBytes = 512L * 1024;
        private static readonly object Sync = new object();

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
                    string line = String.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}\r\n", DateTime.Now, level, message);
                    File.AppendAllText(AppPaths.LogFile, line, Encoding.UTF8);
                    FileInfo fi = new FileInfo(AppPaths.LogFile);
                    if (fi.Length > MaxBytes)
                    {
                        string backup = AppPaths.LogFile + ".1";
                        File.Copy(AppPaths.LogFile, backup, true);
                        File.WriteAllText(AppPaths.LogFile, String.Empty, Encoding.UTF8);
                    }
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}