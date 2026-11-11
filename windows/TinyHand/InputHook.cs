using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace TinyHand
{
    /// <summary>Modifier keys held right after a keyboard event, plus the input counter at that moment.</summary>
    internal readonly struct ModifierSnapshot
    {
        public readonly bool Shift, Control, Alt, Win;
        public readonly uint InputCount;

        public ModifierSnapshot(bool shift, bool control, bool alt, bool win, uint inputCount)
        {
            Shift = shift;
            Control = control;
            Alt = alt;
            Win = win;
            InputCount = inputCount;
        }

        public bool OnlyShift => Shift && !Control && !Alt && !Win;
        public bool None => !Shift && !Control && !Alt && !Win;
    }

    /// <summary>
    /// Port of InputTap.swift. A low-level mouse hook (WH_MOUSE_LL) on its own high-priority
    /// thread with its own message loop, so a busy UI thread never delays the system's mouse.
    /// Clicks are swallowed and reported as taps/strokes, except when the interact key is held,
    /// click-through is latched, the press lands in a pass-through rectangle (taskbar, toolbar,
    /// open menus) or the session is locked. A keyboard hook (WH_KEYBOARD_LL) only watches
    /// modifiers and counts key presses for the double-Shift heuristic; it never blocks a key.
    /// Callbacks are posted to the UI dispatcher.
    /// </summary>
    internal sealed class InputHook
    {
        public Action OnMouseDown;
        public Action OnInteractDown;
        public Action OnInteractUp;
        public Action OnMouseUp;
        public Action OnLeftDragged;
        public Action<Native.POINT> OnPassThroughPress;
        public Action OnScreenLocked;
        public Action<ModifierSnapshot> OnModifiersChanged;
        public Action OnWake;

        private readonly Dispatcher dispatcher;
        private readonly object gate = new object();

        private volatile bool active;
        private volatile bool clickThrough;
        private volatile bool interactPress;
        private volatile int interactVk = Native.VK_MENU;
        private volatile bool sessionLocked;
        private volatile bool wakeOnMove;
        private Native.RECT[] passThroughRects = Array.Empty<Native.RECT>();

        // Touched only on the hook thread.
        private bool passingThrough;
        private bool swallowedButtonHeld;
        private bool leftHeld;
        private bool shiftL, shiftR, ctrlL, ctrlR, altL, altR, winL, winR;
        private bool lastShift, lastCtrl, lastAlt, lastWin;

        private int inputCount;

        private Thread thread;
        private uint threadId;
        private IntPtr mouseHook;
        private IntPtr keyboardHook;
        private Native.HookProc mouseProc;
        private Native.HookProc keyboardProc;

        public bool IsInstalled { get; private set; }

        public InputHook(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
        }

        public Native.RECT[] PassThroughRects
        {
            get { lock (gate) return passThroughRects; }
            set { lock (gate) passThroughRects = value ?? Array.Empty<Native.RECT>(); }
        }

        /// <summary>Virtual key held to click for real (0 = off).</summary>
        public int InteractVk
        {
            get => interactVk;
            set => interactVk = value;
        }

        /// <summary>Latched by double-tapping Shift: every click reaches the app, as if the interact key were held.</summary>
        public bool ClickThrough
        {
            get => clickThrough;
            set => clickThrough = value;
        }

        public bool InteractPressing => interactPress;

        /// <summary>Set while the workstation is locked: everything passes through.</summary>
        public bool SessionLocked
        {
            get => sessionLocked;
            set => sessionLocked = value;
        }

        /// <summary>A swallowed button is still down (the Mac reads the HID button state for this).</summary>
        public bool ButtonHeld => Volatile.Read(ref swallowedHeldFlag) != 0;
        private int swallowedHeldFlag;

        /// <summary>Asks for one <see cref="OnWake"/> on the next mouse move (used while idle).</summary>
        public void RequestWakeOnMove() => wakeOnMove = true;

        /// <summary>Key presses and clicks seen since the hooks started (for the Shift heuristic).</summary>
        public uint InputCount => (uint)Volatile.Read(ref inputCount);

        public bool Install()
        {
            if (IsInstalled)
            {
                return true;
            }
            bool ok = false;
            using var ready = new ManualResetEventSlim(false);
            mouseProc = MouseProc;
            keyboardProc = KeyboardProc;
            var t = new Thread(() =>
            {
                threadId = Native.GetCurrentThreadId();
                IntPtr module = Native.GetModuleHandle(null);
                mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
                keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
                ok = mouseHook != IntPtr.Zero;
                if (!ok)
                {
                    Log.Write($"mouse hook failed: {Marshal.GetLastWin32Error()}");
                }
                if (keyboardHook == IntPtr.Zero)
                {
                    Log.Write($"keyboard hook failed: {Marshal.GetLastWin32Error()}");
                }
                ready.Set();
                if (!ok)
                {
                    if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
                    keyboardHook = IntPtr.Zero;
                    return;
                }
                while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                Native.UnhookWindowsHookEx(mouseHook);
                if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
                mouseHook = IntPtr.Zero;
                keyboardHook = IntPtr.Zero;
            })
            {
                IsBackground = true,
                Name = "TinyHand input hook",
                Priority = ThreadPriority.Highest,
            };
            t.Start();
            ready.Wait(5000);
            if (!ok)
            {
                return false;
            }
            thread = t;
            IsInstalled = true;
            return true;
        }

        public void Uninstall()
        {
            if (!IsInstalled)
            {
                return;
            }
            IsInstalled = false;
            active = false;
            Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread?.Join(2000);
            thread = null;
            ResetHookState();
        }

        public void SetActive(bool value)
        {
            active = value;
            interactPress = false;
        }

        private void ResetHookState()
        {
            passingThrough = false;
            swallowedButtonHeld = false;
            leftHeld = false;
            Volatile.Write(ref swallowedHeldFlag, 0);
            shiftL = shiftR = ctrlL = ctrlR = altL = altR = winL = winR = false;
            lastShift = lastCtrl = lastAlt = lastWin = false;
        }

        private void Post(Action action)
        {
            if (action != null)
            {
                dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
            }
        }

        private static bool IsDown(int msg) =>
            msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN || msg == Native.WM_XBUTTONDOWN;

        private static bool IsUp(int msg) =>
            msg == Native.WM_LBUTTONUP || msg == Native.WM_RBUTTONUP || msg == Native.WM_MBUTTONUP || msg == Native.WM_XBUTTONUP;

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
            {
                return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
            }
            int msg = wParam.ToInt32();
            try
            {
                if (Handle(msg, lParam))
                {
                    return new IntPtr(1); // swallowed
                }
            }
            catch (Exception e)
            {
                Log.Write($"mouse hook error: {e.Message}");
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        /// <returns>true to swallow the event.</returns>
        private bool Handle(int msg, IntPtr lParam)
        {
            bool down = IsDown(msg);
            bool up = IsUp(msg);
            if (down)
            {
                Interlocked.Increment(ref inputCount);
            }
            if (msg == Native.WM_MOUSEMOVE && wakeOnMove)
            {
                wakeOnMove = false;
                Post(OnWake);
            }

            if (!active)
            {
                return false;
            }
            if (sessionLocked)
            {
                if (down) Post(OnScreenLocked);
                return false;
            }

            if (down)
            {
                int vk = interactVk;
                bool viaKey = vk != 0 && Native.IsKeyDown(vk);
                if (viaKey || clickThrough)
                {
                    interactPress = true;
                    Post(OnInteractDown);
                }
            }
            if (interactPress)
            {
                if (up)
                {
                    interactPress = false;
                    Post(OnInteractUp);
                }
                return false;
            }

            if (down)
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                passingThrough = false;
                foreach (var r in PassThroughRects)
                {
                    if (r.Contains(info.pt.X, info.pt.Y))
                    {
                        passingThrough = true;
                        break;
                    }
                }
                if (passingThrough)
                {
                    var pt = info.pt;
                    var cb = OnPassThroughPress;
                    if (cb != null) dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => cb(pt)));
                }
            }
            if (passingThrough)
            {
                if (up)
                {
                    passingThrough = false;
                }
                return false;
            }

            if (down)
            {
                swallowedButtonHeld = true;
                Volatile.Write(ref swallowedHeldFlag, 1);
                if (msg == Native.WM_LBUTTONDOWN)
                {
                    leftHeld = true;
                }
                Post(OnMouseDown);
                return true;
            }
            if (up)
            {
                if (msg == Native.WM_LBUTTONUP)
                {
                    leftHeld = false;
                }
                swallowedButtonHeld = false;
                Volatile.Write(ref swallowedHeldFlag, 0);
                Post(OnMouseUp);
                return true;
            }
            if (msg == Native.WM_MOUSEMOVE && leftHeld && swallowedButtonHeld)
            {
                Post(OnLeftDragged);
            }
            return false; // moves and wheel always reach the system
        }

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                    bool isUp = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                    var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)info.vkCode;
                    if (down || isUp)
                    {
                        if (!TrackModifier(vk, down) && down)
                        {
                            Interlocked.Increment(ref inputCount);
                        }
                        bool shift = shiftL || shiftR, ctrl = ctrlL || ctrlR, alt = altL || altR, win = winL || winR;
                        if (shift != lastShift || ctrl != lastCtrl || alt != lastAlt || win != lastWin)
                        {
                            lastShift = shift;
                            lastCtrl = ctrl;
                            lastAlt = alt;
                            lastWin = win;
                            var snapshot = new ModifierSnapshot(shift, ctrl, alt, win, InputCount);
                            var cb = OnModifiersChanged;
                            if (cb != null) dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => cb(snapshot)));
                            if (wakeOnMove)
                            {
                                wakeOnMove = false;
                                Post(OnWake);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Write($"keyboard hook error: {e.Message}");
                }
            }
            return Native.CallNextHookEx(keyboardHook, nCode, wParam, lParam);
        }

        /// <returns>true if <paramref name="vk"/> is a modifier key.</returns>
        private bool TrackModifier(int vk, bool down)
        {
            switch (vk)
            {
                case Native.VK_LSHIFT: shiftL = down; return true;
                case Native.VK_RSHIFT: shiftR = down; return true;
                case Native.VK_SHIFT: shiftL = down; return true;
                case Native.VK_LCONTROL: ctrlL = down; return true;
                case Native.VK_RCONTROL: ctrlR = down; return true;
                case Native.VK_CONTROL: ctrlL = down; return true;
                case Native.VK_LMENU: altL = down; return true;
                case Native.VK_RMENU: altR = down; return true;
                case Native.VK_MENU: altL = down; return true;
                case Native.VK_LWIN: winL = down; return true;
                case Native.VK_RWIN: winR = down; return true;
                default: return false;
            }
        }
    }
}
