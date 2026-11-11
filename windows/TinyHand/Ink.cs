using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using MediaColor = System.Windows.Media.Color;

namespace TinyHand
{
    /// <summary>
    /// Strokes drawn by dragging. Port of Ink.swift: smoothed quadratic path, round caps, a
    /// soft shadow, an optional arrowhead, and a fade after the stroke ends (unless kept).
    /// Coordinates are overlay pixels, y down.
    /// </summary>
    internal sealed class Ink
    {
        public static readonly MediaColor Purple = MediaColor.FromRgb(135, 84, 242);   // 0.53, 0.33, 0.95
        public static readonly MediaColor Red = MediaColor.FromRgb(255, 41, 31);       // 1.0, 0.16, 0.12

        public static MediaColor Hex(uint v) => MediaColor.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));

        public static Color ToDrawing(MediaColor c, double alpha = 1) =>
            Color.FromArgb((int)Math.Round(Math.Clamp(alpha, 0, 1) * c.A), c.R, c.G, c.B);

        private sealed class Stroke
        {
            public readonly List<PointF> Points = new List<PointF>();
            public MediaColor Color;
            public float Width;
            public bool Arrow;
            public GraphicsPath Path;
            public RectangleF Bounds;
            public double FadeStart = double.MaxValue; // seconds on the ink clock
        }

        private readonly List<Stroke> done = new List<Stroke>();
        private Stroke stroke;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private const double FadeDuration = 0.6;

        public MediaColor StrokeColor = Purple;

        /// <summary>DPI factor, so the shadow and arrowhead keep the Mac's point sizes.</summary>
        public float Scale = 1;

        /// <summary>Area that changed since the last frame (overlay pixels).</summary>
        public RectangleF Dirty { get; private set; } = RectangleF.Empty;

        private static bool Keep => Preferences.FadeSeconds <= 0;
        private static double FadeDelay => Math.Max(0.5, Preferences.FadeSeconds);

        public bool IsDrawing => stroke != null;

        public int StrokeCount => done.Count + (stroke != null ? 1 : 0);

        /// <summary>True while a finished stroke still has to fade (the overlay keeps ticking fast).</summary>
        public bool IsAnimating
        {
            get
            {
                double now = clock.Elapsed.TotalSeconds;
                foreach (var s in done)
                {
                    if (s.FadeStart != double.MaxValue && now >= s.FadeStart - 0.05)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        private bool arrow;

        public bool Arrow
        {
            get => arrow;
            set
            {
                if (arrow != value)
                {
                    arrow = value;
                    Redraw();
                }
            }
        }

        public void Begin(PointF point, float width)
        {
            stroke = new Stroke { Color = StrokeColor, Width = width };
            stroke.Points.Add(point);
            Redraw();
        }

        public void Extend(PointF point)
        {
            if (stroke == null)
            {
                return;
            }
            var last = stroke.Points[stroke.Points.Count - 1];
            if (Hypot(point.X - last.X, point.Y - last.Y) < 1.5f * Scale)
            {
                return;
            }
            stroke.Points.Add(point);
            Redraw();
        }

        public void End()
        {
            var s = stroke;
            if (s == null)
            {
                return;
            }
            stroke = null;
            arrow = false;
            done.Add(s);
            if (!Keep)
            {
                s.FadeStart = clock.Elapsed.TotalSeconds + FadeDelay;
            }
        }

        public void Clear()
        {
            if (stroke != null)
            {
                AddDirty(stroke.Bounds);
                stroke.Path?.Dispose();
            }
            foreach (var s in done)
            {
                AddDirty(s.Bounds);
                s.Path?.Dispose();
            }
            stroke = null;
            arrow = false;
            done.Clear();
        }

        /// <summary>Drops strokes that finished fading and marks fading ones dirty.</summary>
        public void Advance()
        {
            double now = clock.Elapsed.TotalSeconds;
            for (int i = done.Count - 1; i >= 0; i--)
            {
                var s = done[i];
                if (s.FadeStart == double.MaxValue || now < s.FadeStart)
                {
                    continue;
                }
                AddDirty(s.Bounds);
                if (now >= s.FadeStart + FadeDuration)
                {
                    s.Path?.Dispose();
                    done.RemoveAt(i);
                }
            }
        }

        public void ResetDirty() => Dirty = RectangleF.Empty;

        private void AddDirty(RectangleF r)
        {
            if (r.IsEmpty)
            {
                return;
            }
            Dirty = Dirty.IsEmpty ? r : RectangleF.Union(Dirty, r);
        }

        public void Draw(Graphics g, RectangleF clip)
        {
            double now = clock.Elapsed.TotalSeconds;
            foreach (var s in done)
            {
                if (!s.Bounds.IntersectsWith(clip))
                {
                    continue;
                }
                double opacity = 1;
                if (s.FadeStart != double.MaxValue && now > s.FadeStart)
                {
                    double t = Math.Clamp((now - s.FadeStart) / FadeDuration, 0, 1);
                    opacity = 1 - t * t; // ease in
                }
                DrawStroke(g, s, opacity);
            }
            if (stroke != null && stroke.Bounds.IntersectsWith(clip))
            {
                DrawStroke(g, stroke, 1);
            }
        }

        private void DrawStroke(Graphics g, Stroke s, double opacity)
        {
            if (s.Path == null || opacity <= 0.003)
            {
                return;
            }
            // Mac: shadow black 18 %, radius 1.5, offset (1, -1.5) in y-up points.
            using (var shadow = new Pen(Color.FromArgb((int)(0.16 * 255 * opacity), 0, 0, 0), s.Width + 1.5f * Scale))
            {
                shadow.StartCap = shadow.EndCap = LineCap.Round;
                shadow.LineJoin = LineJoin.Round;
                var state = g.Save();
                g.TranslateTransform(1f * Scale, 1.5f * Scale);
                g.DrawPath(shadow, s.Path);
                g.Restore(state);
            }
            using var pen = new Pen(ToDrawing(s.Color, opacity), s.Width);
            pen.StartCap = pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            g.DrawPath(pen, s.Path);
        }

        private void Redraw()
        {
            var s = stroke;
            if (s == null)
            {
                return;
            }
            s.Arrow = arrow;
            AddDirty(s.Bounds);
            var path = SmoothPath(s.Points);
            if (s.Arrow)
            {
                AddHead(path, s.Points, Math.Max(14 * Scale, s.Width * 3.2f));
            }
            s.Path?.Dispose();
            s.Path = path;
            var b = path.GetBounds();
            float pad = s.Width / 2 + 5 * Scale + 3;
            b.Inflate(pad, pad);
            s.Bounds = b;
            AddDirty(b);
        }

        private static GraphicsPath SmoothPath(List<PointF> points)
        {
            var path = new GraphicsPath();
            var first = points[0];
            if (points.Count == 1)
            {
                // A zero-length segment draws nothing in GDI+; a tiny one gets round caps.
                path.AddLine(first, new PointF(first.X + 0.01f, first.Y));
                return path;
            }
            var current = first;
            for (int i = 1; i < points.Count; i++)
            {
                var p = points[i];
                var prev = points[i - 1];
                var mid = new PointF((prev.X + p.X) / 2, (prev.Y + p.Y) / 2);
                // Quadratic (current -> mid, control prev) as a cubic Bézier.
                var c1 = new PointF(current.X + 2f / 3 * (prev.X - current.X), current.Y + 2f / 3 * (prev.Y - current.Y));
                var c2 = new PointF(mid.X + 2f / 3 * (prev.X - mid.X), mid.Y + 2f / 3 * (prev.Y - mid.Y));
                path.AddBezier(current, c1, c2, mid);
                current = mid;
            }
            path.AddLine(current, points[points.Count - 1]);
            return path;
        }

        // Aims the head along the last stretch of the stroke, so a wobble at the very end doesn't twist it.
        private static void AddHead(GraphicsPath path, List<PointF> points, float size)
        {
            var end = points[points.Count - 1];
            PointF back = points[0];
            for (int i = points.Count - 1; i >= 0; i--)
            {
                if (Hypot(end.X - points[i].X, end.Y - points[i].Y) >= size * 1.2f)
                {
                    back = points[i];
                    break;
                }
            }
            float length = Hypot(end.X - back.X, end.Y - back.Y);
            if (length <= 1)
            {
                return;
            }
            float head = Math.Min(size, length * 0.8f);
            double angle = Math.Atan2(end.Y - back.Y, end.X - back.X);
            const double spread = Math.PI / 7;
            foreach (int side in new[] { -1, 1 })
            {
                double a = angle + Math.PI + side * spread;
                path.StartFigure();
                path.AddLine(end, new PointF(end.X + (float)(Math.Cos(a) * head), end.Y + (float)(Math.Sin(a) * head)));
            }
        }

        private static float Hypot(float x, float y) => (float)Math.Sqrt(x * x + y * y);
    }
}
