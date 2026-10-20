using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>Themed secondary window: fades and slides in, footer with buttons, dark/light title bar.</summary>
    class ThemedWindow : Form
    {
        protected readonly Panel Footer = new BufferedPanel();
        protected readonly Panel Body = new BufferedPanel();
        readonly List<FlatButton> buttons = new List<FlatButton>();
        bool closingDone, closing;
        DialogResult pending = DialogResult.Cancel;
        int finalTop;

        protected static int S(int v) { return Theme.S(v); }

        public ThemedWindow(string title, Size client)
        {
            var p = Theme.Current;
            Text = title;
            Font = Theme.UiFont;
            BackColor = p.Bg;
            ForeColor = p.Text;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Icon = Logo.AppIcon;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Opacity = 0;
            ClientSize = client;
            MinimumSize = new Size(S(520), S(360));

            Footer.Dock = DockStyle.Bottom;
            Footer.Height = S(64);
            Footer.BackColor = p.Surface;
            Footer.Paint += (s, e) =>
            {
                using (var pen = new Pen(Theme.Current.Border)) e.Graphics.DrawLine(pen, 0, 0, Footer.Width, 0);
            };
            Footer.Resize += (s, e) => LayoutButtons();
            Body.Dock = DockStyle.Fill;
            Body.BackColor = p.Bg;
            Body.Padding = new Padding(S(16), S(16), S(16), S(16));
            Controls.Add(Body);
            Controls.Add(Footer);
        }

        protected FlatButton AddButton(string text, string glyph, ButtonKind kind, DialogResult result)
        {
            var b = new FlatButton(text, glyph, kind);
            b.Width = Math.Max(b.PreferredWidth, S(110));
            b.Click += (s, e) => Finish(result);
            buttons.Add(b);
            Footer.Controls.Add(b);
            LayoutButtons();
            return b;
        }

        void LayoutButtons()
        {
            int x = Footer.Width - S(24);
            foreach (var b in buttons)
            {
                b.Location = new Point(x - b.Width, (Footer.Height - b.Height) / 2);
                x = b.Left - S(8);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            finalTop = Top;
            int offset = S(14);
            WindowFx.PaintNow(this);
            Anim.Run(this, 220, t =>
            {
                Opacity = t;
                Top = finalTop + (int)((1 - t) * offset);
            }, () => WindowFx.EndFade(this));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Finish(DialogResult.Cancel);
        }

        protected void Finish(DialogResult result)
        {
            if (closing) return;
            closing = true;
            pending = result;
            float from = (float)Opacity;
            Anim.Run(this, 150, t => Opacity = from * (1 - t), () =>
            {
                closingDone = true;
                DialogResult = pending;
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!closingDone)
            {
                e.Cancel = true;
                Finish(DialogResult.Cancel);
                return;
            }
            base.OnFormClosing(e);
        }
    }

    /// <summary>Simple row for lists that only show text.</summary>
    class TextRow : IRowInfo
    {
        public RowKind Kind;
        public string Icon;
        public RowKind RowKind { get { return Kind; } }
        public string IconPath { get { return Icon; } }
        public int Dot { get { return 0; } }
    }
}
