using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace TinyHand
{
    /// <summary>
    /// Turns a picture of a hand into a TinyHand pointer: checks it the same way as the Mac app,
    /// then puts it on a black rod in a 1024x1536 canvas. Port of HandMaker.swift.
    /// </summary>
    internal static class HandMaker
    {
        public const string Placeholder = "[describe your hand, e.g. knitted wool in mustard yellow]";

        public static readonly string BasePrompt = string.Join("\n", new[]
        {
            "Make a pointer image for TinyHand, an app that shows a toy hand on screen.",
            "",
            "Draw one cartoon right hand in this style: " + Placeholder,
            "",
            "Rules:",
            "- We see the BACK of the hand. The palm faces away from us. The knuckles and the smooth back of the hand face the viewer.",
            "- The index finger points straight up. The other three fingers are folded down, so we only see their knuckles, never their fingertips or fingernails.",
            "- The raised index finger sits at the far left of the knuckle row, right next to the thumb. Three folded knuckles sit to its right. It must never look like a middle finger.",
            "- The thumb is tucked against the left side of the hand. Nothing crosses the front of the fist: no thumb, no fingertips, no fingernails, no palm.",
            "- The fingertip is the highest point of the image. Nothing above or beside it.",
            "- The hand ends at the bottom in a wrist cuff or a clean flat wrist, facing straight down, centred.",
            "- Upright, not tilted. Whole hand visible, nothing cropped.",
            "- Transparent background. No shadow, no floor, no outline glow, no text, no stick, no arm.",
            "- Soft 3D toy look, bright and readable at small sizes.",
            "- Portrait image, hand filling most of the height with a little empty space around it.",
        });

        public static string Prompt(string idea)
        {
            string trimmed = (idea ?? "").Trim();
            string style = trimmed.Length == 0 ? "a soft vinyl toy in a bright colour" : trimmed;
            return BasePrompt.Replace(Placeholder, style);
        }

        public sealed class Problem : Exception
        {
            public Problem(string message) : base(message) { }
        }

        private sealed class Pixels
        {
            public int Width;
            public int Height;
            public byte[] Bgra; // straight (not premultiplied) alpha

            public byte Alpha(int x, int y) => Bgra[(y * Width + x) * 4 + 3];
        }

        public static (Bitmap image, uint ink) Make(string path)
        {
            var pixels = Read(path);
            Check(pixels);
            var composed = Compose(pixels) ?? throw new Problem("TinyHand couldn't put that hand on a stick.");
            return (composed, InkOf(pixels));
        }

        private static Pixels Read(string path)
        {
            BitmapSource frame;
            try
            {
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame = decoder.Frames[0];
            }
            catch (Exception)
            {
                throw new Problem("That file isn't an image TinyHand can read.");
            }
            var hasAlpha = frame.Format == WpfPixelFormats.Bgra32 || frame.Format == WpfPixelFormats.Pbgra32
                || frame.Format == WpfPixelFormats.Rgba64 || frame.Format == WpfPixelFormats.Prgba64
                || frame.Format == WpfPixelFormats.Rgba128Float || frame.Format == WpfPixelFormats.Prgba128Float
                || frame.Format == WpfPixelFormats.Indexed8 || frame.Format == WpfPixelFormats.Indexed4
                || frame.Format == WpfPixelFormats.Indexed2 || frame.Format == WpfPixelFormats.Indexed1;
            if (!hasAlpha)
            {
                throw new Problem("The image needs a transparent background. Save it as a PNG with transparency.");
            }
            int w = frame.PixelWidth;
            int h = frame.PixelHeight;
            if (w < 64 || h < 64)
            {
                throw new Problem("The image is too small. Use one at least 512 pixels tall.");
            }
            var converted = new FormatConvertedBitmap(frame, WpfPixelFormats.Bgra32, null, 0);
            var bgra = new byte[w * h * 4];
            converted.CopyPixels(bgra, w * 4, 0);
            bool anyTransparent = false;
            for (int i = 3; i < bgra.Length; i += 4)
            {
                if (bgra[i] < 255)
                {
                    anyTransparent = true;
                    break;
                }
            }
            if (!anyTransparent)
            {
                throw new Problem("The image needs a transparent background. Save it as a PNG with transparency.");
            }
            return new Pixels { Width = w, Height = h, Bgra = bgra };
        }

        private static void Check(Pixels p)
        {
            int w = p.Width;
            int h = p.Height;
            int edge = 0, edgeSolid = 0;
            for (int x = 0; x < w; x++)
            {
                edge += 2;
                edgeSolid += (p.Alpha(x, 0) > 32 ? 1 : 0) + (p.Alpha(x, h - 1) > 32 ? 1 : 0);
            }
            for (int y = 0; y < h; y++)
            {
                edge += 2;
                edgeSolid += (p.Alpha(0, y) > 32 ? 1 : 0) + (p.Alpha(w - 1, y) > 32 ? 1 : 0);
            }
            if ((double)edgeSolid / edge > 0.02)
            {
                throw new Problem("The background isn't transparent, or the hand touches the edge of the image.");
            }

            int step = Math.Max(1, Math.Min(w, h) / 256);
            int gw = w / step;
            int gh = h / step;
            var grid = new bool[gw * gh];
            for (int gy = 0; gy < gh; gy++)
            {
                for (int gx = 0; gx < gw; gx++)
                {
                    if (p.Alpha(gx * step, gy * step) > 128) grid[gy * gw + gx] = true;
                }
            }
            var seen = new bool[gw * gh];
            var sizes = new List<int>();
            var stack = new Stack<int>();
            for (int start = 0; start < grid.Length; start++)
            {
                if (!grid[start] || seen[start]) continue;
                stack.Push(start);
                seen[start] = true;
                int n = 0;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    n++;
                    int x = i % gw;
                    int y = i / gw;
                    foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                    {
                        if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                        int j = ny * gw + nx;
                        if (grid[j] && !seen[j])
                        {
                            seen[j] = true;
                            stack.Push(j);
                        }
                    }
                }
                sizes.Add(n);
            }
            if (sizes.Count == 0)
            {
                throw new Problem("The image is empty.");
            }
            if ((double)sizes.Max() / sizes.Sum() < 0.97)
            {
                throw new Problem("The image has more than one thing in it. Use a single hand.");
            }

            var box = Bounds(p) ?? throw new Problem("The image is empty.");
            int bw = box.maxX - box.minX + 1;
            int bh = box.maxY - box.minY + 1;
            double aspect = (double)bh / bw;
            if (aspect < 1.05 || aspect > 2.8)
            {
                throw new Problem("The hand should stand upright, taller than it is wide.");
            }
            if ((double)bh / h < 0.5)
            {
                throw new Problem("The hand is too small in the image. Crop it closer.");
            }

            int topBand = Math.Max(2, bh * 8 / 100);
            int fingerMin = w, fingerMax = 0;
            for (int y = box.minY; y < box.minY + topBand; y++)
            {
                for (int x = box.minX; x <= box.maxX; x++)
                {
                    if (p.Alpha(x, y) > 128)
                    {
                        fingerMin = Math.Min(fingerMin, x);
                        fingerMax = Math.Max(fingerMax, x);
                    }
                }
            }
            if ((double)(fingerMax - fingerMin + 1) / bw > 0.4)
            {
                throw new Problem("The top of the image should be a single finger pointing up.");
            }

            var baseSpan = BottomSpan(p, box);
            if ((double)baseSpan.width / bw < 0.2)
            {
                throw new Problem("The hand should end in a flat wrist or cuff at the bottom.");
            }

            int soft = 0, any = 0;
            for (int y = 0; y < h; y += 2)
            {
                for (int x = 0; x < w; x += 2)
                {
                    byte a = p.Alpha(x, y);
                    if (a > 20)
                    {
                        any++;
                        if (a < 200) soft++;
                    }
                }
            }
            if ((double)soft / Math.Max(1, any) > 0.08)
            {
                throw new Problem("The hand has a soft shadow or glow around it. Ask for no shadow.");
            }
        }

        private static (int minX, int minY, int maxX, int maxY)? Bounds(Pixels p)
        {
            int minX = p.Width, minY = p.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < p.Height; y++)
            {
                for (int x = 0; x < p.Width; x++)
                {
                    if (p.Alpha(x, y) > 128)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            return maxX < 0 ? null : (minX, minY, maxX, maxY);
        }

        private static (int width, double center) BottomSpan(Pixels p, (int minX, int minY, int maxX, int maxY) box)
        {
            int bh = box.maxY - box.minY + 1;
            int lo = p.Width, hi = -1;
            for (int y = Math.Max(box.minY, box.maxY - Math.Max(2, bh * 4 / 100)); y <= box.maxY; y++)
            {
                for (int x = box.minX; x <= box.maxX; x++)
                {
                    if (p.Alpha(x, y) > 128)
                    {
                        lo = Math.Min(lo, x);
                        hi = Math.Max(hi, x);
                    }
                }
            }
            return hi < 0 ? (0, (box.minX + box.maxX) / 2.0) : (hi - lo + 1, (lo + hi) / 2.0);
        }

        private static Bitmap Compose(Pixels p)
        {
            var box0 = Bounds(p);
            if (box0 == null)
            {
                return null;
            }
            var box = box0.Value;
            const int canvasW = 1024;
            const int canvasH = 1536;
            const double handTop = 150;
            const double handHeight = 560;
            double bw = box.maxX - box.minX + 1;
            double bh = box.maxY - box.minY + 1;
            double scale = Math.Min(handHeight / bh, 700 / bw);
            double drawW = p.Width * scale;
            double drawH = p.Height * scale;
            double wristX = (BottomSpan(p, box).center + 0.5) * scale;
            double originX = canvasW / 2.0 - wristX;
            double originTop = handTop - box.minY * scale;
            double handBottom = handTop + bh * scale;

            var canvas = new Bitmap(canvasW, canvasH, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(canvas))
            using (var source = ToBitmap(p))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                double rodW = Math.Min(76, Math.Max(54, bw * scale * 0.18));
                double rodTop = handBottom - 30;
                const double rodBottom = 1462;
                var rod = new RectangleF((float)(canvasW / 2.0 - rodW / 2), (float)rodTop, (float)rodW, (float)(rodBottom - rodTop));
                using (var path = RoundedRect(rod, (float)(rodW / 2)))
                using (var brush = new LinearGradientBrush(new PointF(rod.Left - 0.5f, 0), new PointF(rod.Right + 0.5f, 0), Color.Black, Color.Black))
                {
                    brush.InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(0, 0, 0), Color.FromArgb(0x12, 0x12, 0x14), Color.FromArgb(0x3A, 0x3A, 0x3E), Color.FromArgb(0x12, 0x12, 0x14), Color.FromArgb(0, 0, 0) },
                        Positions = new[] { 0f, 0.22f, 0.45f, 0.75f, 1f },
                    };
                    g.FillPath(brush, path);
                }
                g.DrawImage(source, new RectangleF((float)originX, (float)originTop, (float)drawW, (float)drawH));
            }
            return canvas;
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Bitmap ToBitmap(Pixels p)
        {
            var bmp = new Bitmap(p.Width, p.Height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, p.Width, p.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < p.Height; y++)
                {
                    Marshal.Copy(p.Bgra, y * p.Width * 4, data.Scan0 + y * data.Stride, p.Width * 4);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }

        /// <summary>The hand's most characteristic colour, as the ink colour.</summary>
        private static uint InkOf(Pixels p)
        {
            (double score, double r, double g, double b) best = (-1, 0.53, 0.33, 0.95);
            var buckets = new Dictionary<int, (int n, double r, double g, double b)>();
            for (int y = 0; y < p.Height; y += 3)
            {
                for (int x = 0; x < p.Width; x += 3)
                {
                    int i = (y * p.Width + x) * 4;
                    if (p.Bgra[i + 3] <= 200) continue;
                    double r = p.Bgra[i + 2] / 255.0;
                    double g = p.Bgra[i + 1] / 255.0;
                    double b = p.Bgra[i] / 255.0;
                    int key = (int)(r * 5) * 36 + (int)(g * 5) * 6 + (int)(b * 5);
                    buckets.TryGetValue(key, out var e);
                    buckets[key] = (e.n + 1, e.r + r, e.g + g, e.b + b);
                }
            }
            foreach (var e in buckets.Values)
            {
                double r = e.r / e.n, g = e.g / e.n, b = e.b / e.n;
                double hi = Math.Max(r, Math.Max(g, b));
                double lo = Math.Min(r, Math.Min(g, b));
                double sat = hi == 0 ? 0 : (hi - lo) / hi;
                double lum = 0.3 * r + 0.59 * g + 0.11 * b;
                double score = e.n * (0.25 + sat) * (lum > 0.12 && lum < 0.85 ? 1 : 0.15);
                if (score > best.score)
                {
                    best = (score, r, g, b);
                }
            }
            double bhi = Math.Max(best.r, Math.Max(best.g, best.b));
            double blo = Math.Min(best.r, Math.Min(best.g, best.b));
            double rr = best.r, gg = best.g, bb = best.b;
            if (bhi - blo < 0.08)
            {
                rr = 0.42; gg = 0.45; bb = 0.5;
            }
            else if (bhi > 0.82)
            {
                rr *= 0.82 / bhi; gg *= 0.82 / bhi; bb *= 0.82 / bhi;
            }
            return (uint)Math.Min(255, rr * 255) << 16 | (uint)Math.Min(255, gg * 255) << 8 | (uint)Math.Min(255, bb * 255);
        }

        public static void Write(Bitmap image, string path)
        {
            string tmp = path + ".tmp";
            image.Save(tmp, ImageFormat.Png);
            File.Move(tmp, path, true);
        }
    }

    internal sealed class CustomHand
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("ink")] public uint Ink { get; set; }
    }

    /// <summary>Hands the user made, in %APPDATA%\TinyHand\Hands (hands.json + one PNG each).</summary>
    internal static class CustomHands
    {
        public static event Action Changed;

        public static string Directory
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("TH_HANDS_DIR");
                return string.IsNullOrEmpty(over) ? Path.Combine(AppPaths.Roaming, "Hands") : over;
            }
        }

        private static string ListPath => Path.Combine(Directory, "hands.json");

        private static List<CustomHand> loaded;

        public static IReadOnlyList<CustomHand> All
        {
            get
            {
                if (loaded != null)
                {
                    return loaded;
                }
                try
                {
                    loaded = File.Exists(ListPath)
                        ? JsonSerializer.Deserialize<List<CustomHand>>(File.ReadAllText(ListPath)) ?? new List<CustomHand>()
                        : new List<CustomHand>();
                }
                catch (Exception)
                {
                    loaded = new List<CustomHand>();
                }
                return loaded;
            }
        }

        public static CustomHand Hand(string id)
        {
            if (id == null || !id.StartsWith("custom-", StringComparison.Ordinal))
            {
                return null;
            }
            return All.FirstOrDefault(h => h.Id == id);
        }

        public static PointerStyle Add(Bitmap image, uint ink, string name)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string id = "custom-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            HandMaker.Write(image, Path.Combine(Directory, id + ".png"));
            string trimmed = (name ?? "").Trim();
            var list = All.ToList();
            list.Add(new CustomHand
            {
                Id = id,
                Name = trimmed.Length == 0 ? $"My hand {list.Count + 1}" : (trimmed.Length > 16 ? trimmed.Substring(0, 16) : trimmed),
                Ink = ink,
            });
            Save(list);
            return PointerStyle.FromId(id) ?? throw new HandMaker.Problem("TinyHand couldn't add that hand.");
        }

        public static void Remove(PointerStyle style)
        {
            if (style.AssetName == null || !style.IsCustom)
            {
                return;
            }
            try
            {
                File.Delete(Path.Combine(Directory, style.AssetName + ".png"));
            }
            catch (Exception)
            {
                // best effort
            }
            HandAsset.Forget(style.AssetName);
            try
            {
                Save(All.Where(h => h.Id != style.Id).ToList());
            }
            catch (Exception e)
            {
                Log.Write($"custom hands save failed: {e.Message}");
            }
        }

        private static void Save(List<CustomHand> list)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = ListPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            File.Move(tmp, ListPath, true);
            loaded = list;
            Changed?.Invoke();
        }

        public static string SuggestedName(string path)
        {
            string b = Path.GetFileNameWithoutExtension(path).Replace("_", " ").Replace("-", " ").Trim();
            string words = string.Join(" ", b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
            if (words.Length == 0 || words.Length > 16 || words.ToLowerInvariant().StartsWith("chatgpt image", StringComparison.Ordinal))
            {
                return $"My hand {All.Count + 1}";
            }
            return char.ToUpperInvariant(words[0]) + words.Substring(1);
        }
    }
}
