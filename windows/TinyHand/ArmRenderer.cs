using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TinyHand
{
    /// <summary>
    /// The hand on a stick (or the laser dot) and the ink, drawn with GDI+ into the overlay's
    /// bitmap. Port of ArmView.swift: same grip, lean, lag, press and lift maths, done in the
    /// Mac's y-up space and turned into a y-down transform only when drawing.
    /// </summary>
    internal sealed class ArmRenderer
    {
        public PointerStyle Style { get; private set; } = PointerStyle.Lavender;
        private HandAsset asset;

        private double handLenFrac = Preferences.PointerSize;
        private bool showShadow = Preferences.ShowShadow;
        private const double MinRodFrac = 0.05;
        private const double GripLeanFrac = 0.30;
        private const double GripDepthFrac = 0.12;
        private const double GripFollow = 0.08;
        private const double LiftFrac = 0.06;

        public readonly Ink Ink = new Ink();
        public bool Held;

        public int Width { get; private set; } = 1440;
        public int Height { get; private set; } = 900;

        /// <summary>DPI factor of the monitor (1 = 96 dpi), for the few fixed point sizes.</summary>
        public float Scale { get; private set; } = 1;

        public float InkWidth => (float)Math.Max(3 * Scale, Height * 0.0055);

        private double pressAmount;
        private double? gripX;
        private double? gripTarget;

        // Image sized for the current screen, plus its two blurred shadows (rest and pressed).
        private Bitmap display;
        private bool displayOwned; // resized copy we made (the full image belongs to the asset cache)
        private HandAsset displayFor;
        private int displayHeight;
        private ShadowSprite restShadow;
        private ShadowSprite pressShadow;

        private Pose pose;
        private double[] lastPose = Array.Empty<double>();
        private double[] lastLaserPose = Array.Empty<double>();
        private RectangleF drawnBounds = RectangleF.Empty;
        private RectangleF dirty = RectangleF.Empty;

        private struct Pose
        {
            public bool Valid;
            public bool Laser;
            public double Rotation, RodLen, HandPts, WidthPts, GripX, GripYDown, Lift, Press;
            public double ShadowOpacity, ShadowRadius;
            public double DotR, DotX, DotY;
        }

        public ArmRenderer()
        {
            SetStyle(PointerStyle.Saved);
        }

        public bool IsSettling
        {
            get
            {
                if (pressAmount > 0.002)
                {
                    return true;
                }
                if (gripX == null || gripTarget == null)
                {
                    return false;
                }
                return Math.Abs(gripTarget.Value - gripX.Value) > 0.5;
            }
        }

        private bool pointerHidden;

        public bool PointerHidden
        {
            get => pointerHidden;
            set
            {
                if (pointerHidden != value)
                {
                    pointerHidden = value;
                    MarkDirty(drawnBounds);
                    lastPose = Array.Empty<double>();
                    lastLaserPose = Array.Empty<double>();
                }
            }
        }

        public void ReloadPreferences()
        {
            handLenFrac = Preferences.PointerSize;
            showShadow = Preferences.ShowShadow;
        }

        public void UpdateInkColor()
        {
            Ink.StrokeColor = Preferences.InkChoice.Resolved(Style);
        }

        public void SetStyle(PointerStyle newStyle)
        {
            Style = newStyle;
            asset = newStyle.AssetName == null ? null : HandAsset.Load(newStyle.AssetName);
            if (!newStyle.IsLaser && asset == null)
            {
                Style = PointerStyle.Laser;
            }
            UpdateInkColor();
            displayHeight = 0;
            lastPose = Array.Empty<double>();
            lastLaserPose = Array.Empty<double>();
            MarkDirty(drawnBounds);
        }

        public void Press()
        {
            pressAmount = 1;
        }

        /// <summary>The overlay moved to a monitor of this size (physical pixels).</summary>
        public void Resize(int width, int height, float scale)
        {
            if (width == Width && height == Height && Math.Abs(scale - Scale) < 0.001f)
            {
                return;
            }
            Width = width;
            Height = height;
            Scale = scale;
            Ink.Scale = scale;
            gripX = null;
            displayHeight = 0;
            lastPose = Array.Empty<double>();
            lastLaserPose = Array.Empty<double>();
            drawnBounds = RectangleF.Empty;
            dirty = new RectangleF(0, 0, width, height);
        }

        /// <param name="cursor">Cursor in overlay pixels, y down.</param>
        public void Aim(PointF cursor)
        {
            double w = Width;
            double screenH = Height;
            // Everything below is the Mac's y-up maths.
            double cx = cursor.X;
            double cy = screenH - cursor.Y;

            double targetX = Math.Min(Math.Max(cx + GripLeanFrac * w, w * 0.15), w * 1.05);
            double gx = gripX.HasValue ? gripX.Value + (targetX - gripX.Value) * GripFollow : targetX;
            gripX = gx;
            gripTarget = targetX;
            double gripY = -GripDepthFrac * screenH;

            if (!Held)
            {
                pressAmount += (0 - pressAmount) * 0.18;
            }

            if (Style.IsLaser)
            {
                AimLaser(cx, cursor.Y);
                return;
            }
            var a = asset;
            if (a == null)
            {
                return;
            }
            UpdateDisplayImage(a);

            double dx = cx - gx;
            double dy = cy - gripY;
            double reach = Math.Sqrt(dx * dx + dy * dy);
            double aimAngle = Math.Atan2(dy, dx);

            double handPts = handLenFrac * screenH * (1 - 0.06 * pressAmount);
            double s = handPts / (a.Wrist - a.HandTop);
            double widthPts = s * a.Aspect;
            double handTipPts = (a.Wrist - a.Tip) * s;
            double tipOffX = (a.TipX - a.RodX) * widthPts;

            double along = Math.Sqrt(Math.Max(0, reach * reach - tipOffX * tipOffX));
            double rodLen = along - handTipPts;
            rodLen = Math.Max(MinRodFrac * screenH, rodLen);
            double tipAngle = Math.Atan2(rodLen + handTipPts, tipOffX);
            double rotation = aimAngle - tipAngle;

            double lift = LiftFrac * screenH * (1 - 0.8 * pressAmount);

            var key = new[] { rotation, rodLen, handPts, widthPts, gx, lift, pressAmount, showShadow ? 1 : 0, a.Aspect, pointerHidden ? 1 : 0 };
            for (int i = 0; i < key.Length; i++)
            {
                key[i] = Math.Round(key[i] * 100);
            }
            if (SameArray(key, lastPose))
            {
                return;
            }
            lastPose = key;

            pose = new Pose
            {
                Valid = true,
                Laser = false,
                Rotation = rotation,
                RodLen = rodLen,
                HandPts = handPts,
                WidthPts = widthPts,
                GripX = gx,
                GripYDown = screenH - gripY,
                Lift = lift,
                Press = pressAmount,
                ShadowRadius = Math.Max(1, lift * 0.35) + 5 * pressAmount,
                ShadowOpacity = showShadow ? 0.26 + 0.02 * pressAmount : 0,
            };
            UpdateBounds();
        }

        private void AimLaser(double cx, double cyDown)
        {
            double press = pressAmount;
            double r = Math.Max(4 * Scale, Height * 0.0038 * handLenFrac / 0.13) * (1 + 0.2 * press);
            var key = new[] { Math.Round(r * 100), Math.Round(cx * 100), Math.Round(cyDown * 100), pointerHidden ? 1 : 0 };
            if (SameArray(key, lastLaserPose))
            {
                return;
            }
            lastLaserPose = key;
            pose = new Pose
            {
                Valid = true,
                Laser = true,
                DotR = r,
                DotX = cx,
                DotY = cyDown,
                Press = press,
                ShadowRadius = r * (2.2 + 1.0 * press),
            };
            UpdateBounds();
        }

        private static bool SameArray(double[] a, double[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private void MarkDirty(RectangleF r)
        {
            if (r.IsEmpty) return;
            dirty = dirty.IsEmpty ? r : RectangleF.Union(dirty, r);
        }

        private void UpdateBounds()
        {
            MarkDirty(drawnBounds);
            drawnBounds = pointerHidden ? RectangleF.Empty : ComputeBounds();
            MarkDirty(drawnBounds);
        }

        /// <summary>The area to repaint this frame (arm and ink), then forgets it.</summary>
        public Rectangle TakeDirty()
        {
            Ink.Advance();
            var r = dirty;
            if (!Ink.Dirty.IsEmpty)
            {
                r = r.IsEmpty ? Ink.Dirty : RectangleF.Union(r, Ink.Dirty);
            }
            dirty = RectangleF.Empty;
            Ink.ResetDirty();
            if (r.IsEmpty)
            {
                return Rectangle.Empty;
            }
            var rect = Rectangle.FromLTRB((int)Math.Floor(r.Left) - 1, (int)Math.Floor(r.Top) - 1, (int)Math.Ceiling(r.Right) + 1, (int)Math.Ceiling(r.Bottom) + 1);
            rect.Intersect(new Rectangle(0, 0, Width, Height));
            return rect;
        }

        /// <summary>Forces the whole overlay to be repainted on the next frame.</summary>
        public void InvalidateAll() => dirty = new RectangleF(0, 0, Width, Height);

        // ---- Geometry ----

        private Matrix ArmMatrix(double offsetX = 0, double offsetY = 0)
        {
            var m = new Matrix();
            m.Translate((float)(pose.GripX + offsetX), (float)(pose.GripYDown + offsetY));
            m.Rotate((float)(-pose.Rotation * 180 / Math.PI));
            return m;
        }

        /// <summary>Rod and hand rectangles in arm space (origin at the grip, the arm going up -y).</summary>
        private (RectangleF rod, RectangleF hand) ArmRects(double pad)
        {
            var a = asset;
            double w = pose.WidthPts;
            double left = -a.RodX * w;
            var rod = RectangleF.FromLTRB((float)(left + a.RodInkMinX * w - pad), (float)-pose.RodLen,
                (float)(left + a.RodInkMaxX * w + pad), 0);
            var hand = RectangleF.FromLTRB((float)(left + a.HandInkMinX * w - pad), (float)(-pose.RodLen - pose.HandPts - pad),
                (float)(left + a.HandInkMaxX * w + pad), (float)-pose.RodLen);
            return (rod, hand);
        }

        private RectangleF ComputeBounds()
        {
            if (!pose.Valid)
            {
                return RectangleF.Empty;
            }
            if (pose.Laser)
            {
                double extent = pose.DotR + pose.ShadowRadius * 1.2 + 2;
                return RectangleF.FromLTRB((float)(pose.DotX - extent), (float)(pose.DotY - extent), (float)(pose.DotX + extent), (float)(pose.DotY + extent));
            }
            if (asset == null)
            {
                return RectangleF.Empty;
            }
            double pad = pose.ShadowOpacity > 0 ? ShadowPad(pose.ShadowRadius) : 2;
            var (rod, hand) = ArmRects(pad);
            var result = RectangleF.Empty;
            foreach (var (ox, oy) in new[] { (0.0, 0.0), (pose.Lift * 0.6, pose.Lift) })
            {
                using var m = ArmMatrix(ox, oy);
                foreach (var r in new[] { rod, hand })
                {
                    var pts = new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom), new PointF(r.Right, r.Bottom) };
                    m.TransformPoints(pts);
                    foreach (var p in pts)
                    {
                        var one = new RectangleF(p.X - 2, p.Y - 2, 4, 4);
                        result = result.IsEmpty ? one : RectangleF.Union(result, one);
                    }
                }
                if (pose.ShadowOpacity <= 0) break;
            }
            return result;
        }

        private static double ShadowPad(double radius) => radius * 1.5 + 4;

        // ---- Display image and shadows ----

        private void UpdateDisplayImage(HandAsset a)
        {
            var full = a.Image;
            double restPts = handLenFrac * Height / (a.Wrist - a.HandTop);
            const int step = 64;
            int needed = ((int)Math.Ceiling(restPts) + step - 1) / step * step;
            int height = needed >= full.Height * 9 / 10 ? full.Height : Math.Max(step, needed);
            if (height == displayHeight && displayFor == a && display != null)
            {
                return;
            }
            displayHeight = height;
            displayFor = a;
            if (displayOwned)
            {
                display?.Dispose();
            }
            displayOwned = height != full.Height;
            display = displayOwned ? HandAsset.Resized(full, height) : full;
            restShadow?.Dispose();
            pressShadow?.Dispose();
            // Shadow radius at rest and when pressed, as on the Mac (CALayer radius ~ 2 sigma).
            double restLift = LiftFrac * Height;
            double pressLift = LiftFrac * Height * 0.2;
            double displayPerPt = display.Height / (Height * handLenFrac / (a.Wrist - a.HandTop));
            restShadow = ShadowSprite.Make(display, (Math.Max(1, restLift * 0.35) / 2) * displayPerPt);
            pressShadow = ShadowSprite.Make(display, ((Math.Max(1, pressLift * 0.35) + 5) / 2) * displayPerPt);
        }

        // ---- Drawing ----

        public void Draw(Graphics g, Rectangle clip)
        {
            Ink.Draw(g, clip);
            if (pointerHidden || !pose.Valid || !drawnBounds.IntersectsWith(clip))
            {
                return;
            }
            if (pose.Laser)
            {
                DrawLaser(g);
                return;
            }
            var a = asset;
            if (a == null || display == null || displayFor != a)
            {
                return;
            }
            var state = g.Save();
            using var baseTransform = g.Transform; // identity on the overlay, a cell offset in the render test
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            double dw = display.Width;
            double dh = display.Height;
            double w = pose.WidthPts;
            double left = -a.RodX * w;
            double sx = w / dw;
            double rodSy = pose.RodLen / ((a.RodEnd - a.Wrist) * dh);
            double handSy = pose.HandPts / ((a.Wrist - a.HandTop) * dh);

            // Source rectangles in display-image pixels.
            var rodSrc = RectangleF.FromLTRB((float)(a.RodInkMinX * dw), (float)(a.Wrist * dh), (float)(a.RodInkMaxX * dw), (float)(a.RodEnd * dh));
            var handSrc = RectangleF.FromLTRB((float)(a.HandInkMinX * dw), (float)(a.HandTop * dh), (float)(a.HandInkMaxX * dw), (float)(a.Wrist * dh));

            RectangleF RodDest(RectangleF src) => RectangleF.FromLTRB(
                (float)(left + src.Left * sx), (float)(-pose.RodLen + (src.Top - a.Wrist * dh) * rodSy),
                (float)(left + src.Right * sx), (float)(-pose.RodLen + (src.Bottom - a.Wrist * dh) * rodSy));
            RectangleF HandDest(RectangleF src) => RectangleF.FromLTRB(
                (float)(left + src.Left * sx), (float)(-pose.RodLen - pose.HandPts + (src.Top - a.HandTop * dh) * handSy),
                (float)(left + src.Right * sx), (float)(-pose.RodLen - pose.HandPts + (src.Bottom - a.HandTop * dh) * handSy));

            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);

            // Rod with its shadow, then the hand with its shadow (the hand's shadow falls on the rod).
            DrawShadow(g, baseTransform, RodDest, rodSrc, extendTop: false);
            using (var m = Combine(baseTransform, ArmMatrix()))
            {
                g.Transform = m;
                g.DrawImage(display, ToPoints(RodDest(rodSrc)), rodSrc, GraphicsUnit.Pixel, attrs);
            }
            g.Transform = baseTransform;
            DrawShadow(g, baseTransform, HandDest, handSrc, extendTop: true);
            using (var m = Combine(baseTransform, ArmMatrix()))
            {
                g.Transform = m;
                g.DrawImage(display, ToPoints(HandDest(handSrc)), handSrc, GraphicsUnit.Pixel, attrs);
            }
            g.Restore(state);
        }

        /// <summary>Arm space -> overlay (<paramref name="arm"/>) -> whatever the Graphics already had.</summary>
        private static Matrix Combine(Matrix baseTransform, Matrix arm)
        {
            var m = baseTransform.Clone();
            m.Multiply(arm, MatrixOrder.Prepend);
            arm.Dispose();
            return m;
        }

        private static PointF[] ToPoints(RectangleF r) =>
            new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom) };

        /// <summary>
        /// Blurred silhouette, offset down and to the right (the Mac's lift), crossfading between
        /// the rest and pressed blur as the hand presses.
        /// </summary>
        private void DrawShadow(Graphics g, Matrix baseTransform, Func<RectangleF, RectangleF> dest, RectangleF src, bool extendTop)
        {
            if (pose.ShadowOpacity <= 0 || restShadow == null || pressShadow == null)
            {
                return;
            }
            double p = Math.Clamp(pose.Press, 0, 1);
            var layers = new[] { (restShadow, (1 - p) * pose.ShadowOpacity), (pressShadow, p * pose.ShadowOpacity) };
            using var m = Combine(baseTransform, ArmMatrix(pose.Lift * 0.6, pose.Lift));
            g.Transform = m;
            foreach (var (sprite, opacity) in layers)
            {
                if (opacity < 0.01)
                {
                    continue;
                }
                float pad = sprite.Pad;
                var padded = RectangleF.FromLTRB(src.Left - pad, extendTop ? src.Top - pad : src.Top, src.Right + pad, src.Bottom);
                var spriteSrc = new RectangleF((padded.Left + sprite.Pad) / sprite.Factor, (padded.Top + sprite.Pad) / sprite.Factor,
                    padded.Width / sprite.Factor, padded.Height / sprite.Factor);
                using var attrs = new ImageAttributes();
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                attrs.SetColorMatrix(new ColorMatrix { Matrix33 = (float)opacity });
                g.DrawImage(sprite.Bitmap, ToPoints(dest(padded)), spriteSrc, GraphicsUnit.Pixel, attrs);
            }
            g.Transform = baseTransform;
        }

        private void DrawLaser(Graphics g)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float x = (float)pose.DotX;
            float y = (float)pose.DotY;
            float r = (float)pose.DotR;
            float glow = (float)(r + pose.ShadowRadius * 1.2);
            var red = Ink.ToDrawing(Ink.Red);

            using (var path = new GraphicsPath())
            {
                path.AddEllipse(x - glow, y - glow, 2 * glow, 2 * glow);
                using var brush = new PathGradientBrush(path)
                {
                    CenterPoint = new PointF(x, y),
                    CenterColor = Color.FromArgb(235, red),
                    SurroundColors = new[] { Color.FromArgb(0, red) },
                    Blend = new Blend
                    {
                        Positions = new[] { 0f, 0.2f, 0.45f, 0.7f, 0.85f, 1f },
                        Factors = new[] { 0f, 0.05f, 0.22f, 0.55f, 0.8f, 1f },
                    },
                };
                g.FillEllipse(brush, x - glow, y - glow, 2 * glow, 2 * glow);
            }
            using (var dot = new SolidBrush(red))
            {
                g.FillEllipse(dot, x - r, y - r, 2 * r, 2 * r);
            }
            float core = r * 0.45f;
            using (var c = new SolidBrush(Color.FromArgb(242, 255, 219, 209)))
            {
                g.FillEllipse(c, x - core, y - core, 2 * core, 2 * core);
            }
            g.Restore(state);
        }

        /// <summary>A blurred black silhouette of the display image, kept small and scaled up when drawn.</summary>
        private sealed class ShadowSprite : IDisposable
        {
            public Bitmap Bitmap;
            public float Factor; // display pixels per sprite pixel
            public float Pad;    // display pixels of margin around the image

            public static ShadowSprite Make(Bitmap display, double sigma)
            {
                sigma = Math.Max(0.5, sigma);
                int factor = (int)Math.Clamp(Math.Round(sigma / 3), 1, 4);
                float pad = (float)Math.Ceiling(3 * sigma) + 2;
                int sw = (int)Math.Ceiling((display.Width + 2 * pad) / factor);
                int sh = (int)Math.Ceiling((display.Height + 2 * pad) / factor);
                var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(small))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(display, new RectangleF(pad / factor, pad / factor, display.Width / (float)factor, display.Height / (float)factor));
                }
                var alpha = HandAsset.AlphaOf(small, out _, out _);
                int radius = Math.Max(1, (int)Math.Round(sigma / factor));
                BoxBlur(alpha, sw, sh, radius);
                BoxBlur(alpha, sw, sh, radius);
                BoxBlur(alpha, sw, sh, radius);
                var data = small.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    var row = new byte[data.Stride];
                    for (int y = 0; y < sh; y++)
                    {
                        Array.Clear(row, 0, row.Length);
                        for (int x = 0; x < sw; x++)
                        {
                            row[x * 4 + 3] = alpha[y * sw + x]; // premultiplied black: colour stays 0
                        }
                        System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
                    }
                }
                finally
                {
                    small.UnlockBits(data);
                }
                return new ShadowSprite { Bitmap = small, Factor = factor, Pad = pad };
            }

            private static void BoxBlur(byte[] a, int w, int h, int r)
            {
                var tmp = new byte[Math.Max(w, h)];
                int div = 2 * r + 1;
                for (int y = 0; y < h; y++)
                {
                    int o = y * w;
                    int sum = 0;
                    for (int i = -r; i <= r; i++) sum += a[o + Math.Clamp(i, 0, w - 1)];
                    for (int x = 0; x < w; x++)
                    {
                        tmp[x] = (byte)(sum / div);
                        sum += a[o + Math.Min(w - 1, x + r + 1)] - a[o + Math.Max(0, x - r)];
                    }
                    Array.Copy(tmp, 0, a, o, w);
                }
                for (int x = 0; x < w; x++)
                {
                    int sum = 0;
                    for (int i = -r; i <= r; i++) sum += a[Math.Clamp(i, 0, h - 1) * w + x];
                    for (int y = 0; y < h; y++)
                    {
                        tmp[y] = (byte)(sum / div);
                        sum += a[Math.Min(h - 1, y + r + 1) * w + x] - a[Math.Max(0, y - r) * w + x];
                    }
                    for (int y = 0; y < h; y++) a[y * w + x] = tmp[y];
                }
            }

            public void Dispose() => Bitmap?.Dispose();
        }
    }
}
