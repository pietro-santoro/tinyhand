using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using PixelFormats = System.Windows.Media.PixelFormats;

namespace TinyHand
{
    /// <summary>One of the 36 hands, a custom hand, or the laser dot. Port of PointerStyle.swift.</summary>
    internal sealed class PointerStyle : IEquatable<PointerStyle>
    {
        public string Id { get; }

        private PointerStyle(string id)
        {
            Id = id;
        }

        public static readonly PointerStyle Lavender = new PointerStyle("lavender");
        public static readonly PointerStyle Glove = new PointerStyle("glove");
        public static readonly PointerStyle Coral = new PointerStyle("coral");
        public static readonly PointerStyle Plush = new PointerStyle("plush");
        public static readonly PointerStyle Robot = new PointerStyle("robot");
        public static readonly PointerStyle Laser = new PointerStyle("laser");

        private static readonly (string Id, string Title, MediaColor Ink)[] BuiltIn =
        {
            ("lavender", "Lavender toy", Ink.Purple),
            ("glove", "Cursor glove", Ink.Purple),
            ("coral", "Coral vinyl", Ink.Red),
            ("plush", "Plush", Ink.Hex(0x2F86F0)),
            ("robot", "Robot", Ink.Hex(0x16A87A)),
            ("pink", "Bubblegum", Ink.Hex(0xEE4F98)),
            ("blue", "Sky", Ink.Hex(0x2F86F0)),
            ("mint", "Mint", Ink.Hex(0x16A87A)),
            ("lemon", "Lemon", Ink.Hex(0xF2B705)),
            ("black", "Midnight", Ink.Hex(0x1D1D21)),
            ("chrome", "Chrome", Ink.Hex(0x6B7380)),
            ("holo", "Holo", Ink.Purple),
            ("fuzzy", "Fuzzy", Ink.Purple),
            ("wood", "Wood", Ink.Hex(0x9A5E2C)),
            ("marble", "Marble", Ink.Hex(0x6B7380)),
            ("terrazzo", "Terrazzo", Ink.Hex(0xF0561D)),
            ("glass", "Glass", Ink.Hex(0x1597E5)),
            ("jelly", "Jelly", Ink.Hex(0xA33FE6)),
            ("pastel", "Pastel", Ink.Hex(0xEE4F98)),
            ("pixel", "Pixel", Ink.Hex(0x1D1D21)),
            ("lowpoly", "Low poly", Ink.Purple),
            ("knit", "Knit", Ink.Purple),
            ("skeleton", "Skeleton", Ink.Hex(0x9A5E2C)),
            ("astro", "Astronaut", Ink.Hex(0x2F86F0)),
            ("space", "Space", Ink.Red),
            ("monster", "Monster", Ink.Hex(0x3E9E2E)),
            ("water", "Water", Ink.Hex(0x1597E5)),
            ("lava", "Lava", Ink.Hex(0xF0561D)),
            ("grass", "Grass", Ink.Hex(0x3E9E2E)),
            ("cactus", "Cactus", Ink.Hex(0x3E9E2E)),
            ("choco", "Chocolate", Ink.Hex(0x9A5E2C)),
            ("cloud", "Cloud", Ink.Hex(0x2F86F0)),
            ("mech", "Mech", Ink.Hex(0xF0561D)),
            ("neon", "Neon", Ink.Hex(0xE0309A)),
            ("leaf", "Leaf", Ink.Hex(0x3E9E2E)),
            ("cookie", "Gingerbread", Ink.Hex(0x9A5E2C)),
        };

        public static IReadOnlyList<PointerStyle> BuiltInHands =>
            BuiltIn.Select(b => b.Id == Lavender.Id ? Lavender : new PointerStyle(b.Id)).ToList();

        public static IReadOnlyList<PointerStyle> CustomHandStyles =>
            CustomHands.All.Select(h => new PointerStyle(h.Id)).ToList();

        public static IReadOnlyList<PointerStyle> All =>
            BuiltInHands.Concat(CustomHandStyles).Concat(new[] { Laser }).ToList();

        public static readonly PointerStyle[] DefaultPinned = { Lavender, Glove, Coral, Plush, Robot, Laser };

        public static PointerStyle FromId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            return All.FirstOrDefault(s => s.Id == id);
        }

        public bool IsLaser => Id == Laser.Id;

        public bool IsCustom => CustomHands.Hand(Id) != null;

        public string Title
        {
            get
            {
                if (IsLaser)
                {
                    return "Laser dot";
                }
                var custom = CustomHands.Hand(Id);
                if (custom != null)
                {
                    return custom.Name;
                }
                foreach (var b in BuiltIn)
                {
                    if (b.Id == Id) return b.Title;
                }
                return Id;
            }
        }

        public string AssetName
        {
            get
            {
                if (IsLaser) return null;
                if (IsCustom) return Id;
                return "hand_" + Id;
            }
        }

        public MediaColor InkColor
        {
            get
            {
                if (IsLaser) return Ink.Red;
                var custom = CustomHands.Hand(Id);
                if (custom != null) return Ink.Hex(custom.Ink);
                foreach (var b in BuiltIn)
                {
                    if (b.Id == Id) return b.Ink;
                }
                return Ink.Purple;
            }
        }

        public static PointerStyle Saved
        {
            get
            {
                string raw = Preferences.SavedStyleId;
                if (raw == "dot") return Laser;
                if (raw == "sunny") return Coral;
                return FromId(raw) ?? Lavender;
            }
            set => Preferences.SavedStyleId = value.Id;
        }

        public bool Equals(PointerStyle other) => other is not null && other.Id == Id;
        public override bool Equals(object obj) => Equals(obj as PointerStyle);
        public override int GetHashCode() => Id.GetHashCode();
        public static bool operator ==(PointerStyle a, PointerStyle b) => a is null ? b is null : a.Equals(b);
        public static bool operator !=(PointerStyle a, PointerStyle b) => !(a == b);
        public override string ToString() => Id;
    }

    /// <summary>
    /// A hand image plus where its parts are, measured from the alpha channel exactly like the
    /// Mac app: fingertip, wrist and where the rod ends, all as fractions of the image height.
    /// </summary>
    internal sealed class HandAsset
    {
        public Bitmap Image { get; private set; } // 32bpp premultiplied, owned by the cache
        public double Aspect { get; private set; }
        public double HandTop { get; private set; }
        public double Tip { get; private set; }
        public double Wrist { get; private set; }
        public double RodEnd { get; private set; }
        public double TipX { get; private set; }
        public double RodX { get; private set; }
        public double HandMinX { get; private set; }
        public double HandMaxX { get; private set; }

        // Horizontal extent of any visible pixel, so the renderer only draws what is there.
        public double RodInkMinX { get; private set; }
        public double RodInkMaxX { get; private set; }
        public double HandInkMinX { get; private set; }
        public double HandInkMaxX { get; private set; }

        private static readonly Dictionary<string, HandAsset> Cache = new Dictionary<string, HandAsset>();
        private static readonly List<string> Recent = new List<string>();
        private const int CacheLimit = 3;
        private static readonly object Gate = new object();

        public static HandAsset Load(string name)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(name, out var hit))
                {
                    Touch(name);
                    return hit;
                }
            }
            var image = LoadImage(name);
            var asset = image == null ? null : Measure(image);
            if (asset == null)
            {
                image?.Dispose();
                return null;
            }
            lock (Gate)
            {
                Cache[name] = asset;
                Touch(name);
            }
            return asset;
        }

        private static void Touch(string name)
        {
            Recent.Remove(name);
            Recent.Add(name);
            while (Recent.Count > CacheLimit)
            {
                string old = Recent[0];
                Recent.RemoveAt(0);
                // Not disposed: a renderer may still hold the bitmap for its current frame.
                Cache.Remove(old);
            }
        }

        private static readonly Dictionary<string, BitmapSource> Thumbnails = new Dictionary<string, BitmapSource>();

        public static void Forget(string name)
        {
            lock (Gate)
            {
                Cache.Remove(name);
                Recent.Remove(name);
                Thumbnails.Remove(name);
            }
        }

        public static BitmapSource CachedThumbnail(string name)
        {
            lock (Gate)
            {
                return Thumbnails.TryGetValue(name, out var t) ? t : null;
            }
        }

        public static void PreloadThumbnails(IEnumerable<string> names, Action done = null)
        {
            var missing = names.Where(n => CachedThumbnail(n) == null).ToList();
            if (missing.Count == 0)
            {
                done?.Invoke();
                return;
            }
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            Task.Run(() =>
            {
                Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = 4 }, name =>
                {
                    try
                    {
                        using var image = LoadImage(name);
                        var asset = image == null ? null : Measure(image, keepImage: false);
                        if (asset != null)
                        {
                            var thumb = asset.MakeThumbnail(image);
                            lock (Gate)
                            {
                                if (!Thumbnails.ContainsKey(name)) Thumbnails[name] = thumb;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Write($"thumbnail {name} failed: {e.Message}");
                    }
                });
                if (done != null)
                {
                    dispatcher.BeginInvoke(done);
                }
            });
        }

        public static BitmapSource Thumbnail(string name)
        {
            var hit = CachedThumbnail(name);
            if (hit != null)
            {
                return hit;
            }
            var asset = Load(name);
            if (asset == null)
            {
                return null;
            }
            var thumb = asset.MakeThumbnail(asset.Image);
            lock (Gate)
            {
                Thumbnails[name] = thumb;
            }
            return thumb;
        }

        private BitmapSource MakeThumbnail(Bitmap image)
        {
            double h = image.Height;
            double w = image.Width;
            double handW = (HandMaxX - HandMinX) * w;
            double handH = (Wrist - HandTop) * h;
            double side = Math.Max(handW, handH) * 1.06;
            double cx = (HandMinX + HandMaxX) / 2 * w;
            double cy = (HandTop + Wrist) / 2 * h;
            var rect = RectangleF.Intersect(new RectangleF((float)(cx - side / 2), (float)(cy - side / 2), (float)side, (float)side),
                new RectangleF(0, 0, (float)w, (float)h));
            const int px = 128;
            using var thumb = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(image, new RectangleF(0, 0, px, px), rect, GraphicsUnit.Pixel);
            }
            return ToBitmapSource(thumb);
        }

        public static BitmapSource ToBitmapSource(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null,
                    data.Scan0, data.Stride * bitmap.Height, data.Stride);
                source.Freeze();
                return source;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>A copy scaled to <paramref name="height"/> rows with high quality filtering.</summary>
        public static Bitmap Resized(Bitmap image, int height)
        {
            int width = Math.Max(1, (int)Math.Round(image.Width * (double)height / image.Height));
            var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(result);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the borders
            g.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attrs);
            return result;
        }

        public static byte[] AlphaOf(Bitmap image, out int w, out int h)
        {
            w = image.Width;
            h = image.Height;
            var data = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[data.Stride];
                var alpha = new byte[w * h];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        alpha[o + x] = row[x * 4 + 3];
                    }
                }
                return alpha;
            }
            finally
            {
                image.UnlockBits(data);
            }
        }

        private static HandAsset Measure(Bitmap image, bool keepImage = true)
        {
            var alpha = AlphaOf(image, out int w, out int h);

            int Count(int y)
            {
                int n = 0;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (alpha[row + x] > 128) n++;
                }
                return n;
            }

            double Center(int y)
            {
                int row = y * w;
                int lo = -1, hi = -1;
                for (int x = 0; x < w; x++)
                {
                    if (alpha[row + x] > 128)
                    {
                        if (lo < 0) lo = x;
                        hi = x;
                    }
                }
                return lo < 0 ? 0.5 : (lo + hi) / 2.0 / w;
            }

            var counts = new int[h];
            for (int y = 0; y < h; y++) counts[y] = Count(y);

            int top = Array.FindIndex(counts, c => c > 0);
            if (top < 0) return null;
            int searchEnd = Math.Min(h, top + (int)(0.45 * h));
            int maxW = 0;
            for (int y = top; y < searchEnd; y++) maxW = Math.Max(maxW, counts[y]);
            if (maxW <= 0) return null;
            int fist = -1;
            for (int y = top; y < h; y++)
            {
                if (counts[y] > 0.6 * maxW) { fist = y; break; }
            }
            if (fist < 0) return null;
            int wrist = -1;
            for (int y = fist; y < h; y++)
            {
                if (counts[y] < 0.35 * maxW) { wrist = y; break; }
            }
            if (wrist < 0) return null;
            int rodW = counts[Math.Min(h - 1, wrist + h / 50)];
            int lastRow = Array.FindLastIndex(counts, c => c > 0);
            if (lastRow < 0) lastRow = h - 1;
            int from = Math.Min(h - 1, wrist + h / 33);
            int handle = -1;
            for (int y = from; y < h; y++)
            {
                if (counts[y] > 1.8 * rodW) { handle = y; break; }
            }
            int rodEnd = handle >= 0 ? handle : lastRow;

            int minX = w, maxX = 0;
            for (int y = top; y < wrist; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (alpha[row + x] > 128)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                    }
                }
            }

            double fh = h;
            var asset = new HandAsset
            {
                Image = keepImage ? image : null,
                Aspect = w / fh,
                HandTop = Math.Max(0, top - 0.004 * fh) / fh,
                Tip = top / fh,
                Wrist = wrist / fh,
                RodEnd = rodEnd / fh,
                TipX = Center(Math.Min(h - 1, top + h / 200)),
                RodX = Center((wrist + rodEnd) / 2),
                HandMinX = minX / (double)w,
                HandMaxX = maxX / (double)w,
            };

            (double lo, double hi) Ink(int y0, int y1)
            {
                int lo = w, hi = -1;
                for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (alpha[row + x] != 0)
                        {
                            if (x < lo) lo = x;
                            if (x > hi) hi = x;
                        }
                    }
                }
                if (hi < 0) return (0, 1);
                return (Math.Max(0, lo - 2) / (double)w, Math.Min(w, hi + 3) / (double)w);
            }

            var rodInk = Ink(wrist, rodEnd);
            var handInk = Ink((int)(asset.HandTop * h), wrist);
            asset.RodInkMinX = rodInk.lo;
            asset.RodInkMaxX = rodInk.hi;
            asset.HandInkMinX = handInk.lo;
            asset.HandInkMaxX = handInk.hi;
            return asset;
        }

        /// <summary>Custom hands live on disk; the 36 built-in ones are embedded in the exe.</summary>
        private static Bitmap LoadImage(string name)
        {
            try
            {
                string custom = Path.Combine(CustomHands.Directory, name + ".png");
                Stream stream = null;
                if (File.Exists(custom))
                {
                    stream = new MemoryStream(File.ReadAllBytes(custom));
                }
                else
                {
                    stream = Resources.Open("hands/" + name + ".png");
                }
                if (stream == null)
                {
                    return null;
                }
                using (stream)
                using (var decoded = new Bitmap(stream))
                {
                    // Copy into a premultiplied bitmap that no longer needs the stream.
                    var copy = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(copy))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(decoded, new Rectangle(0, 0, decoded.Width, decoded.Height));
                    }
                    return copy;
                }
            }
            catch (Exception e)
            {
                Log.Write($"could not load {name}: {e.Message}");
                return null;
            }
        }
    }

    internal static class Resources
    {
        private static readonly Assembly Assembly = typeof(Resources).Assembly;

        public static Stream Open(string logicalName) => Assembly.GetManifestResourceStream(logicalName);

        public static byte[] Bytes(string logicalName)
        {
            using var s = Open(logicalName);
            if (s == null)
            {
                return null;
            }
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }

        public static BitmapSource Image(string logicalName)
        {
            using var s = Open(logicalName);
            if (s == null)
            {
                return null;
            }
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = s;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }
}
