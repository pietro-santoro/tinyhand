using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace TinyHand
{
    /// <summary>
    /// The notification-area icon that replaces the Mac's menu bar item. Left click toggles the
    /// pointer, right click opens the same menu as on the Mac. The icon is the menu bar template
    /// image tinted for the taskbar theme, and orange while clicks go through.
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        public Action OnToggle;
        public Action OnClear;
        public Action OnToggleSound;
        public Action OnSettings;
        public Action OnCheckForUpdates;
        public Action OnQuit;
        public Action<bool> OnMenuOpen;
        public Func<bool> IsActive;
        public Func<bool> IsClickThrough;
        public Func<bool> SoundOn;

        private readonly NotifyIcon icon;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem toggleItem;
        private readonly ToolStripMenuItem clearItem;
        private readonly ToolStripMenuItem soundItem;
        private Icon current;
        private string currentKey;

        public TrayIcon()
        {
            menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
            toggleItem = new ToolStripMenuItem("Turn Pointer On", null, (s, e) => OnToggle?.Invoke());
            clearItem = new ToolStripMenuItem("Clear Drawing", null, (s, e) => OnClear?.Invoke());
            soundItem = new ToolStripMenuItem("Tap Sound", null, (s, e) => OnToggleSound?.Invoke());
            menu.Items.Add(toggleItem);
            menu.Items.Add(clearItem);
            menu.Items.Add(soundItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Settings…", null, (s, e) => OnSettings?.Invoke()));
            menu.Items.Add(new ToolStripMenuItem("Check for Updates…", null, (s, e) => OnCheckForUpdates?.Invoke()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Quit TinyHand", null, (s, e) => OnQuit?.Invoke()));
            menu.Opening += (s, e) =>
            {
                Refresh();
                OnMenuOpen?.Invoke(true);
            };
            menu.Closed += (s, e) => OnMenuOpen?.Invoke(false);

            icon = new NotifyIcon { ContextMenuStrip = menu, Visible = false, Text = "TinyHand" };
            icon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    OnToggle?.Invoke();
                }
            };
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) => dispatcher.BeginInvoke(new Action(UpdateIcon));
        }

        public void Show()
        {
            UpdateIcon();
            icon.Visible = true;
        }

        private void Refresh()
        {
            bool on = IsActive?.Invoke() ?? false;
            toggleItem.Text = on ? "Turn Pointer Off" : "Turn Pointer On";
            toggleItem.ShortcutKeyDisplayString = Preferences.ToggleShortcut.Display;
            clearItem.ShortcutKeyDisplayString = Preferences.ClearShortcut.Display;
            soundItem.Checked = SoundOn?.Invoke() ?? false;
        }

        /// <summary>Redraws the icon and tooltip for the current state (Mac: updateIcon).</summary>
        public void UpdateIcon()
        {
            bool on = IsActive?.Invoke() ?? false;
            bool through = IsClickThrough?.Invoke() ?? false;
            bool dark = Theme.SystemIsDark;
            string key = $"{on}{through}{dark}";
            if (key != currentKey)
            {
                currentKey = key;
                var color = through ? Color.FromArgb(255, 149, 0) : dark ? Color.White : Color.Black;
                var next = MakeIcon(on ? "tray_on.png" : "tray_off.png", color);
                if (next != null)
                {
                    icon.Icon = next;
                    current?.Dispose();
                    current = next;
                }
            }
            icon.Text = through
                ? "TinyHand clicks go through — double-tap Shift to tap again, click to turn off"
                : on ? "TinyHand is on — click to turn off, right-click for the menu"
                     : "TinyHand — click to turn on, right-click for the menu";
        }

        private static Icon MakeIcon(string resource, Color tint)
        {
            try
            {
                using var stream = Resources.Open(resource);
                if (stream == null) return null;
                using var template = new Bitmap(stream);
                int size = Math.Max(16, Native.GetSystemMetrics(Native.SM_CXSMICON)) * 2;
                using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // Template image: keep its alpha, replace its colour.
                    var matrix = new ColorMatrix(new[]
                    {
                        new float[] { 0, 0, 0, 0, 0 },
                        new float[] { 0, 0, 0, 0, 0 },
                        new float[] { 0, 0, 0, 0, 0 },
                        new float[] { 0, 0, 0, 1, 0 },
                        new float[] { tint.R / 255f, tint.G / 255f, tint.B / 255f, 0, 1 },
                    });
                    using var attrs = new ImageAttributes();
                    attrs.SetColorMatrix(matrix);
                    g.DrawImage(template, new Rectangle(0, 0, size, size), 0, 0, template.Width, template.Height, GraphicsUnit.Pixel, attrs);
                }
                IntPtr handle = bmp.GetHicon();
                using var temp = Icon.FromHandle(handle);
                var owned = (Icon)temp.Clone();
                Native.DestroyIcon(handle);
                return owned;
            }
            catch (Exception e)
            {
                Log.Write($"tray icon failed: {e.Message}");
                return null;
            }
        }

        /// <summary>Saves the tinted icons as PNGs (self test).</summary>
        public static void SnapshotIcons(string directory)
        {
            foreach (var (name, color) in new[] { ("tray_on.png", Color.White), ("tray_off.png", Color.Black), ("tray_on.png", Color.FromArgb(255, 149, 0)) })
            {
                using var ico = MakeIcon(name, color);
                if (ico == null) continue;
                using var bmp = ico.ToBitmap();
                bmp.Save(Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)}-{color.Name}.png"), ImageFormat.Png);
            }
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            current?.Dispose();
        }
    }
}
