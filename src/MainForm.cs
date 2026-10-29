using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DriveUp
{
    class MainForm : Form, IHost
    {
        public static readonly int ShowMessage = RegisterWindowMessage(Program.Instance + ".Show");

        readonly Panel root = new BufferedPanel(), header = new BufferedPanel(), content = new BufferedPanel();
        readonly Panel pageHost = new BufferedPanel(), footer = new BufferedPanel();
        readonly TabBar tabBar = new TabBar();
        readonly Segmented themeSwitch;
        readonly FlatButton btnLang, btnJournal;
        readonly ToolTip tip = new ToolTip();
        readonly BusyRing busyRing = new BusyRing();
        readonly StatusText status = new StatusText();
        readonly Toast toast = new Toast();
        readonly UpdatesPage updatesPage;
        readonly DevicesPage devicesPage;
        readonly InstallPage installPage;
        readonly BackupPage backupPage;
        readonly TunePage tunePage;
        readonly Page[] pages;
        int currentTab, busy;
        int titleWidth;
        string[] themeTexts = { "", "" };
        bool compactTheme, instantTab;
        /// <summary>Closing was asked for while Windows Update installed: the window closes once it has stopped.</summary>
        bool closing;
        public int StartTab;
        public string[] StartPaths;

        static int S(int v) { return Theme.S(v); }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string name);

        public MainForm()
        {
            Font = Theme.UiFont;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(1180), S(720));
            MinimumSize = new Size(S(980), S(620));
            KeyPreview = true;
            DoubleBuffered = true;
            Icon = Logo.AppIcon;
            Opacity = 0;

            // ----- header: brand, tabs, language and theme switches -----
            header.Dock = DockStyle.Top;
            header.Height = S(60);
            header.Paint += PaintHeader;
            tabBar.Height = header.Height;
            tabBar.AddTab("", "\uE895");
            tabBar.AddTab("", "\uE7F8");
            tabBar.AddTab("", "\uE896");
            tabBar.AddTab("", "\uE74E");
            tabBar.AddTab("", "\uEC4A");
            tabBar.SelectedIndexChanged += (s, e) => OnTabChanged();
            themeSwitch = new Segmented(new[] { "", "" }, new[] { "\uE708", "\uE706" }, Theme.Current.IsDark ? 0 : 1);
            themeSwitch.SelectedIndexChanged += (s, e) => OnThemeChanged();
            btnLang = new FlatButton("", "\uE774");
            btnLang.Click += (s, e) => OnLanguageClick();
            header.Controls.Add(tabBar);
            header.Controls.Add(btnLang);
            header.Controls.Add(themeSwitch);
            header.Resize += (s, e) => LayoutHeader();

            // ----- pages: not docked (docking skips hidden controls); sized by hand -----
            updatesPage = new UpdatesPage(this);
            devicesPage = new DevicesPage(this);
            installPage = new InstallPage(this);
            backupPage = new BackupPage(this);
            tunePage = new TunePage(this);
            pages = new Page[] { updatesPage, devicesPage, installPage, backupPage, tunePage };
            foreach (var p in pages)
            {
                p.Visible = p == updatesPage;
                pageHost.Controls.Add(p);
            }
            pageHost.Dock = DockStyle.Fill;
            pageHost.Resize += (s, e) =>
            {
                foreach (var p in pages) p.Bounds = pageHost.ClientRectangle;
            };
            content.Dock = DockStyle.Fill;
            content.Controls.Add(pageHost);

            // ----- footer -----
            footer.Dock = DockStyle.Bottom;
            footer.Height = S(40);
            status.SetBounds(S(18), 0, S(700), footer.Height);
            btnJournal = new FlatButton("", "\uE81C", ButtonKind.Ghost);
            btnJournal.SetBaseHeight(32);
            btnJournal.Click += (s, e) => JournalForm.ShowFor(this);
            footer.Controls.Add(status);
            footer.Controls.Add(btnJournal);
            busyRing.Size = new Size(S(18), S(18));
            footer.Controls.Add(busyRing);
            footer.Resize += (s, e) => LayoutFooter();

            // Docking: later-added controls are docked first (outermost).
            root.Dock = DockStyle.Fill;
            root.Controls.Add(content);
            root.Controls.Add(footer);
            root.Controls.Add(header);
            root.Controls.Add(toast);
            root.Resize += (s, e) => toast.Reposition(ToastAnchor());
            Controls.Add(root);

            ApplyTheme();
            ApplyLanguage();
        }

        public Form Window { get { return this; } }

        void PaintHeader(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int size = S(36);
            Logo.DrawSharp(g, S(18), (header.Height - size) / 2, size);
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            TextDraw.DrawText(g, "DriveUP", Theme.TitleFont, new Rectangle(S(64), 0, titleWidth, header.Height), Theme.Current.Text, flags);
        }

        // ---------- language ----------

        static void SetButton(FlatButton b, string text)
        {
            b.Text = text;
            b.Width = b.PreferredWidth;
        }

        void ApplyLanguage()
        {
            Text = L.T("DriveUP — драйверы и настройка системы", "DriveUP — drivers and system tuning");
            tabBar.SetTabText(0, L.T("Обновления", "Updates"));
            tabBar.SetTabText(1, L.T("Устройства", "Devices"));
            tabBar.SetTabText(2, L.T("Установка", "Install"));
            tabBar.SetTabText(3, L.T("Резервные копии", "Backups"));
            tabBar.SetTabText(4, L.T("Оптимизация", "Tuning"));
            themeTexts = new[] { L.T("Тёмная", "Dark"), L.T("Светлая", "Light") };
            themeSwitch.SetTexts(themeTexts);
            compactTheme = false;
            tip.SetToolTip(themeSwitch, L.T("Тема оформления", "Theme"));
            SetButton(btnLang, L.T("RU", "EN"));
            tip.SetToolTip(btnLang, L.T("Язык интерфейса: русский. Switch to English", "Interface language: English. Переключить на русский"));
            SetButton(btnJournal, L.T("Журнал", "Journal"));
            tip.SetToolTip(btnJournal, L.T("Что DriveUP устанавливал, сохранял и удалял", "What DriveUP installed, saved and removed"));
            foreach (var p in pages) p.ApplyLanguage();
            LayoutHeader();
            LayoutFooter();
            UpdateStatus();
        }

        void OnLanguageClick()
        {
            Transition.CrossFade(root, () =>
            {
                L.En = !L.En;
                L.Save();
                ApplyLanguage();
            }, 340);
        }

        // ---------- theme ----------

        void ApplyTheme()
        {
            var p = Theme.Current;
            BackColor = p.Bg;
            foreach (Control c in new Control[] { root, header, content, pageHost, footer })
                c.BackColor = p.Bg;
            foreach (var page in pages) page.ApplyTheme();
            if (IsHandleCreated) Theme.ApplyTitleBar(Handle);
            root.Invalidate(true);
        }

        void OnThemeChanged()
        {
            // Let the switch finish sliding, then cross-fade the whole window into the new palette.
            Anim.Delay(280, () => Transition.CrossFade(root, () =>
            {
                Theme.Current = themeSwitch.SelectedIndex == 0 ? Theme.Dark : Theme.Light;
                Theme.Save();
                ApplyTheme();
            }, 380));
        }

        // ---------- window lifetime ----------

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Create every page's window now: background loads post back to them even while hidden.
            foreach (var p in pages) p.CreateControl();
            foreach (var p in pages)
            {
                IntPtr unused = p.Handle;
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
            if (StartTab > 0)
            {
                // Opened on another tab (/tab:N): switch before the window appears, no transition.
                instantTab = true;
                tabBar.SelectedIndex = StartTab;
                instantTab = false;
            }
            // The first tab starts its work once the window is in: results landing mid-fade made it stutter.
            FadeIn(() =>
            {
                pages[currentTab].Activate();
                if (StartPaths != null && StartPaths.Length > 0) InstallFrom(StartPaths);
            });
            // The other tabs load quietly once the window has settled, so they open ready.
            Anim.Delay(1500, () => devicesPage.Preload());
            Anim.Delay(2600, () => backupPage.Preload());
        }

        void FadeIn(Action done = null)
        {
            // The first opacity change composes the layered window once, which is slow: pay for it
            // (and for the first paint) before the clock starts.
            Opacity = 0.01;
            WindowFx.PaintNow(this);
            Anim.Run(this, 280, t => Opacity = 0.01 + 0.99 * t, () =>
            {
                WindowFx.EndFade(this);
                if (done != null) done();
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && OperationRunning && updatesPage.Installing && !installPage.Installing && !backupPage.Working
                && !tunePage.Working
                )
            {
                // Windows Update can be asked to stop (e.g. when it hangs): then the window closes by itself.
                e.Cancel = true;
                if (closing) return;
                if (ThemedDialog.Confirm(this, L.T("Прервать установку?", "Stop the install?"),
                        L.T("Windows Update ещё скачивает или устанавливает драйверы. Прервать и закрыть окно?\nДрайвер, который ставится прямо сейчас, Windows доустановит или откатит сама.",
                            "Windows Update is still downloading or installing drivers. Stop and close the window?\nThe driver being installed right now is finished or rolled back by Windows."),
                        L.T("Прервать и закрыть", "Stop and close"), "\uE711", true))
                {
                    closing = true;
                    WinUpdate.RequestAbort();
                    ShowToast(L.T("Останавливаю Windows Update…", "Stopping Windows Update…"), ToastKind.Info);
                    Anim.Frame(this, () =>
                    {
                        if (IsDisposed) return false;
                        if (OperationRunning) return true;
                        BeginInvoke((MethodInvoker)Close);
                        return false;
                    });
                }
                return;
            }
            if (e.CloseReason == CloseReason.UserClosing && OperationRunning)
            {
                // Installs and removals run inside DriveUP: closing now would cut them off half-way.
                e.Cancel = true;
                ThemedDialog.Info(this, L.T("Подождите немного", "Please wait"),
                    L.T("DriveUP сейчас устанавливает драйверы или меняет настройки. Если закрыть окно, работа прервётся на середине.\nОкно можно будет закрыть, как только она закончится.",
                        "DriveUP is installing drivers or changing settings right now. Closing the window would cut that off half-way.\nYou can close it as soon as it is done."), false);
                return;
            }
            base.OnFormClosing(e);
        }

        /// <summary>An install, a removal or a backup is running (one at a time, and no closing meanwhile).</summary>
        bool OperationRunning
        {
            get
            {
                if (tunePage.Working) return true;
                return updatesPage.Installing || installPage.Installing || backupPage.Working;
            }
        }

        public bool CanStartOperation()
        {
            if (!OperationRunning) return true;
            ShowToast(L.T("Дождитесь окончания текущей операции", "Wait until the current operation finishes"), ToastKind.Info);
            return false;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            InfFiles.CleanTemp();
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x4A /* WM_COPYDATA */)
            {
                // Paths from a second start of DriveUP. Same rights only: UIPI keeps lower ones out of this window.
                try
                {
                    IntPtr tag = Marshal.ReadIntPtr(m.LParam);
                    int bytes = Marshal.ReadInt32(m.LParam, IntPtr.Size);
                    IntPtr data = Marshal.ReadIntPtr(m.LParam, IntPtr.Size * 2);
                    if (tag == (IntPtr)Program.PathsTag && bytes > 2 && bytes < (1 << 20) && data != IntPtr.Zero)
                    {
                        string text = Marshal.PtrToStringUni(data, bytes / 2 - 1);
                        var paths = text.Split('\n').Where(x => File.Exists(x) || Directory.Exists(x)).ToArray();
                        if (paths.Length > 0)
                            BeginInvoke((MethodInvoker)delegate
                            {
                                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                                Activate();
                                InstallFrom(paths);
                            });
                        m.Result = (IntPtr)1;
                        return;
                    }
                }
                catch { }
            }
            if (m.Msg == ShowMessage && ShowMessage != 0)
            {
                // wParam 1..4 also opens that tab (DriveUP.exe /tab:N on a running instance).
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                int tab = m.WParam.ToInt32();
                if (tab >= 1 && tab <= pages.Length) tabBar.SelectedIndex = tab - 1;
                return;
            }
            base.WndProc(ref m);
        }

        // ---------- layout ----------

        /// <summary>
        /// The interface grows with the window: zoom 1 at the default size, up to Theme.MaxZoom
        /// (a maximized window on a 1080p screen gets about 1.4). Steps of 0.1 with a little
        /// hysteresis, so dragging the border does not flip back and forth between two steps.
        /// </summary>
        float ZoomFor(Size client)
        {
            float raw = Math.Min(client.Width / (1180f * Theme.Dpi), client.Height / (720f * Theme.Dpi));
            float step = (float)Math.Floor(raw * 10 + 0.0001f) / 10;
            step = Math.Max(1f, Math.Min(Theme.MaxZoom, step));
            float now = Theme.Zoom;
            if (step > now && raw < step + 0.02f && step - now < 0.11f) return now;
            if (step < now && raw > now - 0.03f) return now;
            return step;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized || ClientSize.Width < 10 || pages == null) return;
            float z = ZoomFor(ClientSize);
            if (Math.Abs(z - Theme.Zoom) > 0.001f) ApplyZoom(z);
        }

        /// <summary>Switches every control to the new zoom: fonts, sizes, then the layout.</summary>
        void ApplyZoom(float zoom)
        {
            if (!Theme.SetZoom(zoom)) return;
            // Running transitions hold pictures of the old layout.
            Transition.FinishAll();
            root.SuspendLayout();
            Rescale(root);
            header.Height = S(60);
            tabBar.Height = header.Height;
            busyRing.Size = new Size(S(18), S(18));
            footer.Height = S(40);
            root.ResumeLayout(true);
            foreach (var p in pages) p.Bounds = pageHost.ClientRectangle;
            LayoutHeader();
            LayoutFooter();
            foreach (var p in pages) p.ApplyScale();
            toast.Reposition(ToastAnchor());
            root.Invalidate(true);
        }

        static void Rescale(Control c)
        {
            foreach (Control child in c.Controls) Rescale(child);
            var f = Theme.Remap(c.Font);
            if (f != c.Font) c.Font = f;
            var s = c as IScalable;
            if (s != null && !(c is Page)) s.ApplyScale();
        }

        void LayoutHeader()
        {
            titleWidth = TextDraw.MeasureText("DriveUP", Theme.TitleFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(4);
            tabBar.Location = new Point(S(64) + titleWidth + S(28), 0);
            // Narrow window: the theme switch keeps only its icons so nothing overlaps the tabs.
            bool compact = header.Width < tabBar.Right + S(24) + btnLang.Width + S(8) + themeSwitch.Width + S(16) + (compactTheme ? S(200) : 0);
            if (compact != compactTheme)
            {
                compactTheme = compact;
                themeSwitch.SetTexts(compact ? new[] { "", "" } : themeTexts);
            }
            int y = (header.Height - themeSwitch.Height) / 2;
            themeSwitch.Location = new Point(header.Width - themeSwitch.Width - S(16), y);
            btnLang.Location = new Point(themeSwitch.Left - btnLang.Width - S(8), y);
            header.Invalidate();
        }

        void LayoutFooter()
        {
            status.SetBounds(S(18), 0, status.Width, footer.Height);
            btnJournal.Location = new Point(footer.Width - btnJournal.Width - S(16), (footer.Height - btnJournal.Height) / 2);
            // The busy ring sits just left of the Journal button: nothing else moves when it appears.
            busyRing.Location = new Point(btnJournal.Left - S(10) - busyRing.Width, (footer.Height - busyRing.Height) / 2);
            status.Width = Math.Max(S(100), busyRing.Left - status.Left - S(12));
        }

        Point ToastAnchor()
        {
            return new Point(root.ClientSize.Width - S(28), footer.Top - S(14));
        }

        public void ShowToast(string text, ToastKind kind)
        {
            toast.Show(text, kind, ToastAnchor());
        }

        public void UpdateStatus()
        {
            if (pages == null) return;
            status.SetText(pages[tabBar.SelectedIndex].Status);
        }

        public void BeginBusy()
        {
            if (busy++ == 0) busyRing.Start();
        }

        public void EndBusy()
        {
            if (busy > 0 && --busy == 0) busyRing.Stop();
        }

        public void ShowDevices(bool attention)
        {
            OpenTab(1);
            if (attention) Anim.Delay(280, () => devicesPage.ShowAttention());
        }

        public void InstallFrom(string[] paths)
        {
            OpenTab(2);
            Anim.Delay(280, () => installPage.AddPaths(paths));
        }

        public void OpenTab(int index)
        {
            if (tabBar.SelectedIndex != index) tabBar.SelectedIndex = index;
        }

        // ---------- tabs ----------

        void OnTabChanged()
        {
            int idx = tabBar.SelectedIndex;
            int dir = idx > currentTab ? 1 : -1;
            currentTab = idx;
            Action show = () =>
            {
                for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == idx;
            };
            if (instantTab) show();
            // A little longer and a shorter slide, settling gently (quartic ease-out): calmer than before.
            // The tab underline (TabBar) slides for the same 320 ms.
            else Transition.CrossFade(pageHost, show, 320, dir * S(28), t => { float u = 1 - t; return 1 - u * u * u * u; });
            pages[idx].Activate();
            UpdateStatus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.F5) { pages[currentTab].RefreshData(); e.Handled = true; }
            else if (e.KeyCode == Keys.F && e.Control) { pages[currentTab].FocusSearch(); e.Handled = true; }
            else if (e.KeyCode == Keys.O && e.Control) { OpenTab(2); e.Handled = true; }
            else if (e.Control && e.KeyCode >= Keys.D1 && e.KeyCode < Keys.D1 + pages.Length)
            {
                tabBar.SelectedIndex = e.KeyCode - Keys.D1;
                e.Handled = true;
            }
        }
    }
}
