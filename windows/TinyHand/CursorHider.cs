using System;
using System.Diagnostics;
using System.IO;

namespace TinyHand
{
    /// <summary>
    /// Hides the real cursor while the hand is on. macOS hides it with CGDisplayHideCursor; Windows
    /// has no global equivalent, so every standard system cursor is swapped for a transparent one
    /// with SetSystemCursor and put back with SystemParametersInfo(SPI_SETCURSORS).
    ///
    /// Swapped cursors outlive the process, so restoring is guarded four ways: on normal exit,
    /// from the crash handlers, by a tiny watchdog process that restores them if TinyHand dies
    /// (even when killed from Task Manager), and at the next launch via a marker file.
    /// </summary>
    internal static class CursorHider
    {
        private static bool hidden;

        // OCR_NORMAL, IBEAM, WAIT, CROSS, UP, SIZENWSE, SIZENESW, SIZEWE, SIZENS, SIZEALL, NO, HAND, APPSTARTING, HELP, PIN, PERSON
        private static readonly uint[] CursorIds = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672 };

        private static string MarkerPath => Path.Combine(AppPaths.Local, "cursor-hidden");

        public static bool IsHidden => hidden;

        public static void Hide()
        {
            if (hidden)
            {
                return;
            }
            hidden = true;
            WriteMarker();
            var and = new byte[32 * 32 / 8];
            var xor = new byte[32 * 32 / 8];
            for (int i = 0; i < and.Length; i++) and[i] = 0xFF;
            IntPtr module = Native.GetModuleHandle(null);
            foreach (uint id in CursorIds)
            {
                // SetSystemCursor takes ownership of (and destroys) the handle, so each id needs its own.
                IntPtr blank = Native.CreateCursor(module, 0, 0, 32, 32, and, xor);
                if (blank != IntPtr.Zero && !Native.SetSystemCursor(blank, id))
                {
                    Native.DestroyIcon(blank);
                }
            }
        }

        public static void Show()
        {
            if (!hidden)
            {
                return;
            }
            hidden = false;
            Restore();
        }

        /// <summary>Reloads the user's cursor scheme no matter what we think the state is.</summary>
        public static void Restore()
        {
            Native.SystemParametersInfo(Native.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            try
            {
                File.Delete(MarkerPath);
            }
            catch (Exception)
            {
                // best effort
            }
        }

        public static void EmergencyRestore()
        {
            try
            {
                if (hidden || File.Exists(MarkerPath))
                {
                    hidden = false;
                    Restore();
                }
            }
            catch (Exception)
            {
                // nothing else we can do
            }
        }

        /// <summary>A previous run died with the cursor hidden: bring it back.</summary>
        public static void RestoreIfLeftHidden()
        {
            if (File.Exists(MarkerPath))
            {
                Log.Write("cursor was left hidden by a previous run; restoring");
                Restore();
            }
        }

        private static void WriteMarker()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Local);
                File.WriteAllText(MarkerPath, Environment.ProcessId.ToString());
            }
            catch (Exception)
            {
                // the watchdog restores unconditionally anyway
            }
        }

        /// <summary>Starts "TinyHand.exe --cursor-guard &lt;pid&gt;" next to us.</summary>
        public static void StartGuard()
        {
            try
            {
                var psi = new ProcessStartInfo(AppPaths.ExePath, $"--cursor-guard {Environment.ProcessId}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(psi);
            }
            catch (Exception e)
            {
                Log.Write($"cursor guard did not start: {e.Message}");
            }
        }

        /// <summary>Body of the watchdog process: wait for TinyHand to exit, then restore if needed.</summary>
        public static int RunGuard(string pidText)
        {
            if (!int.TryParse(pidText, out int pid))
            {
                return 2;
            }
            try
            {
                using var parent = Process.GetProcessById(pid);
                parent.WaitForExit();
            }
            catch (ArgumentException)
            {
                // already gone
            }
            if (File.Exists(MarkerPath))
            {
                Restore();
                Log.Write("cursor guard restored the cursor after TinyHand exited");
            }
            return 0;
        }
    }
}
