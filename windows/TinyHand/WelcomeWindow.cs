using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TinyHand
{
    /// <summary>
    /// The first-run card. On the Mac it walks through the Accessibility permission; Windows needs
    /// no permission for a mouse hook, so it shows the "You're all set" state with Start.
    /// Port of WelcomeWindow.swift.
    /// </summary>
    internal sealed class WelcomeWindow
    {
        public Action OnTryIt;
        public const string ShownKey = "welcomeShown";

        private Window window;
        private TextBlock subtitle;

        public void Show()
        {
            if (window == null)
            {
                window = Build();
            }
            Refresh();
            Preferences.SetFlag(ShownKey, true);
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (!window.IsVisible)
            {
                window.Show();
            }
            window.Activate();
        }

        private void Refresh()
        {
            if (subtitle != null)
            {
                subtitle.Text = $"Press {Preferences.ToggleShortcut.Display} any time to start.";
            }
        }

        private void Close()
        {
            window?.Hide();
        }

        private Window Build()
        {
            var w = new Window
            {
                Title = "Welcome to TinyHand",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = true,
                Topmost = false,
                UseLayoutRounding = true,
            };
            w.Content = BuildCard();
            w.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { w.DragMove(); } catch (InvalidOperationException) { }
                }
            };
            w.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Close();
                if (e.Key == Key.Enter) Start();
            };
            w.Closing += (s, e) =>
            {
                e.Cancel = true;
                w.Hide();
            };
            var icon = Resources.Image("AppIcon.png");
            if (icon != null) w.Icon = icon;
            return w;
        }

        public FrameworkElement BuildCard()
        {
            var card = new Border
            {
                Width = 340,
                Height = 300,
                CornerRadius = new CornerRadius(20),
                Background = Theme.Brush(Theme.IsDark ? Color.FromArgb(0xF7, 0x2A, 0x2A, 0x2C) : Color.FromArgb(0xFA, 0xF7, 0xF7, 0xF8)),
                BorderBrush = Theme.PrimaryA(0.1),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(24),
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.3 },
            };
            var grid = new Grid();
            card.Child = grid;

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            var icon = Resources.Image("AppIcon.png");
            if (icon != null)
            {
                var img = UI.Image(icon, 76);
                img.Margin = new Thickness(0, 0, 0, 16);
                stack.Children.Add(img);
            }
            var title = UI.Text("You're all set", 19, FontWeights.SemiBold);
            title.FontFamily = Theme.DisplayFont;
            title.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(title);
            subtitle = UI.Secondary($"Press {Preferences.ToggleShortcut.Display} any time to start.", 13, false);
            subtitle.HorizontalAlignment = HorizontalAlignment.Center;
            subtitle.Margin = new Thickness(0, 6, 0, 22);
            stack.Children.Add(subtitle);
            var start = UI.PrimaryButton("Start", null, true, Start);
            start.Width = 180;
            start.Height = 32;
            start.CornerRadius = new CornerRadius(8);
            start.HorizontalAlignment = HorizontalAlignment.Center;
            if (start.Child is FrameworkElement content) content.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(start);
            grid.Children.Add(stack);

            var close = new Clickable
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14, 14, 0),
                Child = UI.GlyphText(Glyph.Close, 10, Theme.PrimaryA(0.35)),
                Normal = Brushes.Transparent,
                Hover = Theme.PrimaryA(0.08),
                ToolTip = "Close",
            };
            close.Click += Close;
            grid.Children.Add(close);
            return card;
        }

        private void Start()
        {
            Close();
            OnTryIt?.Invoke();
        }
    }
}
