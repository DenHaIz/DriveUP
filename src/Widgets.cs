using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>Rounded surface panel (8 px corners, 1 px border); children sit on the Surface color.</summary>
    class Card : BufferedPanel, IScalable
    {
        public string Title = "";
        /// <summary>Title strip height in design pixels.</summary>
        public int TitleBase;
        public int TitleHeight { get { return Theme.S(TitleBase); } }

        public void ApplyScale()
        {
            Padding = new Padding(Theme.S(1));
            Invalidate();
        }

        public Card()
        {
            Padding = new Padding(Theme.S(1));
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Current.Surface;
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : p.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, p.Surface, r, Theme.S(8));
            Theme.DrawRound(g, p.Border, r, Theme.S(8));
            if (Title.Length > 0 && TitleHeight > 0)
                TextRenderer.DrawText(g, Title, Theme.HeadFont, new Rectangle(Theme.S(18), 0, Width - Theme.S(36), TitleHeight), p.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>A line of text that fades in whenever it changes (footer status, hints).</summary>
    class StatusText : SkinControl
    {
        readonly Tweened alpha;
        public Color? ForeOverride;
        public TextFormatFlags Flags = TextLeft;
        string previous = "";

        public StatusText()
        {
            alpha = new Tweened(Invalidate, 1f);
        }

        /// <summary>Changes the text: the old one fades out as the new one fades in.</summary>
        public void SetText(string text)
        {
            text = text ?? "";
            if (text == Text) return;
            // Mid-fade the line still mostly shows the old text: keep fading that one out.
            if (alpha.Value >= 0.5f) previous = Text;
            Text = text;
            alpha.Set(0);
            alpha.To(1, 320);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color pb = ParentBg;
            e.Graphics.Clear(pb);
            Color fg = ForeOverride ?? Theme.Current.TextDim;
            float a = alpha.Value;
            // Out, then in: the two texts never overlap.
            if (a < 0.5f && previous.Length > 0)
                TextRenderer.DrawText(e.Graphics, previous, Font, ClientRectangle, Anim.Blend(pb, fg, 1 - a * 2), Flags);
            if (a > 0.5f)
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Anim.Blend(pb, fg, (a - 0.5f) * 2), Flags);
        }
    }

    /// <summary>Animated check box drawing shared by the category list and the lists.</summary>
    static class CheckMark
    {
        /// <summary>value 0..1 = how checked (animates the tick being drawn); partial = some children checked.</summary>
        public static void Draw(Graphics g, RectangleF r, float value, bool partial, float hover, bool enabled, Color bg)
        {
            var p = Theme.Current;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float on = partial ? 1f : value;
            Color border = Anim.Blend(Anim.Blend(Anim.Blend(p.Border, p.TextDim, 0.45f), p.TextDim, hover), p.Accent, on);
            Color fill = Anim.Blend(Anim.Blend(p.Surface, p.Hover, hover * 0.5f), p.Accent, on);
            if (!enabled)
            {
                border = Anim.Blend(border, bg, 0.55f);
                fill = Anim.Blend(fill, bg, 0.55f);
            }
            Theme.FillRound(g, fill, r, Theme.S(4));
            Theme.DrawRound(g, border, r, Theme.S(4));
            using (var pen = new Pen(enabled ? Color.White : Anim.Blend(Color.White, bg, 0.4f), Math.Max(1.6f, r.Width * 0.13f))
                   { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (partial)
                {
                    float y = r.Y + r.Height / 2;
                    g.DrawLine(pen, r.X + r.Width * 0.28f, y, r.Right - r.Width * 0.28f, y);
                }
                else if (value > 0.01f)
                {
                    // The tick is drawn on as the box fills: first stroke, then the long one.
                    var a = new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.53f);
                    var b = new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.71f);
                    var c = new PointF(r.X + r.Width * 0.77f, r.Y + r.Height * 0.32f);
                    float t1 = Math.Min(1, value / 0.4f), t2 = Math.Max(0, (value - 0.4f) / 0.6f);
                    var b1 = new PointF(Anim.Lerp(a.X, b.X, t1), Anim.Lerp(a.Y, b.Y, t1));
                    if (t2 <= 0) g.DrawLine(pen, a, b1);
                    else g.DrawLines(pen, new[] { a, b, new PointF(Anim.Lerp(b.X, c.X, t2), Anim.Lerp(b.Y, c.Y, t2)) });
                }
            }
            g.SmoothingMode = old;
        }
    }

    /// <summary>
    /// Icon glyphs drawn as shapes: centred on their actual ink (a glyph's text box is not centred
    /// on its drawing), scaled to a given size and made heavier by an outline of the same colour.
    /// Segoe MDL2 has a single thin weight, which looked faint inside the category tiles.
    /// </summary>
    static class GlyphShape
    {
        // Shapes centred on (0,0), keyed by glyph and size.
        static readonly Dictionary<string, GraphicsPath> cache = new Dictionary<string, GraphicsPath>();

        static GraphicsPath Get(string glyph, float ink)
        {
            string key = glyph + "|" + Math.Round(ink * 4);
            GraphicsPath path;
            if (cache.TryGetValue(key, out path)) return path;
            path = new GraphicsPath(FillMode.Winding);
            using (var family = new FontFamily("Segoe MDL2 Assets"))
                path.AddString(glyph, family, 0, 100f, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            if (b.Width > 0 && b.Height > 0)
                using (var m = new Matrix())
                {
                    float k = ink / Math.Max(b.Width, b.Height);
                    m.Scale(k, k);
                    m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
                    path.Transform(m);
                }
            cache[key] = path;
            return path;
        }

        /// <summary>Draws the glyph so its drawing fits a square of size ink centred on c; weight thickens the lines.</summary>
        public static void Draw(Graphics g, string glyph, PointF c, float ink, Color color, float weight)
        {
            if (!Theme.HasGlyphs || string.IsNullOrEmpty(glyph)) return;
            var path = Get(glyph, ink);
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(c.X, c.Y);
            using (var b = new SolidBrush(color)) g.FillPath(b, path);
            if (weight > 0)
                using (var pen = new Pen(color, weight) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, path);
            g.Restore(state);
        }
    }

    /// <summary>
    /// Placeholder bars with a soft light band sweeping across them, shown where a list is still
    /// loading. All placeholders share one clock, so bands on the same screen move in step.
    /// </summary>
    static class Shimmer
    {
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        const int Period = 1500;

        public static Brush Make(int width, Color bg)
        {
            var p = Theme.Current;
            Color bar = Anim.Blend(bg, p.Pressed, p.IsDark ? 0.5f : 0.55f);
            Color hi = Anim.Blend(bar, p.IsDark ? p.TextDim : Color.White, p.IsDark ? 0.16f : 0.75f);
            float w = Math.Max(1, width);
            float band = Math.Max(Theme.S(140), w * 0.3f);
            float left = -2 * band, span = w + 4 * band;
            float phase = Anim.EaseInOut((clock.ElapsedMilliseconds % Period) / (float)Period);
            float c = -band + phase * (w + 2 * band);
            var brush = new LinearGradientBrush(new RectangleF(left, 0, span, 1), bar, bar, 0f);
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[] { bar, bar, hi, bar, bar },
                Positions = new[] { 0f, (c - band / 2 - left) / span, (c - left) / span, (c + band / 2 - left) / span, 1f },
            };
            return brush;
        }

        public static void Bar(Graphics g, Brush b, RectangleF r)
        {
            using (var path = Theme.RoundRect(r, Math.Min(r.Height, r.Width) / 2))
                g.FillPath(b, path);
        }

        public static void Tile(Graphics g, Brush b, RectangleF r, float radius)
        {
            using (var path = Theme.RoundRect(r, radius))
                g.FillPath(b, path);
        }

        /// <summary>A stable pseudo-random 0..1 per (row, column), so placeholder bars vary in length.</summary>
        public static float Vary(int row, int col)
        {
            unchecked
            {
                uint h = (uint)(row * 73856093) ^ (uint)(col * 19349663 + 7);
                h ^= h >> 13;
                h *= 0x5bd1e995;
                h ^= h >> 15;
                return (h % 1000) / 1000f;
            }
        }

        /// <summary>Repaints the control every frame while loading() holds.</summary>
        public static void Run(Control c, object key, Func<bool> loading)
        {
            Anim.Frame(key, () =>
            {
                if (c.IsDisposed || !loading()) return false;
                if (c.Visible) c.Invalidate();
                return true;
            });
        }
    }

    // ---------------------------------------------------------------------------------------------

    class CatItem
    {
        public object Tag;
        public Func<string> Name, Note;
        public long Size = -1;        // -1 = not measured yet
        public bool Checked, Warn, Disabled;
        public Func<string> DisabledText;
        /// <summary>
        /// A link row instead of a check row: no check box, an "open" mark; a click calls this
        /// (e.g. a newer driver on the vendor's site). Link rows never count as ticked.
        /// </summary>
        public Action Open;
        /// <summary>Shown in the size column instead of a size (link rows: the vendor).</summary>
        public string Badge;
    }

    class CatGroup
    {
        public object Tag;
        public Func<string> Name, Desc;
        public string Glyph = "";
        public bool Expanded;
        public readonly List<CatItem> Items = new List<CatItem>();

        public long CheckedSize
        {
            get
            {
                long s = 0;
                foreach (var i in Items) if (i.Checked && !i.Disabled && i.Open == null && i.Size > 0) s += i.Size;
                return s;
            }
        }

        public long TotalSize
        {
            get
            {
                long s = 0;
                foreach (var i in Items) if (i.Size > 0) s += i.Size;
                return s;
            }
        }

        public bool Measured
        {
            get
            {
                foreach (var i in Items) if (i.Size < 0 && !i.Disabled && i.Open == null) return false;
                return true;
            }
        }
    }

    /// <summary>
    /// Expandable list of groups with check boxes and sizes (the "what to clean" list). Custom
    /// painted: expanding, checking, hovering, sizes arriving and scrolling are all animated.
    /// </summary>
    class CategoryList : SkinControl
    {
        public readonly List<CatGroup> Groups = new List<CatGroup>();
        public event EventHandler CheckedChanged;

        readonly Dictionary<object, float> hover = new Dictionary<object, float>();
        readonly Dictionary<object, float> check = new Dictionary<object, float>();
        readonly Dictionary<object, float> expand = new Dictionary<object, float>();
        readonly Dictionary<object, float> sizeIn = new Dictionary<object, float>();
        object hoverObj;
        float scroll, scrollTarget;
        readonly object scrollKey = new object();
        readonly Tweened barHover;
        bool dragging;
        int dragStartY;
        float dragStartScroll;

        int GroupH { get { return S(58); } }
        int ItemH { get { return S(38); } }

        bool loading;
        readonly object shimmerKey = new object();
        /// <summary>Shown when there are no groups and the list is not loading.</summary>
        public string EmptyText = "";
        /// <summary>Optional glyph drawn large above EmptyText, both centred (the "all done" look).</summary>
        public string EmptyGlyph = "";
        /// <summary>While true, clicks do not change the ticks (an operation is running on them).</summary>
        public bool Locked;

        /// <summary>While true and empty, placeholder rows shimmer in place of the groups.</summary>
        public bool Loading
        {
            get { return loading; }
            set
            {
                if (loading == value) return;
                loading = value;
                if (value) Shimmer.Run(this, shimmerKey, () => loading && Groups.Count == 0);
                Invalidate();
            }
        }

        public CategoryList()
        {
            barHover = new Tweened(Invalidate);
            Cursor = Cursors.Hand;
        }

        public void SetGroups(IEnumerable<CatGroup> groups)
        {
            Groups.Clear();
            Groups.AddRange(groups);
            hover.Clear();
            check.Clear();
            expand.Clear();
            sizeIn.Clear();
            hoverObj = null;
            foreach (var g in Groups)
            {
                expand[g] = g.Expanded ? 1 : 0;
                foreach (var i in g.Items) check[i] = i.Checked ? 1 : 0;
            }
            scroll = scrollTarget = 0;
            loading = false;
            Invalidate();
        }

        /// <summary>Call after an item's Size changed; the size fades in.</summary>
        public void SizeArrived(CatItem item)
        {
            var it = item;
            Anim.Run(Tuple.Create(this, (object)it, "sz"), 420, t => { sizeIn[it] = t; Invalidate(); });
        }

        public void RefreshChecks()
        {
            foreach (var g in Groups)
                foreach (var i in g.Items) AnimateCheck(i);
        }

        void AnimateCheck(CatItem i)
        {
            float from;
            if (!check.TryGetValue(i, out from)) from = 0;
            float to = i.Checked ? 1 : 0;
            if (Math.Abs(from - to) < 0.001f) return;
            var it = i;
            Anim.Run(Tuple.Create(this, (object)it, "c"), to > from ? 260 : 160, t => { check[it] = from + (to - from) * t; Invalidate(); });
        }

        void Animate(Dictionary<object, float> map, object key, string tag, float to, int ms, Func<float, float> ease = null)
        {
            float from;
            if (!map.TryGetValue(key, out from)) from = 0;
            Anim.Run(Tuple.Create(this, key, tag), ms, t => { map[key] = from + (to - from) * t; ClampScroll(); Invalidate(); }, null, ease);
        }

        float Get(Dictionary<object, float> map, object key)
        {
            float v;
            return map.TryGetValue(key, out v) ? v : 0;
        }

        float ContentHeight
        {
            get
            {
                float h = 0;
                foreach (var g in Groups) h += GroupH + g.Items.Count * ItemH * Get(expand, g);
                return h + S(4);
            }
        }

        float MaxScroll { get { return Math.Max(0, ContentHeight - Height); } }

        void ClampScroll()
        {
            float max = MaxScroll;
            if (scrollTarget > max) scrollTarget = max;
            if (scroll > max) scroll = max;
        }

        void ScrollTo(float target)
        {
            scrollTarget = Math.Max(0, Math.Min(MaxScroll, target));
            float from = scroll, to = scrollTarget;
            Anim.Run(scrollKey, 220, t => { scroll = from + (to - from) * t; Invalidate(); });
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(scrollTarget - e.Delta / 120f * ItemH * 2.5f);
        }

        // ---------- hit testing ----------

        enum Part { None, GroupCheck, GroupBody, Item }

        Part HitTest(Point pt, out object obj)
        {
            obj = null;
            float y = -scroll;
            foreach (var g in Groups)
            {
                if (pt.Y >= y && pt.Y < y + GroupH)
                {
                    obj = g;
                    return pt.X < S(48) ? Part.GroupCheck : Part.GroupBody;
                }
                y += GroupH;
                float block = g.Items.Count * ItemH * Get(expand, g);
                if (pt.Y >= y && pt.Y < y + block)
                {
                    int idx = (int)((pt.Y - y) / ItemH);
                    if (idx >= 0 && idx < g.Items.Count) { obj = g.Items[idx]; return Part.Item; }
                }
                y += block;
            }
            return Part.None;
        }

        Rectangle ThumbRect()
        {
            float content = ContentHeight;
            if (content <= Height + 1) return Rectangle.Empty;
            int h = Math.Max(S(32), (int)(Height * Height / content));
            int y = (int)((Height - h) * (scroll / Math.Max(1, content - Height)));
            return new Rectangle(Width - S(8), y, S(6), h);
        }

        void SetHover(object o)
        {
            if (o == hoverObj) return;
            if (hoverObj != null) Animate(hover, hoverObj, "h", 0, 220);
            if (o != null) Animate(hover, o, "h", 1, 110);
            hoverObj = o;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                var tr = ThumbRect();
                float range = Height - tr.Height;
                if (range > 0)
                {
                    scroll = scrollTarget = Math.Max(0, Math.Min(MaxScroll, dragStartScroll + (e.Y - dragStartY) * MaxScroll / range));
                    Anim.Cancel(scrollKey);
                    Invalidate();
                }
                return;
            }
            bool onBar = ThumbRect().Width > 0 && e.X >= Width - S(12);
            barHover.To(onBar ? 1 : 0, onBar ? 120 : 260);
            object o = null;
            if (!onBar) HitTest(e.Location, out o);
            var item = o as CatItem;
            if (item != null && item.Disabled) o = null;
            SetHover(o);
            Cursor = o != null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover(null);
            if (!dragging) barHover.To(0, 260);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var tr = ThumbRect();
            if (tr.Width > 0 && e.X >= Width - S(12))
            {
                if (tr.Contains(new Point(tr.X, e.Y)))
                {
                    dragging = true;
                    dragStartY = e.Y;
                    dragStartScroll = scroll;
                    Capture = true;
                }
                else ScrollTo(scrollTarget + (e.Y < tr.Y ? -Height : Height) * 0.9f);
                return;
            }
            object o;
            var part = HitTest(e.Location, out o);
            var g = o as CatGroup;
            var item = o as CatItem;
            if (part == Part.Item && item != null && item.Open != null)
            {
                item.Open();
                return;
            }
            if (part == Part.GroupCheck && g != null && !g.Items.Exists(i => i.Open == null && !i.Disabled)) part = Part.GroupBody;
            if (Locked && (part == Part.GroupCheck || part == Part.Item)) return;
            if (part == Part.GroupCheck && g != null)
            {
                bool all = true;
                foreach (var i in g.Items) if (!i.Disabled && !i.Checked) all = false;
                foreach (var i in g.Items) if (!i.Disabled) { i.Checked = !all; AnimateCheck(i); }
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
            else if (part == Part.GroupBody && g != null)
            {
                g.Expanded = !g.Expanded;
                Animate(expand, g, "e", g.Expanded ? 1 : 0, 320, Anim.EaseInOut);
                if (g.Expanded)
                {
                    // Bring the opened group into view once it has grown.
                    var grp = g;
                    Anim.Delay(330, () =>
                    {
                        float top = -scroll, y = 0;
                        foreach (var x in Groups) { if (x == grp) break; y += GroupH + x.Items.Count * ItemH * Get(expand, x); }
                        float bottom = y + GroupH + grp.Items.Count * ItemH;
                        if (bottom - scrollTarget > Height) ScrollTo(Math.Min(y, bottom - Height + S(8)));
                    });
                }
            }
            else if (part == Part.Item && item != null && !item.Disabled)
            {
                item.Checked = !item.Checked;
                AnimateCheck(item);
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging)
            {
                dragging = false;
                Capture = false;
            }
        }

        // ---------- painting ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color bg = ParentBg;
            g.Clear(bg);
            if (Groups.Count == 0)
            {
                if (loading) PaintSkeleton(g, bg);
                else if (EmptyGlyph.Length > 0 && Theme.HasGlyphs)
                {
                    // Accent tile with the glyph, the text under it, both in the middle of the list.
                    int tile = S(64), top = (Height - tile - S(16) - S(40)) / 2;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var box = new RectangleF((Width - tile) / 2f, top, tile, tile);
                    Theme.FillRound(g, Anim.Blend(p.SurfaceAlt, p.Accent, 0.16f), box, S(16));
                    GlyphShape.Draw(g, EmptyGlyph, new PointF(box.X + tile / 2f, box.Y + tile / 2f), S(28), p.Accent, Math.Max(0.8f, S(1) * 1.1f));
                    TextRenderer.DrawText(g, EmptyText, Font, new Rectangle(S(16), top + tile + S(16), Width - S(32), S(40)), p.TextDim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    g.SmoothingMode = SmoothingMode.None;
                }
                else if (EmptyText.Length > 0)
                    TextRenderer.DrawText(g, EmptyText, Font, new Rectangle(0, S(24), Width, S(40)), p.TextDim, TextCenter);
                return;
            }
            // Whole pixels everywhere: text snaps to pixels, so shapes must too or rows shimmer while moving.
            float y = -(float)Math.Round(scroll);
            int w = Width - S(10);
            for (int gi = 0; gi < Groups.Count; gi++)
            {
                var grp = Groups[gi];
                float ex = Get(expand, grp);
                float block = (float)Math.Round(grp.Items.Count * ItemH * ex);
                if (y + GroupH + block >= 0 && y < Height) PaintGroup(g, grp, y, w, bg, ex);
                y += GroupH;
                if (block > 0.5f && y < Height && y + block > 0)
                {
                    var state = g.Save();
                    g.SetClip(new RectangleF(0, y, Width, block));
                    for (int i = 0; i < grp.Items.Count; i++)
                    {
                        float iy = (float)Math.Round(y + i * ItemH - (1 - ex) * S(10));
                        if (iy + ItemH < 0 || iy > Height) continue;
                        PaintItem(g, grp.Items[i], iy, w, bg, ex);
                    }
                    g.Restore(state);
                }
                y += block;
                if (gi < Groups.Count - 1)
                    using (var pen = new Pen(p.Border)) g.DrawLine(pen, S(16), (int)y, w - S(6), (int)y);
            }

            var tr = ThumbRect();
            if (tr.Width > 0)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float bh = barHover.Value;
                var r = new RectangleF(tr.X + (1 - bh) * S(2), tr.Y, tr.Width - (1 - bh) * S(2), tr.Height);
                Theme.FillRound(g, Anim.Blend(p.Border, p.TextDim, bh * 0.6f), r, r.Width / 2);
            }
        }

        void PaintSkeleton(Graphics g, Color bg)
        {
            var p = Theme.Current;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Width - S(10);
            using (var b = Shimmer.Make(Width, bg))
            using (var pen = new Pen(p.Border))
            {
                int y = 0;
                for (int i = 0; y < Height; i++)
                {
                    float cy = y + GroupH / 2f;
                    Shimmer.Tile(g, b, new RectangleF(S(18), cy - S(9), S(18), S(18)), S(4));
                    Shimmer.Tile(g, b, new RectangleF(S(48), cy - S(17), S(34), S(34)), S(8));
                    float room = Math.Max(S(60), w - S(96) - S(170));
                    Shimmer.Bar(g, b, new RectangleF(S(96), y + S(14), room * (0.28f + 0.3f * Shimmer.Vary(i, 0)), S(12)));
                    Shimmer.Bar(g, b, new RectangleF(S(96), y + S(35), room * (0.45f + 0.4f * Shimmer.Vary(i, 1)), S(9)));
                    Shimmer.Bar(g, b, new RectangleF(w - S(40) - S(64), cy - S(6), S(64), S(12)));
                    y += GroupH;
                    if (y < Height) g.DrawLine(pen, S(16), y, w - S(6), y);
                }
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        string SizeText(long size)
        {
            return size < 0 ? "…" : (size == 0 ? "0" : L.Size(size));
        }

        void PaintGroup(Graphics g, CatGroup grp, float y, int w, Color bg, float ex)
        {
            var p = Theme.Current;
            float h = Get(hover, grp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (h > 0.01f)
                Theme.FillRound(g, Anim.Blend(bg, p.Hover, h * 0.55f), new RectangleF(S(6), y + S(4), w - S(6), GroupH - S(8)), S(6));
            Color rowBg = Anim.Blend(bg, p.Hover, h * 0.55f);

            // group check box: all / some / none of the enabled items
            int on = 0, total = 0;
            float sum = 0;
            bool links = false;
            foreach (var i in grp.Items)
            {
                if (i.Open != null) links = true;
                if (i.Disabled || i.Open != null) continue;
                total++;
                if (i.Checked) on++;
                sum += Get(check, i);
            }
            bool partial = on > 0 && on < total;
            float value = total > 0 ? sum / total : 0;
            bool linkGroup = links && total == 0;
            if (linkGroup) value = 0.6f;
            else CheckMark.Draw(g, new RectangleF(S(18), y + (GroupH - S(18)) / 2f, S(18), S(18)), partial ? 1 : value, partial, h, total > 0, rowBg);

            // icon tile
            var tile = new RectangleF(S(48), y + (GroupH - S(36)) / 2f, S(36), S(36));
            Theme.FillRound(g, Anim.Blend(p.SurfaceAlt, p.Accent, 0.12f + 0.1f * value), tile, S(9));
            float ink = S(18);
            GlyphShape.Draw(g, grp.Glyph, new PointF(tile.X + tile.Width / 2, tile.Y + tile.Height / 2), ink,
                Anim.Blend(p.TextDim, p.Accent, 0.35f + 0.65f * value), Math.Max(0.8f, ink * 0.055f));

            int tx = S(96);
            int sizeW = S(110);
            var titleR = new Rectangle(tx, (int)y + S(10), w - tx - sizeW - S(40), S(20));
            TextRenderer.DrawText(g, grp.Name(), Theme.UiBold, titleR, p.Text, TextLeft);
            TextRenderer.DrawText(g, grp.Desc(), Theme.SmallFont, new Rectangle(tx, (int)y + S(30), titleR.Width, S(18)), p.TextDim, TextLeft);

            bool measured = grp.Measured;
            string size = linkGroup ? "" : (measured ? SizeText(grp.CheckedSize) : "…");
            TextRenderer.DrawText(g, size, Theme.UiBold, new Rectangle(w - sizeW - S(40), (int)y, sizeW, GroupH),
                on == 0 ? p.TextDim : p.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            // chevron rotates from "right" to "down" as the group opens
            var c = new PointF(w - S(18), y + GroupH / 2f);
            float k = S(4);
            double ang = (ex * 90 - 90) * Math.PI / 180;
            Func<float, float, PointF> rot = (dx, dy) => new PointF(
                c.X + (float)(dx * Math.Cos(ang) - dy * Math.Sin(ang)), c.Y + (float)(dx * Math.Sin(ang) + dy * Math.Cos(ang)));
            using (var pen = new Pen(Anim.Blend(p.TextDim, p.Text, h), Math.Max(1.4f, S(2) * 0.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { rot(-k, -k / 2), rot(0, k / 2), rot(k, -k / 2) });
            g.SmoothingMode = SmoothingMode.None;
        }

        void PaintItem(Graphics g, CatItem item, float y, int w, Color bg, float ex)
        {
            var p = Theme.Current;
            float h = Get(hover, item);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (h > 0.01f)
                Theme.FillRound(g, Anim.Blend(bg, p.Hover, h * 0.5f), new RectangleF(S(40), y + S(3), w - S(40), ItemH - S(6)), S(6));
            Color rowBg = Anim.Blend(bg, p.Hover, h * 0.5f);
            Color text = item.Disabled ? Anim.Blend(p.TextDim, bg, 0.3f) : p.Text;
            text = Anim.Blend(bg, text, ex);

            if (item.Open != null)
                GlyphShape.Draw(g, "\uE8A7", new PointF(S(60), y + ItemH / 2f), S(14),
                    Anim.Blend(bg, Anim.Blend(p.TextDim, p.Accent, 0.55f + 0.45f * h), ex), Math.Max(0.6f, S(1) * 0.8f));
            else
                CheckMark.Draw(g, new RectangleF(S(52), y + (ItemH - S(16)) / 2f, S(16), S(16)), Get(check, item), false, h, !item.Disabled, rowBg);
            int x = S(82);
            string name = item.Name();
            int sizeW = S(110);
            int maxName = w - x - sizeW - S(48);
            TextRenderer.DrawText(g, name, Font, new Rectangle(x, (int)y, maxName, ItemH), text, TextLeft);
            x += Math.Min(maxName, MeasureWidth(name, Font)) + S(10);
            if (item.Warn)
            {
                int d = S(7);
                using (var b = new SolidBrush(Anim.Blend(bg, p.Warning, ex)))
                    g.FillEllipse(b, x, y + (ItemH - d) / 2f, d, d);
                x += d + S(8);
            }
            string note = item.Disabled && item.DisabledText != null ? item.DisabledText() : (item.Note != null ? item.Note() : "");
            if (!string.IsNullOrEmpty(note) && x < w - sizeW - S(48))
                TextRenderer.DrawText(g, note, Theme.SmallFont, new Rectangle(x, (int)y, w - sizeW - S(48) - x, ItemH),
                    Anim.Blend(bg, p.TextDim, ex * 0.9f), TextLeft);

            if (!item.Disabled)
            {
                float a = item.Size < 0 ? 0.6f : (sizeIn.ContainsKey(item) ? sizeIn[item] : 1f);
                Color sc = item.Checked ? Anim.Blend(p.TextDim, p.Text, 0.6f) : p.TextDim;
                TextRenderer.DrawText(g, item.Badge ?? SizeText(item.Size), Font, new Rectangle(w - sizeW - S(40), (int)y, sizeW, ItemH),
                    Anim.Blend(bg, sc, a * ex), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            g.SmoothingMode = SmoothingMode.None;
        }
    }

}
