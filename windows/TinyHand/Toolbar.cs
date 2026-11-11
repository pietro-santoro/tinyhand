using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DrawingRect = System.Drawing.Rectangle;

namespace TinyHand
{
    /// <summary>
    /// A borderless, non-activating, topmost WPF window that floats a dark glass card
    /// (the Mac's NSPanel with the vibrant HUD material).
    /// </summary>
    internal sealed class FloatingPanel : Window
    {
        public const double ShadowMargin = 18;
        public readonly Border Card;
        private bool clickThrough;

        public FloatingPanel(double radius, bool clickThrough = false)
        {
            this.clickThrough = clickThrough;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Focusable = false;
            Opacity = 0;
            Title = "TinyHand";
            UseLayoutRounding = true;
            Card = new Border
            {
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(Color.FromArgb(0xDB, 0x1E, 0x1E, 0x20)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(ShadowMargin),
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.38, Color = Colors.Black },
            };
            Content = Card;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | (this.clickThrough ? Native.WS_EX_TRANSPARENT : 0));
                HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            };
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        }

        public IntPtr Handle => new WindowInteropHelper(this).Handle;

        public void SetClickThrough(bool value)
        {
            clickThrough = value;
            var hwnd = Handle;
            if (hwnd != IntPtr.Zero)
            {
                Native.AddExStyle(hwnd, value ? Native.WS_EX_TRANSPARENT : 0, value ? 0 : Native.WS_EX_TRANSPARENT);
            }
            IsHitTestVisible = !value;
        }

        public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

        /// <summary>The card's rectangle on screen in physical pixels (without the shadow margin).</summary>
        public DrawingRect CardRect
        {
            get
            {
                var hwnd = Handle;
                if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r))
                {
                    return DrawingRect.Empty;
                }
                int m = (int)Math.Round(ShadowMargin * DpiScale);
                return DrawingRect.FromLTRB(r.Left + m, r.Top + m, r.Right - m, r.Bottom - m);
            }
        }

        /// <summary>Places the window so its card's top-left corner lands at (x, y) physical pixels.</summary>
        public void MoveCardTo(int x, int y)
        {
            var hwnd = Handle;
            if (hwnd == IntPtr.Zero) return;
            int m = (int)Math.Round(ShadowMargin * DpiScale);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x - m, y - m, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        public void FadeTo(double opacity, double seconds, Action done = null)
        {
            var anim = new DoubleAnimation(opacity, TimeSpan.FromSeconds(seconds));
            if (done != null)
            {
                anim.Completed += (s, e) => done();
            }
            BeginAnimation(OpacityProperty, anim);
        }

        public void SetOpacityNow(double opacity)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = opacity;
        }

        public void BringToFront()
        {
            var hwnd = Handle;
            if (hwnd != IntPtr.Zero)
            {
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            }
        }
    }

    /// <summary>
    /// The toolbar at the bottom of the screen: pinned pointers, ink colour, tap sound, exit.
    /// Port of Toolbar.swift, including the ink tray and the hint bubble.
    /// </summary>
    internal sealed class Toolbar
    {
        public Action<PointerStyle> OnPick;
        public Action OnToggleSound;
        public Action OnExit;
        public Action OnFixPermission;

        private readonly FloatingPanel panel;
        private readonly FloatingPanel tray;
        private FloatingPanel hint;
        private readonly StackPanel stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 8) };
        private readonly StackPanel trayStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 5, 6, 5) };
        private readonly Dictionary<PointerStyle, Clickable> styleButtons = new Dictionary<PointerStyle, Clickable>();
        private List<PointerStyle> shownStyles = new List<PointerStyle>();
        private PointerStyle current;
        private readonly Clickable inkButton;
        private readonly Image inkImage;
        private readonly Dictionary<InkChoice, Clickable> swatches = new Dictionary<InkChoice, Clickable>();
        private bool inkOpen;
        private readonly Clickable permissionButton;
        private readonly Clickable soundButton;
        private readonly TextBlock soundGlyph;
        private readonly Border divider;
        private bool shown;

        public bool Revealed { get; private set; }

        // Where the card sits: horizontal centre and bottom edge, physical pixels.
        private int anchorX;
        private int anchorBottom;
        private bool anchored;

        private static readonly Brush Selected = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

        public Toolbar()
        {
            panel = new FloatingPanel(16);
            panel.Card.Child = stack;
            tray = new FloatingPanel(14);
            tray.Card.Child = trayStack;

            divider = new Border { Width = 1, Height = 24, Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(divider);

            inkImage = new Image { Width = 40, Height = 40 };
            RenderOptions.SetBitmapScalingMode(inkImage, BitmapScalingMode.HighQuality);
            inkButton = MakeButton(inkImage, "Ink colour", () => SetInkOpen(!inkOpen));
            stack.Children.Add(inkButton);

            soundGlyph = UI.GlyphText(Glyph.Speaker, 15, Brushes.White);
            soundButton = MakeButton(soundGlyph, "Tap sound", () => OnToggleSound?.Invoke());
            stack.Children.Add(soundButton);
            stack.Children.Add(MakeButton(UI.GlyphText(Glyph.Close, 13, new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF))), "Exit  (Esc)", () => OnExit?.Invoke()));

            var permRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            permRow.Children.Add(UI.GlyphText(Glyph.Warning, 12, Brushes.Black));
            var permText = UI.Text("  Clicks can't be caught — retry  ", 12, FontWeights.SemiBold, Brushes.Black);
            permRow.Children.Add(permText);
            permissionButton = new Clickable
            {
                Child = permRow,
                Height = 32,
                Padding = new Thickness(10, 0, 4, 0),
                CornerRadius = new CornerRadius(10),
                Normal = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0x95, 0x00)),
                Hover = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x95, 0x00)),
                Margin = new Thickness(3, 0, 3, 0),
                Visibility = Visibility.Collapsed,
                ToolTip = "TinyHand couldn't install its mouse hook, so clicks reach apps. Click to try again.",
            };
            permissionButton.Click += () => OnFixPermission?.Invoke();
            stack.Children.Add(permissionButton);

            panel.SizeChanged += (s, e) => Reposition();
            panel.DpiChanged += (s, e) => panel.Dispatcher.BeginInvoke(new Action(Reposition));

            RebuildStyles();
            RefreshInk();
            Preferences.Changed += () =>
            {
                RebuildStyles();
                RefreshInk();
            };
        }

        public static Clickable MakeButton(UIElement content, string tip, Action action, double side = 40)
        {
            var b = new Clickable
            {
                Width = side,
                Height = side,
                CornerRadius = new CornerRadius(side == 40 ? 10 : side / 2),
                Child = content,
                ToolTip = tip,
                Normal = Brushes.Transparent,
                Hover = HoverBrush,
                Margin = new Thickness(3, 0, 3, 0),
            };
            AutomationProperties(b, tip);
            b.Click += action;
            return b;
        }

        private static void AutomationProperties(UIElement e, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(e, name);

        private void SetButtonBackground(Clickable b, Brush brush)
        {
            b.Normal = brush;
            b.Hover = ReferenceEquals(brush, Selected) ? Selected : HoverBrush;
            b.Background = brush;
        }

        public DrawingRect Frame
        {
            get
            {
                var f = panel.CardRect;
                if (inkOpen)
                {
                    var t = tray.CardRect;
                    if (!t.IsEmpty) f = DrawingRect.Union(f, t);
                }
                return f;
            }
        }

        public IntPtr PanelHandle => panel.Handle;

        private void SetInkOpen(bool open)
        {
            if (open == inkOpen)
            {
                return;
            }
            inkOpen = open;
            if (open)
            {
                if (swatches.Count == 0)
                {
                    foreach (var choice in InkChoiceInfo.All)
                    {
                        var img = new Image { Width = 30, Height = 30 };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                        var c = choice;
                        var b = MakeButton(img, choice.Title(), () =>
                        {
                            Preferences.InkChoice = c;
                            SetInkOpen(false);
                        }, 30);
                        b.Margin = new Thickness(1, 0, 1, 0);
                        swatches[choice] = b;
                        trayStack.Children.Add(b);
                    }
                }
                RefreshInk();
                tray.SetOpacityNow(0);
                if (!tray.IsVisible)
                {
                    tray.Show();
                }
                tray.UpdateLayout();
                PlaceTray();
                tray.BringToFront();
                tray.FadeTo(1, 0.12);
            }
            else
            {
                tray.FadeTo(0, 0.12, () =>
                {
                    if (!inkOpen) tray.Hide();
                });
            }
            RefreshInk();
        }

        private void PlaceTray()
        {
            if (!inkButton.IsVisible || PresentationSource.FromVisual(inkButton) == null)
            {
                return;
            }
            var center = inkButton.PointToScreen(new Point(inkButton.ActualWidth / 2, 0));
            var card = panel.CardRect;
            var trayCard = tray.CardRect;
            double scale = panel.DpiScale;
            int x = (int)Math.Round(center.X - trayCard.Width / 2.0);
            int y = card.Top - (int)Math.Round(8 * scale) - trayCard.Height;
            tray.MoveCardTo(x, y);
        }

        private void RefreshInk()
        {
            var style = current ?? PointerStyle.Saved;
            inkImage.Source = InkDot(Preferences.InkChoice.Resolved(style));
            SetButtonBackground(inkButton, inkOpen ? Selected : Brushes.Transparent);
            bool hidden = !Preferences.ShowInkButton;
            var visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            if (inkButton.Visibility != visibility)
            {
                inkButton.Visibility = visibility;
                if (hidden) SetInkOpen(false);
            }
            foreach (var pair in swatches)
            {
                var img = (Image)pair.Value.Child;
                var color = pair.Key.Color();
                img.Source = color.HasValue ? InkDot(color.Value) : RainbowDot();
                SetButtonBackground(pair.Value, pair.Key == Preferences.InkChoice ? Selected : Brushes.Transparent);
            }
        }

        private void RebuildStyles()
        {
            var styles = Preferences.PinnedStyles;
            if (current != null && !styles.Contains(current))
            {
                styles.Add(current);
            }
            if (styles.SequenceEqual(shownStyles))
            {
                return;
            }
            shownStyles = styles;
            foreach (var b in styleButtons.Values)
            {
                stack.Children.Remove(b);
            }
            styleButtons.Clear();
            for (int i = 0; i < styles.Count; i++)
            {
                var style = styles[i];
                var b = MakeButton(UI.Image(Icon(style), 36), style.Title, () => OnPick?.Invoke(style));
                styleButtons[style] = b;
                stack.Children.Insert(i, b);
            }
            if (current != null)
            {
                Highlight(current);
            }
        }

        public void SetSoundOn(bool on)
        {
            soundGlyph.Text = on ? Glyph.Speaker : Glyph.SpeakerOff;
            soundGlyph.Foreground = new SolidColorBrush(Color.FromArgb((byte)(on ? 0xD9 : 0x73), 0xFF, 0xFF, 0xFF));
            soundButton.ToolTip = on ? "Tap sound on — click to mute" : "Tap sound off — click to unmute";
        }

        /// <summary>Centre of a pointer's button in physical pixels (self test).</summary>
        public System.Drawing.Point? CenterOf(PointerStyle style)
        {
            if (!styleButtons.TryGetValue(style, out var b) || PresentationSource.FromVisual(b) == null)
            {
                return null;
            }
            var p = b.PointToScreen(new Point(b.ActualWidth / 2, b.ActualHeight / 2));
            return new System.Drawing.Point((int)p.X, (int)p.Y);
        }

        public void ShowHint(IList<(string key, string text)> parts)
        {
            HideHint();
            var p = new FloatingPanel(12, clickThrough: true);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 9, 16, 9) };
            for (int i = 0; i < parts.Count; i++)
            {
                var tb = new TextBlock { FontFamily = Theme.Font, FontSize = 12.5, Margin = new Thickness(i == 0 ? 0 : 14, 0, 0, 0) };
                tb.Inlines.Add(new Run(parts[i].key) { FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
                tb.Inlines.Add(new Run(" " + parts[i].text) { Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)) });
                row.Children.Add(tb);
            }
            p.Card.Child = row;
            hint = p;
            if (!shown)
            {
                return; // rendered for snapshots only
            }
            p.Show();
            p.UpdateLayout();
            var card = panel.CardRect;
            var size = p.CardRect;
            int x = card.Left + card.Width / 2 - size.Width / 2;
            int y = card.Top - (int)Math.Round(12 * panel.DpiScale) - size.Height;
            p.MoveCardTo(x, y);
            p.BringToFront();
            p.FadeTo(1, 0.2);
        }

        public void HideHint()
        {
            var p = hint;
            if (p == null)
            {
                return;
            }
            hint = null;
            if (!p.IsVisible)
            {
                p.Close();
                return;
            }
            p.FadeTo(0, 0.3, () => p.Close());
        }

        public void Select(PointerStyle style)
        {
            current = style;
            RebuildStyles();
            Highlight(style);
            RefreshInk();
        }

        private void Highlight(PointerStyle style)
        {
            foreach (var pair in styleButtons)
            {
                SetButtonBackground(pair.Value, pair.Key == style ? Selected : Brushes.Transparent);
            }
        }

        public void SetNeedsPermission(bool needed)
        {
            permissionButton.Visibility = needed ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Bottom centre of the monitor's work area (above the taskbar), like above the Dock.</summary>
        public void Place(DrawingRect workArea, double monitorScale)
        {
            SetInkOpen(false);
            anchorX = workArea.Left + workArea.Width / 2;
            anchorBottom = workArea.Bottom - (int)Math.Round(14 * monitorScale);
            anchored = true;
            Reposition();
        }

        private void Reposition()
        {
            if (!anchored || !panel.IsVisible)
            {
                return;
            }
            var card = panel.CardRect;
            if (card.IsEmpty)
            {
                return;
            }
            panel.MoveCardTo(anchorX - card.Width / 2, anchorBottom - card.Height);
            if (inkOpen) PlaceTray();
        }

        public void Show()
        {
            shown = true;
            if (!panel.IsVisible)
            {
                panel.SetClickThrough(!Revealed);
                panel.Show();
                panel.UpdateLayout();
            }
            Reposition();
            panel.BringToFront();
        }

        public void Hide()
        {
            SetInkOpen(false);
            tray.Hide();
            HideHint();
            Revealed = false;
            shown = false;
            panel.SetOpacityNow(0);
            panel.Hide();
        }

        public void SetRevealed(bool value)
        {
            if (value == Revealed)
            {
                return;
            }
            Revealed = value;
            if (!value)
            {
                SetInkOpen(false);
            }
            panel.SetClickThrough(!value);
            panel.FadeTo(value ? 1 : 0, value ? 0.15 : 0.35);
        }

        public void KeepOnTop()
        {
            if (!shown) return;
            panel.BringToFront();
            if (inkOpen) tray.BringToFront();
            hint?.BringToFront();
        }

        // ---- Images ----

        public static ImageSource Icon(PointerStyle style)
        {
            if (style.AssetName != null)
            {
                return HandAsset.CachedThumbnail(style.AssetName) ?? HandAsset.Thumbnail(style.AssetName) ?? Blank;
            }
            return LaserIcon();
        }

        private static readonly ImageSource Blank = new DrawingImage(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));

        private static DrawingImage Freeze(DrawingGroup g)
        {
            g.Children.Insert(0, new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));
            var image = new DrawingImage(g);
            image.Freeze();
            return image;
        }

        public static ImageSource InkDot(Color color)
        {
            var g = new DrawingGroup();
            var c = new Point(32, 32);
            const double r = 13;
            // y-down version of the Mac drawing: shadow 1 pt lower, highlight in the upper left.
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)), null, new EllipseGeometry(new Point(c.X, c.Y + 1), r + 1.5, r + 1.5)));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, new EllipseGeometry(c, r, r)));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)), null,
                new EllipseGeometry(new Rect(c.X - r * 0.55, c.Y - r * 0.15 - r * 0.5, r * 0.7, r * 0.5))));
            return Freeze(g);
        }

        public static ImageSource RainbowDot()
        {
            var g = new DrawingGroup();
            var c = new Point(32, 32);
            const double r = 13;
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)), null, new EllipseGeometry(new Point(c.X, c.Y + 1), r + 1.5, r + 1.5)));
            var colors = new[] { Color.FromRgb(255, 59, 48), Color.FromRgb(255, 149, 0), Color.FromRgb(255, 204, 0), Color.FromRgb(52, 199, 89), Color.FromRgb(0, 122, 255), Color.FromRgb(175, 82, 222) };
            for (int i = 0; i < colors.Length; i++)
            {
                // Mac angles are counter-clockwise from +x in y-up space; flip y.
                double a0 = (i * 60 + 90) * Math.PI / 180;
                double a1 = ((i + 1) * 60 + 90) * Math.PI / 180;
                var p0 = new Point(c.X + r * Math.Cos(a0), c.Y - r * Math.Sin(a0));
                var p1 = new Point(c.X + r * Math.Cos(a1), c.Y - r * Math.Sin(a1));
                var fig = new PathFigure { StartPoint = c, IsClosed = true };
                fig.Segments.Add(new LineSegment(p0, true));
                fig.Segments.Add(new ArcSegment(p1, new Size(r, r), 0, false, SweepDirection.Counterclockwise, true));
                g.Children.Add(new GeometryDrawing(new SolidColorBrush(colors[i]), null, new PathGeometry(new[] { fig })));
            }
            return Freeze(g);
        }

        public static ImageSource LaserIcon()
        {
            var g = new DrawingGroup();
            var c = new Point(32, 32);
            var red = Ink.Red;
            var glow = new RadialGradientBrush(Color.FromArgb(0x8C, red.R, red.G, red.B), Color.FromArgb(0, red.R, red.G, red.B));
            g.Children.Add(new GeometryDrawing(glow, null, new EllipseGeometry(c, 18, 18)));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(red), null, new EllipseGeometry(c, 7, 7)));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0xF2, 255, 219, 209)), null, new EllipseGeometry(c, 3, 3)));
            return Freeze(g);
        }

        // ---- Snapshots (self tests) ----

        public static void SaveElement(FrameworkElement element, string path, Color? background, double scale = 2)
        {
            var host = new Border { Child = element, Background = background.HasValue ? new SolidColorBrush(background.Value) : Brushes.Transparent };
            TextOptions.SetTextFormattingMode(host, TextFormattingMode.Ideal);
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = host.DesiredSize;
            host.Arrange(new Rect(size));
            host.UpdateLayout();
            int w = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
            int h = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
            var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var file = File.Create(path);
            encoder.Save(file);
            host.Child = null;
        }

        public void Snapshot(string path, bool withHint, bool withInk)
        {
            panel.Content = null;
            SaveElement(panel.Card, path, Color.FromRgb(0x29, 0x29, 0x29));
            if (withHint)
            {
                ShowHint(new List<(string, string)> { ("Click", "to tap"), ("Drag", "to draw"), ("Hold Alt", "to click for real"), ("Esc", "to exit") });
                if (hint != null)
                {
                    hint.Content = null;
                    SaveElement(hint.Card, path.Replace(".png", "-hint.png"), Color.FromRgb(0x29, 0x29, 0x29));
                }
            }
            if (withInk)
            {
                inkOpen = true;
                foreach (var choice in InkChoiceInfo.All)
                {
                    var img = new Image { Width = 30, Height = 30 };
                    var b = MakeButton(img, choice.Title(), () => { }, 30);
                    b.Margin = new Thickness(1, 0, 1, 0);
                    swatches[choice] = b;
                    trayStack.Children.Add(b);
                }
                RefreshInk();
                tray.Content = null;
                SaveElement(tray.Card, path.Replace(".png", "-ink.png"), Color.FromRgb(0x29, 0x29, 0x29));
            }
        }
    }
}
