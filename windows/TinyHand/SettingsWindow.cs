using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Bitmap = System.Drawing.Bitmap;

namespace TinyHand
{
    internal enum SettingsSection { General, Pointers, Appearance, Drawing, Sound, About }

    /// <summary>
    /// The settings window: a sidebar of sections and a scrolling page of cards, rebuilt from the
    /// preferences whenever they change (the way the SwiftUI view re-renders). Port of
    /// SettingsWindow.swift + SettingsView.swift.
    /// </summary>
    internal sealed class SettingsWindow
    {
        public Func<Shortcut, bool> ApplyToggleShortcut;
        public Func<Shortcut, bool> ApplyClearShortcut;
        public Action<bool> OnRecording;

        public const string ReleasesUrl = "https://github.com/pietro-santoro/tinyhand/releases";

        private Window window;
        private Grid root;
        private StackPanel sidebarRows;
        private ScrollViewer scroller;
        private SettingsSection section = SettingsSection.General;
        private readonly DispatcherTimer rebuildTimer;
        private bool recording;
        private bool dragging;

        private string shortcutError;
        private string pinMessage;
        private string handMessage;

        // "Make your own hand"
        private bool makingHand;
        private string handIdea = "";
        private string handName = "";
        private (Bitmap image, uint ink)? draft;
        private string draftError;
        private bool promptCopied;
        private TapSound previewSound;

        public SettingsWindow()
        {
            rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            rebuildTimer.Tick += (s, e) =>
            {
                if (recording || dragging || Mouse.Captured is PillSlider)
                {
                    return; // try again on the next tick
                }
                rebuildTimer.Stop();
                Rebuild();
            };
            Preferences.Changed += ScheduleRebuild;
            CustomHands.Changed += ScheduleRebuild;
        }

        private void ScheduleRebuild()
        {
            if (window == null || !window.IsVisible)
            {
                return;
            }
            rebuildTimer.Stop();
            rebuildTimer.Start();
        }

        public void Show(SettingsSection? target = null)
        {
            if (window == null)
            {
                Build();
            }
            if (target.HasValue)
            {
                section = target.Value;
            }
            shortcutError = null;
            pinMessage = null;
            HandAsset.PreloadThumbnails(PointerStyle.All.Where(s => s.AssetName != null).Select(s => s.AssetName), Rebuild);
            Rebuild();
            if (!window.IsVisible)
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                window.Show();
            }
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }
            window.Activate();
        }

        private void Build()
        {
            window = new Window
            {
                Title = "TinyHand Settings",
                Width = 780,
                Height = 560,
                MinWidth = 760,
                MinHeight = 540,
                ResizeMode = ResizeMode.CanMinimize,
                Background = Theme.Brush(Theme.WindowBackground),
                UseLayoutRounding = true,
                FontFamily = Theme.Font,
            };
            var icon = Resources.Image("AppIcon.png");
            if (icon != null) window.Icon = icon;
            window.SourceInitialized += (s, e) => Theme.ApplyTitleBar(window);
            window.Closing += (s, e) =>
            {
                e.Cancel = true;
                StopRecording?.Invoke();
                OnRecording?.Invoke(false);
                window.Hide();
            };
            window.Deactivated += (s, e) => StopRecording?.Invoke();
            window.Content = BuildRoot();
        }

        private FrameworkElement BuildRoot()
        {
            root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var sidebar = new DockPanel { Background = Theme.Brush(Theme.SidebarBackground), LastChildFill = true };
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 10, 16) };
            var icon = Resources.Image("AppIcon.png");
            if (icon != null) footer.Children.Add(UI.Image(icon, 22));
            var name = UI.Text("TinyHand", 12, FontWeights.SemiBold);
            name.Margin = new Thickness(8, 0, 0, 0);
            name.VerticalAlignment = VerticalAlignment.Center;
            footer.Children.Add(name);
            DockPanel.SetDock(footer, Dock.Bottom);
            sidebar.Children.Add(footer);
            sidebarRows = new StackPanel { Margin = new Thickness(10, 16, 10, 0) };
            sidebar.Children.Add(sidebarRows);
            root.Children.Add(sidebar);

            scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
            Grid.SetColumn(scroller, 1);
            root.Children.Add(scroller);
            return root;
        }

        private void Rebuild()
        {
            if (root == null)
            {
                return;
            }
            sidebarRows.Children.Clear();
            foreach (SettingsSection s in Enum.GetValues(typeof(SettingsSection)))
            {
                sidebarRows.Children.Add(SidebarRow(s));
            }
            var offset = scroller.VerticalOffset;
            var page = new StackPanel { Margin = new Thickness(28, 18, 28, 28) };
            if (section == SettingsSection.Pointers && makingHand)
            {
                var back = new StackPanel { Orientation = Orientation.Horizontal };
                back.Children.Add(UI.GlyphText(Glyph.ChevronLeft, 10, Theme.Brush(Theme.Secondary)));
                var label = UI.Text("Pointers", 12, FontWeights.Medium, Theme.Brush(Theme.Secondary));
                label.Margin = new Thickness(4, 0, 0, 0);
                back.Children.Add(label);
                var backButton = new Clickable { Child = back, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 8) };
                backButton.Click += () =>
                {
                    makingHand = false;
                    Rebuild();
                };
                page.Children.Add(backButton);
            }
            var title = UI.Text(section == SettingsSection.Pointers && makingHand ? "Make your own hand" : section.ToString(), 22, FontWeights.Bold);
            title.FontFamily = Theme.DisplayFont;
            title.Margin = new Thickness(0, 6, 0, 22);
            page.Children.Add(title);
            page.Children.Add(section switch
            {
                SettingsSection.General => GeneralPage(),
                SettingsSection.Pointers => makingHand ? MakeHandPage() : PointersPage(),
                SettingsSection.Appearance => AppearancePage(),
                SettingsSection.Drawing => DrawingPage(),
                SettingsSection.Sound => SoundPage(),
                _ => AboutPage(),
            });
            scroller.Content = page;
            scroller.ScrollToVerticalOffset(offset);
        }

        private static readonly Dictionary<SettingsSection, (string glyph, Color tint)> SectionLook = new Dictionary<SettingsSection, (string, Color)>
        {
            [SettingsSection.General] = (Glyph.Settings, Color.FromRgb(140, 140, 140)),
            [SettingsSection.Pointers] = (Glyph.Hand, Color.FromRgb(140, 107, 242)),
            [SettingsSection.Appearance] = (Glyph.Sparkle, Color.FromRgb(242, 115, 153)),
            [SettingsSection.Drawing] = (Glyph.Pen, Color.FromRgb(245, 148, 51)),
            [SettingsSection.Sound] = (Glyph.Speaker, Color.FromRgb(51, 140, 250)),
            [SettingsSection.About] = (Glyph.Info, Color.FromRgb(115, 115, 115)),
        };

        private FrameworkElement SidebarRow(SettingsSection s)
        {
            bool selected = s == section;
            var look = SectionLook[s];
            var tile = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Background = new LinearGradientBrush(Theme.WithAlpha(look.tint, 0.85), look.tint, 90),
                Child = UI.GlyphText(look.glyph, 12, Brushes.White),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(tile);
            var label = UI.Text(s.ToString(), 13, selected ? FontWeights.SemiBold : FontWeights.Normal);
            label.Margin = new Thickness(10, 0, 0, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            var b = new Clickable
            {
                Child = row,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 2),
                CornerRadius = new CornerRadius(8),
                Normal = selected ? Theme.PrimaryA(0.1) : Brushes.Transparent,
                Hover = selected ? Theme.PrimaryA(0.1) : Theme.PrimaryA(0.05),
            };
            b.Click += () =>
            {
                section = s;
                makingHand = makingHand && s == SettingsSection.Pointers;
                Rebuild();
                scroller.ScrollToTop();
            };
            return b;
        }

        private static StackPanel VStack(double spacing, params UIElement[] children)
        {
            var panel = new StackPanel();
            bool first = true;
            foreach (var c in children)
            {
                if (c == null) continue;
                if (!first && c is FrameworkElement fe)
                {
                    var m = fe.Margin;
                    fe.Margin = new Thickness(m.Left, m.Top + spacing, m.Right, m.Bottom);
                }
                first = false;
                panel.Children.Add(c);
            }
            return panel;
        }

        private static TextBlock ErrorText(string text)
        {
            var t = UI.Text(text, 12, null, Theme.Brush(Color.FromRgb(0xE5, 0x39, 0x35)), true);
            t.Margin = new Thickness(4, 0, 4, 0);
            return t;
        }

        private static void Open(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                Log.Write($"could not open {url}: {e.Message}");
            }
        }

        // ---------------- General ----------------

        private FrameworkElement GeneralPage()
        {
            var interact = Preferences.InteractKey;
            return VStack(22,
                UI.Card("Shortcuts",
                    UI.Row("Turn the pointer on or off", null, new ShortcutRecorder(this, Preferences.ToggleShortcut, s => ApplyToggleShortcut?.Invoke(s) ?? false)),
                    UI.Row("Clear the drawing", null, new ShortcutRecorder(this, Preferences.ClearShortcut, s => ApplyClearShortcut?.Invoke(s) ?? false))),
                shortcutError == null ? null : ErrorText(shortcutError),
                UI.Card("Clicking for real",
                    UI.Row("Hold to use the app", "Clicks, drags and scrolls reach the app.",
                        UI.MenuPicker(InteractKeyInfo.All.Select(k => (k.Title(), k)).ToList(), interact, k => Preferences.InteractKey = k)),
                    interact == InteractKey.Off ? null : UI.Row("While holding",
                        Preferences.InteractKeepsHand ? "The hand stays, in menus too." : "The normal cursor comes back, in menus too.",
                        UI.MenuPicker(new List<(string, bool)> { ("Show the cursor", false), ("Keep the hand", true) }, Preferences.InteractKeepsHand, v => Preferences.InteractKeepsHand = v)),
                    UI.Row("Double-tap Shift to click through",
                        "Clicks reach the app and the hand stays. Double-tap again for taps. The tray icon turns orange.",
                        UI.Switch(Preferences.DoubleShiftClickThrough, v => Preferences.DoubleShiftClickThrough = v))),
                UI.Card("Startup",
                    UI.Row("Open TinyHand when you sign in", null, UI.Switch(Preferences.LaunchAtLogin, v => Preferences.LaunchAtLogin = v))));
        }

        internal Action StopRecording;

        internal void SetRecording(bool on)
        {
            recording = on;
            OnRecording?.Invoke(on);
        }

        internal void ReportShortcut(Shortcut s, bool ok)
        {
            shortcutError = ok ? null : $"{s.Display} is already used by another app. Pick a different shortcut.";
            ScheduleRebuild();
        }

        internal Window Window => window;

        // ---------------- Pointers ----------------

        private List<PointerStyle> dragOrder;
        private PointerStyle dragStyle;
        private bool dragRemoving;

        private FrameworkElement PointersPage()
        {
            var pinned = dragOrder ?? Preferences.PinnedStyles;
            bool full = Preferences.PinnedStyles.Count >= Preferences.MaxPinned;

            var header = new DockPanel { Margin = new Thickness(4, 0, 4, 8) };
            var reset = UI.TextButton("Reset", () =>
            {
                pinMessage = null;
                Preferences.PinnedStyles = PointerStyle.DefaultPinned.ToList();
            });
            DockPanel.SetDock(reset, Dock.Right);
            header.Children.Add(reset);
            header.Children.Add(UI.Text("In your toolbar", 12, FontWeights.SemiBold, Theme.Brush(Theme.Secondary)));

            var grid = new UniformGrid { Rows = 1, Columns = Preferences.MaxPinned };
            var row = new Border
            {
                Padding = new Thickness(7),
                CornerRadius = new CornerRadius(14),
                Background = Theme.PrimaryA(0.045),
                Opacity = Preferences.ShowToolbar ? 1 : 0.45,
                Child = grid,
            };
            foreach (var style in pinned)
            {
                grid.Children.Add(ToolbarSlot(style, grid, row));
            }
            if (!full && dragOrder == null)
            {
                grid.Children.Add(AddSlot());
            }
            while (grid.Children.Count < Preferences.MaxPinned)
            {
                grid.Children.Add(new Border { Height = 58, Margin = new Thickness(3), CornerRadius = new CornerRadius(10), Background = Theme.PrimaryA(0.035) });
            }

            var note = UI.Secondary(full
                ? "The toolbar is full. Remove a pointer to add another. Drag to reorder."
                : "Press + to add a pointer. Drag to reorder, or out to remove.");
            note.Margin = new Thickness(4, 8, 4, 0);

            var top = new StackPanel();
            top.Children.Add(header);
            top.Children.Add(row);
            top.Children.Add(note);
            if (pinMessage != null)
            {
                var m = ErrorText(pinMessage);
                m.Margin = new Thickness(4, 8, 4, 0);
                top.Children.Add(m);
            }

            return VStack(30,
                top,
                UI.Card("Toolbar",
                    UI.Row("Show the toolbar", Preferences.ShowToolbar
                            ? "Pops up when the pointer turns on and near the bottom of the screen."
                            : "Stays hidden. Press Esc to exit, or use the tray icon.",
                        UI.Switch(Preferences.ShowToolbar, v => Preferences.ShowToolbar = v)),
                    UI.Row("Ink button", "Change the colour while you present.",
                        UI.Switch(Preferences.ShowInkButton, v => Preferences.ShowInkButton = v))),
                YourHands());
        }

        private static ImageSource PointerIcon(PointerStyle style)
        {
            if (style.AssetName == null)
            {
                return Toolbar.LaserIcon();
            }
            return HandAsset.CachedThumbnail(style.AssetName) ?? Blank;
        }

        private static readonly ImageSource Blank = new DrawingImage(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));

        private FrameworkElement ToolbarSlot(PointerStyle style, UniformGrid grid, Border row)
        {
            bool lifted = dragStyle == style;
            var cell = new Grid { Height = 58, Margin = new Thickness(3), Background = Brushes.Transparent, ToolTip = style.Title, Cursor = Cursors.Hand };
            var bg = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = Theme.Brush(Theme.WithAlpha(Theme.WindowBackground, 0.95)),
                Effect = new DropShadowEffect { BlurRadius = lifted ? 10 : 3, ShadowDepth = lifted ? 4 : 1, Direction = 270, Opacity = lifted ? 0.22 : 0.08 },
                Child = UI.Image(PointerIcon(style), 40),
            };
            if (lifted)
            {
                bg.RenderTransformOrigin = new Point(0.5, 0.5);
                bg.RenderTransform = new ScaleTransform(1.06, 1.06);
                if (dragRemoving) cell.Opacity = 0.5;
            }
            cell.Children.Add(bg);

            var remove = new Clickable
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Normal = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
                Hover = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
                Child = UI.GlyphText(Glyph.Close, 7, Brushes.White),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -5, -5, 0),
                Visibility = Visibility.Hidden,
                ToolTip = "Remove from the toolbar",
            };
            if (lifted && dragRemoving)
            {
                remove.Normal = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
                remove.Child = UI.GlyphText(Glyph.Remove, 9, Brushes.White);
                remove.Visibility = Visibility.Visible;
            }
            remove.Click += () => Unpin(style);
            cell.Children.Add(remove);
            cell.MouseEnter += (s, e) => { if (dragStyle == null) remove.Visibility = Visibility.Visible; };
            cell.MouseLeave += (s, e) => { if (dragStyle == null) remove.Visibility = Visibility.Hidden; };

            // Drag to reorder, or out of the row to remove.
            Point? start = null;
            cell.MouseLeftButtonDown += (s, e) =>
            {
                start = e.GetPosition(row);
                cell.CaptureMouse();
                e.Handled = true;
            };
            cell.MouseMove += (s, e) =>
            {
                if (start == null || !cell.IsMouseCaptured) return;
                var p = e.GetPosition(row);
                if (dragStyle == null)
                {
                    if (Math.Abs(p.X - start.Value.X) < 3 && Math.Abs(p.Y - start.Value.Y) < 3) return;
                    dragStyle = style;
                    dragging = true;
                    pinMessage = null;
                }
                var baseOrder = Preferences.PinnedStyles;
                var order = baseOrder.Where(x => x != style).ToList();
                bool over = p.X >= -16 && p.X <= row.ActualWidth + 16 && p.Y >= -28 && p.Y <= row.ActualHeight + 28;
                bool removing = false;
                if (over)
                {
                    double step = (row.ActualWidth - 14) / Preferences.MaxPinned;
                    int slot = (int)Math.Clamp(Math.Floor((p.X - 7) / step), 0, order.Count);
                    order.Insert(slot, style);
                }
                else
                {
                    order = baseOrder;
                    removing = baseOrder.Count > 1;
                }
                if (!order.SequenceEqual(dragOrder ?? new List<PointerStyle>()) || removing != dragRemoving)
                {
                    dragOrder = order;
                    dragRemoving = removing;
                    // Swap the cells in place; the captured cell must stay alive while dragging.
                    RebuildToolbarRow(grid, row);
                }
            };
            cell.MouseLeftButtonUp += (s, e) =>
            {
                bool wasDragging = dragStyle != null;
                cell.ReleaseMouseCapture();
                start = null;
                if (!wasDragging) return;
                var order = dragOrder;
                bool removing = dragRemoving;
                dragStyle = null;
                dragOrder = null;
                dragRemoving = false;
                dragging = false;
                if (removing)
                {
                    Unpin(style);
                }
                else if (order != null)
                {
                    Preferences.PinnedStyles = order;
                }
                Rebuild();
            };
            cell.Tag = style;
            return cell;
        }

        private void RebuildToolbarRow(UniformGrid grid, Border row)
        {
            var cells = grid.Children.OfType<FrameworkElement>().Where(c => c.Tag is PointerStyle).ToDictionary(c => (PointerStyle)c.Tag);
            var order = dragOrder ?? Preferences.PinnedStyles;
            var captured = cells.Values.FirstOrDefault(c => c.IsMouseCaptured);
            int i = 0;
            foreach (var style in order)
            {
                if (!cells.TryGetValue(style, out var cell)) continue;
                int at = grid.Children.IndexOf(cell);
                if (at != i)
                {
                    grid.Children.Remove(cell);
                    grid.Children.Insert(i, cell);
                }
                if (cell.Children() is Border bg && style == dragStyle)
                {
                    bg.RenderTransformOrigin = new Point(0.5, 0.5);
                    bg.RenderTransform = new ScaleTransform(1.06, 1.06);
                }
                cell.Opacity = style == dragStyle && dragRemoving ? 0.5 : 1;
                i++;
            }
            captured?.CaptureMouse();
        }

        private FrameworkElement AddSlot()
        {
            var dash = new Rectangle
            {
                RadiusX = 10,
                RadiusY = 10,
                Stroke = Theme.PrimaryA(0.16),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
            var plus = UI.GlyphText(Glyph.Add, 15, Theme.Brush(Theme.Secondary));
            var inner = new Grid();
            inner.Children.Add(dash);
            inner.Children.Add(plus);
            var b = new Clickable
            {
                Height = 58,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(10),
                Normal = Theme.PrimaryA(0.05),
                Hover = Theme.PrimaryA(0.09),
                Child = inner,
                ToolTip = "Add a pointer",
            };
            b.Hovered += h => plus.Foreground = Theme.Brush(h ? Theme.Primary : Theme.Secondary);
            var popup = MakePopup(b, PointerPicker(() => { }), 400);
            b.Click += () => popup.IsOpen = !popup.IsOpen;
            var host = new Grid();
            host.Children.Add(b);
            host.Children.Add(popup);
            return host;
        }

        private static Popup MakePopup(UIElement target, FrameworkElement content, double width)
        {
            var popup = new Popup
            {
                PlacementTarget = target,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                VerticalOffset = 6,
            };
            popup.Child = new Border
            {
                Width = width,
                Margin = new Thickness(0, 0, 14, 16),
                CornerRadius = new CornerRadius(10),
                Background = Theme.Brush(Theme.IsDark ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Colors.White),
                BorderBrush = Theme.PrimaryA(0.12),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.22, Direction = 270 },
                Child = content,
            };
            content.Tag = popup;
            return popup;
        }

        private static void ClosePopupOf(FrameworkElement e)
        {
            DependencyObject d = e;
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.Tag is Popup p)
                {
                    p.IsOpen = false;
                    return;
                }
                d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
        }

        private FrameworkElement PointerPicker(Action done)
        {
            var pinned = new HashSet<PointerStyle>(Preferences.PinnedStyles);
            var content = new StackPanel();
            var body = new StackPanel { Margin = new Thickness(12) };
            body.Children.Add(PickerSection(null, PointerStyle.BuiltInHands.Concat(new[] { PointerStyle.Laser }), pinned, content));
            if (PointerStyle.CustomHandStyles.Count > 0)
            {
                var custom = PickerSection("Your hands", PointerStyle.CustomHandStyles, pinned, content);
                custom.Margin = new Thickness(0, 14, 0, 0);
                body.Children.Add(custom);
            }
            content.Children.Add(new ScrollViewer { Height = 340, Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            content.Children.Add(new Border { Height = 1, Background = Theme.PrimaryA(0.1) });
            var makeRow = new StackPanel { Orientation = Orientation.Horizontal };
            makeRow.Children.Add(UI.GlyphText(Glyph.Wand, 11, Theme.Brush(Theme.Secondary)));
            var makeLabel = UI.Text("Make your own hand…", 12, null, Theme.Brush(Theme.Secondary));
            makeLabel.Margin = new Thickness(6, 0, 0, 0);
            makeRow.Children.Add(makeLabel);
            var make = new Clickable { Child = makeRow, Padding = new Thickness(14, 10, 14, 10), Normal = Brushes.Transparent, Hover = Theme.PrimaryA(0.05) };
            make.Click += () =>
            {
                ClosePopupOf(content);
                done();
                StartMakingHand();
            };
            content.Children.Add(make);
            return content;
        }

        private FrameworkElement PickerSection(string title, IEnumerable<PointerStyle> styles, HashSet<PointerStyle> pinned, FrameworkElement popupContent)
        {
            var panel = new StackPanel();
            if (title != null)
            {
                var t = UI.Text(title, 11, FontWeights.SemiBold, Theme.Brush(Theme.Secondary));
                t.Margin = new Thickness(4, 0, 0, 6);
                panel.Children.Add(t);
            }
            var wrap = new WrapPanel();
            foreach (var style in styles)
            {
                bool added = pinned.Contains(style);
                var cell = new Clickable
                {
                    Width = 62,
                    Height = 58,
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(10),
                    Normal = Brushes.Transparent,
                    Hover = added ? Brushes.Transparent : Theme.PrimaryA(0.08),
                    Child = UI.Image(PointerIcon(style), 42),
                    Opacity = added ? 0.35 : 1,
                    ToolTip = added ? $"{style.Title} is already in the toolbar" : style.Title,
                };
                var s = style;
                cell.Click += () =>
                {
                    if (added) return;
                    ClosePopupOf(popupContent);
                    Pin(s);
                };
                wrap.Children.Add(cell);
            }
            panel.Children.Add(wrap);
            return panel;
        }

        private void Pin(PointerStyle style)
        {
            var pinned = Preferences.PinnedStyles;
            if (pinned.Contains(style))
            {
                Unpin(style);
                return;
            }
            if (pinned.Count >= Preferences.MaxPinned)
            {
                pinMessage = $"The toolbar is full. Remove one above to add {style.Title}.";
                Rebuild();
                return;
            }
            pinned.Add(style);
            pinMessage = null;
            Preferences.PinnedStyles = pinned;
        }

        private void Unpin(PointerStyle style)
        {
            var pinned = Preferences.PinnedStyles;
            int i = pinned.IndexOf(style);
            if (i < 0) return;
            if (pinned.Count == 1)
            {
                pinMessage = "Keep at least one pointer in the toolbar.";
                Rebuild();
                return;
            }
            pinned.RemoveAt(i);
            pinMessage = null;
            Preferences.PinnedStyles = pinned;
        }

        private void Swap(PointerStyle old, PointerStyle replacement)
        {
            var pinned = Preferences.PinnedStyles;
            int i = pinned.IndexOf(old);
            if (i < 0 || pinned.Contains(replacement)) return;
            pinned[i] = replacement;
            pinMessage = null;
            Preferences.PinnedStyles = pinned;
        }

        private FrameworkElement YourHands()
        {
            var panel = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(4, 0, 4, 8) };
            var make = UI.TextButton("Make your own…", StartMakingHand);
            DockPanel.SetDock(make, Dock.Right);
            header.Children.Add(make);
            header.Children.Add(UI.Text("Your hands", 12, FontWeights.SemiBold, Theme.Brush(Theme.Secondary)));
            panel.Children.Add(header);
            var customs = PointerStyle.CustomHandStyles;
            if (customs.Count == 0)
            {
                var t = UI.Secondary("Turn any idea into a hand with ChatGPT or another image tool.");
                t.Margin = new Thickness(4, 0, 4, 0);
                panel.Children.Add(t);
            }
            else
            {
                var wrap = new WrapPanel();
                foreach (var style in customs)
                {
                    wrap.Children.Add(CustomHandChip(style));
                }
                panel.Children.Add(wrap);
            }
            if (handMessage != null)
            {
                var m = UI.Secondary(handMessage);
                m.Margin = new Thickness(4, 8, 4, 0);
                panel.Children.Add(m);
            }
            return panel;
        }

        private FrameworkElement CustomHandChip(PointerStyle style)
        {
            var pinnedList = Preferences.PinnedStyles;
            bool pinned = pinnedList.Contains(style);
            bool full = pinnedList.Count >= Preferences.MaxPinned;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(UI.Image(PointerIcon(style), 24));
            var name = UI.Text(style.Title, 12);
            name.Margin = new Thickness(6, 0, 6, 0);
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            var remove = new Clickable
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Normal = Brushes.Transparent,
                Hover = Theme.PrimaryA(0.12),
                Child = UI.GlyphText(Glyph.Close, 7, Theme.Brush(Theme.Secondary)),
                Opacity = 0,
                ToolTip = $"Delete {style.Title}",
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Click += () => RemoveHand(style);
            row.Children.Add(remove);
            var chip = new Clickable
            {
                Child = row,
                Padding = new Thickness(6, 5, 6, 5),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(14),
                Normal = Theme.PrimaryA(0.05),
                Hover = pinned ? Theme.PrimaryA(0.05) : Theme.PrimaryA(0.09),
                ToolTip = pinned ? $"{style.Title} is in your toolbar" : $"Click to add {style.Title} to the toolbar",
            };
            chip.Hovered += h => remove.Opacity = h ? 1 : 0;
            var swapContent = new StackPanel { Margin = new Thickness(12) };
            var popup = MakePopup(chip, swapContent, double.NaN);
            var question = UI.Secondary($"The toolbar is full. Swap {style.Title} in for:", 12, false);
            question.Margin = new Thickness(0, 0, 0, 8);
            swapContent.Children.Add(question);
            var cells = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var old in pinnedList)
            {
                var o = old;
                var cell = new Clickable
                {
                    Width = 44,
                    Height = 44,
                    Margin = new Thickness(0, 0, 4, 0),
                    CornerRadius = new CornerRadius(9),
                    Normal = Theme.PrimaryA(0.04),
                    Hover = Theme.PrimaryA(0.1),
                    Child = UI.Image(PointerIcon(old), 32),
                    ToolTip = $"Replace {old.Title}",
                };
                cell.Click += () =>
                {
                    popup.IsOpen = false;
                    Swap(o, style);
                };
                cells.Children.Add(cell);
            }
            swapContent.Children.Add(cells);
            chip.Click += () =>
            {
                if (pinned) return;
                if (full)
                {
                    popup.IsOpen = true;
                }
                else
                {
                    Pin(style);
                }
            };
            var host = new Grid();
            host.Children.Add(chip);
            host.Children.Add(popup);
            return host;
        }

        private void RemoveHand(PointerStyle style)
        {
            var pinned = Preferences.PinnedStyles.Where(s => s != style).ToList();
            if (PointerStyle.Saved == style)
            {
                PointerStyle.Saved = PointerStyle.Lavender;
            }
            CustomHands.Remove(style);
            Preferences.PinnedStyles = pinned.Count == 0 ? PointerStyle.DefaultPinned.ToList() : pinned;
            handMessage = null;
            Rebuild();
        }

        // ---------------- Make your own hand ----------------

        private void StartMakingHand()
        {
            draft = null;
            draftError = null;
            promptCopied = false;
            handMessage = null;
            makingHand = true;
            section = SettingsSection.Pointers;
            Rebuild();
            scroller.ScrollToTop();
        }

        private FrameworkElement MakeHandPage()
        {
            var idea = PlainField("e.g. knitted wool in mustard yellow", handIdea, v => handIdea = v, 420);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var chat = UI.PrimaryButton("Open in ChatGPT", Glyph.OpenExternal, true, () =>
                Open("https://chatgpt.com/?q=" + Uri.EscapeDataString(HandMaker.Prompt(handIdea))));
            buttons.Children.Add(chat);
            var copy = UI.PrimaryButton(promptCopied ? "Copied" : "Copy prompt", promptCopied ? Glyph.Check : Glyph.Copy, false, CopyPrompt);
            copy.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(copy);

            return VStack(26,
                Step(1, "Describe it", "What should your hand look like?", idea),
                Step(2, "Make the image", "Open it in ChatGPT, or copy the prompt for another image tool. Download the result as a PNG with a transparent background.", buttons),
                Step(3, "Add it", null, draft.HasValue ? DraftPreview() : DropZone()));
        }

        private void CopyPrompt()
        {
            try
            {
                Clipboard.SetText(HandMaker.Prompt(handIdea));
            }
            catch (Exception e)
            {
                Log.Write($"clipboard: {e.Message}");
            }
            promptCopied = true;
            Rebuild();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                promptCopied = false;
                Rebuild();
            };
            t.Start();
        }

        private static FrameworkElement Step(int number, string title, string detail, FrameworkElement content)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var badge = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = Theme.PrimaryA(0.75),
                VerticalAlignment = VerticalAlignment.Top,
                Child = UI.Text(number.ToString(), 11, FontWeights.Bold, Theme.Brush(Theme.WindowBackground)),
            };
            ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
            ((TextBlock)badge.Child).VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(badge);
            var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            right.Children.Add(UI.Text(title, 13, FontWeights.SemiBold));
            if (detail != null)
            {
                var d = UI.Secondary(detail, 12);
                d.Margin = new Thickness(0, 2, 0, 0);
                right.Children.Add(d);
            }
            content.Margin = new Thickness(0, 10, 0, 0);
            content.HorizontalAlignment = HorizontalAlignment.Left;
            right.Children.Add(content);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return grid;
        }

        private static FrameworkElement PlainField(string placeholder, string text, Action<string> changed, double width, int maxLength = 0)
        {
            var box = new TextBox
            {
                Text = text,
                FontFamily = Theme.Font,
                FontSize = 13,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Theme.Brush(Theme.Primary),
                CaretBrush = Theme.Brush(Theme.Primary),
                Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            if (maxLength > 0) box.MaxLength = maxLength;
            var hint = UI.Text(placeholder, 13, null, Theme.PrimaryA(0.35));
            hint.IsHitTestVisible = false;
            hint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
            hint.Margin = new Thickness(2, 0, 0, 0);
            box.TextChanged += (s, e) =>
            {
                hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                changed(box.Text);
            };
            var grid = new Grid();
            grid.Children.Add(box);
            grid.Children.Add(hint);
            return new Border
            {
                Width = width,
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(8),
                Background = Theme.PrimaryA(0.06),
                BorderBrush = Theme.PrimaryA(0.1),
                BorderThickness = new Thickness(1),
                Child = grid,
            };
        }

        private FrameworkElement DropZone()
        {
            bool error = draftError != null;
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 24, 0) };
            stack.Children.Add(UI.GlyphText(error ? Glyph.Warning : Glyph.Download, 20, error ? Theme.Brush(Color.FromRgb(0xFF, 0x95, 0x00)) : Theme.Brush(Theme.Secondary)));
            var message = UI.Text(draftError ?? "Drop the PNG here, or click to choose it", 12, null, error ? Theme.Brush(Theme.Primary) : Theme.Brush(Theme.Secondary), true);
            message.TextAlignment = TextAlignment.Center;
            message.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(message);
            if (error)
            {
                var again = UI.Secondary("Try again with another image", 11, false);
                again.HorizontalAlignment = HorizontalAlignment.Center;
                again.Margin = new Thickness(0, 4, 0, 0);
                stack.Children.Add(again);
            }
            var dash = new Rectangle { RadiusX = 12, RadiusY = 12, Stroke = Theme.PrimaryA(0.14), StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 5, 4 } };
            var inner = new Grid();
            inner.Children.Add(dash);
            inner.Children.Add(stack);
            var zone = new Clickable
            {
                Height = 130,
                Width = 480,
                CornerRadius = new CornerRadius(12),
                Normal = Theme.PrimaryA(0.03),
                Hover = Theme.PrimaryA(0.05),
                Child = inner,
                AllowDrop = true,
                Cursor = Cursors.Hand,
            };
            zone.Click += ChooseHandFile;
            zone.DragEnter += (s, e) =>
            {
                zone.Background = Theme.PrimaryA(0.07);
                dash.Stroke = Theme.Brush(Theme.Accent);
            };
            zone.DragLeave += (s, e) =>
            {
                zone.Background = Theme.PrimaryA(0.03);
                dash.Stroke = Theme.PrimaryA(0.14);
            };
            zone.Drop += (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    LoadDraft(files[0]);
                }
            };
            return zone;
        }

        private void ChooseHandFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pick the PNG of your hand",
                Filter = "Images|*.png;*.webp;*.gif;*.tif;*.tiff|All files|*.*",
                Multiselect = false,
            };
            if (dialog.ShowDialog(window) == true)
            {
                LoadDraft(dialog.FileName);
            }
        }

        internal void LoadDraft(string path)
        {
            try
            {
                var made = HandMaker.Make(path);
                draft?.image.Dispose();
                draft = made;
                draftError = null;
                handName = CustomHands.SuggestedName(path);
            }
            catch (HandMaker.Problem p)
            {
                draft = null;
                draftError = p.Message;
            }
            catch (Exception e)
            {
                Log.Write($"hand draft failed: {e}");
                draft = null;
                draftError = "TinyHand couldn't read that file.";
            }
            Rebuild();
        }

        private FrameworkElement DraftPreview()
        {
            var image = draft.Value.image;
            System.Windows.Media.Imaging.BitmapSource source;
            using (var crop = image.Clone(new System.Drawing.Rectangle(112, 90, 800, 1000), System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                source = HandAsset.ToBitmapSource(crop);
            }
            var picture = new Image { Source = source, Width = 144, Height = 180, Stretch = Stretch.Uniform, Margin = new Thickness(13, 10, 13, 10) };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            var frame = new Border { Width = 170, CornerRadius = new CornerRadius(12), Background = Theme.Brush(Color.FromRgb(232, 207, 181)), Child = picture };

            var right = new StackPanel { Margin = new Thickness(22, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(UI.Text("Name", 11, FontWeights.Medium, Theme.Brush(Theme.Secondary)));
            var name = PlainField("My hand", handName, v => handName = v, 220, 16);
            name.Margin = new Thickness(0, 4, 0, 12);
            right.Children.Add(name);
            var note = UI.Secondary("It should show the back of the hand, like the ones in TinyHand. If you see the palm, try another image.");
            note.MaxWidth = 300;
            note.HorizontalAlignment = HorizontalAlignment.Left;
            right.Children.Add(note);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(UI.PrimaryButton("Add hand", null, true, SaveDraft));
            var another = UI.TextButton("Use another image", ChooseHandFile);
            another.Margin = new Thickness(10, 0, 0, 0);
            another.VerticalAlignment = VerticalAlignment.Center;
            buttons.Children.Add(another);
            right.Children.Add(buttons);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(frame);
            row.Children.Add(right);
            return row;
        }

        private void SaveDraft()
        {
            if (!draft.HasValue) return;
            try
            {
                var style = CustomHands.Add(draft.Value.image, draft.Value.ink, handName);
                if (style.AssetName != null)
                {
                    HandAsset.Thumbnail(style.AssetName);
                }
                var pinned = Preferences.PinnedStyles;
                if (pinned.Count < Preferences.MaxPinned)
                {
                    int at = Math.Max(0, pinned.Count - (pinned.LastOrDefault() == PointerStyle.Laser ? 1 : 0));
                    pinned.Insert(at, style);
                    Preferences.PinnedStyles = pinned;
                    handMessage = $"{style.Title} is in your toolbar.";
                }
                else
                {
                    handMessage = $"{style.Title} is ready. Click it to add it to the toolbar.";
                }
                PointerStyle.Saved = style;
                Preferences.NotifyChanged();
                draft.Value.image.Dispose();
                draft = null;
                handIdea = "";
                makingHand = false;
                Rebuild();
            }
            catch (Exception e)
            {
                Log.Write($"save hand failed: {e.Message}");
                draftError = "TinyHand couldn't save that hand.";
                Rebuild();
            }
        }

        // ---------------- Appearance ----------------

        private FrameworkElement AppearancePage()
        {
            var size = new StackPanel { Orientation = Orientation.Horizontal };
            size.Children.Add(UI.GlyphText(Glyph.Hand, 10, Theme.Brush(Theme.Secondary)));
            var slider = new PillSlider(Preferences.PointerSizeMin, Preferences.PointerSizeMax, Preferences.PointerSize) { Width = 180, Margin = new Thickness(10, 0, 10, 0) };
            slider.ValueChanged += v => Preferences.PointerSize = v;
            size.Children.Add(slider);
            size.Children.Add(UI.GlyphText(Glyph.Hand, 16, Theme.Brush(Theme.Secondary)));
            return UI.Card("Pointer",
                UI.Row("Size", null, size),
                UI.Row("Shadow under the pointer", null, UI.Switch(Preferences.ShowShadow, v => Preferences.ShowShadow = v)));
        }

        // ---------------- Drawing ----------------

        private FrameworkElement DrawingPage()
        {
            var choice = Preferences.InkChoice;
            var swatches = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var c in InkChoiceInfo.All)
            {
                swatches.Children.Add(InkSwatch(c, c == choice));
            }
            var fades = new List<(string, double)> { ("After 3 seconds", 3), ("After 6 seconds", 6), ("After 10 seconds", 10), ("Keep until cleared", 0) };
            double fade = Preferences.FadeSeconds;
            if (!fades.Any(f => f.Item2 == fade)) fade = 3;
            return UI.Card("Ink",
                UI.Row("Ink colour", choice == InkChoice.Match ? "Matches each pointer." : $"All pointers draw in {choice.Title().ToLowerInvariant()}.", swatches),
                UI.Row("Lines fade", $"Clear them any time with {Preferences.ClearShortcut.Display}.",
                    UI.MenuPicker(fades, fade, v => Preferences.FadeSeconds = v)));
        }

        private static FrameworkElement InkSwatch(InkChoice choice, bool selected)
        {
            Shape dot;
            var color = choice.Color();
            if (color.HasValue)
            {
                dot = new Ellipse
                {
                    Fill = new SolidColorBrush(color.Value),
                    Stroke = Theme.PrimaryA(choice == InkChoice.Black ? 0.35 : 0.15),
                    StrokeThickness = 1,
                };
            }
            else
            {
                dot = new Ellipse { Fill = new ImageBrush(Toolbar.RainbowDot()) { Viewbox = new Rect(0.29, 0.29, 0.42, 0.42) } };
            }
            dot.Width = 20;
            dot.Height = 20;
            var ring = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                BorderBrush = selected ? Theme.Brush(Theme.Accent) : Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Child = dot,
            };
            var b = new Clickable { Child = ring, Margin = new Thickness(0, 0, 8, 0), ToolTip = choice.Title(), Cursor = Cursors.Hand };
            b.Hovered += h => dot.RenderTransform = h && !selected ? new ScaleTransform(1.08, 1.08, 10, 10) : null;
            b.Click += () => Preferences.InkChoice = choice;
            return b;
        }

        // ---------------- Sound ----------------

        private FrameworkElement SoundPage()
        {
            bool on = Preferences.HandSound;
            var grid = new UniformGrid { Columns = 4 };
            grid.Children.Add(SoundTile("Off", Glyph.SpeakerOff, !on, () => Preferences.HandSound = false));
            foreach (var choice in TapSoundInfo.All)
            {
                var c = choice;
                grid.Children.Add(SoundTile(choice.Title(), Glyph.Wave, on && Preferences.TapSound == choice, () =>
                {
                    Preferences.TapSound = c;
                    Preferences.HandSound = true;
                    Preview(c);
                }));
            }
            var caption = UI.Text("Tap sound", 12, FontWeights.SemiBold, Theme.Brush(Theme.Secondary));
            caption.Margin = new Thickness(4, 0, 4, 8);
            var note = UI.Secondary("Click a sound to hear it. It plays each time the hand taps.");
            note.Margin = new Thickness(4, 8, 4, 0);
            var top = new StackPanel();
            top.Children.Add(caption);
            top.Children.Add(grid);
            top.Children.Add(note);
            var laser = UI.Card(null, UI.Row("Laser dot taps too", "Off by default, for quiet pointing.",
                UI.Switch(Preferences.LaserSound, v => Preferences.LaserSound = v, enabled: on)));
            laser.Opacity = on ? 1 : 0.5;
            WarmPreview();
            return VStack(22, top, laser);
        }

        private static FrameworkElement SoundTile(string title, string glyph, bool selected, Action action)
        {
            var accent = Theme.Accent;
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var icon = UI.GlyphText(glyph, 18, selected ? Theme.Brush(accent) : Theme.Brush(Theme.Secondary));
            icon.Height = 22;
            stack.Children.Add(icon);
            var label = UI.Text(title, 12, selected ? FontWeights.SemiBold : FontWeights.Normal, selected ? Theme.Brush(Theme.Primary) : Theme.Brush(Theme.Secondary));
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(label);
            var tile = new Clickable
            {
                Child = stack,
                Padding = new Thickness(4, 14, 4, 14),
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(12),
                Normal = selected ? Theme.Brush(Theme.WithAlpha(accent, 0.12)) : Theme.PrimaryA(0.035),
                Hover = selected ? Theme.Brush(Theme.WithAlpha(accent, 0.12)) : Theme.PrimaryA(0.07),
                BorderBrush = selected ? Theme.Brush(Theme.WithAlpha(accent, 0.7)) : Brushes.Transparent,
                BorderThickness = new Thickness(1.5),
            };
            tile.Click += action;
            return tile;
        }

        private void WarmPreview()
        {
            previewSound ??= new TapSound();
            previewSound.Warm();
        }

        private void Preview(TapSoundChoice choice)
        {
            previewSound ??= new TapSound(choice);
            previewSound.Load(choice);
            previewSound.Enabled = true;
            previewSound.Play();
        }

        // ---------------- About ----------------

        private FrameworkElement AboutPage()
        {
            string version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";
            var head = new DockPanel();
            var update = UI.PillButton("Check for Updates", () => Open(ReleasesUrl));
            update.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(update, Dock.Right);
            head.Children.Add(update);
            var icon = Resources.Image("AppIcon.png");
            if (icon != null)
            {
                var img = UI.Image(icon, 72);
                DockPanel.SetDock(img, Dock.Left);
                head.Children.Add(img);
            }
            var text = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(UI.Text("TinyHand", 18, FontWeights.Bold));
            var v = UI.Secondary($"Version {version} for Windows", 12, false);
            v.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(v);
            var d = UI.Secondary("A tiny hand for your screen. Free and open source.", 12, false);
            d.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(d);
            head.Children.Add(text);

            var reset = UI.TextButton("Reset all settings…", ResetAll);
            reset.HorizontalAlignment = HorizontalAlignment.Right;

            return VStack(22,
                head,
                UI.Card(null,
                    LinkRow("Website", "tinyhand.app", "https://tinyhand.app"),
                    LinkRow("Source code", "github.com/nilbuild/tinyhand", "https://github.com/nilbuild/tinyhand"),
                    LinkRow("Windows port", "github.com/pietro-santoro/tinyhand", "https://github.com/pietro-santoro/tinyhand"),
                    LinkRow("Made by", "@nilbuild on X", "https://x.com/nilbuild"),
                    LinkRow("License", "MIT", "https://github.com/nilbuild/tinyhand/blob/main/LICENSE")),
                reset);
        }

        private static FrameworkElement LinkRow(string title, string detail, string url)
        {
            var grid = new DockPanel { Margin = new Thickness(14, 0, 14, 0), MinHeight = 44, LastChildFill = true };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(UI.Text(detail, 12, null, Theme.Brush(Theme.Secondary)));
            var arrow = UI.GlyphText(Glyph.OpenExternal, 10, Theme.Brush(Theme.Secondary));
            arrow.Margin = new Thickness(6, 1, 0, 0);
            right.Children.Add(arrow);
            DockPanel.SetDock(right, Dock.Right);
            grid.Children.Add(right);
            var t = UI.Text(title, 13);
            t.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(t);
            var b = new Clickable { Child = grid, Normal = Brushes.Transparent, Hover = Theme.PrimaryA(0.04), Cursor = Cursors.Hand };
            b.Click += () => Open(url);
            return b;
        }

        private void ResetAll()
        {
            var answer = MessageBox.Show(window,
                "Shortcuts, toolbar pointers, size, shadow, drawing and sound go back to how they were at first. Open at sign-in stays as it is.",
                "Reset all settings?", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK)
            {
                return;
            }
            Preferences.ResetAll();
            ApplyToggleShortcut?.Invoke(Shortcut.ToggleDefault);
            ApplyClearShortcut?.Invoke(Shortcut.ClearDefault);
            shortcutError = null;
            pinMessage = null;
            Rebuild();
        }

        // ---------------- Snapshots (self tests) ----------------

        public void Snapshot(SettingsSection target, string path, bool dark)
        {
            Theme.Force(dark);
            section = target;
            foreach (var style in PointerStyle.All)
            {
                if (style.AssetName != null) HandAsset.Thumbnail(style.AssetName);
            }
            if (root == null)
            {
                Build();
            }
            window.Background = Theme.Brush(Theme.WindowBackground);
            Rebuild();
            window.Content = null;
            root.Width = 780;
            root.Height = 560;
            var host = new Border { Background = Theme.Brush(Theme.WindowBackground), Child = root };
            Toolbar.SaveElement(host, path, null);
            host.Child = null;
            window.Content = root;
            Theme.Force(null);
        }
    }

    internal static class PanelExtensions
    {
        public static UIElement Children(this FrameworkElement e) => e is Panel p && p.Children.Count > 0 ? p.Children[0] : null;
    }

    /// <summary>
    /// Click, then press the new shortcut; Esc cancels. Needs Ctrl, Alt or Win, like the Mac's
    /// ⌃, ⌥ or ⌘. Port of ShortcutRecorder (SettingsWindow.swift).
    /// </summary>
    internal sealed class ShortcutRecorder : Clickable
    {
        private readonly SettingsWindow owner;
        private readonly Func<Shortcut, bool> apply;
        private Shortcut shortcut;
        private readonly StackPanel keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private bool recording;

        public ShortcutRecorder(SettingsWindow owner, Shortcut shortcut, Func<Shortcut, bool> apply)
        {
            this.owner = owner;
            this.shortcut = shortcut;
            this.apply = apply;
            Width = 132;
            Height = 30;
            CornerRadius = new CornerRadius(7);
            Normal = Theme.PrimaryA(0.05);
            Hover = Theme.PrimaryA(0.07);
            Child = keys;
            ToolTip = "Click, then press the new shortcut. Esc cancels.";
            SetLabel(shortcut.Display);
            Click += Start;
            Unloaded += (s, e) => Stop();
        }

        private static readonly string[] ModifierWords = { "Ctrl", "Alt", "Shift", "Win" };

        private void SetLabel(string text, bool dim = false)
        {
            keys.Children.Clear();
            if (dim)
            {
                keys.Children.Add(UI.Text(text, 12, FontWeights.Medium, Theme.Brush(Theme.Secondary)));
                return;
            }
            var caps = new List<string>();
            string rest = text;
            bool found = true;
            while (found)
            {
                found = false;
                foreach (var m in ModifierWords)
                {
                    if (rest.StartsWith(m + "+", StringComparison.Ordinal))
                    {
                        caps.Add(m);
                        rest = rest.Substring(m.Length + 1);
                        found = true;
                    }
                }
            }
            if (ModifierWords.Contains(rest))
            {
                caps.Add(rest);
                rest = "";
            }
            if (rest.Length > 0) caps.Add(rest);
            for (int i = 0; i < caps.Count; i++)
            {
                var label = UI.Text(caps[i], 12, FontWeights.Medium);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                var cap = new Border
                {
                    Height = 20,
                    MinWidth = 22,
                    Padding = new Thickness(6, 0, 6, 0),
                    Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
                    CornerRadius = new CornerRadius(5),
                    BorderThickness = new Thickness(0.5, 0.5, 0.5, 1.2),
                    BorderBrush = Theme.PrimaryA(0.18),
                    Background = Theme.PrimaryA(0.1),
                    Child = label,
                };
                keys.Children.Add(cap);
            }
        }

        private void Start()
        {
            if (recording) return;
            owner.StopRecording?.Invoke();
            recording = true;
            Normal = Theme.PrimaryA(0.09);
            Background = Normal;
            SetLabel("Press keys", true);
            owner.SetRecording(true);
            owner.StopRecording = Stop;
            var w = owner.Window;
            if (w != null)
            {
                w.PreviewKeyDown += OnKeyDown;
                w.PreviewKeyUp += OnKeyUp;
                w.PreviewMouseDown += OnMouseDown;
            }
        }

        private void Stop()
        {
            if (!recording) return;
            recording = false;
            var w = owner.Window;
            if (w != null)
            {
                w.PreviewKeyDown -= OnKeyDown;
                w.PreviewKeyUp -= OnKeyUp;
                w.PreviewMouseDown -= OnMouseDown;
            }
            if (owner.StopRecording == (Action)Stop) owner.StopRecording = null;
            Normal = Theme.PrimaryA(0.05);
            Background = Normal;
            SetLabel(shortcut.Display);
            owner.SetRecording(false);
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight)
            {
                Stop();
            }
        }

        private static uint CurrentModifiers()
        {
            uint m = 0;
            var k = Keyboard.Modifiers;
            if ((k & ModifierKeys.Control) != 0) m |= Native.MOD_CONTROL;
            if ((k & ModifierKeys.Alt) != 0) m |= Native.MOD_ALT;
            if ((k & ModifierKeys.Shift) != 0) m |= Native.MOD_SHIFT;
            if ((k & ModifierKeys.Windows) != 0 || Native.IsKeyDown(Native.VK_LWIN) || Native.IsKeyDown(Native.VK_RWIN)) m |= Native.MOD_WIN;
            return m;
        }

        private static bool IsModifier(Key key) =>
            key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt
            || key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin;

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (IsModifier(key))
            {
                uint mods = CurrentModifiers();
                if (mods == 0) SetLabel("Press keys", true); else SetLabel(Shortcut.ModifierText(mods));
                e.Handled = true;
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                Stop();
                return;
            }
            uint mods = CurrentModifiers();
            if (IsModifier(key))
            {
                SetLabel(Shortcut.ModifierText(mods));
                return;
            }
            if ((mods & (Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_WIN)) == 0)
            {
                SetLabel("Add Ctrl, Alt or Win", true);
                return;
            }
            int vk = KeyInterop.VirtualKeyFromKey(key);
            var s = new Shortcut((uint)vk, mods, Shortcut.ModifierText(mods) + "+" + KeyName(key, vk));
            bool ok = apply(s);
            if (ok)
            {
                shortcut = s;
            }
            Stop();
            owner.ReportShortcut(s, ok);
        }

        private static readonly Dictionary<Key, string> Special = new Dictionary<Key, string>
        {
            [Key.Space] = "Space", [Key.Enter] = "Enter", [Key.Tab] = "Tab", [Key.Back] = "Backspace", [Key.Delete] = "Del",
            [Key.Insert] = "Ins", [Key.Left] = "←", [Key.Right] = "→", [Key.Up] = "↑", [Key.Down] = "↓",
            [Key.Home] = "Home", [Key.End] = "End", [Key.PageUp] = "PgUp", [Key.PageDown] = "PgDn",
            [Key.PrintScreen] = "PrtSc", [Key.Pause] = "Pause",
        };

        public static string KeyName(Key key, int vk)
        {
            if (Special.TryGetValue(key, out var name)) return name;
            if (key >= Key.F1 && key <= Key.F24) return "F" + (key - Key.F1 + 1);
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Num " + (key - Key.NumPad0);
            uint ch = Native.MapVirtualKey((uint)vk, 2) & 0x7FFF;
            if (ch > 32) return char.ToUpperInvariant((char)ch).ToString();
            return key.ToString();
        }
    }
}
