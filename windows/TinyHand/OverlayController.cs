using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Threading;

namespace TinyHand
{
    /// <summary>
    /// Runs the pointer: follows the monitor under the cursor, turns clicks into taps and drags
    /// into ink, hides the real cursor, shows the toolbar, and paces itself at 120 Hz while
    /// something moves and 20 Hz when still. Port of OverlayController.swift.
    /// </summary>
    internal sealed class OverlayController
    {
        private readonly Dispatcher dispatcher;
        private readonly OverlayWindow window;
        private readonly ArmRenderer arm = new ArmRenderer();
        private readonly InputHook inputHook;
        private readonly TapSound tapSound = new TapSound();
        private readonly Toolbar toolbar = new Toolbar();
        private readonly DispatcherTimer timer;

        private bool interactHeld;
        private bool menuHover;
        private DateTime guardCursorUntil = DateTime.MinValue;
        private Rectangle? currentFrame;
        private IntPtr currentMonitor;
        private float currentScale = 1;
        private PointF? downPoint;
        private bool buttonSeenDown;
        private int dragEvents;

        private DateTime revealUntil = DateTime.MinValue;
        private bool normalCursor;
        private bool ownMenuOpen;
        private List<Native.RECT> menuRects = new List<Native.RECT>();
        private List<Native.RECT> taskbars = new List<Native.RECT>();
        private DateTime lastMenuScan = DateTime.MinValue;
        private DateTime menuOpeningUntil = DateTime.MinValue;
        private int? escHotKey;
        private DateTime lastZOrder = DateTime.MinValue;

        private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(1.0 / 120.0);
        private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1.0 / 20.0);
        private const double IdleAfter = 0.5;
        private const double MenuScanFast = 0.05;
        private const double MenuScanSlow = 0.25;
        private TimeSpan timerInterval = TimeSpan.Zero;
        private bool highResTimer;
        private Point? lastMouse;
        private DateTime stillSince = DateTime.Now;
        private DateTime lastButtonEvent = DateTime.MinValue;

        public bool IsActive { get; private set; }

        private DateTime? shiftDownAt;
        private uint shiftDownInputs;
        private DateTime lastShiftTap = DateTime.MinValue;
        private uint lastShiftTapInputs;
        private const double ShiftTapMax = 0.3;
        private const double ShiftDoubleMax = 0.35;

        public Action OnActiveChanged;
        public Action OnNeedsPermission;
        public Action OnSoundChanged;

        public OverlayController(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            window = new OverlayWindow();
            inputHook = new InputHook(dispatcher);
            timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher);
            timer.Tick += (s, e) => Tick();

            inputHook.OnMouseDown = () =>
            {
                arm.Press();
                arm.Held = true;
                tapSound.Play();
                toolbar.HideHint();
                downPoint = LocalMouse();
                dragEvents = 0;
                Log.Write($"press at {(downPoint.HasValue ? $"{(int)downPoint.Value.X},{(int)downPoint.Value.Y}" : "?")}");
                NoteButtonEvent();
            };
            inputHook.OnLeftDragged = () =>
            {
                dragEvents++;
                Drag();
                Wake();
            };
            inputHook.OnMouseUp = () =>
            {
                Release();
                NoteButtonEvent();
            };
            inputHook.OnInteractDown = () =>
            {
                arm.Press();
                arm.Held = true;
                NoteButtonEvent();
            };
            inputHook.OnInteractUp = () =>
            {
                arm.Held = false;
                guardCursorUntil = DateTime.Now.AddSeconds(2);
                NoteButtonEvent();
            };
            inputHook.OnPassThroughPress = location =>
            {
                if (taskbars.Any(r => r.Contains(location.X, location.Y)))
                {
                    menuOpeningUntil = DateTime.Now.AddSeconds(1);
                }
                NoteButtonEvent();
            };
            inputHook.OnScreenLocked = () => TurnOffForSystem("click on locked screen");
            inputHook.OnModifiersChanged = ShiftChanged;
            inputHook.OnWake = Wake;
            ObserveLockAndSleep();

            toolbar.OnPick = Pick;
            toolbar.OnToggleSound = () => SoundOn = !SoundOn;
            toolbar.OnExit = Exit;
            toolbar.OnFixPermission = RetryHook;
            toolbar.Select(arm.Style);
            tapSound.Enabled = SoundOn;
            toolbar.SetSoundOn(SoundOn);

            Preferences.Changed += () =>
            {
                arm.ReloadPreferences();
                SyncSound();
                inputHook.InteractVk = Preferences.InteractKey.VirtualKey();
                if (!Preferences.DoubleShiftClickThrough && IsClickThrough)
                {
                    SetClickThrough(false);
                }
                arm.UpdateInkColor();
                tapSound.Load(Preferences.TapSound);
                if (arm.Style != PointerStyle.Saved)
                {
                    Pick(PointerStyle.Saved);
                }
            };
        }

        public bool SoundOn
        {
            get => arm.Style.IsLaser ? Preferences.LaserSound : Preferences.HandSound;
            set
            {
                if (arm.Style.IsLaser)
                {
                    Preferences.LaserSound = value;
                }
                else
                {
                    Preferences.HandSound = value;
                }
            }
        }

        public void Pick(PointerStyle style)
        {
            arm.SetStyle(style);
            PointerStyle.Saved = arm.Style;
            toolbar.Select(arm.Style);
            SyncSound();
        }

        private void SyncSound()
        {
            tapSound.Enabled = SoundOn;
            toolbar.SetSoundOn(SoundOn);
            OnSoundChanged?.Invoke();
        }

        /// <summary>Our own tray menu is open: let every click through (Mac: menuWillOpen).</summary>
        public void SuspendInput(bool suspended)
        {
            if (!IsActive || !inputHook.IsInstalled)
            {
                return;
            }
            ownMenuOpen = suspended;
            inputHook.SetActive(!suspended);
            if (currentFrame.HasValue && Native.GetCursorPos(out var p))
            {
                UpdateZones(new Point(p.X, p.Y));
            }
        }

        public void Exit()
        {
            ClearInk();
            Deactivate();
        }

        private void ObserveLockAndSleep()
        {
            Microsoft.Win32.SystemEvents.SessionSwitch += (s, e) =>
            {
                bool locked = e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock
                    || e.Reason == Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect
                    || e.Reason == Microsoft.Win32.SessionSwitchReason.RemoteDisconnect
                    || e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLogoff;
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock
                    || e.Reason == Microsoft.Win32.SessionSwitchReason.ConsoleConnect
                    || e.Reason == Microsoft.Win32.SessionSwitchReason.RemoteConnect)
                {
                    inputHook.SessionLocked = false;
                }
                if (locked)
                {
                    inputHook.SessionLocked = true;
                    dispatcher.BeginInvoke(new Action(() => TurnOffForSystem(e.Reason.ToString())));
                }
            };
            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
                {
                    dispatcher.BeginInvoke(new Action(() => TurnOffForSystem("sleep")));
                }
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) =>
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    currentFrame = null; // re-read the monitor on the next tick
                    if (IsActive) Tick();
                }));
            };
        }

        public bool IsClickThrough => IsActive && inputHook.ClickThrough;

        private void ShiftChanged(ModifierSnapshot flags)
        {
            if (arm.Ink.IsDrawing)
            {
                arm.Ink.Arrow = flags.Control; // Mac: hold ⌘ while drawing for an arrowhead
            }
            Wake();
            if (!Preferences.DoubleShiftClickThrough)
            {
                return;
            }
            if (flags.OnlyShift)
            {
                shiftDownAt = DateTime.Now;
                shiftDownInputs = flags.InputCount;
                return;
            }
            if (!flags.None || shiftDownAt == null)
            {
                shiftDownAt = null;
                lastShiftTap = DateTime.MinValue;
                return;
            }
            var down = shiftDownAt.Value;
            shiftDownAt = null;
            uint inputs = flags.InputCount;
            var now = DateTime.Now;
            // Comparing the key/click counter across the tap tells a clean Shift tap from typing a capital.
            if ((now - down).TotalSeconds >= ShiftTapMax || inputs != shiftDownInputs)
            {
                lastShiftTap = DateTime.MinValue;
                return;
            }
            bool isDouble = (down - lastShiftTap).TotalSeconds < ShiftDoubleMax && shiftDownInputs == lastShiftTapInputs;
            if (isDouble)
            {
                lastShiftTap = DateTime.MinValue;
                SetClickThrough(!inputHook.ClickThrough);
                return;
            }
            lastShiftTap = now;
            lastShiftTapInputs = inputs;
        }

        private void SetClickThrough(bool on)
        {
            if (!IsActive)
            {
                return;
            }
            inputHook.ClickThrough = on;
            Log.Write(on ? "click-through on" : "click-through off");
            if (on)
            {
                Release();
            }
            toolbar.ShowHint(on
                ? new List<(string, string)> { ("Clicks go through", "the hand stays"), ("Double-tap Shift", "to tap again") }
                : new List<(string, string)> { ("Taps", "clicks don't reach apps"), ("Double-tap Shift", "to click through") });
            After(1.5, () => toolbar.HideHint());
            Wake();
            OnActiveChanged?.Invoke();
        }

        private void After(double seconds, Action action)
        {
            var t = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(seconds) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                action();
            };
            t.Start();
        }

        private void TurnOffForSystem(string reason)
        {
            if (!IsActive)
            {
                return;
            }
            Log.Write($"turning pointer off: {reason}");
            Deactivate();
        }

        private void Release()
        {
            if (downPoint.HasValue)
            {
                var start = downPoint.Value;
                var p = LocalMouse();
                int moved = p.HasValue ? (int)Math.Sqrt(Math.Pow(p.Value.X - start.X, 2) + Math.Pow(p.Value.Y - start.Y, 2)) : -1;
                Log.Write($"release, drag events={dragEvents} moved={moved}px stroke drawn={arm.Ink.IsDrawing}");
            }
            buttonSeenDown = false;
            arm.Held = false;
            downPoint = null;
            arm.Ink.End();
        }

        public PointerStyle Style => arm.Style;

        public int InkStrokes => arm.Ink.StrokeCount;

        public string DebugState => $"held={arm.Held} downPoint={downPoint.HasValue} drawing={arm.Ink.IsDrawing} strokes={InkStrokes}";

        public Point? ToolbarButtonCenter(PointerStyle style) => toolbar.CenterOf(style);

        public void ClearInk()
        {
            arm.Ink.Clear();
            if (IsActive) Wake();
        }

        private void Drag()
        {
            if (!downPoint.HasValue)
            {
                return;
            }
            var p = LocalMouse();
            if (!p.HasValue)
            {
                return;
            }
            var start = downPoint.Value;
            var ink = arm.Ink;
            if (!ink.IsDrawing)
            {
                if (Math.Sqrt(Math.Pow(p.Value.X - start.X, 2) + Math.Pow(p.Value.Y - start.Y, 2)) < 4 * currentScale)
                {
                    return;
                }
                ink.Begin(start, arm.InkWidth);
            }
            ink.Arrow = WantsArrow();
            ink.Extend(p.Value);
        }

        private static bool WantsArrow() => Native.IsKeyDown(Native.VK_CONTROL);

        private PointF? LocalMouse()
        {
            if (!currentFrame.HasValue || !Native.GetCursorPos(out var m))
            {
                return null;
            }
            var f = currentFrame.Value;
            return new PointF(m.X - f.X, m.Y - f.Y);
        }

        public void Toggle()
        {
            if (IsActive)
            {
                Deactivate();
                return;
            }
            Activate();
        }

        private const string HintShownKey = "firstRunHintShown";

        public bool Activate()
        {
            if (IsActive)
            {
                return inputHook.IsInstalled;
            }
            IsActive = true;
            currentFrame = null;
            tapSound.Warm();

            CursorHider.Hide();
            Tick();
            window.Show();

            stillSince = DateTime.Now;
            RunTimer(BusyInterval);

            bool installed = inputHook.Install();
            inputHook.InteractVk = Preferences.InteractKey.VirtualKey();
            inputHook.ClickThrough = false;
            inputHook.SetActive(installed);
            Log.Write($"activate hookInstalled={installed}");

            if (currentFrame.HasValue)
            {
                PlaceToolbar();
            }
            toolbar.SetNeedsPermission(!installed);
            toolbar.Show();
            window.BringToFront();
            revealUntil = DateTime.Now.AddSeconds(3);
            if (installed && !Preferences.GetFlag(HintShownKey))
            {
                Preferences.SetFlag(HintShownKey, true);
                var parts = new List<(string, string)> { ("Click", "to tap"), ("Drag", "to draw") };
                if (Preferences.InteractKey != InteractKey.Off)
                {
                    parts.Add(($"Hold {Preferences.InteractKey.Symbol()}", "to click for real"));
                }
                if (Preferences.DoubleShiftClickThrough)
                {
                    parts.Add(("Double-tap Shift", "to click through"));
                }
                parts.Add(("Esc", "to exit"));
                toolbar.ShowHint(parts);
                revealUntil = DateTime.Now.AddSeconds(10);
                After(10, () => toolbar.HideHint());
            }
            SetEscEnabled(true);
            OnActiveChanged?.Invoke();
            return installed;
        }

        private void RetryHook()
        {
            if (!IsActive || inputHook.IsInstalled)
            {
                return;
            }
            bool installed = inputHook.Install();
            inputHook.SetActive(installed);
            toolbar.SetNeedsPermission(!installed);
            Log.Write($"retry hook installed={installed}");
        }

        public void Deactivate()
        {
            if (!IsActive)
            {
                return;
            }
            IsActive = false;

            timer.Stop();
            SetHighResTimer(false);
            timerInterval = TimeSpan.Zero;
            inputHook.SetActive(false);
            inputHook.ClickThrough = false;
            inputHook.Uninstall();
            shiftDownAt = null;
            lastShiftTap = DateTime.MinValue;
            arm.Held = false;
            downPoint = null;
            arm.Ink.Clear();
            window.ClearAll();
            window.Hide();
            toolbar.Hide();
            normalCursor = false;
            arm.PointerHidden = false;
            inputHook.PassThroughRects = Array.Empty<Native.RECT>();
            SetEscEnabled(false);
            ownMenuOpen = false;
            CursorHider.Show();
            OnActiveChanged?.Invoke();
        }

        private void PlaceToolbar()
        {
            var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfo(currentMonitor, ref info))
            {
                var work = Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
                toolbar.Place(work, currentScale);
            }
        }

        private void UpdateZones(Point mouse)
        {
            var f = toolbar.Frame;
            float s = currentScale;
            bool near = false;
            if (!f.IsEmpty)
            {
                var wide = Rectangle.Inflate(f, (int)(80 * s), 0);
                var above = f;
                above.Offset(0, -(int)(90 * s));
                var zone = Rectangle.Union(wide, above);
                zone.Inflate(0, (int)(10 * s));
                near = zone.Contains(mouse);
            }
            bool needsPermission = !inputHook.IsInstalled;
            bool drawing = arm.Ink.IsDrawing;
            toolbar.SetRevealed(needsPermission || (Preferences.ShowToolbar && !drawing && (near || DateTime.Now < revealUntil)));

            bool overTaskbar = taskbars.Any(r => r.Contains(mouse.X, mouse.Y));
            bool nearTaskbar = taskbars.Any(r => r.Left - 40 * s <= mouse.X && mouse.X < r.Right + 40 * s && r.Top - 40 * s <= mouse.Y && mouse.Y < r.Bottom + 40 * s);
            bool menuLikely = menuRects.Count > 0 || ownMenuOpen || DateTime.Now < menuOpeningUntil
                || (DateTime.Now - lastButtonEvent).TotalSeconds < 2 || nearTaskbar;
            double scanEvery = menuLikely ? MenuScanFast : MenuScanSlow;
            if ((DateTime.Now - lastMenuScan).TotalSeconds > scanEvery)
            {
                lastMenuScan = DateTime.Now;
                SystemUi.Scan(out taskbars, out menuRects, window.Handle);
                overTaskbar = taskbars.Any(r => r.Contains(mouse.X, mouse.Y));
            }
            bool overMenu = overTaskbar || menuRects.Any(r => r.Contains(mouse.X, mouse.Y)) || DateTime.Now < menuOpeningUntil;
            int vk = Preferences.InteractKey.VirtualKey();
            bool interacting = (vk != 0 && Native.IsKeyDown(vk)) || inputHook.InteractPressing;
            interactHeld = interacting;
            menuHover = overMenu;
            bool through = inputHook.ClickThrough;
            bool normal = !drawing && !Preferences.InteractKeepsHand && ((interacting && !through) || overMenu || ownMenuOpen);
            SetEscEnabled(menuRects.Count == 0 && !ownMenuOpen && !through);

            if (normal != normalCursor)
            {
                string at = $"at {mouse.X},{mouse.Y}";
                string why = interacting ? "interact key" : overTaskbar ? "taskbar" : "menu";
                Log.Write(normal ? $"real cursor ({why}) {at}" : $"pointer back {at}");
                normalCursor = normal;
                arm.PointerHidden = normal;
                if (normal)
                {
                    CursorHider.Show();
                }
                else
                {
                    CursorHider.Hide();
                }
            }

            // The tray icon lives in the taskbar, so the taskbar covers the Mac's status item rect too.
            var rects = new List<Native.RECT>();
            rects.AddRange(taskbars);
            rects.AddRange(menuRects);
            if (toolbar.Revealed && !f.IsEmpty)
            {
                rects.Add(new Native.RECT { Left = f.Left, Top = f.Top, Right = f.Right, Bottom = f.Bottom });
            }
            inputHook.PassThroughRects = rects.ToArray();
        }

        private void SetEscEnabled(bool enabled)
        {
            if (enabled && IsActive && escHotKey == null)
            {
                escHotKey = HotKeys.Shared.Register(Native.VK_ESCAPE, 0, Exit);
            }
            if (!enabled && escHotKey.HasValue)
            {
                HotKeys.Shared.Unregister(escHotKey.Value);
                escHotKey = null;
            }
        }

        private void RunTimer(TimeSpan interval)
        {
            if (timer.IsEnabled && interval == timerInterval)
            {
                return;
            }
            timer.Stop();
            timer.Interval = interval;
            timerInterval = interval;
            // The default 15.6 ms system tick can't do 120 Hz; ask for 1 ms while busy
            // (the Mac asks for a latency-critical activity instead).
            SetHighResTimer(interval == BusyInterval);
            timer.Start();
        }

        private void SetHighResTimer(bool on)
        {
            if (on == highResTimer)
            {
                return;
            }
            highResTimer = on;
            if (on)
            {
                Native.timeBeginPeriod(1);
            }
            else
            {
                Native.timeEndPeriod(1);
            }
        }

        private void NoteButtonEvent()
        {
            lastButtonEvent = DateTime.Now;
            Wake();
        }

        private void Wake()
        {
            stillSince = DateTime.Now;
            if (!IsActive || timerInterval == BusyInterval)
            {
                return;
            }
            RunTimer(BusyInterval);
            Tick();
        }

        private void UpdatePace(bool busy)
        {
            if (timerInterval == IdleInterval)
            {
                if (busy)
                {
                    Wake();
                }
                return;
            }
            bool still = (DateTime.Now - stillSince).TotalSeconds > IdleAfter;
            if (!still || busy || arm.Held || arm.IsSettling || arm.Ink.IsDrawing || arm.Ink.IsAnimating)
            {
                return;
            }
            inputHook.RequestWakeOnMove();
            RunTimer(IdleInterval);
        }

        private void Tick()
        {
            if (!IsActive || !Native.GetCursorPos(out var cursor))
            {
                return;
            }
            var mouse = new Point(cursor.X, cursor.Y);
            var monitor = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
            var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (!Native.GetMonitorInfo(monitor, ref info))
            {
                return;
            }
            var frame = Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);

            if (currentFrame != frame)
            {
                bool moving = currentFrame.HasValue;
                currentFrame = frame;
                currentMonitor = monitor;
                currentScale = (float)Native.ScaleForMonitor(monitor);
                if (moving)
                {
                    Release();
                    revealUntil = DateTime.Now.AddSeconds(1.5);
                }
                arm.Ink.Clear();
                window.SetBounds(frame);
                arm.Resize(frame.Width, frame.Height, currentScale);
                PlaceToolbar();
            }

            if (lastMouse != mouse)
            {
                lastMouse = mouse;
                stillSince = DateTime.Now;
                if (timerInterval == IdleInterval)
                {
                    Wake();
                    return;
                }
            }

            UpdateZones(mouse);
            bool urgent = interactHeld || menuHover || DateTime.Now < guardCursorUntil;

            if ((DateTime.Now - lastZOrder).TotalSeconds > 1)
            {
                // Other topmost windows can come up over us; stay on top like a screen-saver level window.
                lastZOrder = DateTime.Now;
                toolbar.KeepOnTop();
                window.BringToFront();
            }

            var local = new PointF(mouse.X - frame.X, mouse.Y - frame.Y);
            arm.Aim(local);

            if (downPoint.HasValue)
            {
                bool down = inputHook.ButtonHeld;
                if (down)
                {
                    buttonSeenDown = true;
                }
                if (buttonSeenDown && !down)
                {
                    Release();
                }
                else
                {
                    Drag();
                }
            }
            Render();

            if (!downPoint.HasValue)
            {
                bool busy = !normalCursor && urgent;
                UpdatePace(busy);
            }
        }

        private void Render()
        {
            var dirty = arm.TakeDirty();
            window.Render(dirty, arm.Draw);
        }

        // ---- Self tests ----

        public ArmRenderer ArmForTests => arm;
    }
}
