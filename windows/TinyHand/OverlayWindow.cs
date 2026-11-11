using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TinyHand
{
    /// <summary>
    /// A full-monitor, topmost, click-through layered window (WS_EX_LAYERED | WS_EX_TRANSPARENT |
    /// WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE). It owns a premultiplied DIB that
    /// GDI+ draws into, and pushes only the dirty part with UpdateLayeredWindowIndirect.
    /// The Mac equivalent is the borderless NSWindow at screenSaver+2 that ignores mouse events.
    /// </summary>
    internal sealed class OverlayWindow : IDisposable
    {
        private const string ClassName = "TinyHandOverlay";
        private static Native.WndProc wndProc; // kept alive for the lifetime of the class registration
        private static bool registered;

        public IntPtr Handle { get; private set; }
        public Rectangle Bounds { get; private set; }
        public bool Visible { get; private set; }

        private IntPtr memDC;
        private IntPtr dib;
        private IntPtr oldBitmap;
        private IntPtr bits;
        private Bitmap surface;
        private Graphics graphics;
        private bool needsFullPush = true;

        public OverlayWindow()
        {
            Register();
            uint ex = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            Handle = Native.CreateWindowEx(ex, ClassName, "TinyHand", Native.WS_POPUP, 0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
            if (Handle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
            }
        }

        private static void Register()
        {
            if (registered)
            {
                return;
            }
            wndProc = WindowProc;
            var wc = new Native.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = Native.GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            if (Native.RegisterClassEx(ref wc) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 1410) // ERROR_CLASS_ALREADY_EXISTS
                {
                    throw new InvalidOperationException($"RegisterClassEx failed: {error}");
                }
            }
            registered = true;
        }

        private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_MOUSEACTIVATE:
                    return new IntPtr(Native.MA_NOACTIVATE);
                case Native.WM_NCHITTEST:
                    return new IntPtr(Native.HTTRANSPARENT);
            }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        /// <summary>Moves the window over a monitor (physical pixels) and resizes its surface.</summary>
        public void SetBounds(Rectangle bounds)
        {
            if (bounds == Bounds && surface != null)
            {
                return;
            }
            bool resize = surface == null || bounds.Width != Bounds.Width || bounds.Height != Bounds.Height;
            Bounds = bounds;
            if (resize)
            {
                CreateSurface(bounds.Width, bounds.Height);
            }
            needsFullPush = true;
        }

        private void CreateSurface(int width, int height)
        {
            FreeSurface();
            var screen = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);
            var header = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };
            dib = Native.CreateDIBSection(memDC, ref header, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDIBSection failed");
            }
            oldBitmap = Native.SelectObject(memDC, dib);
            // The DIB starts zeroed: fully transparent. GDI+ draws straight into it.
            surface = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
            graphics = Graphics.FromImage(surface);
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
        }

        private void FreeSurface()
        {
            graphics?.Dispose();
            graphics = null;
            surface?.Dispose();
            surface = null;
            if (memDC != IntPtr.Zero)
            {
                if (oldBitmap != IntPtr.Zero)
                {
                    Native.SelectObject(memDC, oldBitmap);
                }
                Native.DeleteDC(memDC);
                memDC = IntPtr.Zero;
            }
            if (dib != IntPtr.Zero)
            {
                Native.DeleteObject(dib);
                dib = IntPtr.Zero;
            }
        }

        /// <summary>Clears <paramref name="dirty"/>, lets <paramref name="paint"/> draw it, and shows the result.</summary>
        public void Render(Rectangle dirty, Action<Graphics, Rectangle> paint)
        {
            if (graphics == null)
            {
                return;
            }
            if (needsFullPush)
            {
                dirty = new Rectangle(0, 0, Bounds.Width, Bounds.Height);
            }
            if (dirty.IsEmpty)
            {
                return;
            }
            var g = graphics;
            g.ResetTransform();
            g.SetClip(dirty);
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
            {
                g.FillRectangle(clear, dirty);
            }
            g.CompositingMode = CompositingMode.SourceOver;
            paint(g, dirty);
            g.ResetTransform();
            g.ResetClip();
            g.Flush(FlushIntention.Sync);
            Push(dirty);
        }

        private unsafe void Push(Rectangle dirty)
        {
            var dst = new Native.POINT(Bounds.X, Bounds.Y);
            var size = new Native.SIZE { cx = Bounds.Width, cy = Bounds.Height };
            var src = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
            var rect = new Native.RECT { Left = dirty.Left, Top = dirty.Top, Right = dirty.Right, Bottom = dirty.Bottom };
            var info = new Native.UPDATELAYEREDWINDOWINFO
            {
                cbSize = sizeof(Native.UPDATELAYEREDWINDOWINFO),
                hdcDst = IntPtr.Zero,
                pptDst = &dst,
                psize = &size,
                hdcSrc = memDC,
                pptSrc = &src,
                pblend = &blend,
                dwFlags = Native.ULW_ALPHA,
                prcDirty = needsFullPush ? null : &rect,
            };
            if (!Native.UpdateLayeredWindowIndirect(Handle, &info))
            {
                Log.Write($"UpdateLayeredWindowIndirect failed: {Marshal.GetLastWin32Error()}");
                needsFullPush = true;
                return;
            }
            needsFullPush = false;
        }

        public void Show()
        {
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            BringToFront();
            Visible = true;
        }

        public void Hide()
        {
            Native.ShowWindow(Handle, Native.SW_HIDE);
            Visible = false;
        }

        /// <summary>Back on top of other topmost windows (and above the toolbar, as on the Mac).</summary>
        public void BringToFront()
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        /// <summary>Wipes the whole surface (used when the pointer turns off).</summary>
        public void ClearAll()
        {
            if (graphics == null)
            {
                return;
            }
            Render(new Rectangle(0, 0, Bounds.Width, Bounds.Height), (g, r) => { });
        }

        public void Dispose()
        {
            FreeSurface();
            if (Handle != IntPtr.Zero)
            {
                Native.DestroyWindow(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }
}
