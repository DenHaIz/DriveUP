using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>Every device with its driver: version, date, provider and whether it works.</summary>
    class DevicesPage : Page
    {
        readonly Panel toolbar = new BufferedPanel(), listHost = new BufferedPanel(), content = new BufferedPanel();
        readonly Segmented filter;
        readonly FlatButton btnProps, btnFind, btnSite, btnRescan;
        readonly SearchBox search = new SearchBox();
        readonly ThemedListView lv = new ThemedListView();
        readonly System.Windows.Forms.Timer searchDebounce = new System.Windows.Forms.Timer { Interval = 200 };
        readonly ToolTip tip = new ToolTip();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem miProps, miFind, miSite, miCopy, miUpdates;
        bool loading, rescanning;

        public DevicesPage(IHost host) : base(host)
        {
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = S(60);
            filter = new Segmented(new[] { "", "", "" }, new[] { "", "", "" }, 0);
            filter.SelectedIndexChanged += (s, e) => Transition.CrossFade(listHost, Render, 200);
            btnProps = Button("\uE9E9");
            btnProps.Click += (s, e) => OpenProperties();
            btnFind = Button("\uE721");
            btnFind.Click += (s, e) => FindInCatalog();
            btnSite = Button("\uE774");
            btnSite.Click += (s, e) => OpenVendorSite();
            btnRescan = Button("\uE72C");
            btnRescan.Click += (s, e) => Rescan();
            search.TextChanged += (s, e) => { searchDebounce.Stop(); searchDebounce.Start(); };
            searchDebounce.Tick += (s, e) =>
            {
                searchDebounce.Stop();
                Transition.CrossFade(listHost, Render, 160);
            };
            foreach (Control c in new Control[] { filter, btnProps, btnFind, btnSite, btnRescan, search }) toolbar.Controls.Add(c);
            toolbar.Resize += (s, e) => LayoutToolbar();

            lv.Icons = Inventory.Icons;
            lv.MultiSelect = false;
            lv.Numeric = new[] { false, false, false, true, true, false };
            lv.SortColumn = 5;
            lv.DotColumn = 5;
            lv.DefaultGlyph = "\uE772";
            lv.ItemActivate += (s, e) => OpenProperties();
            lv.SelectedIndexChanged += (s, e) => UpdateButtons();
            lv.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var it = lv.GetItemAt(e.X, e.Y);
                if (it == null) return;
                it.Selected = true;
                it.Focused = true;
                var d = (Device)it.Tag;
                miFind.Enabled = BestId(d) != null;
                miSite.Enabled = VendorCheck.PageFor(d, Inventory.Pc) != null;
                miCopy.Enabled = d.HardwareIds.Length > 0;
                miUpdates.Visible = d.Update != null;
                menu.Show(lv, e.Location);
            };
            foreach (int w in new[] { 320, 170, 160, 120, 100, 200 }) lv.Columns.Add("", S(w));
            lv.ColumnWeights = new[] { 0.3f, 0.15f, 0.15f, 0.12f, 0.1f, 0.18f };
            lv.ColumnMins = new[] { 200, 110, 110, 90, 80, 130 };
            lv.Loading = true;
            lv.Dock = DockStyle.Fill;
            listHost.Padding = new Padding(1);
            listHost.Controls.Add(lv);

            miProps = MenuItem("\uE9E9", (s, e) => OpenProperties());
            miFind = MenuItem("\uE721", (s, e) => FindInCatalog());
            miSite = MenuItem("\uE774", (s, e) => OpenVendorSite());
            miCopy = MenuItem("\uE8C8", (s, e) => CopyIds());
            miUpdates = MenuItem("\uE895", (s, e) => Host.OpenTab(0));
            menu.Renderer = new ThemedRenderer();
            menu.Font = Theme.UiFont;
            menu.Padding = new Padding(0, S(4), 0, S(4));
            foreach (ToolStripItem item in menu.Items) item.Padding = new Padding(S(20), S(4), 0, S(4));

            content.Padding = new Padding(S(16), 0, S(16), S(4));
            content.Dock = DockStyle.Fill;
            content.Controls.Add(listHost);
            listHost.Dock = DockStyle.Fill;
            Controls.Add(content);
            Controls.Add(toolbar);

            Inventory.Changed += () =>
            {
                if (IsDisposed) return;
                if (Visible) Transition.CrossFade(listHost, Render, 240);
                else Render();
            };
        }

        ToolStripMenuItem MenuItem(string glyph, EventHandler click)
        {
            var mi = new ToolStripMenuItem { Tag = new MenuTag { Glyph = glyph } };
            mi.Click += click;
            menu.Items.Add(mi);
            return mi;
        }

        public override void ApplyTheme()
        {
            base.ApplyTheme();
            foreach (Control c in Controls) c.BackColor = Theme.Current.Bg;
            listHost.BackColor = Theme.Current.Border;
            search.ApplyTheme();
            lv.ApplyTheme();
        }

        public override void ApplyLanguage()
        {
            filter.SetTexts(new[] { L.T("Все", "All"), L.T("Требуют внимания", "Need attention"), L.T("Сторонние драйверы", "Third-party drivers") });
            SetButton(btnProps, L.T("Свойства", "Properties"));
            SetButton(btnFind, L.T("Найти драйвер", "Find a driver"));
            search.SetCue(L.T("Поиск устройств", "Search devices"));
            tip.SetToolTip(btnProps, L.T("Свойства устройства в Диспетчере устройств (двойной щелчок по строке)", "Device properties in Device Manager (double-click a row)"));
            tip.SetToolTip(btnFind, L.T("Открыть каталог Центра обновления Майкрософт с поиском по ИД оборудования.\n" +
                "Скачанный .cab перетащите на вкладку «Установка».",
                "Open the Microsoft Update Catalog searching by hardware ID.\nDrop the downloaded .cab on the Install tab."));
            tip.SetToolTip(btnRescan, L.T("Найти новое оборудование, как в Диспетчере устройств, и перечитать список",
                "Scan for hardware changes, as Device Manager does, and reload the list"));
            miProps.Text = L.T("Свойства устройства", "Device properties");
            miFind.Text = L.T("Найти драйвер в каталоге Microsoft", "Find a driver in the Microsoft catalog");
            miSite.Text = L.T("Открыть сайт производителя", "Open the vendor's site");
            tip.SetToolTip(btnSite, L.T("Страница драйверов производителя для этого устройства (или вашего ПК)", "The vendor's driver page for this device (or your PC)"));
            miCopy.Text = L.T("Копировать ИД оборудования", "Copy hardware IDs");
            miUpdates.Text = L.T("Перейти к обновлению", "Go to the update");
            string[] cols = { L.T("Устройство", "Device"), L.T("Тип", "Type"), L.T("Поставщик драйвера", "Driver provider"),
                              L.T("Версия", "Version"), L.T("Дата", "Date"), L.T("Состояние", "Status") };
            for (int i = 0; i < cols.Length; i++) lv.Columns[i].Text = cols[i];
            Render();
            UpdateButtons();
            LayoutToolbar();
        }

        public override void ApplyScale()
        {
            toolbar.Height = S(60);
            content.Padding = new Padding(S(16), 0, S(16), S(4));
            menu.Font = Theme.UiFont;
            LayoutToolbar();
        }

        void LayoutToolbar()
        {
            int x = S(16), y = (toolbar.Height - S(Theme.ControlH)) / 2;
            filter.Location = new Point(x, y);
            x = filter.Right + S(16);
            // Narrow window: buttons give up their captions one by one (the tooltips still explain them),
            // the least needed first: Properties (also on double-click), then Find, then the vendor's site.
            var buttons = new[] { btnProps, btnFind, btnSite };
            var captions = new[] { L.T("Свойства", "Properties"), L.T("Найти драйвер", "Find a driver"), L.T("Сайт производителя", "Vendor's site") };
            var widths = captions.Select(c => S(24) + S(16) + S(8) + TextDraw.MeasureText(c, Theme.UiFont, Size.Empty, TextFormatFlags.NoPadding).Width).ToArray();
            int room = toolbar.Width - x - (btnRescan.Width + S(8)) - S(120) - S(28);
            int bare = S(40);
            int folded = 0;
            while (folded < buttons.Length && widths.Select((wd, i) => (i < folded ? bare : wd) + S(8)).Sum() > room) folded++;
            for (int i = 0; i < buttons.Length; i++)
            {
                string want = i < folded ? "" : captions[i];
                if (buttons[i].Text != want) SetButton(buttons[i], want);
                buttons[i].Location = new Point(x, y);
                x = buttons[i].Right + S(8);
            }
            btnRescan.Location = new Point(x, y);
            x = btnRescan.Right + S(8);
            int w = Math.Max(S(120), Math.Min(S(300), toolbar.Width - x - S(28)));
            search.SetBounds(toolbar.Width - S(16) - w, y, w, S(Theme.ControlH));
        }

        public override void FocusSearch() { search.FocusBox(); }

        protected override void FirstActivate()
        {
            if (Inventory.DevicesLoaded) Render();
            else Load();
        }

        public override void RefreshData()
        {
            if (!loading && !rescanning) Load();
        }

        /// <summary>Opens the tab filtered to devices that need attention.</summary>
        public void ShowAttention()
        {
            if (filter.SelectedIndex != 1) filter.SelectedIndex = 1;
        }

        void Load()
        {
            if (loading) return;
            loading = true;
            Host.BeginBusy();
            UpdateButtons();
            var th = new Thread(() =>
            {
                Dictionary<string, Bitmap> icons;
                var devs = Inventory.LoadDevices(out icons);
                Post(() =>
                {
                    loading = false;
                    Host.EndBusy();
                    Inventory.SetDevices(devs, icons);
                    UpdateButtons();
                });
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            th.Start();
        }

        void Rescan()
        {
            if (rescanning || loading) return;
            rescanning = true;
            Host.BeginBusy();
            UpdateButtons();
            int before = Inventory.Devices.Count;
            var th = new Thread(() =>
            {
                Devices.Rescan();
                Thread.Sleep(600);   // let new devices settle before reading the list
                Dictionary<string, Bitmap> icons;
                var devs = Inventory.LoadDevices(out icons);
                Post(() =>
                {
                    rescanning = false;
                    Host.EndBusy();
                    Inventory.SetDevices(devs, icons);
                    UpdateButtons();
                    int diff = devs.Count - before;
                    Host.ShowToast(diff > 0 ? L.T("Найдено новых устройств: ", "New devices found: ") + diff
                        : L.T("Список устройств обновлён", "Device list refreshed"), ToastKind.Success);
                });
            }) { IsBackground = true };
            th.Start();
        }

        string[] Cells(Device d)
        {
            return new[] { d.Name, d.ClassName, d.Provider, d.VersionText, d.Date.HasValue ? L.Date(d.Date.Value) : "", d.StatusText };
        }

        void Render()
        {
            if (!Inventory.DevicesLoaded)
            {
                lv.Loading = true;
                return;
            }
            string q = search.Text.Trim();
            int f = filter.SelectedIndex;
            var rows = new List<ListViewItem>();
            foreach (var d in Inventory.Devices)
            {
                // Nameless root nodes (such as HTREE\ROOT\0) say nothing to a person.
                if (d.Name == d.InstanceId && d.Problem == 0 && !d.HasDriver) continue;
                if (f == 1 && !d.NeedsAttention && d.Update == null && d.Vendor == null) continue;
                if (f == 2 && !d.ThirdParty) continue;
                if (q.Length > 0 && d.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    d.ClassName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    d.Provider.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    d.InstanceId.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(new ListViewItem(Cells(d)) { Tag = d });
            }
            lv.BeginUpdate();
            lv.Items.Clear();
            lv.ResetRowStates();
            lv.Items.AddRange(rows.ToArray());
            lv.Sort();
            lv.EndUpdate();
            lv.Loading = false;
            lv.FitLastColumn();
            if (q.Length > 0)
            {
                lv.EmptyGlyph = "\uE721";
                lv.EmptyText = L.T("Ничего не найдено по запросу ", "Nothing matches ") + L.Quote(q);
            }
            else if (f == 1)
            {
                lv.EmptyGlyph = "\uE73E";
                lv.EmptyText = L.T("Все устройства работают, обновлений для них нет", "All devices work and have no pending updates");
            }
            else
            {
                lv.EmptyGlyph = "\uE772";
                lv.EmptyText = L.T("Устройств нет", "No devices");
            }
            UpdateButtons();
            Host.UpdateStatus();
        }

        Device Selected
        {
            get
            {
                var it = lv.SelectedItems.Count > 0 ? lv.SelectedItems[0] : lv.FocusedItem;
                return it != null ? it.Tag as Device : null;
            }
        }

        void UpdateButtons()
        {
            var d = Selected;
            btnProps.Enabled = d != null;
            btnFind.Enabled = d != null && BestId(d) != null;
            btnSite.Enabled = d != null && VendorCheck.PageFor(d, Inventory.Pc) != null;
            btnRescan.Enabled = !loading && !rescanning;
        }

        void OpenProperties()
        {
            var d = Selected;
            if (d != null) Devices.ShowProperties(d.InstanceId);
        }

        /// <summary>The shortest hardware ID: "PCI\VEN_8086&amp;DEV_46A6" finds more in the catalog than the full one.</summary>
        static string BestId(Device d)
        {
            var ids = d.HardwareIds.Where(x => x.Length >= 8).OrderBy(x => x.Length).ToList();
            return ids.Count > 0 ? ids[0] : null;
        }

        bool finding;

        /// <summary>
        /// Looks the device up in the Microsoft Update Catalog right here: a driver whose hardware IDs
        /// include this device's (newer than its own, or any for a device without a working driver) is
        /// offered for download and install. Nothing found: the catalog search opens in the browser.
        /// </summary>
        void FindInCatalog()
        {
            var d = Selected;
            string id = d != null ? BestId(d) : null;
            if (id == null || finding) return;
            finding = true;
            Host.BeginBusy();
            Host.ShowToast(L.T("Ищу драйвер в каталоге Microsoft…", "Looking in the Microsoft catalog…"), ToastKind.Info);
            var dev = d;
            new System.Threading.Thread(() =>
            {
                CatalogHit hit = null;
                string error = null;
                try { hit = CatalogSource.Lookup(dev, !dev.HasDriver || dev.NeedsAttention, DateTime.UtcNow.AddSeconds(60)); }
                catch (Exception ex) { error = ex.Message; }
                Post(() =>
                {
                    finding = false;
                    Host.EndBusy();
                    if (hit != null)
                    {
                        if (ThemedDialog.Confirm(Host.Window, L.T("Найден драйвер", "Driver found"),
                                dev.Name + "\n\n" + hit.Title + "\n" + L.T("Версия ", "Version ") + hit.Version +
                                (hit.Date.HasValue ? L.T(" от ", ", ") + L.Date(hit.Date.Value) : "") + (hit.Size > 0 ? " · " + L.Size(hit.Size) : "") + "\n" +
                                L.T("Сейчас: ", "Now: ") + (dev.HasDriver ? dev.VersionText : L.T("драйвера нет", "no driver")) + "\n\n" +
                                L.T("Скачать из каталога Microsoft Update и открыть на вкладке «Установка»?", "Download from the Microsoft Update Catalog and open it on the Install tab?"),
                                L.T("Скачать", "Download"), "\uE896", false))
                            CatalogSource.Fetch(Host, this, hit.Id, hit.Title, hit.Size);
                        return;
                    }
                    Host.ShowToast(error != null ? L.T("Каталог недоступен: ", "The catalog is unavailable: ") + error
                                                 : L.T("Подходящего драйвера новее в каталоге нет — открываю поиск", "No newer matching driver in the catalog — opening the search"), ToastKind.Info);
                    try { UserShell.OpenUrl(CatalogSource.Base + "Search.aspx?q=" + Uri.EscapeDataString(id)); }
                    catch { }
                });
            }) { IsBackground = true, Name = "catalog lookup" }.Start();
        }

        void OpenVendorSite()
        {
            var d = Selected;
            string url = d != null ? VendorCheck.PageFor(d, Inventory.Pc) : null;
            if (url == null) return;
            try { UserShell.OpenUrl(url); }
            catch (Exception ex) { Host.ShowToast(ex.Message, ToastKind.Error); }
        }

        void CopyIds()
        {
            var d = Selected;
            if (d == null || d.HardwareIds.Length == 0) return;
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, d.HardwareIds));
                Host.ShowToast(L.T("ИД оборудования скопированы", "Hardware IDs copied"), ToastKind.Success);
            }
            catch { }
        }

        public override string Status
        {
            get
            {
                if (!Inventory.DevicesLoaded) return L.T("Загрузка…", "Loading…");
                var all = Inventory.Devices;
                return string.Format(L.T("Устройств: {0}  ·  требуют внимания: {1}  ·  с обновлениями: {2}  ·  со сторонними драйверами: {3}",
                    "Devices: {0}  ·  need attention: {1}  ·  with updates: {2}  ·  third-party drivers: {3}"),
                    all.Count, all.Count(d => d.NeedsAttention), all.Count(d => d.Update != null || d.Vendor != null), all.Count(d => d.ThirdParty));
            }
        }
    }
}
