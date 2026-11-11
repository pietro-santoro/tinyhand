using System;
using System.Diagnostics;
using System.IO;

namespace TinyHand
{
    /// <summary>Where TinyHand keeps its files (macOS: ~/Library/...).</summary>
    internal static class AppPaths
    {
        /// <summary>%APPDATA%\TinyHand — settings, state.json, custom hands.</summary>
        public static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TinyHand");

        /// <summary>%LOCALAPPDATA%\TinyHand — log and the hidden-cursor marker.</summary>
        public static string Local => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TinyHand");

        public static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "TinyHand.exe";
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly string FilePath = Path.Combine(AppPaths.Local, "TinyHand.log");
        private static bool checkedSize;

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.Local);
                    if (!checkedSize)
                    {
                        checkedSize = true;
                        var info = new FileInfo(FilePath);
                        if (info.Exists && info.Length > 2 * 1024 * 1024)
                        {
                            File.Delete(FilePath);
                        }
                    }
                    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}";
                    File.AppendAllText(FilePath, line);
                }
            }
            catch (Exception)
            {
                // Logging must never take the app down.
            }
        }
    }
}
