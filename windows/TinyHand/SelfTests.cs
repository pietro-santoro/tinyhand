using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Point = System.Drawing.Point;

namespace TinyHand
{
    /// <summary>
    /// Command-line self tests, run by CI on a Windows runner (port of RenderTest.swift,
    /// E2ETest.swift and the --*-test switches in main.swift). Each writes PNGs or a report.
    /// </summary>
    internal static class SelfTests
    {
        public static bool TryRun(string[] args, out int code)
        {
            code = 0;
            string Arg(int i, string fallback) => args.Length > i ? args[i] : fallback;
            if (args.Length == 0)
            {
                return false;
            }
            try
            {
                switch (args[0])
                {
                    case "--render-test":
                        code = RenderTest(Arg(1, "armtest.png"), args.Contains("--resting"));
                        return true;
                    case "--toolbar-test":
                        new Toolbar().Snapshot(Arg(1, "toolbar.png"), args.Contains("--hint"), args.Contains("--ink"));
                        return true;
                    case "--settings-test":
                        {
                            var section = Enum.TryParse<SettingsSection>(Arg(2, "General"), true, out var s) ? s : SettingsSection.General;
                            new SettingsWindow().Snapshot(section, Arg(1, "settings.png"), args.Contains("--dark"));
                            return true;
                        }
                    case "--welcome-test":
                        Toolbar.SaveElement(new WelcomeWindow().BuildCard(), Arg(1, "welcome.png"), System.Windows.Media.Color.FromRgb(0xE9, 0xE9, 0xEB));
                        return true;
                    case "--tray-test":
                        Directory.CreateDirectory(Arg(1, "."));
                        TrayIcon.SnapshotIcons(Arg(1, "."));
                        return true;
                    case "--make-hand":
                        {
                            var made = HandMaker.Make(Arg(1, "in.png"));
                            HandMaker.Write(made.image, Arg(2, "out.png"));
                            Console.WriteLine($"ok {made.ink:X6}");
                            return true;
                        }
                    case "--e2e-test":
                        code = E2E.Run(Arg(1, "e2e.txt"));
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception e)
            {
                File.AppendAllText("selftest-error.txt", $"{args[0]}: {e}\n");
                Console.Error.WriteLine(e);
                code = 1;
                return true;
            }
        }

        /// <summary>Every style, pressed, with an ink loop ending at the target (yellow dot).</summary>
        private static int RenderTest(string outPath, bool resting)
        {
            var styles = PointerStyle.All;
            const int pw = 420, ph = 560;
            const int columns = 8;
            int rows = (styles.Count + columns - 1) / columns;
            using var sheet = new Bitmap(pw * columns, ph * rows, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(sheet);
            g.Clear(Color.FromArgb(237, 240, 235));
            // Mac target (y-up) at (pw - 230, 380) is (190, 180) y-down.
            var target = new PointF(pw - 230, ph - 380);
            for (int i = 0; i < styles.Count; i++)
            {
                var style = styles[i];
                var arm = new ArmRenderer();
                arm.Resize(pw, ph, 1);
                arm.SetStyle(style);
                if (!resting)
                {
                    arm.Press();
                    arm.Held = true;
                }
                var center = new PointF(target.X + 40, target.Y + 8);
                double a0 = Math.Atan2(target.Y - center.Y, target.X - center.X);
                for (int k = 0; k <= 60; k++)
                {
                    double a = a0 + k / 60.0 * 2 * Math.PI;
                    var p = new PointF(center.X + 40 * (float)Math.Cos(a), center.Y + 32 * (float)Math.Sin(a));
                    if (k == 0) arm.Ink.Begin(p, arm.InkWidth); else arm.Ink.Extend(p);
                }
                arm.Ink.Extend(target);
                if (resting) arm.Ink.Clear();
                arm.Aim(target);
                var state = g.Save();
                g.TranslateTransform((i % columns) * pw, (i / columns) * ph);
                g.SetClip(new Rectangle(0, 0, pw, ph));
                arm.TakeDirty();
                arm.Draw(g, new Rectangle(0, 0, pw, ph));
                g.ResetClip();
                if (!style.IsLaser)
                {
                    using var marker = new SolidBrush(Color.FromArgb(242, 204, 26));
                    g.FillEllipse(marker, target.X - 3, target.Y - 3, 6, 6);
                }
                using (var font = new Font("Segoe UI", 11))
                using (var brush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                {
                    g.DrawString(style.Title, font, brush, 10, 10);
                }
                g.Restore(state);
            }
            sheet.Save(outPath, ImageFormat.Png);
            Console.WriteLine($"rendered {outPath} {sheet.Width}x{sheet.Height}");
            return 0;
        }
    }

    /// <summary>
    /// End-to-end run on a real desktop: turns the pointer on, injects clicks and drags with
    /// SendInput, and checks they become taps and strokes without reaching the window underneath.
    /// </summary>
    internal static class E2E
    {
        private static readonly StringBuilder Report = new StringBuilder();
        private static int failures;

        private static void Line(string text)
        {
            Report.AppendLine(text);
            Console.WriteLine(text);
        }

        private static void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            Line($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        }

        private static void Pump(double seconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static Native.INPUT Mouse(uint flags, int x = 0, int y = 0, uint data = 0)
        {
            return new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = x, dy = y, dwFlags = flags, mouseData = data } } };
        }

        private static Native.INPUT Key(ushort vk, bool up)
        {
            return new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } } };
        }

        private static uint Send(params Native.INPUT[] inputs) =>
            Native.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());

        private static void MoveTo(Point p)
        {
            int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);
            int vw = Native.GetSystemMetrics(78), vh = Native.GetSystemMetrics(79);
            int nx = (int)Math.Round((p.X - vx) * 65535.0 / Math.Max(1, vw - 1));
            int ny = (int)Math.Round((p.Y - vy) * 65535.0 / Math.Max(1, vh - 1));
            Send(Mouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, nx, ny));
        }

        private static void Down() => Send(Mouse(Native.MOUSEEVENTF_LEFTDOWN));
        private static void Up() => Send(Mouse(Native.MOUSEEVENTF_LEFTUP));

        private static void Screenshot(string path)
        {
            try
            {
                int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);
                int vw = Native.GetSystemMetrics(78), vh = Native.GetSystemMetrics(79);
                using var bmp = new Bitmap(vw, vh, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    // CAPTUREBLT includes layered windows such as the overlay.
                    IntPtr dest = g.GetHdc();
                    IntPtr screen = Native.GetDC(IntPtr.Zero);
                    Native.BitBlt(dest, 0, 0, vw, vh, screen, vx, vy, Native.SRCCOPY | Native.CAPTUREBLT);
                    Native.ReleaseDC(IntPtr.Zero, screen);
                    g.ReleaseHdc(dest);
                }
                bmp.Save(path, ImageFormat.Png);
                Line($"screenshot {path} {vw}x{vh}");
            }
            catch (Exception e)
            {
                Line($"screenshot failed: {e.Message}");
            }
        }

        public static int Run(string reportPath)
        {
            string shotBase = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".", "e2e");
            try
            {
                return RunInner(shotBase);
            }
            catch (Exception e)
            {
                Line($"FAIL  exception: {e}");
                return 1;
            }
            finally
            {
                CursorHider.EmergencyRestore();
                File.WriteAllText(reportPath, Report.ToString());
            }
        }

        private static int RunInner(string shotBase)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var dispatcher = app.Dispatcher;
            Native.GetCursorPos(out var saved);

            // A window under the pointer that counts the clicks and scrolls that leak through.
            int clicks = 0, scrolls = 0;
            var catcher = new Window
            {
                Width = 520,
                Height = 360,
                WindowStyle = WindowStyle.None,
                Topmost = true,
                ShowInTaskbar = false,
                Background = System.Windows.Media.Brushes.WhiteSmoke,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Title = "catcher",
            };
            catcher.PreviewMouseDown += (s, e) => clicks++;
            catcher.PreviewMouseWheel += (s, e) => scrolls++;
            catcher.Show();
            catcher.Activate();
            Pump(0.5);

            var topLeft = catcher.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = catcher.PointToScreen(new System.Windows.Point(catcher.ActualWidth, catcher.ActualHeight));
            var mid = new Point((int)((topLeft.X + bottomRight.X) / 2), (int)((topLeft.Y + bottomRight.Y) / 2));
            var start = new Point(mid.X - 150, mid.Y);
            Line($"catcher at {topLeft.X:0},{topLeft.Y:0} - {bottomRight.X:0},{bottomRight.Y:0}");

            // Sanity: injected clicks reach the catcher while TinyHand is off.
            MoveTo(start);
            Pump(0.1);
            Down();
            Up();
            Pump(0.3);
            Check("injected click reaches a window while the pointer is off", clicks == 1, $"clicks={clicks}");
            clicks = 0;

            Preferences.SavedStyleId = "lavender";
            var overlay = new OverlayController(dispatcher);
            bool installed = overlay.Activate();
            Check("mouse hook installed", installed);
            Pump(0.4);
            Check("pointer is active", overlay.IsActive);
            Check("real cursor hidden", CursorHider.IsHidden);

            MoveTo(start);
            Pump(0.2);
            Line($"before press: {overlay.DebugState}");
            Down();
            Pump(0.1);
            Line($"pressed: {overlay.DebugState}");
            for (int k = 1; k <= 40; k++)
            {
                MoveTo(new Point(start.X + k * 6, start.Y + (int)(30 * Math.Sin(k / 6.0))));
                Pump(0.016);
            }
            Line($"dragged: {overlay.DebugState}");
            Screenshot(shotBase + "-drawing.png");
            Up();
            Pump(0.2);
            Line($"released: {overlay.DebugState}");
            Check("drag drew a stroke", overlay.InkStrokes >= 1, $"strokes={overlay.InkStrokes}");
            Check("drag did not click the window underneath", clicks == 0, $"clicks={clicks}");

            for (int i = 0; i < 5; i++)
            {
                Send(Mouse(Native.MOUSEEVENTF_WHEEL, data: unchecked((uint)-120)));
                Pump(0.03);
            }
            Pump(0.1);
            Check("scrolling still reaches the app", scrolls > 0, $"scrolls={scrolls} of 5");

            int before = overlay.InkStrokes;
            MoveTo(start);
            Pump(0.05);
            Down();
            Pump(0.05);
            Up();
            Pump(0.2);
            Check("plain click is a tap (no new stroke)", overlay.InkStrokes == before, $"strokes {before} -> {overlay.InkStrokes}");
            Check("plain click did not reach the window", clicks == 0, $"clicks={clicks}");

            // Holding Alt clicks for real.
            Send(Key((ushort)Native.VK_MENU, false));
            Pump(0.05);
            Down();
            Pump(0.05);
            Up();
            Pump(0.1);
            Send(Key((ushort)Native.VK_MENU, true));
            Pump(0.2);
            Check("Alt + click reaches the window", clicks == 1, $"clicks={clicks}");
            clicks = 0;

            // Double-tap Shift latches click-through.
            for (int i = 0; i < 2; i++)
            {
                Send(Key((ushort)Native.VK_LSHIFT, false));
                Pump(0.04);
                Send(Key((ushort)Native.VK_LSHIFT, true));
                Pump(0.08);
            }
            Pump(0.2);
            Check("double-tap Shift turns on click-through", overlay.IsClickThrough);
            Down();
            Pump(0.05);
            Up();
            Pump(0.2);
            Check("click-through click reaches the window", clicks == 1, $"clicks={clicks}");
            for (int i = 0; i < 2; i++)
            {
                Send(Key((ushort)Native.VK_LSHIFT, false));
                Pump(0.04);
                Send(Key((ushort)Native.VK_LSHIFT, true));
                Pump(0.08);
            }
            Pump(0.2);
            Check("double-tap Shift again turns click-through off", !overlay.IsClickThrough);
            clicks = 0;

            // The toolbar takes real clicks.
            var laser = overlay.ToolbarButtonCenter(PointerStyle.Laser);
            Line($"laser button at {(laser.HasValue ? laser.Value.ToString() : "?")}");
            if (laser.HasValue)
            {
                MoveTo(laser.Value);
                Pump(0.4);
                Down();
                Pump(0.05);
                Up();
                Pump(0.4);
            }
            Check("clicking the toolbar's laser button switches style", overlay.Style == PointerStyle.Laser, $"style={overlay.Style}");
            MoveTo(start);
            Pump(0.3);
            Screenshot(shotBase + "-laser.png");
            overlay.Pick(PointerStyle.Lavender);

            // Esc turns the pointer off.
            Send(Key((ushort)Native.VK_ESCAPE, false));
            Pump(0.05);
            Send(Key((ushort)Native.VK_ESCAPE, true));
            Pump(0.3);
            Check("Esc turns the pointer off", !overlay.IsActive);
            Check("real cursor restored", !CursorHider.IsHidden);

            overlay.Deactivate();
            catcher.Close();
            MoveTo(new Point(saved.X, saved.Y));
            Pump(0.1);
            Line(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
            return failures == 0 ? 0 : 1;
        }
    }
}
