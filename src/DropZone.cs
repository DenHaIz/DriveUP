using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>
    /// "Drop a driver here": dashed rounded area with the logo arrow. Files arrive as WM_DROPFILES,
    /// which (unlike OLE drag and drop) can be let through from Explorer into this elevated window.
    /// Explorer cannot tell an elevated window that a drag is passing over it, so the highlight
    /// watches the mouse instead: left button held over the area while another program is active.
    /// </summary>
    class DropZone : SkinControl, IScalable
    {
        public event Action<string[]> FilesDropped;
        public string Title = "", Subtitle = "", BusyText = "";
        /// <summary>Compact = one row (once there is a list under it); otherwise a tall, centred block.</summary>
        public float Compact;
        bool busy;
        readonly Tweened hover, drag, pulse, busyT;
        readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer { Interval = 50 };
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly object frameKey = new object();
        bool animating;
        float dashPhase;
        double lastPaint;

        [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int DragQueryFileW(IntPtr hDrop, int index, StringBuilder file, int size);
        [DllImport("shell32.dll")] static extern void DragFinish(IntPtr hDrop);
        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, int action, IntPtr info);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

        const int WM_DROPFILES = 0x233, WM_COPYDATA = 0x4A, WM_COPYGLOBALDATA = 0x49, MSGFLT_ALLOW = 1;

        public DropZone()
        {
            hover = new Tweened(Invalidate);
            drag = new Tweened(Invalidate);
            pulse = new Tweened(Invalidate);
            busyT = new Tweened(Invalidate);
            watch.Tick += (s, e) => WatchDrag();
        }

        public void ApplyScale() { Invalidate(); }

        public bool Busy
        {
            get { return busy; }
            set
            {
                if (busy == value) return;
                busy = value;
                busyT.To(value ? 1 : 0, 260);
                Animate();
            }
        }

        /// <summary>A ripple that confirms the drop.</summary>
        public void Pulse()
        {
            pulse.Set(1);
            pulse.To(0, 700);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DragAcceptFiles(Handle, true);
            try
            {
                foreach (int m in new[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
                    ChangeWindowMessageFilterEx(Handle, m, MSGFLT_ALLOW, IntPtr.Zero);
            }
            catch (EntryPointNotFoundException) { }
            SyncWatch();
        }

        Form hooked;

        /// <summary>
        /// The 50 ms mouse watch runs only while the zone is shown and another program is active (a drag
        /// from Explorer); otherwise it would wake DriveUP 20 times a second for its whole life.
        /// </summary>
        void SyncWatch()
        {
            if (IsDisposed) return;
            var f = FindForm();
            if (f != null && f != hooked)
            {
                hooked = f;
                f.Activated += (s, e) => SyncWatch();
                f.Deactivate += (s, e) => SyncWatch();
                f.Resize += (s, e) => SyncWatch();
            }
            bool on = IsHandleCreated && Visible && f != null && f.WindowState != FormWindowState.Minimized && Form.ActiveForm != f;
            watch.Enabled = on || drag.Value > 0.01f;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SyncWatch();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DROPFILES)
            {
                IntPtr hDrop = m.WParam;
                int n = DragQueryFileW(hDrop, -1, null, 0);
                var files = new string[Math.Max(0, n)];
                for (int i = 0; i < n; i++)
                {
                    var sb = new StringBuilder(32768);
                    DragQueryFileW(hDrop, i, sb, sb.Capacity);
                    files[i] = sb.ToString();
                }
                DragFinish(hDrop);
                drag.To(0, 200);
                Pulse();
                if (files.Length > 0 && FilesDropped != null) FilesDropped(files);
                return;
            }
            base.WndProc(ref m);
        }

        void WatchDrag()
        {
            if (!Visible || IsDisposed) return;
            var form = FindForm();
            if (form == null || form.WindowState == FormWindowState.Minimized) return;
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            bool over = RectangleToScreen(ClientRectangle).Contains(Cursor.Position);
            bool dragging = down && over && Form.ActiveForm != form;
            float target = dragging ? 1 : 0;
            if (Math.Abs(drag.Value - target) > 0.01f && !busy) { drag.To(target, dragging ? 160 : 260); Animate(); }
            else if (target == 0 && Form.ActiveForm == form) SyncWatch();
        }

        void Animate()
        {
            if (animating) return;
            animating = true;
            Anim.Frame(frameKey, () =>
            {
                if (IsDisposed) return animating = false;
                if (Visible) Invalidate();
                animating = busy || drag.Value > 0.01f || busyT.Value > 0.01f;
                return animating;
            });
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1, 160); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover.To(0, 260); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            Color bg = ParentBg;
            g.Clear(bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float h = Math.Max(hover.Value * 0.5f, drag.Value), pl = pulse.Value, bt = busyT.Value;
            float sec = clock.ElapsedMilliseconds / 1000f;

            var r = new RectangleF(S(1), S(1), Width - S(2) - 1, Height - S(2) - 1);
            Theme.FillRoundFast(g, Anim.Blend(Anim.Blend(p.Surface, p.Accent, 0.05f * h + 0.08f * pl), bg, 0), r, S(12));
            using (var path = Theme.RoundRect(r, S(12)))
            using (var pen = new Pen(Anim.Blend(Anim.Blend(p.Border, p.TextDim, 0.35f), p.Accent, Math.Max(h, pl)), Math.Max(1.2f, S(2) * (0.75f + 0.25f * h)))
                   { DashStyle = DashStyle.Custom, DashPattern = new[] { 4f, 3f } })
            {
                // The dashes creep along the border while a drag hovers over the area (and simply stop after).
                double now = clock.Elapsed.TotalSeconds;
                dashPhase -= (float)Math.Min(0.1, now - lastPaint) * 6f * drag.Value;
                lastPaint = now;
                pen.DashOffset = dashPhase % 7f;
                g.DrawPath(pen, path);
            }

            // ----- layout: everything glides between the tall (centred) and the compact (one row) look -----
            float k = Math.Max(0, Math.Min(1, Compact));   // already eased by the page
            var L0 = Tall();
            float tile = Anim.Lerp(S(76), S(52), k);
            var tr = new RectangleF(Anim.Lerp(L0.TileX, S(22), k), Anim.Lerp(L0.TileY, (Height - tile) / 2f, k), tile, tile);
            string title = busy ? BusyText : Title;
            int titleW = MeasureWidth(title, Theme.HeadFont), subW = MeasureWidth(Subtitle, Font);
            float compactX = tr.Right + S(16);
            float tx = Anim.Lerp((Width - titleW) / 2f, compactX, k), sx = Anim.Lerp((Width - subW) / 2f, compactX, k);
            float ty = Anim.Lerp(L0.TextY, Height / 2f - S(24), k), sy = Anim.Lerp(L0.TextY + S(26), Height / 2f + S(1), k);
            float right = Anim.Lerp(Width - S(16), Width - ButtonsWidth - S(24), k);

            // icon tile: the logo's arrow over its line (the arrow bobs while a file is dragged over), or a spinner while busy
            Theme.FillRound(g, Anim.Blend(p.SurfaceAlt, p.Accent, 0.14f + 0.12f * h), tr, tile * 0.24f);
            var c = new PointF(tr.X + tile / 2, tr.Y + tile / 2);
            float lw = Math.Max(S(2), tile * 0.075f);
            if (bt > 0.01f)
            {
                using (var pen = new Pen(Color.FromArgb((int)(255 * bt), Logo.Mark), lw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    float rr = tile * 0.26f;
                    g.DrawArc(pen, c.X - rr, c.Y - rr, rr * 2, rr * 2, (sec * 320) % 360, 250);
                }
            }
            if (bt < 0.99f)
            {
                float bob = (float)Math.Sin(sec * 6) * S(3) * drag.Value - S(3) * pl;
                float ah = tile * 0.5f;
                Color ac = Color.FromArgb((int)(255 * (1 - bt)), Logo.Mark);
                using (var path = new GraphicsPath())
                {
                    path.AddLine(c.X - ah * 0.55f, c.Y + ah * 0.55f, c.X + ah * 0.55f, c.Y + ah * 0.55f);
                    Logo.FillStroke(g, path, ac, lw);
                }
                Logo.DrawArrow(g, ac, lw, new PointF(c.X, c.Y + ah * 0.22f + bob), new PointF(c.X, c.Y - ah * 0.55f + bob), ah * 0.4f);
            }

            TextDraw.DrawText(g, title, Theme.HeadFont, new RectangleF(tx, ty, Math.Max(S(40), (int)(right - tx)), S(24)), p.Text, TextLeft);
            TextDraw.DrawText(g, Subtitle, Font, new RectangleF(sx, sy, Math.Max(S(40), (int)(right - sx)), S(22)), p.TextDim, TextLeft);
        }

        struct TallLayout { public float TileX, TileY, TextY, ButtonsY; }

        /// <summary>The tall look: tile, two lines and the buttons as one block in the middle.</summary>
        TallLayout Tall()
        {
            float tile = S(76), textH = S(48);
            float block = tile + S(14) + textH + S(14) + S(36);
            float top = Math.Max(S(12), (Height - block) / 2f);
            return new TallLayout
            {
                TileX = (Width - tile) / 2f, TileY = top, TextY = top + tile + S(14), ButtonsY = top + tile + S(14) + textH + S(14),
            };
        }

        /// <summary>Room the page's buttons take on the right in compact mode.</summary>
        public int ButtonsWidth;

        /// <summary>Where the page puts its buttons: glides from under the text (tall) to the right (compact).</summary>
        public Point ButtonsOrigin(int buttonsWidth, int buttonHeight)
        {
            ButtonsWidth = buttonsWidth;
            float k = Math.Max(0, Math.Min(1, Compact));   // already eased by the page
            var t = Tall();
            return new Point((int)Math.Round(Anim.Lerp((Width - buttonsWidth) / 2f, Width - buttonsWidth - S(20), k)),
                             (int)Math.Round(Anim.Lerp(t.ButtonsY, (Height - buttonHeight) / 2f, k)));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) watch.Dispose();
            base.Dispose(disposing);
        }
    }
}
