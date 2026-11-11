using System;
using System.Collections.Generic;
using System.Windows.Interop;

namespace TinyHand
{
    /// <summary>
    /// Global hot keys with RegisterHotKey on a hidden message-only window. Port of
    /// HotKeyCenter.swift (Carbon RegisterEventHotKey).
    /// </summary>
    internal sealed class HotKeys
    {
        public static readonly HotKeys Shared = new HotKeys();

        private readonly HwndSource source;
        private readonly Dictionary<int, Action> handlers = new Dictionary<int, Action>();
        private int nextId = 1;

        private HotKeys()
        {
            var p = new HwndSourceParameters("TinyHandHotKeys")
            {
                ParentWindow = Native.HWND_MESSAGE,
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            };
            source = new HwndSource(p);
            source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (handlers.TryGetValue(id, out var action))
                {
                    handled = true;
                    action();
                }
            }
            return IntPtr.Zero;
        }

        /// <returns>An id, or null if another app already owns the combination.</returns>
        public int? Register(uint keyCode, uint modifiers, Action action)
        {
            int id = nextId++;
            if (!Native.RegisterHotKey(source.Handle, id, modifiers | Native.MOD_NOREPEAT, keyCode))
            {
                return null;
            }
            handlers[id] = action;
            return id;
        }

        public void Unregister(int id)
        {
            if (handlers.Remove(id))
            {
                Native.UnregisterHotKey(source.Handle, id);
            }
        }
    }
}
