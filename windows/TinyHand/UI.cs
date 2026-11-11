using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace TinyHand
{
    /// <summary>Light/dark palette that follows the Windows app theme (the Mac follows its appearance).</summary>
    internal static class Theme
    {
        public static bool IsDark
        {
            get
            {
                if (forced.HasValue) return forced.Value;
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>The taskbar/tray theme, which can differ from the app theme.</summary>
        public static bool SystemIsDark
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    return !(key?.GetValue("SystemUsesLightTheme") is int v && v == 1);
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        private static bool? forced;

        /// <summary>For snapshot tests.</summary>
        public static void Force(bool? dark) => forced = dark;

        public static Color Primary => IsDark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x1F);
        public static Color Secondary => IsDark ? Color.FromArgb(0x99, 0xEB, 0xEB, 0xF5) : Color.FromArgb(0x99, 0x3C, 0x3C, 0x43);
        public static Color WindowBackground => IsDark ? Color.FromRgb(0x20, 0x20, 0x22) : Color.FromRgb(0xF6, 0xF6, 0xF7);
        public static Color SidebarBackground => IsDark ? Color.FromRgb(0x18, 0x18, 0x1A) : Color.FromRgb(0xEB, 0xEB, 0xEE);
        public static Color CardFill => WithAlpha(Primary, 0.045);

        public static Color Accent
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                    if (key?.GetValue("AccentColor") is int abgr)
                    {
                        var c = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
                        // Very dark or very light accents read badly on both themes; fall back.
                        double lum = 0.3 * c.R + 0.59 * c.G + 0.11 * c.B;
                        if (lum > 40 && lum < 215) return c;
                    }
                }
                catch (Exception)
                {
                    // default below
                }
                return Color.FromRgb(0x0A, 0x84, 0xFF);
            }
        }

        public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

        public static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static SolidColorBrush PrimaryA(double alpha) => Brush(WithAlpha(Primary, alpha));

        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily DisplayFont = new FontFamily("Segoe UI Variable Display, Segoe UI");

        /// <summary>Icon font: Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10.</summary>
        public static readonly FontFamily Glyphs = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        /// <summary>Gives a window the dark title bar when the theme is dark.</summary>
        public static void ApplyTitleBar(Window w)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                int dark = IsDark ? 1 : 0;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch (Exception)
            {
                // older Windows: keep the default title bar
            }
        }
    }

    /// <summary>Segoe Fluent/MDL2 code points used in place of SF Symbols.</summary>
    internal static class Glyph
    {
        public const string Settings = "";      // gearshape
        public const string Hand = "";          // hand.point.up (TouchPointer)
        public const string Sparkle = "";       // sparkles (Color)
        public const string Pen = "";           // pencil.tip (Edit)
        public const string Speaker = "";       // speaker.wave.2 (Volume)
        public const string SpeakerOff = "";    // speaker.slash (Mute)
        public const string Info = "";          // info.circle
        public const string Close = "";         // xmark
        public const string Add = "";           // plus
        public const string ChevronDown = "";
        public const string ChevronLeft = "";
        public const string OpenExternal = "";  // arrow.up.right
        public const string Copy = "";
        public const string Check = "";
        public const string Download = "";
        public const string Warning = "";
        public const string Wave = "";          // waveform (Audio)
        public const string Wand = "";          // wand.and.stars (Personalize)
        public const string Remove = "";
    }

    /// <summary>Small builders that stand in for the SwiftUI views (Card, Row, PillButton...).</summary>
    internal static class UI
    {
        public static TextBlock Text(string text, double size, FontWeight? weight = null, Brush brush = null, bool wrap = false)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = Theme.Font,
                FontSize = size,
                FontWeight = weight ?? FontWeights.Normal,
                Foreground = brush ?? Theme.Brush(Theme.Primary),
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            };
        }

        public static TextBlock Secondary(string text, double size = 11, bool wrap = true) =>
            Text(text, size, null, Theme.Brush(Theme.Secondary), wrap);

        public static TextBlock GlyphText(string glyph, double size, Brush brush = null)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = Theme.Glyphs,
                FontSize = size,
                Foreground = brush ?? Theme.Brush(Theme.Primary),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
        }

        /// <summary>A rounded card with an optional caption; rows get hairline separators.</summary>
        public static FrameworkElement Card(string title, params UIElement[] rows)
        {
            var outer = new StackPanel();
            if (title != null)
            {
                var caption = Text(title, 12, FontWeights.SemiBold, Theme.Brush(Theme.Secondary));
                caption.Margin = new Thickness(4, 0, 0, 8);
                outer.Children.Add(caption);
            }
            var stack = new StackPanel();
            bool first = true;
            foreach (var row in rows)
            {
                if (row == null) continue;
                if (!first)
                {
                    stack.Children.Add(new Border { Height = 1, Background = Theme.PrimaryA(0.07), Margin = new Thickness(14, 0, 0, 0) });
                }
                first = false;
                stack.Children.Add(row);
            }
            outer.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = Theme.Brush(Theme.CardFill), Child = stack, ClipToBounds = true });
            return outer;
        }

        /// <summary>Title (and detail) on the left, a control on the right.</summary>
        public static FrameworkElement Row(string title, string detail, UIElement trailing)
        {
            var grid = new Grid { Margin = new Thickness(14, 10, 14, 10), MinHeight = 24 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(Text(title, 13, wrap: true));
            if (detail != null)
            {
                var d = Secondary(detail);
                d.Margin = new Thickness(0, 2, 0, 0);
                left.Children.Add(d);
            }
            grid.Children.Add(left);
            if (trailing is FrameworkElement fe)
            {
                fe.VerticalAlignment = VerticalAlignment.Center;
                fe.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(fe, 1);
                grid.Children.Add(fe);
            }
            return new Border { MinHeight = 44, Child = grid };
        }

        public static Clickable TextButton(string title, Action action)
        {
            var label = Text(title, 11, FontWeights.Medium, Theme.Brush(Theme.Secondary));
            var b = new Clickable { Child = label, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            b.Hovered += h => label.Foreground = Theme.Brush(h ? Theme.Primary : Theme.Secondary);
            b.Click += action;
            return b;
        }

        public static Clickable PillButton(string title, Action action)
        {
            var b = new Clickable
            {
                Child = Text(title, 12, FontWeights.Medium),
                Padding = new Thickness(12, 6, 12, 6),
                CornerRadius = new CornerRadius(14),
                Normal = Theme.PrimaryA(0.09),
                Hover = Theme.PrimaryA(0.14),
            };
            b.Click += action;
            return b;
        }

        public static Clickable PrimaryButton(string title, string glyph, bool primary, Action action)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var fg = primary ? Brushes.White : Theme.Brush(Theme.Primary);
            if (glyph != null)
            {
                var g = GlyphText(glyph, 11, fg);
                g.Margin = new Thickness(0, 0, 6, 0);
                row.Children.Add(g);
            }
            row.Children.Add(Text(title, 12, FontWeights.SemiBold, fg));
            var accent = Theme.Accent;
            var b = new Clickable
            {
                Child = row,
                Height = 30,
                Padding = new Thickness(14, 0, 14, 0),
                CornerRadius = new CornerRadius(15),
                Normal = primary ? Theme.Brush(accent) : Theme.PrimaryA(0.09),
                Hover = primary ? Theme.Brush(Theme.WithAlpha(accent, 0.88)) : Theme.PrimaryA(0.14),
            };
            b.Click += action;
            return b;
        }

        public static Image Image(ImageSource source, double size)
        {
            var img = new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        /// <summary>A dropdown in the style of the Mac's menu pickers: label + chevron, opens a themed list.</summary>
        public static FrameworkElement MenuPicker<T>(IList<(string title, T value)> options, T selected, Action<T> onPick)
        {
            string current = "";
            foreach (var o in options)
            {
                if (EqualityComparer<T>.Default.Equals(o.value, selected)) current = o.title;
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Text(current, 13));
            var chevron = GlyphText(Glyph.ChevronDown, 9, Theme.Brush(Theme.Secondary));
            chevron.Margin = new Thickness(8, 2, 0, 0);
            row.Children.Add(chevron);
            var button = new Clickable
            {
                Child = row,
                Padding = new Thickness(10, 4, 8, 5),
                CornerRadius = new CornerRadius(6),
                Normal = Theme.PrimaryA(0.06),
                Hover = Theme.PrimaryA(0.1),
                BorderBrush = Theme.PrimaryA(0.1),
                BorderThickness = new Thickness(1),
            };
            var list = new StackPanel { Margin = new Thickness(4) };
            var popup = new Popup
            {
                PlacementTarget = button,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                VerticalOffset = 4,
                Child = new Border
                {
                    Margin = new Thickness(0, 0, 10, 12),
                    CornerRadius = new CornerRadius(8),
                    Background = Theme.Brush(Theme.IsDark ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Colors.White),
                    BorderBrush = Theme.PrimaryA(0.12),
                    BorderThickness = new Thickness(1),
                    Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.22, Direction = 270 },
                    Child = list,
                },
            };
            foreach (var o in options)
            {
                var item = new Clickable
                {
                    Padding = new Thickness(10, 5, 16, 5),
                    CornerRadius = new CornerRadius(5),
                    Normal = Brushes.Transparent,
                    Hover = Theme.Brush(Theme.WithAlpha(Theme.Accent, 0.9)),
                };
                var check = GlyphText(EqualityComparer<T>.Default.Equals(o.value, selected) ? Glyph.Check : "", 10);
                check.Width = 18;
                var label = Text(o.title, 13);
                var itemRow = new StackPanel { Orientation = Orientation.Horizontal };
                itemRow.Children.Add(check);
                itemRow.Children.Add(label);
                item.Child = itemRow;
                item.Hovered += h =>
                {
                    var fg = h ? Brushes.White : Theme.Brush(Theme.Primary);
                    label.Foreground = fg;
                    check.Foreground = fg;
                };
                var value = o.value;
                item.Click += () =>
                {
                    popup.IsOpen = false;
                    onPick(value);
                };
                list.Children.Add(item);
            }
            button.Click += () => popup.IsOpen = !popup.IsOpen;
            var host = new Grid();
            host.Children.Add(button);
            host.Children.Add(popup);
            return host;
        }

        public static FrameworkElement Switch(bool on, Action<bool> changed, bool enabled = true)
        {
            var s = new ToggleSwitch { IsOn = on, IsEnabled = enabled };
            s.Toggled += changed;
            return s;
        }
    }

    /// <summary>A Border that behaves like a plain button (SwiftUI .buttonStyle(.plain)).</summary>
    internal class Clickable : Border
    {
        public event Action Click;
        public event Action<bool> Hovered;
        private Brush normal;
        private Brush hover;
        private bool pressed;
        private bool over;

        /// <summary>Background when idle; applied right away (not only once loaded).</summary>
        public Brush Normal
        {
            get => normal;
            set
            {
                normal = value;
                if (!over && value != null) Background = value;
            }
        }

        public Brush Hover
        {
            get => hover;
            set
            {
                hover = value;
                if (over && value != null) Background = value;
            }
        }

        public Clickable()
        {
            Focusable = false;
            Background = Brushes.Transparent;
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            over = true;
            if (Hover != null) Background = Hover;
            Hovered?.Invoke(true);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            over = false;
            if (Normal != null) Background = Normal;
            Hovered?.Invoke(false);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            pressed = true;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            bool was = pressed;
            pressed = false;
            ReleaseMouseCapture();
            if (!was) return;
            var p = e.GetPosition(this);
            if (p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight)
            {
                e.Handled = true;
                Click?.Invoke();
            }
        }

        /// <summary>For tests and keyboard-free automation.</summary>
        public void PerformClick() => Click?.Invoke();
    }

    /// <summary>The Mac's switch-style toggle.</summary>
    internal sealed class ToggleSwitch : Border
    {
        public event Action<bool> Toggled;
        private readonly Ellipse knob;
        private readonly TranslateTransform shift = new TranslateTransform();
        private bool isOn;

        public ToggleSwitch()
        {
            Width = 38;
            Height = 22;
            CornerRadius = new CornerRadius(11);
            Cursor = Cursors.Hand;
            Focusable = false;
            knob = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 0, 0, 0),
                RenderTransform = shift,
                Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.25, Direction = 270 },
            };
            Child = knob;
            Apply(false);
            IsEnabledChanged += (s, e) => Opacity = IsEnabled ? 1 : 0.45;
        }

        public bool IsOn
        {
            get => isOn;
            set
            {
                isOn = value;
                Apply(false);
            }
        }

        private void Apply(bool animate)
        {
            Background = isOn ? Theme.Brush(Theme.Accent) : Theme.PrimaryA(Theme.IsDark ? 0.25 : 0.18);
            double to = isOn ? 16 : 0;
            if (animate)
            {
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase() });
            }
            else
            {
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                shift.X = to;
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!IsEnabled) return;
            isOn = !isOn;
            Apply(true);
            e.Handled = true;
            Toggled?.Invoke(isOn);
        }
    }

    /// <summary>A slim slider: accent fill, round white knob.</summary>
    internal sealed class PillSlider : Grid
    {
        public event Action<double> ValueChanged;
        private readonly double min, max;
        private double value;
        private readonly Border fill;
        private readonly Ellipse thumb;
        private bool dragging;

        public PillSlider(double min, double max, double value)
        {
            this.min = min;
            this.max = max;
            this.value = value;
            Height = 22;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.PrimaryA(0.14), VerticalAlignment = VerticalAlignment.Center };
            fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Brush(Theme.Accent), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            thumb = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = Brushes.White,
                Stroke = Theme.PrimaryA(0.12),
                StrokeThickness = 0.5,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.3, Direction = 270 },
            };
            Children.Add(track);
            Children.Add(fill);
            Children.Add(thumb);
            SizeChanged += (s, e) => Layout();
        }

        private void Layout()
        {
            double t = (value - min) / (max - min);
            double usable = Math.Max(0, ActualWidth - thumb.Width);
            thumb.Margin = new Thickness(t * usable, 0, 0, 0);
            fill.Width = t * usable + thumb.Width / 2;
        }

        private void SetFrom(Point p)
        {
            double usable = Math.Max(1, ActualWidth - thumb.Width);
            double t = Math.Clamp((p.X - thumb.Width / 2) / usable, 0, 1);
            value = min + t * (max - min);
            Layout();
            ValueChanged?.Invoke(value);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            dragging = true;
            CaptureMouse();
            SetFrom(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetFrom(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            dragging = false;
            ReleaseMouseCapture();
        }
    }
}
