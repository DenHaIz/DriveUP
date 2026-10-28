using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>
    /// Main tab: the big ring (one-button check and install), the updates Windows Update offers,
    /// grouped by kind of device, and a small card with the two settings that matter.
    /// </summary>
    class UpdatesPage : Page
    {
        readonly UpdateRing ring = new UpdateRing();
        readonly FlatButton btnMain, btnRecheck, btnShowProblems;
        readonly StatusText hint = new StatusText { Flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis };
        readonly Card listCard = new Card(), optCard = new Card();
        readonly CategoryList list = new CategoryList();
        readonly ToggleSwitch swRestore = new ToggleSwitch(""), swOnStart = new ToggleSwitch("");
        readonly StatusText info = new StatusText();
        readonly StatusText attention = new StatusText { Flags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding };
        readonly ToolTip tip = new ToolTip();
        readonly System.Windows.Forms.Timer stageTimer = new System.Windows.Forms.Timer { Interval = 3800 };

        List<CatGroup> cats = new List<CatGroup>();
        // Link rows for newer drivers on vendors' sites, shown under the installable ones.
        List<CatGroup> vendorCats = new List<CatGroup>();
        bool scanning, working, checkedOnce, failedCheck;
        int stage, installedCount, attemptedCount;
        string error, retryNote;
        UpdateResult lastResult;

        public UpdatesPage(IHost host) : base(host)
        {
            ring.Click += (s, e) => { if (ring.Clickable) MainAction(); };
            btnMain = new FlatButton("", "\uE895", ButtonKind.Primary);
            btnMain.SetBaseHeight(42);
            btnMain.Font = Theme.UiBold;
            btnMain.Click += (s, e) => MainAction();
            Controls.Add(ring);
            Controls.Add(btnMain);
            Controls.Add(hint);

            btnRecheck = new FlatButton("", "\uE72C", ButtonKind.Ghost);
            btnRecheck.Click += (s, e) => StartCheck();
            listCard.TitleBase = 52;
            list.CheckedChanged += (s, e) => OnChecksChanged();
            listCard.Controls.Add(list);
            listCard.Controls.Add(btnRecheck);
            listCard.Resize += (s, e) => LayoutListCard();
            Controls.Add(listCard);

            swRestore.Checked = Settings.RestorePoint;
            swOnStart.Checked = Settings.CheckOnStart;
            swRestore.CheckedChanged += (s, e) => { Settings.RestorePoint = swRestore.Checked; Settings.Save(); };
            swOnStart.CheckedChanged += (s, e) => { Settings.CheckOnStart = swOnStart.Checked; Settings.Save(); };
            btnShowProblems = new FlatButton("", "\uE7BA", ButtonKind.Ghost);
            btnShowProblems.SetBaseHeight(30);
            btnShowProblems.Click += (s, e) => Host.ShowDevices(true);
            btnShowProblems.Visible = false;
            foreach (Control c in new Control[] { swRestore, swOnStart, info, attention, btnShowProblems }) optCard.Controls.Add(c);
            optCard.Resize += (s, e) => LayoutOptCard();
            Controls.Add(optCard);

            stageTimer.Tick += (s, e) => { stage++; UpdateHint(); };
            Inventory.Changed += () =>
            {
                if (IsDisposed) return;
                UpdateInfo();
                Host.UpdateStatus();
            };
            Resize += (s, e) => LayoutPage();
        }

        // ---------- layout ----------

        void LayoutPage()
        {
            // Same proportions as Wisp: the left column takes ~38 % and the ring fills it.
            int pad = S(16);
            int leftW = Math.Max(S(320), Math.Min(S(640), (int)(Width * 0.38)));
            btnMain.SetBaseHeight(Height > S(760) ? 46 : 42);
            int below = S(16) + btnMain.Height + S(10) + S(24);
            int ringSize = Math.Max(S(160), Math.Min(S(600), Math.Min(leftW, Height - pad * 2 - below - S(20))));
            int top = Math.Max(pad, (Height - ringSize - below) / 2);
            ring.SetBounds(pad + (leftW - ringSize) / 2, top, ringSize, ringSize);
            btnMain.Top = ring.Bottom + S(16);
            PlaceMainButton(false);
            hint.SetBounds(pad, btnMain.Bottom + S(10), leftW, S(24));

            int x = pad + leftW + S(20);
            int w = Math.Max(S(300), Width - x - pad);
            int optH = S(108);
            optCard.SetBounds(x, Height - pad - optH, w, optH);
            listCard.SetBounds(x, pad, w, Math.Max(S(120), optCard.Top - S(12) - pad));
            LayoutOptCard();
        }

        readonly object mainKey = new object();

        /// <summary>Centres the main button under the ring; a new caption changes its width smoothly.</summary>
        void PlaceMainButton(bool animate)
        {
            int w = MainButtonWidth(), x = ring.Left + (ring.Width - w) / 2;
            if (!animate || !Visible || (w == btnMain.Width && x == btnMain.Left))
            {
                Anim.Cancel(mainKey);
                btnMain.SetBounds(x, btnMain.Top, w, btnMain.Height);
                return;
            }
            int w0 = btnMain.Width, x0 = btnMain.Left;
            Anim.Run(mainKey, 240, t => btnMain.SetBounds((int)Math.Round(Anim.Lerp(x0, x, t)), btnMain.Top, (int)Math.Round(Anim.Lerp(w0, w, t)), btnMain.Height));
        }

        int MainButtonWidth()
        {
            return Math.Max(Math.Max(S(220), (int)(ring.Width * 0.55f)), btnMain.PreferredWidth + S(24));
        }

        void LayoutListCard()
        {
            btnRecheck.SetBaseHeight(30);
            btnRecheck.Location = new Point(listCard.Width - btnRecheck.Width - S(12), (listCard.TitleHeight - btnRecheck.Height) / 2);
            list.SetBounds(S(2), listCard.TitleHeight, listCard.Width - S(4), listCard.Height - listCard.TitleHeight - S(6));
        }

        void LayoutOptCard()
        {
            int x = S(18);
            swRestore.Location = new Point(x, S(12));
            swOnStart.Location = new Point(Math.Max(swRestore.Right + S(24), Math.Min(optCard.Width - swOnStart.Width - S(18), x + S(420))), S(12));
            int y = S(58);
            int right = optCard.Width - S(14);
            if (btnShowProblems.Visible)
            {
                btnShowProblems.Location = new Point(right - btnShowProblems.Width, y + (S(32) - btnShowProblems.Height) / 2);
                right = btnShowProblems.Left - S(4);
            }
            int aw = attention.Text.Length > 0 ? TextRenderer.MeasureText(attention.Text, attention.Font, Size.Empty, TextFormatFlags.NoPadding).Width + S(4) : 0;
            attention.SetBounds(right - aw, y, aw, S(32));
            right -= aw + S(12);
            info.SetBounds(x + S(2), y, Math.Max(S(60), right - x), S(32));
        }

        public override void ApplyScale()
        {
            LayoutOptCard();
            LayoutListCard();
            LayoutPage();
            ring.Invalidate();
        }

        // ---------- language / theme ----------

        public override void ApplyLanguage()
        {
            listCard.Title = L.T("Доступные обновления", "Available updates");
            SetButton(btnRecheck, L.T("Проверить снова", "Check again"));
            swRestore.Text = L.T("Точка восстановления перед установкой", "Restore point before installing");
            swOnStart.Text = L.T("Проверять при запуске", "Check on start");
            SetButton(btnShowProblems, L.T("Показать", "Show"));
            tip.SetToolTip(swRestore, L.T("Если после обновления что-то пойдёт не так, Windows можно будет вернуть к этой точке.\n" +
                "В Windows Server восстановления системы нет — там шаг пропускается.",
                "If something goes wrong after an update, Windows can be rolled back to this point.\n" +
                "Windows Server has no System Restore — the step is skipped there."));
            tip.SetToolTip(swOnStart, L.T("Искать новые драйверы сразу при открытии DriveUP", "Look for new drivers as soon as DriveUP opens"));
            tip.SetToolTip(btnRecheck, L.T("Спросить Windows Update ещё раз", "Ask Windows Update again"));
            UpdateTexts();
            UpdateInfo();
            UpdateEmpty();
            LayoutOptCard();
            LayoutListCard();
            LayoutPage();
            list.Invalidate();
            listCard.Invalidate();
        }

        public override void ApplyTheme()
        {
            base.ApplyTheme();
            list.Invalidate();
            ring.Invalidate();
        }

        // ---------- texts ----------

        int Available { get { return cats.Sum(g => g.Items.Count(i => !i.Disabled)); } }
        int Selected { get { return cats.Sum(g => g.Items.Count(i => i.Checked && !i.Disabled)); } }

        void UpdateTexts()
        {
            int sel = Selected;
            switch (ring.State)
            {
                case RingState.Idle:
                    ring.Caption = L.T("нажмите, чтобы проверить", "click to check");
                    break;
                case RingState.Scanning:
                    ring.Caption = L.T("поиск обновлений…", "looking for updates…");
                    break;
                case RingState.Ready:
                    ring.Caption = L.Word(Available, "обновление", "обновления", "обновлений", "update", "updates") + L.T(" драйверов", " available");
                    break;
                case RingState.UpToDate:
                    ring.Caption = VendorCount > 0 ? L.T("в Windows Update новее нет", "nothing newer in Windows Update")
                                                   : L.T("все драйверы актуальны", "all drivers are up to date");
                    break;
                case RingState.Done:
                    ring.Caption = attemptedCount > installedCount
                        ? string.Format(L.T("установлено {0} из {1}", "{0} of {1} installed"), installedCount, attemptedCount)
                        : L.T("установлено: ", "installed: ") + installedCount;
                    break;
                case RingState.Error:
                    ring.Caption = failedCheck ? L.T("не удалось проверить", "check failed") : L.T("не удалось установить", "installation failed");
                    break;
            }

            if (working) { btnMain.Text = L.T("Установка…", "Installing…"); btnMain.Glyph = "\uE896"; }
            else if (scanning) { btnMain.Text = L.T("Проверка…", "Checking…"); btnMain.Glyph = "\uE895"; }
            else if (ring.State == RingState.Ready)
            {
                btnMain.Text = sel > 0 ? L.T("Установить", "Install") + " (" + sel + ")" : L.T("Отметьте обновления", "Tick updates to install");
                btnMain.Glyph = "\uE896";
            }
            else
            {
                btnMain.Text = checkedOnce ? L.T("Проверить снова", "Check again") : L.T("Проверить обновления", "Check for updates");
                btnMain.Glyph = "\uE895";
            }
            PlaceMainButton(true);
            btnMain.Enabled = !working && !scanning && (ring.State != RingState.Ready || sel > 0);
            btnRecheck.Enabled = !working && !scanning;
            swRestore.Enabled = !working;
            bool showRecheck = cats.Count > 0 && ring.State != RingState.Idle;
            if (btnRecheck.Visible != showRecheck)
            {
                btnRecheck.Visible = showRecheck;
                if (showRecheck) btnRecheck.Appear();
            }
            ring.Clickable = btnMain.Enabled;
            ring.Invalidate();
            UpdateHint();
        }

        void UpdateHint()
        {
            if (scanning)
            {
                string[] stages =
                {
                    L.T("Подключаемся к Windows Update…", "Connecting to Windows Update…"),
                    L.T("Сверяем устройства этого компьютера…", "Matching the devices of this PC…"),
                    L.T("Ищем новые драйверы в каталоге Microsoft…", "Searching the Microsoft catalog…"),
                    L.T("Сверяем версии на сайтах производителей…", "Comparing with vendors' sites…"),
                    L.T("Это может занять пару минут…", "This may take a couple of minutes…"),
                };
                hint.SetText(stages[Math.Min(stage, stages.Length - 1)]);
            }
            else if (working) { }
            else if (ring.State == RingState.Error) hint.SetText(error ?? "");
            else if (ring.State == RingState.Ready)
                hint.SetText(retryNote ?? L.T("Во время установки экран может мигнуть", "The screen may flicker while installing"));
            else if (ring.State == RingState.Done && lastResult != null && lastResult.RebootRequired)
                hint.SetText(L.T("Изменения вступят в силу после перезагрузки", "Changes take effect after a restart"));
            else if (ring.State == RingState.UpToDate)
                hint.SetText(VendorCount > 0
                    ? L.T("На сайтах производителей есть новее: ", "Newer on vendors' sites: ") + VendorCount + L.T(" — в списке справа", " — see the list")
                    : L.T("Новее нет ни в Windows Update, ни на сайтах производителей", "Nothing newer in Windows Update or on vendors' sites"));
            else if (ring.State == RingState.Idle)
                hint.SetText(L.T("Драйверы — из официального каталога Microsoft", "Drivers come from the official Microsoft catalog"));
            else hint.SetText("");
        }

        void UpdateInfo()
        {
            int problems = Inventory.Attention;
            string text = Settings.LastCheck == DateTime.MinValue
                ? L.T("Проверок ещё не было", "Not checked yet")
                : L.T("Последняя проверка: ", "Last checked: ") + L.When(Settings.LastCheck);
            if (Inventory.DevicesLoaded)
                text += "  ·  " + L.Count(Inventory.Devices.Count, "устройство", "устройства", "устройств", "device", "devices");
            info.SetText(text);
            attention.ForeOverride = Theme.Current.Warning;
            attention.SetText(problems > 0 ? L.T("Требуют внимания: ", "Need attention: ") + problems : "");
            bool show = problems > 0;
            if (show != btnShowProblems.Visible)
            {
                btnShowProblems.Visible = show;
                if (show) btnShowProblems.Appear();
            }
            LayoutOptCard();
        }

        void UpdateEmpty()
        {
            if (failedCheck)
            {
                list.EmptyGlyph = "\uE7BA";
                list.EmptyText = L.T("Windows Update не ответил. ", "Windows Update did not answer. ") + (error ?? "");
            }
            else if (!checkedOnce)
            {
                list.EmptyGlyph = "\uE895";
                list.EmptyText = L.T("Нажмите «Проверить обновления» — DriveUP спросит Windows Update о новых драйверах для этого компьютера",
                    "Click \"Check for updates\" — DriveUP will ask Windows Update for newer drivers for this PC");
            }
            else
            {
                list.EmptyGlyph = "\uE73E";
                list.EmptyText = L.T("Все драйверы актуальны", "All drivers are up to date");
            }
            list.Invalidate();
        }

        // ---------- checking ----------

        public bool Busy { get { return scanning || working; } }
        public bool Installing { get { return working; } }

        protected override void FirstActivate()
        {
            UpdateEmpty();
            if (Settings.CheckOnStart) StartCheck();
            else
            {
                ring.SetState(RingState.Idle);
                UpdateTexts();
                LoadDevicesQuietly();
            }
        }

        public override void RefreshData()
        {
            if (!Busy) StartCheck();
        }

        void MainAction()
        {
            if (Busy) return;
            if (ring.State == RingState.Ready) DoInstall();
            else StartCheck();
        }

        void LoadDevicesQuietly()
        {
            if (Inventory.DevicesLoaded) return;
            var th = new Thread(() =>
            {
                Dictionary<string, Bitmap> icons;
                var devs = Inventory.LoadDevices(out icons);
                Post(() => { if (!Inventory.DevicesLoaded) Inventory.SetDevices(devs, icons); });
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            th.Start();
        }

        public void StartCheck()
        {
            if (Busy) return;
            scanning = true;
            failedCheck = false;
            retryNote = null;
            error = null;
            stage = 0;
            stageTimer.Start();
            ring.SetState(RingState.Scanning);
            if (cats.Count > 0 || vendorCats.Count > 0)
                Transition.CrossFade(list, () =>
                {
                    cats = new List<CatGroup>();
                    vendorCats = new List<CatGroup>();
                    list.SetGroups(cats);
                    list.Loading = true;
                }, 200);
            else list.Loading = true;
            UpdateTexts();
            Host.BeginBusy();
            Host.UpdateStatus();

            var th = new Thread(() =>
            {
                Dictionary<string, Bitmap> icons;
                var devs = Inventory.LoadDevices(out icons);
                Post(() => Inventory.SetDevices(devs, icons));
                // Vendors' sites are asked at the same time as Windows Update.
                List<VendorUpdate> vendors = null;
                PcInfo pc = null;
                var vt = new Thread(() =>
                {
                    try
                    {
                        pc = PcInfo.Read();
                        vendors = VendorCheck.CheckAll(devs, pc);
                    }
                    catch { }
                }) { IsBackground = true };
                vt.SetApartmentState(ApartmentState.MTA);
                vt.Start();
                List<DriverUpdate> found = null;
                string err = null;
                try { found = WinUpdate.Search(); }
                catch (Exception ex) { err = WinUpdate.ErrorText(ex); }
                // A little longer than the vendor check's own limit (40 s): its partial results still arrive.
                vt.Join(46000);
                RunUI(() => CheckDone(found, err, vendors ?? new List<VendorUpdate>(), pc));
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.MTA);
            th.Start();
        }

        void CheckDone(List<DriverUpdate> found, string err, List<VendorUpdate> vendors, PcInfo pc)
        {
            scanning = false;
            stageTimer.Stop();
            Host.EndBusy();
            checkedOnce = true;
            vendorCats = VendorGroups(vendors).Concat(RuntimeGroups()).ToList();
            if (found == null)
            {
                failedCheck = true;
                error = err;
                Inventory.SetUpdates(null, vendors, pc);
                list.Loading = false;
                if (vendorCats.Count > 0) Transition.CrossFade(list, () => list.SetGroups(vendorCats), 260);
                UpdateEmpty();
                ring.SetState(RingState.Error);
                UpdateTexts();
                Host.ShowToast(L.T("Не удалось проверить обновления: ", "Could not check for updates: ") + err, ToastKind.Error);
                Host.UpdateStatus();
                return;
            }
            Settings.LastCheck = DateTime.Now;
            Settings.Save();
            Inventory.SetUpdates(found, vendors, pc);
            var made = Group(found);
            UpdateEmpty();
            var shown = made.Concat(vendorCats).ToList();
            Transition.CrossFade(list, () => { cats = made; list.SetGroups(shown); }, 260);
            cats = made;
            ring.SetNumber(0, false);
            ring.SetState(found.Count > 0 ? RingState.Ready : RingState.UpToDate);
            if (found.Count > 0) ring.SetNumber(found.Count);
            UpdateTexts();
            UpdateInfo();
            Host.UpdateStatus();
        }

        // ---------- grouping ----------

        /// <summary>Kind of device → name and icon of its group.</summary>
        public static void ClassInfo(string key, out string ru, out string en, out string glyph)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case "display": ru = "Видеокарты"; en = "Display adapters"; glyph = "\uE7F4"; return;
                case "monitor": ru = "Мониторы"; en = "Monitors"; glyph = "\uE7F4"; return;
                case "net": ru = "Сеть"; en = "Network"; glyph = "\uE839"; return;
                case "bluetooth": ru = "Bluetooth"; en = "Bluetooth"; glyph = "\uE702"; return;
                case "media":
                case "audioendpoint":
                case "audioprocessingobject": ru = "Звук"; en = "Sound"; glyph = "\uE767"; return;
                case "camera":
                case "image": ru = "Камеры и сканеры"; en = "Cameras and scanners"; glyph = "\uE722"; return;
                case "printer":
                case "printqueue": ru = "Принтеры"; en = "Printers"; glyph = "\uE749"; return;
                case "usb":
                case "usbdevice": ru = "USB"; en = "USB"; glyph = "\uE88E"; return;
                case "hidclass":
                case "keyboard": ru = "Устройства ввода"; en = "Input devices"; glyph = "\uE765"; return;
                case "mouse": ru = "Мыши"; en = "Mice"; glyph = "\uE962"; return;
                case "system": ru = "Системные устройства"; en = "System devices"; glyph = "\uE770"; return;
                case "firmware": ru = "Прошивки"; en = "Firmware"; glyph = "\uE950"; return;
                case "processor": ru = "Процессоры"; en = "Processors"; glyph = "\uE950"; return;
                case "softwarecomponent":
                case "extension": ru = "Компоненты драйверов"; en = "Driver components"; glyph = "\uEA86"; return;
                case "diskdrive":
                case "hdc":
                case "scsiadapter": ru = "Накопители"; en = "Storage"; glyph = "\uEDA2"; return;
                case "biometric": ru = "Биометрия"; en = "Biometrics"; glyph = "\uE928"; return;
                case "sensor": ru = "Датчики"; en = "Sensors"; glyph = "\uE957"; return;
                case "battery": ru = "Питание"; en = "Batteries"; glyph = "\uE83F"; return;
                case "securitydevices": ru = "Безопасность"; en = "Security"; glyph = "\uE83D"; return;
                case "smartcardreader": ru = "Смарт-карты"; en = "Smart cards"; glyph = "\uE8D7"; return;
                case "ports": ru = "Порты"; en = "Ports"; glyph = "\uE964"; return;
                default: ru = "Другие устройства"; en = "Other devices"; glyph = "\uF158"; return;
            }
        }

        static List<CatGroup> Group(List<DriverUpdate> updates)
        {
            var result = new List<CatGroup>();
            foreach (var grp in updates.GroupBy(u =>
            {
                string ru, en, glyph;
                ClassInfo(u.ClassKey, out ru, out en, out glyph);
                return en;
            }).OrderBy(g => g.Key))
            {
                string ru, en, glyph;
                ClassInfo(grp.First().ClassKey, out ru, out en, out glyph);
                var items = grp.ToList();
                var cg = new CatGroup
                {
                    Tag = grp.Key, Glyph = glyph, Expanded = true, Name = () => L.T(ru, en),
                    Desc = () => L.Count(items.Count, "обновление", "обновления", "обновлений", "update", "updates") +
                                 (items.Any(x => x.Device != null) ? "  ·  " + string.Join(", ", items.Where(x => x.Device != null).Select(x => x.Device.Name).Distinct().Take(2)) : ""),
                };
                foreach (var u in items)
                {
                    var up = u;
                    cg.Items.Add(new CatItem
                    {
                        Tag = up, Checked = up.Checked, Size = up.Size >= 0 ? up.Size : 0,
                        Name = () => up.DisplayName,
                        Note = () => NoteFor(up),
                    });
                }
                result.Add(cg);
            }
            // Expanded groups only while the list is short enough to read at a glance.
            if (updates.Count > 8) foreach (var g in result) g.Expanded = false;
            return result;
        }

        /// <summary>
        /// Newer drivers on vendors' sites: one group per vendor, link rows that open the download page
        /// (with the user's rights). When the vendor's feed names the file, the row also has a
        /// "download and install" button (VendorInstall: vendor's host, hash, signature, confirmation).
        /// </summary>
        List<CatGroup> VendorGroups(List<VendorUpdate> vendors)
        {
            var result = new List<CatGroup>();
            foreach (var grp in vendors.GroupBy(v => v.Vendor).OrderBy(g => g.Key))
            {
                string vendor = grp.Key;
                bool catalog = vendor == "Microsoft";
                var cg = new CatGroup
                {
                    Tag = "vendor:" + vendor, Glyph = catalog ? "\uE896" : "\uE774", Expanded = true,
                    Name = () => catalog ? L.T("В каталоге Microsoft Update", "In the Microsoft Update Catalog") : L.T("На сайте ", "On the ") + vendor + L.T("", " site"),
                };
                foreach (var v in grp)
                {
                    var vu = v;
                    var ci = new CatItem
                    {
                        Tag = vu, Size = 0, Badge = vu.CatalogId.Length > 0 ? L.T("скачать", "download") : L.T("открыть", "open"),
                        Name = () => vu.Title,
                        Note = () => vu.VersionText + (vu.Date.HasValue ? L.T(" от ", ", ") + L.Date(vu.Date.Value) : "") +
                                     (vu.CurrentText.Length > 0 ? L.T("  ·  сейчас ", "  ·  now ") + vu.CurrentText : ""),
                        Open = () =>
                        {
                            if (vu.CatalogId.Length > 0) CatalogSource.Fetch(Host, this, vu.CatalogId, vu.Title, vu.Size);
                            else try { UserShell.OpenUrl(vu.Url); } catch { }
                        },
                    };
                    if (VendorInstall.Offers(vu))
                    {
                        var item = ci;
                        item.Badge = InstallCaption;
                        item.Act = () => InstallVendor(vu, item);
                    }
                    cg.Items.Add(ci);
                }
                var rows = cg.Items;
                cg.Desc = () => L.Count(rows.Count, "драйвер новее", "драйвера новее", "драйверов новее", "newer driver", "newer drivers") +
                    (catalog ? L.T("  ·  нажмите, чтобы скачать и установить", "  ·  click to download and install")
                     : rows.Any(i => i.Act != null) ? L.T("  ·  файл производителя, подпись проверяется; строка — страница на сайте", "  ·  the vendor's own file, signature checked; the row opens its page")
                     : L.T("  ·  скачайте и перетащите на «Установку»", "  ·  download, then drop it on the Install tab"));
                result.Add(cg);
            }
            return result;
        }

        static string InstallCaption { get { return L.T("скачать и установить", "download and install"); } }

        bool vendorWorking;

        /// <summary>"Download and install" of one vendor row: everything on a worker thread, the row's button shows the step.</summary>
        void InstallVendor(VendorUpdate vu, CatItem item)
        {
            if (Busy || vendorWorking) { Host.ShowToast(L.T("Дождитесь окончания проверки или установки", "Wait until the check or install finishes"), ToastKind.Info); return; }
            if (!Host.CanStartOperation()) return;
            vendorWorking = true;
            item.Warn = false;
            Host.BeginBusy();
            Host.ShowToast(L.T("Скачиваю: ", "Downloading: ") + vu.Title + (vu.Size > 0 ? " (" + L.Size(vu.Size) + ")" : ""), ToastKind.Info);
            int lastPercent = -1;
            long lastBytes = 0;
            Action<string> stage = s => Post(() => { item.Badge = s; list.Invalidate(); });
            Action<long, long> progress = (done, total) =>
            {
                // A few updates a second at most: per percent, or per 8 MB when the size is unknown.
                int pc = total > 0 ? (int)(done * 100 / total) : -1;
                if (total > 0 ? pc == lastPercent : done - lastBytes < (8 << 20)) return;
                lastPercent = pc;
                lastBytes = done;
                string text = total > 0 ? L.T("скачано ", "downloaded ") + pc + " %" : L.Size(done) + "…";
                Post(() => { item.Badge = text; list.Invalidate(); });
            };
            var th = new Thread(() =>
            {
                var r = VendorInstall.Run(vu, Host.Window, AskUI, stage, progress);
                RunUI(() => VendorDone(vu, item, r));
            }) { IsBackground = true, Name = "vendor install" };
            th.Start();
        }

        void VendorDone(VendorUpdate vu, CatItem item, VendorResult r)
        {
            vendorWorking = false;
            Host.EndBusy();
            item.Badge = InstallCaption;
            list.Invalidate();
            if (r.OpenFile != null)
            {
                Host.ShowToast(L.T("Архив скачан и проверен — открыт на «Установке»", "The archive is downloaded and checked — opened on the Install tab"), ToastKind.Success);
                Host.InstallFrom(new[] { r.OpenFile });
                return;
            }
            if (r.Cancelled) { Host.ShowToast(L.T("Установка отменена", "Install cancelled") + (r.Message.Length > 0 ? ": " + r.Message : ""), ToastKind.Info); return; }
            if (!r.Ok)
            {
                item.Warn = true;
                list.Invalidate();
                Host.ShowToast(L.T("Не удалось: ", "Failed: ") + r.Message, ToastKind.Error);
                return;
            }
            Host.ShowToast(L.T("Драйвер установлен: ", "Driver installed: ") + vu.Title + (r.Reboot ? L.T(" · нужна перезагрузка", " · restart needed") : ""), ToastKind.Success);
            // The row leaves the list (its group too, when it was the last one).
            foreach (var g in vendorCats) g.Items.Remove(item);
            vendorCats = vendorCats.Where(g => g.Items.Count > 0).ToList();
            var shown = cats.Concat(vendorCats).ToList();
            Transition.CrossFade(list, () => list.SetGroups(shown), 260);
            UpdateEmpty();
            UpdateTexts();
            Host.UpdateStatus();
            // Versions changed: read the devices again (the Devices tab and the vendor findings follow).
            var th = new Thread(() =>
            {
                Dictionary<string, Bitmap> icons;
                var devs = Inventory.LoadDevices(out icons);
                Post(() => Inventory.SetDevices(devs, icons));
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
            if (r.Reboot) Anim.Delay(900, AskRestart);
        }

        int VendorCount { get { return vendorCats.Where(g => !"runtimes".Equals(g.Tag)).Sum(g => g.Items.Count); } }

        /// <summary>
        /// Libraries programs and games need that are missing here: one row each, plus "install all".
        /// Official Microsoft downloads, run only when signed by Microsoft (see Runtimes).
        /// </summary>
        List<CatGroup> RuntimeGroups()
        {
            List<Runtime> missing;
            try { missing = Runtimes.Missing(); }
            catch { missing = new List<Runtime>(); }
            if (missing.Count == 0) return new List<CatGroup>();
            var g = new CatGroup
            {
                Tag = "runtimes", Glyph = "\uE74C", Expanded = missing.Count <= 6,
                Name = () => L.T("Компоненты для программ и игр", "Components for programs and games"),
                Desc = () => L.Count(missing.Count, "не хватает компонента", "не хватает компонентов", "не хватает компонентов", "component missing", "components missing") +
                             L.T("  ·  с сайта Microsoft, подпись проверяется", "  ·  from Microsoft, signature checked"),
            };
            if (missing.Count > 1)
                g.Items.Add(new CatItem
                {
                    Size = 0, Badge = L.T("установить", "install"),
                    Name = () => L.T("Установить все недостающие", "Install everything missing"),
                    Note = () => string.Join(", ", missing.Select(r => r.Name)),
                    Open = () => InstallRuntimes(missing),
                });
            foreach (var r in missing)
            {
                var rt = r;
                bool old = false;
                try { old = rt.Installed(); } catch { }
                g.Items.Add(new CatItem
                {
                    Size = 0, Badge = old ? L.T("обновить", "update") : L.T("установить", "install"),
                    Name = () => rt.Name,
                    Note = () => old ? L.T("установлена старая версия", "an old version is installed") : L.T("не установлен", "not installed"),
                    Open = () => InstallRuntimes(new List<Runtime> { rt }),
                });
            }
            return new List<CatGroup> { g };
        }

        void InstallRuntimes(List<Runtime> items)
        {
            if (working || scanning) { Host.ShowToast(L.T("Дождитесь окончания проверки или установки", "Wait until the check or install finishes"), ToastKind.Info); return; }
            Runtimes.Install(Host, this, items, (ok, errors) =>
            {
                if (errors.Count == 0) Host.ShowToast(L.T("Установлено компонентов: ", "Components installed: ") + ok, ToastKind.Success);
                else Host.ShowToast(string.Join("\n", errors.Take(3)), ok > 0 ? ToastKind.Info : ToastKind.Error);
                // What is still missing.
                vendorCats = vendorCats.Where(x => !"runtimes".Equals(x.Tag)).Concat(RuntimeGroups()).ToList();
                var shown = cats.Concat(vendorCats).ToList();
                Transition.CrossFade(list, () => list.SetGroups(shown), 260);
                UpdateEmpty();
                Host.UpdateStatus();
            });
        }

        static string NoteFor(DriverUpdate u)
        {
            if (u.Status.Length > 0) return u.Status;
            var parts = new List<string>();
            if (u.VersionText.Length > 0) parts.Add(u.VersionText);
            if (u.Date.HasValue) parts.Add(L.Date(u.Date.Value));
            string s = string.Join(L.T(" от ", ", "), parts);
            if (u.Device != null && u.Device.VersionText.Length > 0)
                s += L.T("  ·  сейчас ", "  ·  now ") + u.Device.VersionText;
            else if (u.Device != null && !u.Device.HasDriver)
                s += L.T("  ·  сейчас без драйвера", "  ·  no driver now");
            return s;
        }

        IEnumerable<CatItem> AllItems() { return cats.SelectMany(g => g.Items); }

        void OnChecksChanged()
        {
            foreach (var ci in AllItems()) ((DriverUpdate)ci.Tag).Checked = ci.Checked;
            UpdateTexts();
            Host.UpdateStatus();
        }

        // ---------- installing ----------

        void DoInstall()
        {
            if (Busy || !Host.CanStartOperation()) return;
            var items = AllItems().Where(i => i.Checked && !i.Disabled).ToList();
            var updates = items.Select(i => (DriverUpdate)i.Tag).ToList();
            if (updates.Count == 0)
            {
                Host.ShowToast(L.T("Отметьте обновления в списке", "Tick updates in the list"), ToastKind.Info);
                return;
            }
            working = true;
            list.Locked = true;
            foreach (var u in updates) { u.Status = L.T("В очереди", "Queued"); u.Failed = false; }
            foreach (var ci in items) ci.Warn = false;
            list.Invalidate();
            ring.SetState(RingState.Working);
            ring.Caption = Settings.RestorePoint ? L.T("точка восстановления…", "restore point…") : L.T("подготовка…", "preparing…");
            hint.SetText(L.T("Не выключайте компьютер до конца установки", "Do not turn off the PC until it is done"));
            UpdateTexts();
            Host.BeginBusy();
            Host.UpdateStatus();
            int total = updates.Count;
            bool wantPoint = Settings.RestorePoint;   // the switch is locked until the end, but read it once
            retryNote = null;

            var th = new Thread(() =>
            {
                string rpNote = null;
                bool rpOk = false;
                if (wantPoint)
                {
                    rpNote = SysRestore.Create("DriveUP: " + L.T("обновление драйверов", "driver update"), out rpOk);
                    Post(() => ring.SetProgress(0.04f));
                    if (!rpOk && !SysRestore.ContinueWithout(AskUI, Host.Window, rpNote))
                    {
                        var cancelled = new UpdateResult { Error = L.T("отменено: нет точки восстановления", "cancelled: no restore point") };
                        foreach (var u in updates)
                        {
                            u.Failed = true;
                            u.Status = L.T("Не установлено: нет точки восстановления", "Not installed: no restore point");
                            cancelled.Failed.Add(u);
                        }
                        RunUI(() => InstallDone(updates, cancelled, rpOk, rpNote, wantPoint));
                        return;
                    }
                }
                UpdateResult result;
                try
                {
                    int lastIdx = -1, lastPhase = -1;
                    result = WinUpdate.Install(updates, (phase, frac, idx) => Post(() =>
                    {
                        float baseP = wantPoint ? 0.04f : 0;
                        float overall = baseP + (1 - baseP) * (phase == 0 ? frac * 0.45f : 0.45f + frac * 0.55f);
                        ring.SetProgress(overall);
                        int n = Math.Max(0, Math.Min(total - 1, idx));
                        ring.Caption = (phase == 0 ? L.T("скачивание", "downloading") : L.T("установка", "installing")) +
                                       (total > 1 ? " · " + (n + 1) + L.T(" из ", " of ") + total : "");
                        if (n != lastIdx || phase != lastPhase)
                        {
                            for (int i = 0; i < updates.Count; i++)
                            {
                                var u = updates[i];
                                // Final results are written by the installer: never paint over them.
                                if (u.Failed || u.Installed) continue;
                                if (i < n && phase == 1) u.Status = L.T("Установлено…", "Installed…");
                                else if (i == n) u.Status = phase == 0 ? L.T("Скачивание…", "Downloading…") : L.T("Установка…", "Installing…");
                                else if (phase == 1 && i > n) u.Status = L.T("Скачано, ждёт установки", "Downloaded, waiting");
                            }
                            lastIdx = n;
                            lastPhase = phase;
                            list.Invalidate();
                        }
                    }));
                }
                catch (Exception ex)
                {
                    result = new UpdateResult { Error = WinUpdate.ErrorText(ex) };
                    foreach (var u in updates)
                    {
                        if (u.Installed) continue;
                        u.Failed = true;
                        u.Status = L.T("Ошибка: ", "Error: ") + result.Error;
                        result.Failed.Add(u);
                    }
                }
                RunUI(() => InstallDone(updates, result, rpOk, rpNote, wantPoint));
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.MTA);
            th.Start();
        }

        void InstallDone(List<DriverUpdate> updates, UpdateResult result, bool rpOk, string rpNote, bool wantPoint)
        {
            working = false;
            list.Locked = false;
            lastResult = result;
            installedCount = result.Installed.Count;
            attemptedCount = updates.Count;
            Host.EndBusy();
            ring.SetProgress(1);

            if (result.Installed.Count > 0)
                Journal.Add(JournalKind.Update, result.Installed.Count, result.Failed.Count,
                    string.Join(", ", result.Installed.Select(u => u.DisplayName + (u.VersionText.Length > 0 ? " " + u.VersionText : ""))));
            else if (result.Failed.Count > 0)
                Journal.Add(JournalKind.Update, 0, result.Failed.Count, result.Error ?? string.Join(", ", result.Failed.Select(u => u.DisplayName)));
            Inventory.UpdatesInstalled(result.Installed);
            if (result.Installed.Count > 0)
            {
                // Versions changed: read the devices again (the Devices tab and vendor rows follow).
                var th = new Thread(() =>
                {
                    Dictionary<string, Bitmap> icons;
                    var devs = Inventory.LoadDevices(out icons);
                    Post(() => Inventory.SetDevices(devs, icons));
                }) { IsBackground = true };
                th.SetApartmentState(ApartmentState.STA);
                th.Start();
            }

            // Installed rows leave the list; failed ones stay with a warning dot and the reason.
            var installed = new HashSet<DriverUpdate>(result.Installed);
            foreach (var ci in AllItems())
            {
                var u = (DriverUpdate)ci.Tag;
                if (u.Failed) { ci.Warn = true; ci.Checked = true; }
                else if (!installed.Contains(u)) u.Status = "";
            }
            var left = cats.Select(g =>
            {
                var ng = new CatGroup { Tag = g.Tag, Glyph = g.Glyph, Name = g.Name, Expanded = g.Expanded };
                ng.Items.AddRange(g.Items.Where(i => !installed.Contains((DriverUpdate)i.Tag)));
                var keep = ng.Items.Select(i => (DriverUpdate)i.Tag).ToList();
                ng.Desc = () => L.Count(keep.Count, "обновление", "обновления", "обновлений", "update", "updates");
                return ng;
            }).Where(g => g.Items.Count > 0).ToList();

            // What failed stays ticked in the list and the ring stays "ready": one click tries again.
            int remaining = left.Sum(g => g.Items.Count);
            if (remaining > 0)
                retryNote = L.T("Не удалось: ", "Failed: ") + (result.Error ?? (result.Failed.Count > 0 ? result.Failed[0].Status : "")) +
                            L.T(" — можно повторить", " — you can try again");
            Anim.Delay(260, () =>
            {
                // F5 in the meantime started a new check: that one owns the ring and the list now.
                if (scanning || working) return;
                if (remaining > 0)
                {
                    ring.SetState(RingState.Ready);
                    ring.SetNumber(remaining);
                }
                else
                {
                    ring.SetState(RingState.Done);
                    ring.SetNumber(installedCount, false);
                }
                cats = left;
                var shown = left.Concat(vendorCats).ToList();
                Transition.CrossFade(list, () => list.SetGroups(shown), 260);
                UpdateEmpty();
                UpdateTexts();
                Host.UpdateStatus();
            });

            string text;
            if (installedCount > 0)
            {
                text = installedCount == 1 && updates.Count == 1
                    ? L.T("Драйвер обновлён: ", "Driver updated: ") + result.Installed[0].DisplayName
                    : L.T("Обновлено драйверов: ", "Drivers updated: ") + installedCount;
                if (result.Failed.Count > 0) text += L.T(" · не удалось: ", " · failed: ") + result.Failed.Count;
            }
            else text = L.T("Не удалось установить", "Could not install") + (result.Error != null ? ": " + result.Error : "");
            if (wantPoint && installedCount > 0 && !rpOk)
                text += L.T(" · без точки восстановления (", " · no restore point (") + rpNote + ")";
            else if (wantPoint && installedCount > 0 && rpNote != null)
                text += " · " + rpNote;
            Host.ShowToast(text, installedCount == 0 ? ToastKind.Error : (result.Failed.Count > 0 ? ToastKind.Info : ToastKind.Success));

            if (result.RebootRequired) Anim.Delay(900, AskRestart);
        }

        void AskRestart()
        {
            if (ThemedDialog.Confirm(Host.Window, L.T("Нужна перезагрузка", "Restart needed"),
                L.T("Новые драйверы начнут работать после перезагрузки.\nСохраните открытые документы и перезагрузите компьютер.",
                    "The new drivers start working after a restart.\nSave your open documents and restart the PC."),
                L.T("Перезагрузить", "Restart now"), "\uE777", false))
            {
                try
                {
                    UserShell.RestartPc();
                }
                catch (Exception ex) { Host.ShowToast(ex.Message, ToastKind.Error); }
            }
        }

        public override string Status
        {
            get
            {
                if (scanning) return L.T("Поиск обновлений драйверов…", "Looking for driver updates…");
                if (working) return L.T("Установка драйверов…", "Installing drivers…");
                var parts = new List<string>();
                if (Inventory.DevicesLoaded)
                    parts.Add(L.T("Устройств: ", "Devices: ") + Inventory.Devices.Count);
                if (checkedOnce && !failedCheck)
                    parts.Add(L.T("обновлений: ", "updates: ") + Available + (Available > 0 ? L.T(" · выбрано: ", " · selected: ") + Selected : ""));
                parts.Add(Settings.LastCheck == DateTime.MinValue ? L.T("ещё не проверялось", "not checked yet")
                    : L.T("последняя проверка: ", "last checked: ") + L.When(Settings.LastCheck));
                return string.Join("  ·  ", parts);
            }
        }
    }
}
