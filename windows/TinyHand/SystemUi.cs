using System;
using System.Collections.Generic;
using System.Text;

namespace TinyHand
{
    /// <summary>
    /// Finds the parts of the Windows shell that clicks must always reach, the way the Mac app
    /// treats the menu bar and open menus (CGWindowList at popUpMenuWindow level): the taskbars,
    /// classic popup menus, and shell flyouts such as Start, search and the notification area.
    /// </summary>
    internal static class SystemUi
    {
        private static readonly HashSet<string> TaskbarClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
        };

        private static readonly HashSet<string> MenuClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "#32768",                               // classic popup / context menus
            "NotifyIconOverflowWindow",             // hidden tray icons (Windows 10)
            "TopLevelWindowForOverflowXamlIsland",  // hidden tray icons (Windows 11)
            "Windows.UI.Core.CoreWindow",           // Start, search, action center (only when not cloaked)
            "Xaml_WindowedPopupClass",              // Windows 11 shell popups and context menus
            "XamlExplorerHostIslandWindow",         // Windows 11 shell flyouts
            "TaskListThumbnailWnd",                 // taskbar thumbnails
            "DropDown",                             // some app dropdowns
            "ComboLBox",                            // combo box lists
        };

        public static void Scan(out List<Native.RECT> taskbars, out List<Native.RECT> menus, IntPtr ownWindowToSkip)
        {
            var bars = new List<Native.RECT>();
            var open = new List<Native.RECT>();
            var name = new StringBuilder(256);
            Native.EnumWindows((hwnd, _) =>
            {
                if (hwnd == ownWindowToSkip || !Native.IsWindowVisible(hwnd))
                {
                    return true;
                }
                name.Clear();
                if (Native.GetClassName(hwnd, name, name.Capacity) == 0)
                {
                    return true;
                }
                string cls = name.ToString();
                bool bar = TaskbarClasses.Contains(cls);
                bool menu = !bar && (MenuClasses.Contains(cls) || cls.StartsWith("WindowsForms10.", StringComparison.Ordinal) && IsOwnProcess(hwnd));
                if (!bar && !menu)
                {
                    return true;
                }
                if (Native.IsCloaked(hwnd) || !Native.GetWindowRect(hwnd, out var r) || r.Width <= 0 || r.Height <= 0)
                {
                    return true;
                }
                (bar ? bars : open).Add(r);
                return true;
            }, IntPtr.Zero);
            taskbars = bars;
            menus = open;
        }

        private static bool IsOwnProcess(IntPtr hwnd)
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }
    }
}
