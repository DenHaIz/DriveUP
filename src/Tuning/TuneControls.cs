using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>A pill with a short label ("включён", "после перезагрузки").</summary>
    static class Pill
    {
        public static int Width(string text)
        {
            return TextDraw.MeasureText(text, Theme.SmallFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + Theme.S(16);
        }

        /// <summary>Draws it with its left edge at x, centred on cy; returns its width. alpha fades it in.</summary>
        public static int Draw(Graphics g, string text, int x, float cy, Color color, Color bg, float alpha)
        {
            int w = Width(text), h = Theme.S(20);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(x, cy - h / 2f, w, h);
            Theme.FillRound(g, Anim.Blend(bg, Anim.Blend(bg, color, Theme.Current.IsDark ? 0.2f : 0.13f), alpha), r, h / 2f);
            TextDraw.DrawText(g, text, Theme.SmallFont, r, Anim.Blend(bg, color, alpha),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            g.SmoothingMode = old;
            return w;
        }
    }

    /// <summary>One mode to pick (Games / Work / Quiet): selection, hover and badges all animate.</summary>
    class ModeCard : SkinControl
    {
        public readonly TuneMode Mode;
        readonly Tweened hover, sel, active, rec, enabledT;
        bool selected, isActive, recommended;

        public ModeCard(TuneMode mode)
        {
            Mode = mode;
            hover = new Tweened(Invalidate);
            sel = new Tweened(Invalidate);
            active = new Tweened(Invalidate);
            rec = new Tweened(Invalidate);
            enabledT = new Tweened(Invalidate, 1f);
            Cursor = Cursors.Hand;
        }

        public bool Selected
        {
            get { return selected; }
            set { if (selected != value) { selected = value; sel.To(value ? 1 : 0, value ? 260 : 220); } }
        }

        /// <summary>This mode is the one applied now.</summary>
        public bool Active
        {
            get { return isActive; }
            set { if (isActive != value) { isActive = value; active.To(value ? 1 : 0, 320); } }
        }

        /// <summary>The smart pick suggests this mode for this computer.</summary>
        public bool Recommended
        {
            get { return recommended; }
            set { if (recommended != value) { recommended = value; rec.To(value ? 1 : 0, 360); } }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Enabled) hover.To(1, 120); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0, 240); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            enabledT.To(Enabled ? 1 : 0, 200);
            if (!Enabled) hover.To(0, 120);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color pb = ParentBg;
            g.Clear(pb);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = hover.Value, s = sel.Value, en = 0.55f + 0.45f * enabledT.Value;

            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color fill = Anim.Blend(Anim.Blend(p.Surface, p.Hover, h * 0.45f), p.Accent, s * (p.IsDark ? 0.1f : 0.035f));
            Theme.FillRound(g, fill, r, S(10));
            using (var pen = new Pen(Anim.Blend(Anim.Blend(p.Border, p.TextDim, h * 0.35f), p.Accent, s), 1 + s * Math.Max(1, S(1))))
            using (var path = Theme.RoundRect(new RectangleF(r.X + s * 0.5f, r.Y + s * 0.5f, r.Width - s, r.Height - s), S(10)))
                g.DrawPath(pen, path);

            int tile = S(44);
            var tr = new RectangleF(S(16), (Height - tile) / 2f, tile, tile);
            Theme.FillRound(g, Anim.Blend(p.SurfaceAlt, p.Accent, 0.12f + 0.16f * s + 0.05f * h), tr, S(12));
            GlyphShape.Draw(g, Mode.Glyph, new PointF(tr.X + tile / 2f, tr.Y + tile / 2f), S(22),
                Anim.Blend(fill, Anim.Blend(p.TextDim, p.Accent, 0.35f + 0.65f * Math.Max(s, h * 0.5f)), en), Math.Max(0.8f, S(1) * 1.1f));

            int x = S(76), right = Width - S(14);
            string title = Mode.Name();
            int tw = MeasureWidth(title, Theme.UiBold);
            int ty = Height / 2 - S(21);
            TextDraw.DrawText(g, title, Theme.UiBold, new Rectangle(x, ty, right - x, S(22)), Anim.Blend(fill, p.Text, en), TextLeft);
            int bx = x + tw + S(10);
            float cy = ty + S(11);
            if (active.Value > 0.01f && bx < right - S(60))
                bx += Pill.Draw(g, L.T("включён", "on"), bx, cy, p.Success, fill, active.Value * en) + S(6);
            if (rec.Value > 0.01f && bx < right - S(60))
                Pill.Draw(g, L.T("подходит этому ПК", "suits this PC"), bx, cy, p.Accent, fill, rec.Value * en);
            TextDraw.DrawText(g, Mode.Desc(), Theme.SmallFont, new Rectangle(x, ty + S(24), right - x, S(36)), Anim.Blend(fill, p.TextDim, en),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// The settings a mode changes (now → new value, why) or the ones already changed (was → now,
    /// with a Return button each). Rows slide in, ticks draw on, a spinner turns while a setting
    /// is applied and a tick or a warning replaces it; returned rows fold away.
    /// </summary>
    class TuneList : SkinControl
    {
        readonly List<TuneRow> rows = new List<TuneRow>();
        readonly Dictionary<TuneRow, float> appear = new Dictionary<TuneRow, float>();
        readonly Dictionary<TuneRow, float> check = new Dictionary<TuneRow, float>();
        readonly Dictionary<TuneRow, float> hover = new Dictionary<TuneRow, float>();
        readonly Dictionary<TuneRow, float> btnHover = new Dictionary<TuneRow, float>();
        readonly Dictionary<TuneRow, float> stateT = new Dictionary<TuneRow, float>();
        readonly Dictionary<TuneRow, float> fold = new Dictionary<TuneRow, float>();
        readonly object scrollKey = new object(), spinKey = new object(), shimmerKey = new object();
        TuneRow hoverRow, hoverBtn;
        float scroll, scrollTarget;
        bool loading;

        public event EventHandler CheckedChanged;
        public event Action<TuneRow> ReturnClicked;
        /// <summary>A group title in the "Changed" list was clicked: return all of its rows.</summary>
        public event Action<List<TuneRow>> ReturnGroup;
        readonly ToolTip tip = new ToolTip { InitialDelay = 500, ReshowDelay = 200, AutoPopDelay = 15000 };
        TuneRow tipRow;
        public string EmptyText = "", EmptyGlyph = "";
        /// <summary>While true, clicks change nothing (settings are being applied).</summary>
        public bool Locked;

        int RowH { get { return S(76); } }

        public List<TuneRow> Rows { get { return rows; } }

        public bool Loading
        {
            get { return loading; }
            set
            {
                if (loading == value) return;
                loading = value;
                if (value) Shimmer.Run(this, shimmerKey, () => loading && rows.Count == 0);
                Invalidate();
            }
        }

        public void SetRows(IEnumerable<TuneRow> list, bool stagger)
        {
            rows.Clear();
            rows.AddRange(list);
            foreach (var d in new[] { appear, check, hover, btnHover, stateT, fold }) d.Clear();
            hoverRow = hoverBtn = null;
            scroll = scrollTarget = 0;
            loading = false;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                check[r] = r.Checked ? 1 : 0;
                fold[r] = 1;
                stateT[r] = r.State == TuneState.Idle ? 0 : 1;
                if (!stagger) { appear[r] = 1; continue; }
                appear[r] = 0;
                // The wave covers the first screen; rows further down are already in place when scrolled to.
                Anim.Delay(35 * Math.Min(i, 10), () => Animate(appear, r, "a", 1, 360));
            }
            Invalidate();
        }

        void Animate(Dictionary<TuneRow, float> map, TuneRow key, string tag, float to, int ms, Action done = null)
        {
            float from;
            if (!map.TryGetValue(key, out from)) from = 0;
            if (Math.Abs(from - to) < 0.001f) { if (done != null) done(); return; }
            Anim.Run(Tuple.Create(this, key, tag), ms, t => { map[key] = from + (to - from) * t; Invalidate(); }, done);
        }

        static float Get(Dictionary<TuneRow, float> map, TuneRow key)
        {
            float v;
            return map.TryGetValue(key, out v) ? v : 0;
        }

        /// <summary>Scrolls so the row is in view (the one being applied follows the work down the list).</summary>
        public void EnsureVisible(TuneRow row)
        {
            float y = 0;
            foreach (var r in rows)
            {
                if (r == row) break;
                y += H(r);
            }
            if (y < scrollTarget) ScrollTo(y - S(44));
            else if (y + RowH > scrollTarget + Height) ScrollTo(y + RowH - Height + S(8));
        }

        public void SetState(TuneRow row, TuneState state, string error = "")
        {
            if (state == TuneState.Working) EnsureVisible(row);
            row.State = state;
            row.Error = error ?? "";
            stateT[row] = 0;
            Animate(stateT, row, "s", 1, state == TuneState.Done ? 420 : 260);
            if (state == TuneState.Working)
                Anim.Frame(spinKey, () =>
                {
                    if (IsDisposed || !rows.Any(r => r.State == TuneState.Working)) return false;
                    if (Visible) Invalidate();
                    return true;
                });
            Invalidate();
        }

        /// <summary>Folds the row away, then drops it.</summary>
        public void Remove(TuneRow row, Action done = null)
        {
            Animate(fold, row, "f", 0, 300, () =>
            {
                rows.Remove(row);
                // A group title left with nothing under it folds away too.
                for (int i = 0; i < rows.Count; i++)
                {
                    var hr = rows[i];
                    if (hr.Kind != TuneKind.Header || (i + 1 < rows.Count && rows[i + 1].Kind != TuneKind.Header)) continue;
                    if (Get(fold, hr) > 0.99f) Animate(fold, hr, "f", 0, 260, () => { rows.Remove(hr); ClampScroll(); Invalidate(); });
                }
                ClampScroll();
                Invalidate();
                if (done != null) done();
            });
        }

        /// <summary>The rows of a group title's group.</summary>
        IEnumerable<TuneRow> Group(TuneRow header)
        {
            int i = rows.IndexOf(header);
            for (int j = i + 1; i >= 0 && j < rows.Count && rows[j].Kind != TuneKind.Header; j++) yield return rows[j];
        }

        public void RefreshChecks()
        {
            foreach (var r in rows) Animate(check, r, "c", r.Checked ? 1 : 0, r.Checked ? 260 : 160);
        }

        // ---------- geometry ----------

        int BaseH(TuneRow r) { return r.Kind == TuneKind.Header ? S(44) : RowH; }

        float H(TuneRow r) { return BaseH(r) * (fold.ContainsKey(r) ? Anim.EaseInOut(fold[r]) : 1); }

        float ContentHeight { get { return rows.Sum(r => H(r)) + S(4); } }
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
            ScrollTo(scrollTarget - e.Delta / 120f * RowH * 1.5f);
        }

        Rectangle ButtonRect(float y)
        {
            int w = Math.Max(S(96), MeasureWidth(L.T("Вернуть", "Return"), Font) + S(44)), h = S(30);
            return new Rectangle(Width - S(10) - S(14) - w, (int)(y + (RowH - h) / 2f), w, h);
        }

        TuneRow HitTest(Point pt, out bool onButton)
        {
            onButton = false;
            float y = -scroll;
            foreach (var r in rows)
            {
                float h = H(r);
                if (pt.Y >= y && pt.Y < y + h)
                {
                    onButton = r.Kind == TuneKind.Changed && r.Ready && ButtonRect(y).Contains(pt);
                    return r;
                }
                y += h;
            }
            return null;
        }

        bool Clickable(TuneRow r, bool onButton)
        {
            if (r == null || Locked) return false;
            if (r.Kind == TuneKind.Changed) return onButton;
            if (r.Kind == TuneKind.Header) return Group(r).Any(x => (x.Actionable || x.Kind == TuneKind.Changed) && x.Ready);
            return r.Actionable && r.Ready;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool onButton;
            var r = HitTest(e.Location, out onButton);
            var hr = r != null && r.Kind != TuneKind.Changed && Clickable(r, false) ? r : null;
            if (hr != hoverRow)
            {
                if (hoverRow != null) Animate(hover, hoverRow, "h", 0, 220);
                if (hr != null) Animate(hover, hr, "h", 1, 110);
                hoverRow = hr;
            }
            var hb = onButton && !Locked ? r : null;
            if (hb != hoverBtn)
            {
                if (hoverBtn != null) Animate(btnHover, hoverBtn, "b", 0, 220);
                if (hb != null) Animate(btnHover, hb, "b", 1, 110);
                hoverBtn = hb;
            }
            Cursor = hr != null || hb != null ? Cursors.Hand : Cursors.Default;
            if (r != tipRow)
            {
                tipRow = r;
                tip.SetToolTip(this, r != null && r.Kind != TuneKind.Header ? TipText(r) : null);
            }
        }

        /// <summary>Everything about a row in full: the list cuts long texts short.</summary>
        static string TipText(TuneRow r)
        {
            var lines = new List<string> { r.Tweak.Name() };
            if (r.Kind == TuneKind.Changed)
                lines.Add(L.T("Было: ", "Was: ") + Show(r, r.Was) + L.T("   Сейчас: ", "   Now: ") + Show(r, r.Now));
            else if (r.Kind == TuneKind.Same) lines.Add(L.T("Уже: ", "Already: ") + Show(r, r.Now));
            else lines.Add(L.T("Сейчас: ", "Now: ") + Show(r, r.Now) + L.T("   Станет: ", "   Becomes: ") + Show(r, r.To));
            string reason = r.Reason != null ? r.Reason() : "";
            if (reason.Length > 0 && r.Kind != TuneKind.Changed) lines.Add(reason);
            if (r.Warn != null) lines.Add(L.T("Внимание: ", "Note: ") + r.Warn());
            if (r.Tweak.NeedsReboot) lines.Add(L.T("Заработает после перезагрузки.", "Takes effect after a restart."));
            if (r.State == TuneState.Failed && r.Error.Length > 0) lines.Add(L.T("Ошибка: ", "Error: ") + r.Error);
            return string.Join("\n", lines);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverRow != null) Animate(hover, hoverRow, "h", 0, 220);
            if (hoverBtn != null) Animate(btnHover, hoverBtn, "b", 0, 220);
            hoverRow = hoverBtn = null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            bool onButton;
            var r = HitTest(e.Location, out onButton);
            if (!Clickable(r, onButton)) return;
            if (r.Kind == TuneKind.Changed)
            {
                if (ReturnClicked != null) ReturnClicked(r);
                return;
            }
            if (r.Kind == TuneKind.Header)
            {
                // The group title ticks the whole group, or unticks it when all are ticked.
                if (Group(r).Any(x => x.Kind == TuneKind.Changed))
                {
                    // In the "Changed" list a group title returns its whole group.
                    var back = Group(r).Where(x => x.Kind == TuneKind.Changed && x.Ready).ToList();
                    if (back.Count > 0 && ReturnGroup != null) ReturnGroup(back);
                    return;
                }
                var items = Group(r).Where(x => x.Actionable && x.Ready).ToList();
                bool all = items.All(x => x.Checked);
                foreach (var x in items)
                {
                    x.Checked = !all;
                    Animate(check, x, "c", x.Checked ? 1 : 0, x.Checked ? 260 : 160);
                }
            }
            else
            {
                r.Checked = !r.Checked;
                Animate(check, r, "c", r.Checked ? 1 : 0, r.Checked ? 260 : 160);
            }
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }

        // ---------- painting ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color bg = ParentBg;
            g.Clear(bg);
            if (rows.Count == 0)
            {
                if (loading) PaintSkeleton(g, bg);
                else PaintEmpty(g, bg);
                return;
            }
            float y = -(float)Math.Round(scroll);
            int w = Width - S(10);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                float h = H(r);
                if (y + h > 0 && y < Height && h > 0.5f)
                {
                    var state = g.Save();
                    g.SetClip(new RectangleF(0, y, Width, h));
                    float a = Anim.EaseOut(Get(appear, r)) * Math.Min(1, Get(fold, r) * 1.6f);
                    // Rows rise into place as they appear.
                    float ry = (float)Math.Round(y + (1 - a) * S(12));
                    if (r.Kind == TuneKind.Header) PaintHeader(g, r, ry, w, bg, a);
                    else PaintRow(g, r, ry, w, bg, a);
                    g.Restore(state);
                }
                y += h;
                if (i < rows.Count - 1 && h > 0.5f && r.Kind != TuneKind.Header && rows[i + 1].Kind != TuneKind.Header)
                    using (var pen = new Pen(Anim.Blend(bg, p.Border, Get(appear, rows[i + 1]))))
                        g.DrawLine(pen, S(16), (int)y, w - S(6), (int)y);
            }

            float content = ContentHeight;
            if (content > Height + 1)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int th = Math.Max(S(32), (int)(Height * Height / content));
                int ty = (int)((Height - th) * (scroll / Math.Max(1, content - Height)));
                Theme.FillRound(g, p.Border, new RectangleF(Width - S(6), ty, S(4), th), S(2));
            }
        }

        void PaintEmpty(Graphics g, Color bg)
        {
            var p = Theme.Current;
            if (EmptyText.Length == 0) return;
            int tile = S(64), top = (Height - tile - S(16) - S(40)) / 2;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (EmptyGlyph.Length > 0 && Theme.HasGlyphs)
            {
                var box = new RectangleF((Width - tile) / 2f, top, tile, tile);
                Theme.FillRound(g, Anim.Blend(p.SurfaceAlt, p.Accent, 0.16f), box, S(16));
                GlyphShape.Draw(g, EmptyGlyph, new PointF(box.X + tile / 2f, box.Y + tile / 2f), S(28), p.Accent, Math.Max(0.8f, S(1) * 1.1f));
            }
            TextDraw.DrawText(g, EmptyText, Font, new Rectangle(S(24), top + tile + S(16), Width - S(48), S(44)), p.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
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
                    float cy = y + RowH / 2f;
                    Shimmer.Tile(g, b, new RectangleF(S(18), cy - S(9), S(18), S(18)), S(4));
                    Shimmer.Tile(g, b, new RectangleF(S(48), cy - S(18), S(36), S(36)), S(9));
                    float room = Math.Max(S(60), w - S(96) - S(150));
                    Shimmer.Bar(g, b, new RectangleF(S(96), y + S(14), room * (0.3f + 0.25f * Shimmer.Vary(i, 0)), S(12)));
                    Shimmer.Bar(g, b, new RectangleF(S(96), y + S(35), room * (0.5f + 0.35f * Shimmer.Vary(i, 1)), S(10)));
                    Shimmer.Bar(g, b, new RectangleF(S(96), y + S(54), room * (0.4f + 0.4f * Shimmer.Vary(i, 2)), S(8)));
                    y += RowH;
                    if (y < Height) g.DrawLine(pen, S(16), y, w - S(6), y);
                }
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        void PaintHeader(Graphics g, TuneRow r, float y, int w, Color bg, float a)
        {
            var p = Theme.Current;
            float h = Get(hover, r);
            var items = Group(r).ToList();
            var act = items.Where(i => i.Actionable).ToList();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float top = y + S(8);
            if (h > 0.01f)
                Theme.FillRound(g, Anim.Blend(bg, p.Hover, h * 0.45f * a), new RectangleF(S(6), top, w - S(6), S(32)), S(8));
            Color rowBg = Anim.Blend(bg, p.Hover, h * 0.45f);
            int x = S(18);
            if (act.Count > 0)
            {
                int on = act.Count(i => i.Checked);
                float value = act.Sum(i => Get(check, i)) / act.Count;
                bool partial = on > 0 && on < act.Count;
                CheckMark.Draw(g, new RectangleF(x, top + (S(32) - S(18)) / 2f, S(18), S(18)), (partial ? 1 : value) * a, partial, h,
                    !Locked && act.Any(i => i.Ready), rowBg);
            }
            string title = Tuner.CategoryName(r.Category);
            TextDraw.DrawText(g, title, Theme.HeadFont, new RectangleF(S(48), top, w - S(220), S(32)), Anim.Blend(bg, p.Text, a), TextLeft);
            string count;
            if (items.Any(i => i.Kind == TuneKind.Changed))
            {
                count = L.Count(items.Count, "параметр", "параметра", "параметров", "setting", "settings") +
                        (items.Any(i => i.Ready) && !Locked ? L.T(" · вернуть группу", " · return group") : "");
            }
            else
            {
                int on = act.Count(i => i.Checked), same = items.Count(i => i.Kind == TuneKind.Same);
                count = act.Count > 0 ? L.T("выбрано ", "") + on + L.T(" из ", " of ") + act.Count + L.T("", " picked") : "";
                if (same > 0) count += (count.Length > 0 ? " · " : "") + L.T("уже так: ", "already set: ") + same;
            }
            TextDraw.DrawText(g, count, Theme.SmallFont, new RectangleF(w - S(260), top, S(246), S(32)),
                Anim.Blend(bg, Anim.Blend(p.TextDim, p.Accent, items.Any(i => i.Kind == TuneKind.Changed) ? h : 0), a),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            g.SmoothingMode = SmoothingMode.None;
        }

        static string Show(TuneRow r, string value)
        {
            return string.IsNullOrEmpty(value) ? "?" : r.Tweak.Describe(value);
        }

        void PaintRow(Graphics g, TuneRow r, float y, int w, Color bg, float a)
        {
            var p = Theme.Current;
            float h = Get(hover, r);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (h > 0.01f)
                Theme.FillRound(g, Anim.Blend(bg, p.Hover, h * 0.5f * a), new RectangleF(S(6), y + S(4), w - S(6), RowH - S(8)), S(8));
            Color rowBg = Anim.Blend(bg, p.Hover, h * 0.5f);
            float on = r.Kind == TuneKind.Changed ? 1 : (r.Actionable ? Get(check, r) : 0.25f);
            // Unticked rows step back a little: they will be left as they are.
            float ink = a * (r.Actionable ? 0.62f + 0.38f * on : (r.Kind == TuneKind.Same ? 0.75f : 1));

            if (r.Actionable)
                CheckMark.Draw(g, new RectangleF(S(18), y + (RowH - S(18)) / 2f, S(18), S(18)), Get(check, r) * a, false, h, !Locked && r.Ready, rowBg);
            else if (r.Kind == TuneKind.Same)
                using (var pen = new Pen(Anim.Blend(bg, p.Success, a * 0.9f), Math.Max(1.6f, S(2) * 0.9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    float cx = S(27), cy = y + RowH / 2f, k = S(5);
                    g.DrawLines(pen, new[] { new PointF(cx - k, cy), new PointF(cx - k * 0.3f, cy + k * 0.7f), new PointF(cx + k, cy - k * 0.8f) });
                }

            var tile = new RectangleF(S(48), y + (RowH - S(36)) / 2f, S(36), S(36));
            Theme.FillRound(g, Anim.Blend(bg, Anim.Blend(p.SurfaceAlt, p.Accent, 0.1f + 0.12f * on), a), tile, S(9));
            GlyphShape.Draw(g, r.Tweak.Glyph, new PointF(tile.X + tile.Width / 2, tile.Y + tile.Height / 2), S(17),
                Anim.Blend(bg, Anim.Blend(p.TextDim, p.Accent, 0.3f + 0.7f * on), a), Math.Max(0.8f, S(1) * 0.95f));

            int x = S(96);
            int rightW = r.Kind == TuneKind.Changed ? ButtonRect(y).Width + S(24) : (r.Kind == TuneKind.Same ? S(130) : S(64));
            int textR = w - rightW;
            string title = r.Tweak.Name();
            int tw = Math.Min(textR - x, MeasureWidth(title, Theme.UiBold));
            TextDraw.DrawText(g, title, Theme.UiBold, new RectangleF(x, y + S(10), textR - x, S(22)), Anim.Blend(bg, p.Text, ink), TextLeft);
            int px = x + tw + S(10);
            float pcy = y + S(21);
            if (r.Tweak.NeedsReboot && r.Kind != TuneKind.Same && px < textR - S(80))
                px += Pill.Draw(g, L.T("после перезагрузки", "after a restart"), px, pcy, p.Warning, rowBg, ink) + S(6);
            if (r.Kind == TuneKind.Return && px < textR - S(60))
                px += Pill.Draw(g, L.T("вернётся", "goes back"), px, pcy, p.Accent, rowBg, ink) + S(6);
            if (r.Procs > 0 && r.Kind != TuneKind.Changed)
            {
                string run = L.T("в фоне: ", "running: ") + L.Count(r.Procs, "процесс", "процесса", "процессов", "process", "processes") +
                             (r.ProcMemory > 0 ? " · " + L.Size(r.ProcMemory) : "");
                if (px + Pill.Width(run) < textR) Pill.Draw(g, run, px, pcy, p.TextDim, rowBg, ink);
            }

            // Values: now → new (or was → now).
            var vr = new RectangleF(x, y + S(32), textR - x, S(20));
            if (r.Kind == TuneKind.Same)
                DrawValueLine(g, vr, L.T("уже ", "already ") + Show(r, r.Now), null, bg, ink);
            else if (r.Kind == TuneKind.Changed)
                DrawValueLine(g, vr, L.T("было: ", "was: ") + Show(r, r.Was), L.T("сейчас: ", "now: ") + Show(r, r.Now), bg, ink);
            else
                DrawValueLine(g, vr, Show(r, r.Now), Show(r, r.To), bg, ink);

            string sub;
            Color subColor = p.TextDim;
            if (r.State == TuneState.Failed) { sub = r.Error; subColor = p.Danger; }
            else if (r.Kind == TuneKind.Changed)
            {
                var mode = Tuner.Mode(r.ModeId);
                sub = (mode != null ? L.T("режим «", "") + mode.Name() + L.T("»", " mode") : "") +
                      (r.When != DateTime.MinValue ? (mode != null ? " · " : "") + L.When(r.When) : "");
            }
            else sub = r.Reason();
            int sx = x;
            if (r.Warn != null && r.State != TuneState.Failed && sx < textR - S(80))
                sx += Pill.Draw(g, r.Warn(), sx, y + S(61), p.Warning, rowBg, ink) + S(8);
            TextDraw.DrawText(g, sub, Theme.SmallFont, new RectangleF(sx, y + S(52), textR - sx, S(18)), Anim.Blend(bg, subColor, ink * 0.95f), TextLeft);

            PaintRight(g, r, y, w, bg, rowBg, a);
            g.SmoothingMode = SmoothingMode.None;
        }

        /// <summary>"first → second": the arrow and the new value stand out.</summary>
        void DrawValueLine(Graphics g, RectangleF r, string first, string second, Color bg, float ink)
        {
            var p = Theme.Current;
            TextDraw.DrawText(g, first, Font, r, Anim.Blend(bg, p.TextDim, ink), TextLeft);
            if (second == null) return;
            float x = r.X + MeasureWidth(first, Font) + S(10);
            if (x > r.Right - S(40)) return;
            using (var pen = new Pen(Anim.Blend(bg, p.Accent, ink), Math.Max(1.4f, S(2) * 0.75f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float cy = r.Y + r.Height / 2f, len = S(14);
                g.DrawLine(pen, x, cy, x + len, cy);
                g.DrawLines(pen, new[] { new PointF(x + len - S(4), cy - S(4)), new PointF(x + len, cy), new PointF(x + len - S(4), cy + S(4)) });
            }
            x += S(14) + S(10);
            TextDraw.DrawText(g, second, Theme.UiBold, new RectangleF(x, r.Y, r.Right - x, r.Height), Anim.Blend(bg, p.Text, ink), TextLeft);
        }

        void PaintRight(Graphics g, TuneRow r, float y, int w, Color bg, Color rowBg, float a)
        {
            var p = Theme.Current;
            float st = Get(stateT, r);
            float cx = w - S(30), cy = y + RowH / 2f;
            if (r.Kind == TuneKind.Changed && r.Ready)
            {
                var br = ButtonRect(y);
                float bh = Get(btnHover, r);
                Color fill = Anim.Blend(Anim.Blend(p.SurfaceAlt, p.Hover, bh), bg, Locked ? 0.5f : 0);
                Theme.FillRound(g, Anim.Blend(bg, fill, a), br, S(6));
                Theme.DrawRound(g, Anim.Blend(bg, Anim.Blend(p.Border, p.Hover, bh), a), br, S(6));
                Color ink = Anim.Blend(bg, Locked ? p.TextDim : p.Text, a);
                int gx = br.X + S(12);
                if (Theme.HasGlyphs)
                {
                    DrawGlyph(g, "\uE7A7", new Rectangle(gx, br.Y, S(16), br.Height), Anim.Blend(bg, Anim.Blend(p.TextDim, p.Accent, bh), a));
                    gx += S(24);
                }
                TextDraw.DrawText(g, L.T("Вернуть", "Return"), Font, new Rectangle(gx, br.Y, br.Right - gx - S(8), br.Height), ink, TextLeft);
                return;
            }
            if (r.State == TuneState.Working)
            {
                float rad = S(9);
                double ang = (Anim.Now * 0.4) % 360;
                using (var track = new Pen(Anim.Blend(bg, p.Border, a), Math.Max(2f, S(2) * 1.1f)))
                    g.DrawEllipse(track, cx - rad, cy - rad, rad * 2, rad * 2);
                using (var pen = new Pen(Anim.Blend(bg, p.Accent, a * st), Math.Max(2f, S(2) * 1.1f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, cx - rad, cy - rad, rad * 2, rad * 2, (float)ang, 100);
            }
            else if (r.State == TuneState.Done)
            {
                // A green disc grows and the tick is drawn on it.
                float rad = S(11) * (0.6f + 0.4f * Anim.EaseOut(Math.Min(1, st * 1.6f)));
                using (var b = new SolidBrush(Anim.Blend(bg, Anim.Blend(bg, p.Success, p.IsDark ? 0.22f : 0.16f), a * Math.Min(1, st * 2))))
                    g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
                float t = Math.Max(0, (st - 0.25f) / 0.75f);
                if (t > 0)
                    using (var pen = new Pen(Anim.Blend(bg, p.Success, a), Math.Max(1.8f, S(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        float k = Theme.Scale;
                        var p1 = new PointF(cx - 5 * k, cy + 0.5f * k);
                        var p2 = new PointF(cx - 1.5f * k, cy + 4 * k);
                        var p3 = new PointF(cx + 5.5f * k, cy - 4 * k);
                        float t1 = Math.Min(1, t / 0.4f), t2 = Math.Max(0, (t - 0.4f) / 0.6f);
                        if (t2 <= 0) g.DrawLine(pen, p1, new PointF(Anim.Lerp(p1.X, p2.X, t1), Anim.Lerp(p1.Y, p2.Y, t1)));
                        else g.DrawLines(pen, new[] { p1, p2, new PointF(Anim.Lerp(p2.X, p3.X, t2), Anim.Lerp(p2.Y, p3.Y, t2)) });
                    }
            }
            else if (r.State == TuneState.Failed)
            {
                float rad = S(11);
                using (var b = new SolidBrush(Anim.Blend(bg, Anim.Blend(bg, p.Danger, 0.2f), a * st)))
                    g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
                TextDraw.DrawText(g, "!", Theme.UiBold, new RectangleF(cx - rad, cy - rad, rad * 2, rad * 2), Anim.Blend(bg, p.Danger, a * st), TextCenter);
            }
            else if (r.Kind == TuneKind.Same)
            {
                string t = L.T("уже так", "already set");
                Pill.Draw(g, t, w - S(14) - Pill.Width(t), cy, p.Success, rowBg, a * 0.9f);
            }
        }
    }
}
