using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DriveUp
{
    enum RingState { Idle, Scanning, Ready, UpToDate, Working, Done, Error }

    /// <summary>
    /// The big round button of the Updates tab: the DriveUP mark drawn large.
    /// <para>
    /// The ring is always one arc (where it starts, how long it is, its colour). A state only says
    /// where the arc should be; on a change the arc travels there from wherever it is now, so nothing
    /// ever jumps: a spinning arc slows down and settles into the logo, the logo's gap closes into a
    /// full ring, a full ring unwinds into the spinner. The middle (picture or number) and the caption
    /// cross-fade the same way.
    /// </para>
    /// Idle: the logo's arrow over its line. Searching: the arc runs like a loader (head and tail at
    /// different speeds) while arrows rise out of the line. Ready: the number of updates. Installing:
    /// the arc fills up with a glint running along it, the percentage in the middle. Up to date / done:
    /// the ring closes and a tick is drawn. Error: amber ring and a warning sign.
    /// </summary>
    class UpdateRing : SkinControl, IScalable
    {
        public void ApplyScale()
        {
            DisposeFonts();
            FitFonts(RingSize);
            Invalidate();
        }

        RingState state = RingState.Idle, prevState = RingState.Idle;
        bool introPending = true;

        // Arc travel: from the arc shown when the state changed to the new state's arc.
        float fromStart = Logo.SolidStart, fromSweep = 0, startOffset, travel = 1;
        Func<float, float> travelEase = Anim.EaseInOut;
        float lastStart = Logo.SolidStart, lastSweep;
        float warn, centerFade = 1, popIn = 1, prevPop = 1, progress, shownNumber, capFade = 1;
        string caption = "", prevCaption = "";
        readonly Tweened hover, press;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly object frameKey = new object(), numKey = new object(), travelKey = new object(), progKey = new object(),
            fadeKey = new object(), popKey = new object(), warnKey = new object(), capKey = new object();
        bool running;
        public bool Clickable;
        Font numFont, capFont;

        public UpdateRing()
        {
            hover = new Tweened(Invalidate);
            press = new Tweened(Invalidate);
        }

        float RingSize { get { return (Math.Min(Width, Height) - S(24)) * 0.84f; } }
        float Sec { get { return clock.ElapsedMilliseconds / 1000f; } }

        void DisposeFonts()
        {
            if (numFont != null) numFont.Dispose();
            if (capFont != null) capFont.Dispose();
            numFont = capFont = null;
        }

        /// <summary>The ring grows with the window; its number and caption grow with it.</summary>
        void FitFonts(float ring)
        {
            float k = Math.Max(0.85f, Math.Min(1.8f, ring / S(276)));
            float numPt = (float)Math.Round(Theme.BigFont.SizeInPoints * 1.25f * k * 2) / 2;
            float capPt = (float)Math.Round(Theme.UiFont.SizeInPoints * Math.Max(1f, Math.Min(1.35f, k)) * 2) / 2;
            if (numFont == null || Math.Abs(numFont.SizeInPoints - numPt) > 0.01f)
            {
                if (numFont != null) numFont.Dispose();
                numFont = new Font(Theme.BigFont.FontFamily, numPt, Theme.BigFont.Style);
            }
            if (capFont == null || Math.Abs(capFont.SizeInPoints - capPt) > 0.01f)
            {
                if (capFont != null) capFont.Dispose();
                capFont = new Font(Theme.UiFont.FontFamily, capPt, Theme.UiFont.Style);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            FitFonts(RingSize);
        }

        public RingState State { get { return state; } }

        /// <summary>The line under the middle; a new text cross-fades with the old one.</summary>
        public string Caption
        {
            get { return caption; }
            set
            {
                value = value ?? "";
                if (value == caption) return;
                prevCaption = capFade > 0.5f ? caption : prevCaption;
                caption = value;
                capFade = 0;
                Anim.Run(capKey, 320, t => { capFade = t; Invalidate(); });
            }
        }

        public void SetState(RingState s)
        {
            if (s == state && !introPending) return;
            introPending = false;
            prevState = state;
            state = s;

            // The arc sets off from exactly where it is drawn now.
            fromStart = lastStart;
            fromSweep = lastSweep;
            float targetStart, targetSweep;
            Target(s, Sec, out targetStart, out targetSweep);
            // Keep turning clockwise: the target is taken a whole number of turns ahead of the arc.
            startOffset = 360f * (float)Math.Ceiling((fromStart - targetStart) / 360f - 0.001f);
            int ms;
            if (prevState == RingState.Scanning && s != RingState.Scanning)
            {
                ms = 1000;
                travelEase = EaseOutCubic;   // a spinning arc slows down into place
            }
            else
            {
                ms = s == RingState.Scanning ? 650 : (s == RingState.Working ? 550 : 850);
                travelEase = Anim.EaseInOut;
            }
            travel = 0;
            Anim.Run(travelKey, ms, t => { travel = travelEase(t); Invalidate(); }, null, x => x);

            float wFrom = warn, wTo = s == RingState.Error ? 1 : 0;
            if (Math.Abs(wFrom - wTo) > 0.001f) Anim.Run(warnKey, 500, t => { warn = Anim.Lerp(wFrom, wTo, t); Invalidate(); });

            prevPop = popIn;
            if (Kind(prevState) != Kind(s))
            {
                centerFade = 0;
                Anim.Run(fadeKey, 380, t => { centerFade = t; Invalidate(); });
            }
            if (s == RingState.UpToDate || s == RingState.Done || s == RingState.Error)
            {
                popIn = 0;
                Anim.Delay(ms / 3, () => Anim.Run(popKey, 650, t => { popIn = t; Invalidate(); }, null, x => x));
            }
            if (s == RingState.Working && prevState != RingState.Working)
            {
                Anim.Cancel(progKey);
                progress = 0;
            }

            bool needsFrames = s == RingState.Scanning || s == RingState.Working;
            if (needsFrames && !running)
            {
                running = true;
                Anim.Frame(frameKey, () =>
                {
                    if (IsDisposed) return running = false;
                    if (Visible) Invalidate();
                    running = state == RingState.Scanning || state == RingState.Working;
                    return running;
                });
            }
            Invalidate();
        }

        static float EaseOutCubic(float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            float u = 1 - t;
            return 1 - u * u * u;
        }

        /// <summary>Overshooting ease for the "pop".</summary>
        static float Back(float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            const float c1 = 1.70158f, c3 = c1 + 1;
            return 1 + c3 * (float)Math.Pow(t - 1, 3) + c1 * (float)Math.Pow(t - 1, 2);
        }

        /// <summary>Where the arc of a state is at time <paramref name="sec"/> (start may run past 360).</summary>
        void Target(RingState s, float sec, out float start, out float sweep)
        {
            switch (s)
            {
                case RingState.Scanning:
                {
                    // Loader: the head runs ahead during the first half of a cycle, the tail catches up
                    // in the second; the whole arc also turns slowly. Seamless from cycle to cycle.
                    const float period = 1.5f, reach = 250f, min = 22f;
                    float cyc = sec / period;
                    float k = (float)Math.Floor(cyc), u = cyc - k;
                    float head = reach * Anim.EaseInOut(Math.Min(1, u * 2));
                    float tail = reach * Anim.EaseInOut(Math.Max(0, u * 2 - 1));
                    start = Logo.SolidStart + sec * 140 + k * reach + tail;
                    sweep = min + head - tail;
                    return;
                }
                case RingState.Working:
                    start = Logo.SolidStart;
                    sweep = Math.Max(2, 360 * progress);
                    return;
                case RingState.UpToDate:
                case RingState.Done:
                case RingState.Error:
                    start = Logo.SolidStart;
                    sweep = 360;
                    return;
                default:
                    start = Logo.SolidStart;
                    sweep = Logo.SolidSweep;
                    return;
            }
        }

        enum Center { Logo, Rising, Number, Percent, Tick, Warning }

        static Center Kind(RingState s)
        {
            switch (s)
            {
                case RingState.Scanning: return Center.Rising;
                case RingState.Ready: return Center.Number;
                case RingState.Working: return Center.Percent;
                case RingState.UpToDate:
                case RingState.Done: return Center.Tick;
                case RingState.Error: return Center.Warning;
                default: return Center.Logo;
            }
        }

        /// <summary>The number in the middle counts to the new value.</summary>
        public void SetNumber(int n, bool animate = true)
        {
            float from = shownNumber;
            if (!animate) { Anim.Cancel(numKey); shownNumber = n; Invalidate(); return; }
            Anim.Run(numKey, 600, t => { shownNumber = from + (n - from) * t; Invalidate(); });
        }

        /// <summary>Installation progress 0..1; the arc glides to it (it never moves backwards).</summary>
        public void SetProgress(float value)
        {
            value = Math.Max(progress, Math.Max(0, Math.Min(1, value)));
            float from = progress;
            if (Math.Abs(value - from) < 0.0005f) return;
            Anim.Run(progKey, 320, t => { progress = from + (value - from) * t; Invalidate(); });
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Clickable) hover.To(1, 140); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0, 260); press.To(0, 160); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Clickable && e.Button == MouseButtons.Left) press.To(1, 80); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); press.To(0, 220); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color bg = ParentBg;
            g.Clear(bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Cursor = Clickable ? Cursors.Hand : Cursors.Default;
            if (!Clickable && hover.Value > 0.001f && hover.Value < 0.999f) hover.To(0, 200);

            float scale = 1 + hover.Value * 0.02f - press.Value * 0.03f;
            float ring = RingSize * scale;
            if (ring < 4) return;
            float stroke = Math.Max(S(8), ring * 0.055f);
            var c = new PointF(Width / 2f, Height / 2f);
            float radius = ring / 2 - stroke / 2;
            var arc = new RectangleF(c.X - radius, c.Y - radius, radius * 2, radius * 2);
            float sec = Sec;
            Color violet = Logo.Mark;
            Color accent = Anim.Blend(violet, Anim.Blend(bg, p.Warning, 0.88f), warn);

            // soft glow behind the ring on hover; the middle is painted in the same colour
            Color inner = Anim.Blend(bg, accent, 0.06f * hover.Value);
            if (hover.Value > 0.01f)
                using (var b = new SolidBrush(inner))
                    g.FillEllipse(b, c.X - ring / 2 - S(6), c.Y - ring / 2 - S(6), ring + S(12), ring + S(12));

            // track
            using (var pen = new Pen(Anim.Blend(bg, p.IsDark ? p.SurfaceAlt : p.Pressed, 0.9f), stroke))
                g.DrawEllipse(pen, arc);

            // ----- the arc -----
            float ts, tw;
            Target(state, sec, out ts, out tw);
            float start = Anim.Lerp(fromStart, ts + startOffset, travel);
            float sweep = Math.Max(0, Anim.Lerp(fromSweep, tw, travel));
            if (travel >= 1)
            {
                // Settled: forget the extra turns so the numbers stay small.
                startOffset = 0;
                start = ts;
            }
            lastStart = start % 360f;
            if (lastStart < 0) lastStart += 360;
            lastSweep = sweep;

            if (sweep >= 359.6f)
                using (var pen = new Pen(accent, stroke)) g.DrawEllipse(pen, arc);
            else if (sweep > 0.3f)
                using (var pen = new Pen(accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, arc, start, sweep);

            // While installing a glint runs along the filled part: work goes on even when the percentage waits.
            if (state == RingState.Working && sweep > 12)
            {
                float capDeg = (float)(stroke / 2 / radius * 180 / Math.PI);
                const float len = 46;
                float span = sweep + len;
                float head = ((sec * 190) % (span + 120)) - 0;
                float a0 = Math.Max(start + capDeg, start + head - len), a1 = Math.Min(start + sweep - capDeg, start + head);
                if (a1 - a0 > 0.5f)
                {
                    float edge = Math.Min(1, Math.Min(head / 20f, (span - head + 20) / 40f));
                    Color glint = Anim.Blend(accent, Color.White, (p.IsDark ? 0.32f : 0.42f) * Math.Max(0, edge) * centerFade);
                    using (var gp = new Pen(glint, stroke * 0.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(gp, arc, a0, a1 - a0);
                }
            }

            // ----- the middle: old content fades up and out, the new one rises in -----
            if (numFont == null) FitFonts(RingSize);
            int ch = capFont.Height;
            float gh = numFont.Height;
            float blockTop = c.Y - (gh + S(4) + ch) / 2f;
            var pic = new RectangleF(c.X - gh / 2, blockTop, gh, gh);
            float lw = Math.Max(S(3), stroke * 0.62f);
            float shift = S(8);
            // Out, then in (a little overlap): two pictures or numbers never sit on top of each other.
            float outA = 1 - Smooth(0, 0.55f, centerFade), inA = Smooth(0.4f, 1, centerFade);
            if (centerFade < 1 && Kind(prevState) != Kind(state))
                DrawCenter(g, Kind(prevState), OffsetY(pic, -shift * centerFade), outA, bg, inner, lw, sec, prevPop);
            DrawCenter(g, Kind(state), OffsetY(pic, shift * (1 - centerFade)), inA, bg, inner, lw, sec, popIn);

            int innerW = (int)(radius * 2 - stroke * 2);
            var capRect = new Rectangle((Width - innerW) / 2, (int)Math.Round(blockTop + gh + S(4)), innerW, ch);
            if (capFade < 1 && prevCaption.Length > 0)
                TextDraw.DrawText(g, prevCaption, capFont, capRect, Anim.Blend(inner, p.TextDim, 1 - Smooth(0, 0.5f, capFade)), TextCenter | TextFormatFlags.EndEllipsis);
            TextDraw.DrawText(g, caption, capFont, capRect, Anim.Blend(inner, p.TextDim, Smooth(0.5f, 1, capFade)), TextCenter | TextFormatFlags.EndEllipsis);
        }

        static float Smooth(float a, float b, float t)
        {
            float x = Math.Max(0, Math.Min(1, (t - a) / (b - a)));
            return x * x * (3 - 2 * x);
        }

        static RectangleF OffsetY(RectangleF r, float dy)
        {
            return new RectangleF(r.X, r.Y + dy, r.Width, r.Height);
        }

        void DrawCenter(Graphics g, Center kind, RectangleF pic, float alpha, Color bg, Color inner, float lw, float sec, float pop)
        {
            if (alpha <= 0.01f) return;
            var p = Theme.Current;
            Color violet = Color.FromArgb((int)(255 * Math.Min(1, alpha)), Logo.Mark);
            float cx = pic.X + pic.Width / 2, h = pic.Height;
            float lineY = pic.Y + h * 0.9f, lineW = h * 0.62f;
            switch (kind)
            {
                case Center.Logo:
                {
                    // The logo's arrow over its line; the arrow lifts a little when the ring is hovered.
                    float lift = hover.Value * S(3);
                    using (var path = new GraphicsPath())
                    {
                        path.AddLine(cx - lineW / 2, lineY, cx + lineW / 2, lineY);
                        Logo.FillStroke(g, path, violet, lw);
                    }
                    Logo.DrawArrow(g, violet, lw, new PointF(cx, pic.Y + h * 0.66f - lift), new PointF(cx, pic.Y + h * 0.06f - lift), h * 0.3f);
                    break;
                }
                case Center.Rising:
                {
                    // Arrows come up out of the line one after another and fade away near the top.
                    // Each arrow is one shape, so a half-transparent one has no darker joint.
                    using (var path = new GraphicsPath())
                    {
                        path.AddLine(cx - lineW / 2, lineY, cx + lineW / 2, lineY);
                        Logo.FillStroke(g, path, violet, lw);
                    }
                    float winBottom = lineY - lw * 1.1f, winTop = pic.Y;
                    float len = h * 0.5f, travel = winBottom - winTop + len;
                    var st = g.Save();
                    // Only the bottom is clipped (the arrows come out of the line); they are gone before the top.
                    g.SetClip(new RectangleF(pic.X - h, pic.Y - h, h * 3, winBottom - (pic.Y - h)));
                    for (int k = 0; k < 2; k++)
                    {
                        float ph = ((sec / 1.4f) + k * 0.5f) % 1f;
                        float tip = winBottom + len - ph * travel;
                        float a = (ph < 0.55f ? 1 : 1 - Smooth(0.55f, 0.97f, ph)) * alpha;
                        if (a <= 0.01f) continue;
                        Logo.DrawArrow(g, Color.FromArgb((int)(255 * a), Logo.Mark), lw, new PointF(cx, tip + len), new PointF(cx, tip), len * 0.6f);
                    }
                    g.Restore(st);
                    break;
                }
                case Center.Number:
                case Center.Percent:
                {
                    string text = kind == Center.Percent ? (int)Math.Round(progress * 100) + "%" : ((int)Math.Round(shownNumber)).ToString();
                    var r = new Rectangle(0, (int)Math.Round(pic.Y), Width, numFont.Height);
                    TextDraw.DrawText(g, text, numFont, r, Anim.Blend(inner, p.Text, alpha), TextCenter);
                    break;
                }
                case Center.Tick:
                    DrawTick(g, pic, violet, lw * 1.1f, pop);
                    break;
                case Center.Warning:
                    DrawWarning(g, pic, Color.FromArgb((int)(255 * Math.Min(1, alpha)), Anim.Blend(bg, p.Warning, 0.95f)), lw * 0.9f, pop);
                    break;
            }
        }

        /// <summary>A tick drawn on stroke by stroke (t 0..1), growing with a slight overshoot.</summary>
        static void DrawTick(Graphics g, RectangleF r, Color color, float width, float t)
        {
            if (t <= 0.001f) return;
            float s = 0.82f + 0.18f * Back(Math.Min(1, t * 1.4f));
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, w = r.Width * s, h = r.Height * s;
            var a = new PointF(cx - w * 0.38f, cy + h * 0.02f);
            var b = new PointF(cx - w * 0.1f, cy + h * 0.3f);
            var c = new PointF(cx + w * 0.42f, cy - h * 0.3f);
            float t1 = Math.Min(1, t / 0.35f), t2 = Math.Max(0, (t - 0.35f) / 0.65f);
            using (var path = new GraphicsPath())
            {
                var b1 = new PointF(Anim.Lerp(a.X, b.X, Anim.EaseOut(t1)), Anim.Lerp(a.Y, b.Y, Anim.EaseOut(t1)));
                if (t2 <= 0) path.AddLine(a, b1);
                else path.AddLines(new[] { a, b, new PointF(Anim.Lerp(b.X, c.X, Anim.EaseOut(t2)), Anim.Lerp(b.Y, c.Y, Anim.EaseOut(t2))) });
                Logo.FillStroke(g, path, color, width);
            }
        }

        /// <summary>Warning sign in the same line weight: a rounded triangle, a bar and a dot; pops in.</summary>
        static void DrawWarning(Graphics g, RectangleF r, Color color, float width, float t)
        {
            if (t <= 0.001f) return;
            float s = 0.75f + 0.25f * Back(t);
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2 + r.Height * 0.04f;
            float w = r.Width * 0.92f * s, h = r.Height * 0.8f * s;
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(new[] { new PointF(cx, cy - h / 2), new PointF(cx + w / 2, cy + h / 2), new PointF(cx - w / 2, cy + h / 2) });
                path.StartFigure();
                path.AddLine(cx, cy - h * 0.12f, cx, cy + h * 0.16f);
                Logo.FillStroke(g, path, Color.FromArgb((int)(color.A * Math.Min(1, t * 2)), color), width);
            }
            float d = width * 1.1f;
            using (var b = new SolidBrush(Color.FromArgb((int)(color.A * Math.Min(1, t * 2)), color)))
                g.FillEllipse(b, cx - d / 2, cy + h * 0.33f - d / 2, d, d);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeFonts();
            base.Dispose(disposing);
        }
    }
}
