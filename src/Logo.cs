using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace DriveUp
{
    /// <summary>
    /// The DriveUP mark: Lighter's open violet ring with an upward arrow over a base line inside
    /// ("update"), drawn with one line weight like Wisp's broom, on the dark gray tile. Used for the
    /// header, the window/taskbar icon and (via tools\MakeIcon.cs) the .exe icon; the big button on
    /// the Updates tab draws the same parts large (UpdateRing).
    /// </summary>
    static class Logo
    {
        public static readonly Color Violet = Color.FromArgb(0x9D, 0x6B, 0xFF);
        /// <summary>DriveUP Pro: the same mark in crimson.</summary>
        public static readonly Color Crimson = Color.FromArgb(0xD6, 0x30, 0x4F);
        /// <summary>Colour of the mark in this edition (the big ring on the Updates tab follows it).</summary>
        public static Color Mark = Edition.Pro ? Crimson : Violet;
        public static readonly Color Tile = Color.FromArgb(0x2B, 0x2D, 0x31);

        public const float SolidStart = 270f;   // 12 o'clock (GDI angles grow clockwise from 3 o'clock)
        public const float SolidSweep = 247.4f; // ends at about 8 o'clock

        static Icon appIcon;

        public static Icon AppIcon
        {
            get
            {
                if (appIcon == null)
                    appIcon = new Icon(new MemoryStream(BuildIco(new[] { 16, 20, 24, 32, 40, 48, 64, 256 })));
                return appIcon;
            }
        }

        static readonly Color TileTop = Color.FromArgb(0x33, 0x35, 0x3B);
        static readonly Color TileBottom = Color.FromArgb(0x27, 0x28, 0x2C);

        public static void Draw(Graphics g, RectangleF r, bool tile)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float size = Math.Min(r.Width, r.Height);
            var box = new RectangleF(r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size);
            if (tile)
            {
                using (var path = Theme.RoundRect(box, size * 0.22f))
                using (var b = new LinearGradientBrush(new PointF(0, box.Top - 1), new PointF(0, box.Bottom + 1), TileTop, TileBottom))
                    g.FillPath(b, path);
            }
            // Everything below is drawn in a 256 x 256 box.
            g.TranslateTransform(box.X, box.Y);
            g.ScaleTransform(size / 256f, size / 256f);

            // Small icons (tray, title bar) get heavier lines so the mark survives 16 px.
            bool small = size < 40;
            float ringW = size < 24 ? 24 : (small ? 17 : 10);
            float lineW = size < 24 ? 30 : (small ? 24 : 17);

            using (var pen = new Pen(Mark, ringW) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, 40, 40, 176, 176, SolidStart, SolidSweep);
            if (small) DrawUpload(g, Mark, lineW, 128, 78, 150, 176, 36, 72);
            else DrawUpload(g, Mark, lineW, 128, 80, 148, 176, 34, 68);
            g.Restore(state);
        }

        /// <summary>
        /// The arrow over its base line, as in the logo: arrow from <paramref name="arrowBottom"/> up to
        /// <paramref name="tipY"/>, base line at <paramref name="lineY"/>, <paramref name="lineW"/> wide.
        /// </summary>
        public static void DrawUpload(Graphics g, Color color, float width, float cx, float tipY, float arrowBottom, float lineY, float head, float lineW)
        {
            using (var path = new GraphicsPath())
            {
                AddArrow(path, new PointF(cx, arrowBottom), new PointF(cx, tipY), head);
                path.StartFigure();
                path.AddLine(cx - lineW / 2, lineY, cx + lineW / 2, lineY);
                FillStroke(g, path, color, width);
            }
        }

        /// <summary>A single-weight arrow from <paramref name="from"/> to <paramref name="tip"/>, arms at 45 degrees.</summary>
        public static void DrawArrow(Graphics g, Color color, float width, PointF from, PointF tip, float head)
        {
            using (var path = new GraphicsPath())
            {
                AddArrow(path, from, tip, head);
                FillStroke(g, path, color, width);
            }
        }

        static void AddArrow(GraphicsPath path, PointF from, PointF tip, float head)
        {
            float dx = tip.X - from.X, dy = tip.Y - from.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) return;
            dx /= len;
            dy /= len;
            const float c = 0.7071f;
            var a1 = new PointF(tip.X - head * (dx * c - dy * c), tip.Y - head * (dy * c + dx * c));
            var a2 = new PointF(tip.X - head * (dx * c + dy * c), tip.Y - head * (dy * c - dx * c));
            path.StartFigure();
            path.AddLine(from, tip);
            path.StartFigure();
            path.AddLines(new[] { a1, tip, a2 });
        }

        /// <summary>
        /// Strokes all figures as one shape: where the shaft meets the head the paint is laid once,
        /// so a half-transparent arrow has no darker joint.
        /// </summary>
        public static void FillStroke(Graphics g, GraphicsPath path, Color color, float width)
        {
            if (color.A < 2 || path.PointCount == 0) return;
            using (var pen = new Pen(Color.Black, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var outline = (GraphicsPath)path.Clone())
            {
                outline.Widen(pen, null, 0.08f);
                outline.FillMode = FillMode.Winding;
                using (var b = new SolidBrush(color)) g.FillPath(b, outline);
            }
        }

        public static Bitmap Render(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, new RectangleF(0, 0, size, size), true);
            }
            return bmp;
        }

        /// <summary>Multi-size .ico: 32-bit DIB frames for small sizes, PNG for 256.</summary>
        public static byte[] BuildIco(int[] sizes)
        {
            var frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var bmp = Render(sizes[i]))
                    frames[i] = sizes[i] >= 256 ? Png(bmp) : Dib(bmp);

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var f in frames) w.Write(f);
                return ms.ToArray();
            }
        }

        static byte[] Png(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        static byte[] Dib(Bitmap bmp)
        {
            int n = bmp.Width;
            int maskStride = ((n + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40);
                w.Write(n);
                w.Write(n * 2);     // XOR + AND masks
                w.Write((short)1);
                w.Write((short)32);
                w.Write(0);
                w.Write(n * n * 4 + maskStride * n);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int y = n - 1; y >= 0; y--)
                    for (int x = 0; x < n; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                    }
                w.Write(new byte[maskStride * n]); // alpha channel does the masking
                return ms.ToArray();
            }
        }
    }
}
