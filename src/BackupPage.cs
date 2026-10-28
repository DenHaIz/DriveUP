using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>
    /// Third-party drivers in the Windows driver store: save copies (to reinstall after a clean
    /// Windows install), restore them, and remove unused old versions that take up space.
    /// </summary>
    class BackupPage : Page
    {
        readonly Panel toolbar = new BufferedPanel(), listHost = new BufferedPanel(), content = new BufferedPanel(), bottom = new BufferedPanel();
        readonly Segmented filter;
        readonly FlatButton btnBackup, btnRestore, btnCleanup, btnOpen, btnChange;
        readonly SearchBox search = new SearchBox();
        readonly ThemedListView lv = new ThemedListView();
        readonly StatusText where = new StatusText();
        readonly System.Windows.Forms.Timer searchDebounce = new System.Windows.Forms.Timer { Interval = 200 };
        readonly ToolTip tip = new ToolTip();

        List<DriverPackage> packages = new List<DriverPackage>();
        bool loading, loaded, working;
        string lastBackup;

        public BackupPage(IHost host) : base(host)
        {
            Inventory.Changed += () => { if (loaded) stale = true; };
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = S(60);
            filter = new Segmented(new[] { "", "" }, new[] { "", "" }, 0);
            filter.SelectedIndexChanged += (s, e) => Transition.CrossFade(listHost, Render, 200);
            btnBackup = Button("\uE74E", ButtonKind.Primary);
            btnBackup.Click += (s, e) => DoBackup();
            btnRestore = Button("\uE777");
            btnRestore.Click += (s, e) => DoRestore();
            btnCleanup = Button("\uE74D", ButtonKind.Danger);
            btnCleanup.Click += (s, e) => DoCleanup();
            search.TextChanged += (s, e) => { searchDebounce.Stop(); searchDebounce.Start(); };
            searchDebounce.Tick += (s, e) =>
            {
                searchDebounce.Stop();
                Transition.CrossFade(listHost, Render, 160);
            };
            foreach (Control c in new Control[] { filter, btnBackup, btnRestore, btnCleanup, search }) toolbar.Controls.Add(c);
            toolbar.Resize += (s, e) => LayoutToolbar();

            lv.CheckBoxesEx = true;
            lv.Icons = Inventory.Icons;
            lv.DefaultGlyph = "\uE8A5";
            lv.Numeric = new[] { false, false, false, true, true, true, false };
            lv.SortColumn = 0;
            lv.DotColumn = 6;
            lv.CheckChanged += (s, e) => UpdateButtons();
            lv.ItemActivate += (s, e) => OpenStoreFolder();
            foreach (int w in new[] { 220, 170, 170, 120, 100, 100, 160 }) lv.Columns.Add("", S(w));
            lv.ColumnWeights = new[] { 0.2f, 0.16f, 0.16f, 0.12f, 0.1f, 0.1f, 0.16f };
            lv.ColumnMins = new[] { 150, 110, 110, 90, 80, 72, 110 };
            lv.Loading = true;
            lv.Dock = DockStyle.Fill;
            listHost.Padding = new Padding(1);
            listHost.Controls.Add(lv);

            bottom.Dock = DockStyle.Bottom;
            bottom.Height = S(46);
            btnOpen = new FlatButton("", "\uED25", ButtonKind.Ghost);
            btnOpen.SetBaseHeight(32);
            btnOpen.Click += (s, e) => OpenBackupFolder();
            btnChange = new FlatButton("", "\uE70F", ButtonKind.Ghost);
            btnChange.SetBaseHeight(32);
            btnChange.Click += (s, e) => ChangeFolder();
            bottom.Controls.Add(where);
            bottom.Controls.Add(btnOpen);
            bottom.Controls.Add(btnChange);
            bottom.Resize += (s, e) => LayoutBottom();

            content.Padding = new Padding(S(16), 0, S(16), 0);
            content.Dock = DockStyle.Fill;
            content.Controls.Add(listHost);
            listHost.Dock = DockStyle.Fill;
            Controls.Add(content);
            Controls.Add(bottom);
            Controls.Add(toolbar);
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
            filter.SetTexts(new[] { L.T("Все", "All"), L.T("Старые версии", "Old versions") });
            SetButton(btnRestore, L.T("Восстановить из копии…", "Restore from a backup…"));
            SetButton(btnOpen, L.T("Открыть", "Open"));
            SetButton(btnChange, L.T("Изменить…", "Change…"));
            search.SetCue(L.T("Поиск драйверов", "Search drivers"));
            tip.SetToolTip(btnBackup, L.T("Копии отмеченных драйверов — готовые к установке папки. Пригодятся после переустановки Windows.",
                "Copies of the ticked drivers — folders ready to install. Handy after reinstalling Windows."));
            tip.SetToolTip(btnRestore, L.T("Выберите папку с копией — драйверы откроются на вкладке «Установка»",
                "Choose a backup folder — the drivers open on the Install tab"));
            tip.SetToolTip(btnCleanup, L.T("Старые версии драйверов, которыми не пользуется ни одно устройство.\nПеред удалением DriveUP сохранит их копию.",
                "Old driver versions no device uses.\nDriveUP saves a copy of them before removing."));
            string[] cols = { L.T("Драйвер", "Driver"), L.T("Поставщик", "Provider"), L.T("Тип", "Type"), L.T("Версия", "Version"),
                              L.T("Дата", "Date"), L.T("Размер", "Size"), L.T("Состояние", "Status") };
            for (int i = 0; i < cols.Length; i++) lv.Columns[i].Text = cols[i];
            if (loaded) Render();
            UpdateWhere();
            UpdateButtons();
            LayoutToolbar();
            LayoutBottom();
        }

        public override void ApplyScale()
        {
            toolbar.Height = S(60);
            bottom.Height = S(46);
            content.Padding = new Padding(S(16), 0, S(16), 0);
            LayoutToolbar();
            LayoutBottom();
        }

        void LayoutToolbar()
        {
            int x = S(16), y = (toolbar.Height - S(Theme.ControlH)) / 2;
            filter.Location = new Point(x, y);
            x = filter.Right + S(20);
            foreach (var b in new[] { btnBackup, btnRestore, btnCleanup })
            {
                b.Location = new Point(x, y);
                x = b.Right + S(8);
            }
            int w = Math.Max(S(140), Math.Min(S(280), toolbar.Width - x - S(32)));
            search.SetBounds(toolbar.Width - S(16) - w, y, w, S(Theme.ControlH));
            search.Visible = toolbar.Width - x - S(32) >= S(140);
        }

        void LayoutBottom()
        {
            int y = (bottom.Height - btnOpen.Height) / 2;
            btnChange.Location = new Point(bottom.Width - S(16) - btnChange.Width, y);
            btnOpen.Location = new Point(btnChange.Left - S(6) - btnOpen.Width, y);
            where.SetBounds(S(18), 0, Math.Max(S(60), btnOpen.Left - S(28)), bottom.Height);
        }

        void UpdateWhere()
        {
            where.SetText(L.T("Копии сохраняются в: ", "Backups go to: ") + Settings.BackupDir);
        }

        public override void FocusSearch() { search.FocusBox(); }

        protected override void FirstActivate() { Load(); }

        // Drivers were installed meanwhile (devices reloaded): "in use" and "old version" may have changed.
        bool stale;

        protected override void OnActivated()
        {
            if (stale && loaded && !working && !loading)
            {
                stale = false;
                Load();
            }
        }

        public override void RefreshData()
        {
            if (!working) Load();
        }

        // ---------- loading ----------

        void Load()
        {
            if (loading) return;
            loading = true;
            Host.BeginBusy();
            UpdateButtons();
            var ticked = new HashSet<string>(packages.Where(p => !p.IsChecked).Select(p => p.Oem), StringComparer.OrdinalIgnoreCase);
            var th = new Thread(() =>
            {
                List<DriverPackage> list;
                try { list = DriverStore.Load(Devices.InfsInUse()); }
                catch { list = new List<DriverPackage>(); }   // includes a failed device list: show nothing rather than guess
                foreach (var p in list) if (ticked.Contains(p.Oem)) p.IsChecked = false;
                Post(() => ApplyData(list));
                foreach (var p in list)
                {
                    long size = DriverStore.FolderSize(p.Folder);
                    var pkg = p;
                    Post(() => { pkg.Size = size; UpdateRow(pkg); Host.UpdateStatus(); });
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            th.Start();
        }

        void ApplyData(List<DriverPackage> list)
        {
            loading = false;
            loaded = true;
            Host.EndBusy();
            foreach (var p in list) Inventory.EnsureClassIcon(p.ClassGuid);
            packages = list;
            Transition.CrossFade(listHost, Render, 260);
            UpdateButtons();
        }

        string[] Cells(DriverPackage p)
        {
            return new[]
            {
                p.Original, p.Provider, p.ClassName, p.VersionText, p.Date.HasValue ? L.Date(p.Date.Value) : "",
                p.Size >= 0 ? L.Size(p.Size) : "…", p.StateText
            };
        }

        void Render()
        {
            string q = search.Text.Trim();
            bool oldOnly = filter.SelectedIndex == 1;
            var rows = new List<ListViewItem>();
            foreach (var p in packages)
            {
                if (oldOnly && !p.Outdated) continue;
                if (q.Length > 0 && p.Original.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    p.Provider.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    p.ClassName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(new ListViewItem(Cells(p)) { Tag = p });
            }
            lv.BeginUpdate();
            lv.Items.Clear();
            lv.ResetRowStates();
            lv.Items.AddRange(rows.ToArray());
            lv.Sort();
            lv.EndUpdate();
            lv.Loading = !loaded;
            lv.FitLastColumn();
            if (q.Length > 0)
            {
                lv.EmptyGlyph = "\uE721";
                lv.EmptyText = L.T("Ничего не найдено по запросу ", "Nothing matches ") + L.Quote(q);
            }
            else if (oldOnly)
            {
                lv.EmptyGlyph = "\uE73E";
                lv.EmptyText = L.T("Старых версий нет — хранилище в порядке", "No old versions — the store is tidy");
            }
            else
            {
                lv.EmptyGlyph = "\uE8A5";
                lv.EmptyText = L.T("Сторонних драйверов нет: всё, что стоит, — из комплекта Windows", "No third-party drivers: everything installed comes with Windows");
            }
            Host.UpdateStatus();
        }

        ListViewItem RowOf(DriverPackage p)
        {
            foreach (ListViewItem it in lv.Items) if (it.Tag == p) return it;
            return null;
        }

        void UpdateRow(DriverPackage p)
        {
            var row = RowOf(p);
            if (row == null) return;
            var cells = Cells(p);
            for (int i = 0; i < cells.Length; i++)
                if (row.SubItems[i].Text != cells[i]) row.SubItems[i].Text = cells[i];
        }

        void UpdateButtons()
        {
            int sel = packages.Count(p => p.IsChecked);
            int old = packages.Count(p => p.Outdated);
            string b1 = L.T("Сохранить копию", "Back up") + (sel > 0 ? " (" + sel + ")" : "");
            if (btnBackup.Text != b1) SetButton(btnBackup, b1);
            string b3 = L.T("Удалить старые версии", "Remove old versions") + (old > 0 ? " (" + old + ")" : "");
            if (btnCleanup.Text != b3) SetButton(btnCleanup, b3);
            btnBackup.Enabled = sel > 0 && !working && !loading;
            btnCleanup.Enabled = old > 0 && !working && !loading;
            btnRestore.Enabled = !working;
            filter.Enabled = !working;
            LayoutToolbar();
            Host.UpdateStatus();
        }

        // ---------- actions ----------

        void DoBackup()
        {
            var items = packages.Where(p => p.IsChecked).ToList();
            if (items.Count == 0 || working || !Host.CanStartOperation()) return;
            string dest;
            try { dest = DriverStore.NewBackupFolder(Settings.BackupDir); }
            catch (Exception ex)
            {
                Host.ShowToast(L.T("Не удалось создать папку: ", "Could not create the folder: ") + ex.Message, ToastKind.Error);
                return;
            }
            working = true;
            UpdateButtons();
            Host.BeginBusy();
            var th = new Thread(() =>
            {
                var errors = new List<string>();
                int ok = DriverStore.Backup(items, dest, (done, total) =>
                    Post(() => where.SetText(string.Format(L.T("Копирование: {0} из {1}…", "Copying: {0} of {1}…"), done, total))), errors);
                Post(() =>
                {
                    working = false;
                    Host.EndBusy();
                    lastBackup = dest;
                    UpdateWhere();
                    UpdateButtons();
                    Journal.Add(JournalKind.Backup, ok, errors.Count, dest);
                    foreach (var p in items)
                    {
                        var row = RowOf(p);
                        if (row != null) lv.Flash(new[] { row }, Theme.Current.Success);
                    }
                    if (errors.Count > 0 && ok == 0)
                        Host.ShowToast(L.T("Копия не сохранена: ", "Backup failed: ") + errors[0], ToastKind.Error);
                    else
                        Host.ShowToast(L.T("Копия сохранена: ", "Backup saved: ") + L.Count(ok, "драйвер", "драйвера", "драйверов", "driver", "drivers") +
                            (errors.Count > 0 ? L.T(" · не удалось: ", " · failed: ") + errors.Count : "") + L.T(" — «Открыть» внизу", " — \"Open\" below"),
                            errors.Count > 0 ? ToastKind.Info : ToastKind.Success);
                });
            }) { IsBackground = true };
            th.Start();
        }

        void DoRestore()
        {
            string start = lastBackup ?? (Directory.Exists(Settings.BackupDir) ? Settings.BackupDir : null);
            string path = FolderPicker.Pick(Host.Window, L.T("Папка с резервной копией драйверов", "Driver backup folder"), start);
            if (path != null) Host.InstallFrom(new[] { path });
        }

        void DoCleanup()
        {
            var old = packages.Where(p => p.Outdated).ToList();
            if (old.Count == 0 || working || !Host.CanStartOperation()) return;
            long bytes = old.Where(p => p.Size > 0).Sum(p => p.Size);
            string msg = L.T("Удалить старые версии драйверов (" + old.Count + ")?", "Remove old driver versions (" + old.Count + ")?") + "\n\n" +
                L.T("Ни одно устройство ими не пользуется: у каждого есть более новая версия того же драйвера.",
                    "No device uses them: each has a newer version of the same driver.") +
                (bytes > 0 ? L.T(" Освободится ", " Frees ") + L.Size(bytes) + "." : "") + "\n" +
                L.T("Перед удалением DriveUP сохранит их копию в папке резервных копий.", "DriveUP saves a copy of them to the backup folder first.");
            if (!ThemedDialog.Confirm(Host.Window, L.T("Старые версии драйверов", "Old driver versions"), msg, L.T("Удалить", "Remove"), "\uE74D", true)) return;

            string dest;
            try { dest = DriverStore.NewBackupFolder(Path.Combine(Settings.BackupDir, L.T("Удалённые старые версии", "Removed old versions"))); }
            catch (Exception ex)
            {
                Host.ShowToast(L.T("Не удалось создать папку для копии: ", "Could not create the backup folder: ") + ex.Message, ToastKind.Error);
                return;
            }
            working = true;
            UpdateButtons();
            Host.BeginBusy();
            var th = new Thread(() =>
            {
                var errors = new List<string>();
                var copied = new HashSet<DriverPackage>();
                DriverStore.Backup(old, dest, (d, t) => { }, errors, copied);
                // Without a copy a package stays: removal must always be undoable.
                var removed = new List<DriverPackage>();
                var failed = new List<Tuple<DriverPackage, string>>();
                // The list may be minutes old: ask Windows again which packages devices use right now.
                HashSet<string> usedNow = null;
                try { usedNow = Devices.InfsInUse(); } catch { }
                foreach (var p in old)
                {
                    if (usedNow == null) { failed.Add(Tuple.Create(p, L.T("не удалось проверить, используется ли пакет", "could not check whether the package is used"))); continue; }
                    if (usedNow.Contains(p.Oem)) { failed.Add(Tuple.Create(p, L.T("теперь используется устройством", "now used by a device"))); continue; }
                    if (!copied.Contains(p)) { failed.Add(Tuple.Create(p, L.T("копия не сохранена", "no backup copy"))); continue; }
                    string err;
                    if (DriverStore.Remove(p, out err)) removed.Add(p);
                    else failed.Add(Tuple.Create(p, err));
                }
                Post(() => CleanupDone(removed, failed, dest));
            }) { IsBackground = true };
            th.Start();
        }

        void CleanupDone(List<DriverPackage> removed, List<Tuple<DriverPackage, string>> failed, string dest)
        {
            working = false;
            Host.EndBusy();
            long bytes = removed.Where(p => p.Size > 0).Sum(p => p.Size);
            var rows = removed.Select(RowOf).Where(r => r != null).ToList();
            foreach (var p in removed) packages.Remove(p);
            lv.FadeOutAndRemove(rows, () => { UpdateButtons(); Host.UpdateStatus(); });
            foreach (var f in failed)
            {
                f.Item1.Failed = true;
                f.Item1.Status = L.T("Не удалено: ", "Not removed: ") + f.Item2;
                UpdateRow(f.Item1);
                var row = RowOf(f.Item1);
                if (row != null) lv.Flash(new[] { row }, Theme.Current.Danger);
            }
            UpdateButtons();
            if (removed.Count > 0)
                Journal.Add(JournalKind.Cleanup, removed.Count, failed.Count,
                    string.Join(", ", removed.Select(p => p.Original + " " + p.VersionText)) + " · " + L.T("копия: ", "copy: ") + dest);
            string text = removed.Count > 0
                ? L.T("Удалено старых версий: ", "Old versions removed: ") + removed.Count + (bytes > 0 ? " · " + L.Size(bytes) : "")
                : L.T("Ничего не удалено: ", "Nothing removed: ") + (failed.Count > 0 ? failed[0].Item2 : "");
            if (removed.Count > 0 && failed.Count > 0) text += L.T(" · не удалось: ", " · failed: ") + failed.Count;
            Host.ShowToast(text, removed.Count == 0 ? ToastKind.Error : (failed.Count > 0 ? ToastKind.Info : ToastKind.Success));
        }

        void OpenBackupFolder()
        {
            string path = lastBackup ?? Settings.BackupDir;
            try
            {
                Directory.CreateDirectory(path);
                UserShell.Explore("\"" + path + "\"");
            }
            catch (Exception ex) { Host.ShowToast(ex.Message, ToastKind.Error); }
        }

        void OpenStoreFolder()
        {
            var row = lv.FocusedItem;
            var p = row != null ? row.Tag as DriverPackage : null;
            if (p == null || !Directory.Exists(p.Folder)) return;
            try { UserShell.Explore("\"" + p.Folder + "\""); }
            catch { }
        }

        void ChangeFolder()
        {
            string path = FolderPicker.Pick(Host.Window, L.T("Куда сохранять резервные копии", "Where to save backups"), Settings.BackupDir);
            if (path == null) return;
            Settings.BackupRoot = path.Equals(Settings.DefaultBackupRoot, StringComparison.OrdinalIgnoreCase) ? "" : path;
            Settings.Save();
            lastBackup = null;
            UpdateWhere();
        }

        public bool Working { get { return working; } }

        public override string Status
        {
            get
            {
                if (!loaded) return L.T("Загрузка…", "Loading…");
                if (working) return L.T("Работаем с хранилищем драйверов…", "Working with the driver store…");
                var old = packages.Where(p => p.Outdated).ToList();
                return string.Format(L.T("Сторонних драйверов: {0}  ·  используются: {1}  ·  старых версий: {2} ({3})  ·  всего занимают: {4}",
                    "Third-party drivers: {0}  ·  in use: {1}  ·  old versions: {2} ({3})  ·  total size: {4}"),
                    packages.Count, packages.Count(p => p.InUse), old.Count, L.Size(old.Where(p => p.Size > 0).Sum(p => p.Size)),
                    L.Size(packages.Where(p => p.Size > 0).Sum(p => p.Size)));
            }
        }
    }
}
