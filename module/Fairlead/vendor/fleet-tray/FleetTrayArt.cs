// FleetHacker tray icons, drawn at the exact size the notification area shows them.
//
// Why: a tray icon drawn at 32 px (or loaded from an .ico without asking for a size) is shrunk by
// Windows to 16 px and blurs; one converted with Bitmap.GetHicon loses its soft edges; and a badge
// with a small emblem in the middle leaves about 6 px of symbol at 16 px. Enterprise tray icons are
// one bold shape that fills the square, drawn for each size. This draws that: a rounded tile in the
// app's colour, nudged so it reads on both a light and a dark taskbar, with one bold letter, at the
// size Windows wants for the current display scale (16 px at 100 %, 20 at 125 %, 24 at 150 %...).
//
// Rules this file keeps so every tool can carry it unchanged:
// - C# 5 (the Add-Type compiler of Windows PS 5.1) and .NET Framework or .NET 8+ WinForms.
// - No P/Invoke, no network, no files written: phoenix-lai's source guard allows only kernel32
//   and vulkan imports, and Constrained Language Mode sites load it with Add-Type alone.
// - Icons are built as in-memory .ico data with a real alpha channel, never through GetHicon.
//
// Source of truth: edge-ops/ui-kit/tray/. Tools copy this file; see README.md for the version.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace FleetTray
{
    public static class Art
    {
        public const string Version = "1.0.0";

        // Each tool's letter and brand colour (fleet.css --fl-accent-night; Windlass and Phoenix from
        // their own stylesheets). The colour is adjusted for contrast when drawn; the hue stays.
        static readonly Dictionary<string, KeyValuePair<string, string>> Apps = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "lazarette", new KeyValuePair<string, string>("L", "#c9a66b") },
            { "windlass", new KeyValuePair<string, string>("W", "#7f9db4") },
            { "fairlead", new KeyValuePair<string, string>("F", "#a3d65c") },
            { "capstan", new KeyValuePair<string, string>("C", "#d9845a") },
            { "snapshackle", new KeyValuePair<string, string>("S", "#ef6a5a") },
            { "phoenix", new KeyValuePair<string, string>("P", "#e8c15a") },
            { "semaphore", new KeyValuePair<string, string>("S", "#c9a66b") },
            { "scrimshaw", new KeyValuePair<string, string>("S", "#f1e4c8") },
            { "halyard", new KeyValuePair<string, string>("H", "#3fb6a8") },
            { "shellback", new KeyValuePair<string, string>("S", "#5cf1cb") },
            { "gangplank", new KeyValuePair<string, string>("G", "#c99566") },
            { "packetrat", new KeyValuePair<string, string>("P", "#6aa7e0") },
            { "eight-bells", new KeyValuePair<string, string>("8", "#d6a258") },
            { "bowerbird", new KeyValuePair<string, string>("B", "#9b98f3") }
        };

        /// <summary>Status colours for the corner badge and the status dot.</summary>
        public static readonly Color Good = Color.FromArgb(46, 160, 90);
        public static readonly Color Busy = Color.FromArgb(240, 150, 30);
        public static readonly Color Bad = Color.FromArgb(229, 72, 77);
        public static readonly Color Idle = Color.FromArgb(140, 146, 156);

        // ---- size ---------------------------------------------------------------------------

        /// <summary>
        /// The notification area's icon size in physical pixels for a display scale given in DPI
        /// (96 = 100 %): Windows' small-icon size, 16 px scaled and rounded.
        /// </summary>
        public static int SizeForDpi(int dpi)
        {
            if (dpi < 96) dpi = 96;
            if (dpi > 480) dpi = 480;
            return (int)Math.Round(16.0 * dpi / 96.0, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// The display scale in DPI as Windows applied it at sign-in (HKCU Control Panel\Desktop\
        /// WindowMetrics\AppliedDPI), else 96. Read from the registry, not asked of Windows, so it is
        /// right even in a process that is not DPI aware (Windows tells those 96 whatever the scale).
        /// </summary>
        public static int SystemDpi()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop\WindowMetrics"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppliedDPI");
                        if (v is int && (int)v >= 96) return (int)v;
                    }
                }
            }
            catch (Exception) { }
            return 96;
        }

        /// <summary>The tray icon size for this display, in physical pixels.</summary>
        public static int TraySize()
        {
            return SizeForDpi(SystemDpi());
        }

        // ---- the icons ------------------------------------------------------------------------

        /// <summary>A tool's tray icon at the tray's size. Status: null, "good", "busy", "bad" or "idle".</summary>
        public static Icon ForApp(string app, string status)
        {
            return ForApp(app, status, TraySize());
        }

        public static Icon ForApp(string app, string status, int size)
        {
            KeyValuePair<string, string> a = AppOrDefault(app);
            return Tile(a.Key, ParseHex(a.Value), StatusColor(status), size);
        }

        /// <summary>A letter tile: a rounded square in `accent` with one bold letter, and an optional corner badge.</summary>
        public static Icon Tile(string letter, Color accent, Color? badge, int size)
        {
            using (Bitmap bmp = DrawTile(letter, accent, badge, size)) return ToIcon(new Bitmap[] { bmp });
        }

        /// <summary>A status dot filling the square, with a darker rim so it reads on any taskbar.</summary>
        public static Icon Dot(Color color, int size)
        {
            using (Bitmap bmp = DrawDot(color, size)) return ToIcon(new Bitmap[] { bmp });
        }

        public static Icon Dot(Color color)
        {
            return Dot(color, TraySize());
        }

        /// <summary>A tool's tile as a bitmap, for previews.</summary>
        public static Bitmap DrawApp(string app, string status, int size)
        {
            KeyValuePair<string, string> a = AppOrDefault(app);
            return DrawTile(a.Key, ParseHex(a.Value), StatusColor(status), size);
        }

        /// <summary>The tile as a bitmap, for previews and for writing a multi-size .ico.</summary>
        public static Bitmap DrawTile(string letter, Color accent, Color? badge, int size)
        {
            if (size < 8) size = 8;
            Color fill = ForTaskbars(accent);
            Color ink = InkFor(fill);
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                float r = Math.Max(2f, (float)Math.Round(size * 0.22));
                using (GraphicsPath tile = Rounded(0, 0, size, size, r))
                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, tile);
                // A one-pixel darker rim inside the edge keeps the tile's outline on a taskbar of the same tone.
                using (GraphicsPath rim = Rounded(0.5f, 0.5f, size - 1f, size - 1f, r - 0.5f))
                using (var pen = new Pen(Mix(fill, Color.Black, 0.35), 1f))
                    g.DrawPath(pen, rim);
                // With a badge in the corner the letter moves up and left, so the two barely overlap.
                DrawLetter(g, letter, ink, size, badge.HasValue ? (float)Math.Round(size * 0.07) : 0f);
                if (badge.HasValue) DrawBadge(g, badge.Value, size);
            }
            return bmp;
        }

        public static Bitmap DrawDot(Color color, int size)
        {
            if (size < 8) size = 8;
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float line = Math.Max(1f, (float)Math.Round(size / 16f));
                float inset = line;
                using (var brush = new SolidBrush(color))
                    g.FillEllipse(brush, inset, inset, size - 2 * inset, size - 2 * inset);
                using (var pen = new Pen(Mix(color, Color.Black, 0.45), line))
                    g.DrawEllipse(pen, inset + line / 2, inset + line / 2, size - 2 * inset - line, size - 2 * inset - line);
            }
            return bmp;
        }

        static void DrawLetter(Graphics g, string letter, Color ink, int size, float shift)
        {
            if (string.IsNullOrEmpty(letter)) return;
            string text = letter.Substring(0, 1);
            using (var path = new GraphicsPath())
            {
                path.AddString(text, PickFamily(), (int)FontStyle.Bold, size, new PointF(0, 0), StringFormat.GenericTypographic);
                RectangleF b = path.GetBounds();
                if (b.Height <= 0 || b.Width <= 0) return;
                // Cap height about 62 % of the tile, the weight of a bold letter in Office's tray icons.
                float scale = Math.Min((size * 0.62f) / b.Height, (size * 0.72f) / b.Width);
                using (var m = new Matrix())
                {
                    m.Scale(scale, scale);
                    path.Transform(m);
                }
                b = path.GetBounds();
                // Centre, then snap to whole pixels so the stems land on the pixel grid.
                float dx = (float)Math.Round((size - b.Width) / 2f - b.X) - shift;
                float dy = (float)Math.Round((size - b.Height) / 2f - b.Y) - shift;
                using (var m = new Matrix())
                {
                    m.Translate(dx, dy);
                    path.Transform(m);
                }
                using (var brush = new SolidBrush(ink)) g.FillPath(brush, path);
            }
        }

        static void DrawBadge(Graphics g, Color color, int size)
        {
            float d = (float)Math.Round(size * 0.44);
            float x = size - d;
            float y = size - d;
            float gap = Math.Max(1f, (float)Math.Round(size / 16f));
            // Cut a transparent ring round the badge so it reads as separate from the tile.
            CompositingMode before = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
                g.FillEllipse(clear, x - gap, y - gap, d + 2 * gap, d + 2 * gap);
            g.CompositingMode = before;
            using (var brush = new SolidBrush(color)) g.FillEllipse(brush, x, y, d, d);
        }

        static FontFamily cached;

        static FontFamily PickFamily()
        {
            if (cached != null) return cached;
            foreach (string name in new string[] { "Segoe UI", "Arial", "Tahoma" })
            {
                try
                {
                    var f = new FontFamily(name);
                    if (f.IsStyleAvailable(FontStyle.Bold)) { cached = f; return f; }
                }
                catch (ArgumentException) { }
            }
            cached = FontFamily.GenericSansSerif;
            return cached;
        }

        static GraphicsPath Rounded(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = Math.Max(0.1f, 2 * r);
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static KeyValuePair<string, string> AppOrDefault(string app)
        {
            KeyValuePair<string, string> a;
            if (Apps.TryGetValue(app ?? "", out a)) return a;
            string letter = string.IsNullOrEmpty(app) ? "?" : app.Substring(0, 1).ToUpperInvariant();
            return new KeyValuePair<string, string>(letter, "#8a94a6");
        }

        // ---- colour -------------------------------------------------------------------------

        public static Color? StatusColor(string status)
        {
            switch ((status ?? "").ToLowerInvariant())
            {
                case "good": return Good;
                case "busy": return Busy;
                case "bad": return Bad;
                case "idle": return Idle;
                default: return null;
            }
        }

        public static Color ParseHex(string hex)
        {
            string h = (hex ?? "").Trim().TrimStart('#');
            if (h.Length != 6) return Color.FromArgb(138, 148, 166);
            return Color.FromArgb(Convert.ToInt32(h.Substring(0, 2), 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));
        }

        /// <summary>WCAG relative luminance.</summary>
        public static double Luminance(Color c)
        {
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        static double Lin(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a);
            double lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        // Light taskbar #f3f3f3 and dark taskbar #202020 (Windows 11), the two backgrounds a tray icon meets.
        public static readonly Color LightBar = Color.FromArgb(243, 243, 243);
        public static readonly Color DarkBar = Color.FromArgb(32, 32, 32);

        /// <summary>
        /// The accent made light or dark enough to stand out from both taskbars by at least 2.4:1 where
        /// it can, by moving it toward black or white and keeping its hue. Most brand colours need little.
        /// </summary>
        public static Color ForTaskbars(Color accent)
        {
            Color best = accent;
            double bestScore = Score(accent);
            for (int i = 1; i <= 20 && bestScore < 2.4; i++)
            {
                double t = i * 0.04;
                Color darker = Mix(accent, Color.Black, t);
                Color lighter = Mix(accent, Color.White, t);
                double sd = Score(darker);
                double sl = Score(lighter);
                if (sd > bestScore + 1e-9) { best = darker; bestScore = sd; }
                if (sl > bestScore + 1e-9) { best = lighter; bestScore = sl; }
            }
            return best;
        }

        /// <summary>The lower of the colour's contrast against a light and a dark taskbar.</summary>
        public static double Score(Color c)
        {
            return Math.Min(Contrast(c, LightBar), Contrast(c, DarkBar));
        }

        /// <summary>White or near-black, whichever stands out more from the fill.</summary>
        public static Color InkFor(Color fill)
        {
            Color dark = Color.FromArgb(17, 20, 24);
            return Contrast(fill, Color.White) >= Contrast(fill, dark) ? Color.White : dark;
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(a.A, (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // ---- .ico -----------------------------------------------------------------------------

        /// <summary>
        /// An Icon from one or more 32-bit bitmaps, built as .ico data (BMP frames with a real alpha
        /// channel and an empty AND mask), so the soft edges survive.
        /// </summary>
        public static Icon ToIcon(Bitmap[] frames)
        {
            byte[] data = IcoBytes(frames);
            int size = frames.Length > 0 ? frames[0].Width : 16;
            using (var ms = new MemoryStream(data)) return new Icon(ms, size, size);
        }

        /// <summary>.ico file bytes for the frames.</summary>
        public static byte[] IcoBytes(Bitmap[] frames)
        {
            var images = new List<byte[]>();
            foreach (Bitmap f in frames) images.Add(Dib(f));
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)frames.Length);
                int offset = 6 + 16 * frames.Length;
                for (int i = 0; i < frames.Length; i++)
                {
                    w.Write((byte)(frames[i].Width >= 256 ? 0 : frames[i].Width));
                    w.Write((byte)(frames[i].Height >= 256 ? 0 : frames[i].Height));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] img in images) w.Write(img);
                w.Flush();
                return ms.ToArray();
            }
        }

        static byte[] Dib(Bitmap src)
        {
            int wd = src.Width;
            int ht = src.Height;
            byte[] px = new byte[wd * ht * 4];
            using (var copy = new Bitmap(wd, ht, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(copy))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(src, 0, 0);
                }
                BitmapData bd = copy.LockBits(new Rectangle(0, 0, wd, ht), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < ht; y++)
                        System.Runtime.InteropServices.Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), px, y * wd * 4, wd * 4);
                }
                finally { copy.UnlockBits(bd); }
            }
            int maskStride = ((wd + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40);
                w.Write(wd);
                w.Write(ht * 2);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(0);
                w.Write(wd * ht * 4 + maskStride * ht);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                // Rows bottom-up, BGRA (GDI+'s own layout for Format32bppArgb).
                for (int y = ht - 1; y >= 0; y--) w.Write(px, y * wd * 4, wd * 4);
                w.Write(new byte[maskStride * ht]);
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>A multi-size .ico for a tool (16 to 48 px, every size a tray uses up to 300 % scale): the fallback file a tool ships. No 256 px frame: uncompressed it alone is 260 KB, and a tray never asks for it.</summary>
        public static byte[] AppIcoBytes(string app)
        {
            int[] sizes = new int[] { 16, 20, 24, 28, 32, 40, 48 };
            if (!Apps.ContainsKey(app ?? "")) throw new ArgumentException("Unknown tool: " + app);
            var frames = new Bitmap[sizes.Length];
            try
            {
                for (int i = 0; i < sizes.Length; i++) frames[i] = DrawApp(app, null, sizes[i]);
                return IcoBytes(frames);
            }
            finally
            {
                foreach (Bitmap b in frames) if (b != null) b.Dispose();
            }
        }

        /// <summary>The tools this file knows, for previews and tests.</summary>
        public static string[] KnownApps()
        {
            var list = new List<string>(Apps.Keys);
            list.Sort(StringComparer.Ordinal);
            return list.ToArray();
        }
    }
}
