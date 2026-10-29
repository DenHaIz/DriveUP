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
    /// Install a driver from a file: drop a folder, .inf, .zip or .cab (or pick one), see which
    /// devices each driver is for, tick and install. Restoring a backup lands here too.
    /// </summary>
    class InstallPage : Page
    {
        readonly DropZone drop = new DropZone();
        readonly FlatButton btnFolder, btnFile, btnInstall, btnClear;
        readonly Panel bar = new BufferedPanel(), listHost = new BufferedPanel();
        readonly StatusText summary = new StatusText { Flags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis };
        readonly ThemedListView lv = new ThemedListView();
        readonly ToolTip tip = new ToolTip();
        readonly List<InfDriver> drivers = new List<InfDriver>();
        readonly object compactKey = new object();
        float compact;
        bool reading, installing;
        string lastFolder;
        /// <summary>Downloads that arrived while files were read or drivers installed: added right after.</summary>
        readonly List<DownloadJob> waiting = new List<DownloadJob>();

        public InstallPage(IHost host) : base(host)
        {
            drop.FilesDropped += files => AddPaths(files);
            btnFolder = new FlatButton("", "\uF12B", ButtonKind.Primary);
            btnFolder.Click += (s, e) => PickFolder();
            btnFile = new FlatButton("", "\uE8E5");
            btnFile.Click += (s, e) => PickFile();
            drop.Controls.Add(btnFolder);
            drop.Controls.Add(btnFile);
            drop.Resize += (s, e) => LayoutDropButtons();
            Controls.Add(drop);

            btnInstall = new FlatButton("", "\uE896", ButtonKind.Primary);
            btnInstall.Click += (s, e) => DoInstall();
            btnClear = new FlatButton("", "\uE894", ButtonKind.Ghost);
            btnClear.Click += (s, e) => ClearList();
            bar.Controls.Add(btnInstall);
            bar.Controls.Add(btnClear);
            bar.Controls.Add(summary);
            bar.Resize += (s, e) => LayoutBar();
            bar.Visible = false;
            Controls.Add(bar);

            lv.CheckBoxesEx = true;
            lv.Icons = Inventory.Icons;
            lv.DefaultGlyph = "\uE8A5";
            lv.Numeric = new[] { false, false, false, true, true, false, false };
            lv.SortColumn = 5;
            lv.DotColumn = 6;
            lv.CheckChanged += (s, e) => UpdateButtons();
            lv.ItemActivate += (s, e) => OpenFolderOf();
            foreach (int w in new[] { 230, 150, 150, 110, 100, 230, 180 }) lv.Columns.Add("", S(w));
            lv.ColumnWeights = new[] { 0.19f, 0.12f, 0.13f, 0.1f, 0.09f, 0.21f, 0.16f };
            lv.ColumnMins = new[] { 150, 90, 100, 80, 80, 140, 120 };
            lv.Dock = DockStyle.Fill;
            listHost.Padding = new Padding(1);
            listHost.Controls.Add(lv);
            listHost.Visible = false;
            Controls.Add(listHost);

            Resize += (s, e) => LayoutPage();
            Downloads.Finished += job => { if (!IsDisposed && job.State == DownloadState.Done) Receive(job); };
            Inventory.Changed += () =>
            {
                // New devices (after an install or a rescan) may change what each driver is for.
                if (IsDisposed || installing || reading || drivers.Count == 0) return;
                foreach (var d in drivers)
                {
                    // An installer's device is the one the vendor named; it has no hardware IDs to match.
                    if (d.IsInstaller) continue;
                    d.Matches.Clear();
                    if (d.WrongArch) continue;
                    foreach (var dev in Inventory.Devices)
                        foreach (var id in d.HardwareIds)
                            if (dev.MatchesId(id)) { d.Matches.Add(dev); break; }
                }
                Render();
            };
        }

        // ---------- layout ----------

        void LayoutPage()
        {
            int pad = S(16);
            int tall = Height - pad * 2;
            int small = S(96);
            int dropH = (int)Math.Round(Anim.Lerp(tall, small, compact));
            drop.SetBounds(pad, pad, Width - pad * 2, dropH);
            int barY = drop.Bottom + S(10);
            bar.SetBounds(pad, barY, Width - pad * 2, S(44));
            int listY = bar.Bottom + S(6);
            listHost.SetBounds(pad, listY, Width - pad * 2, Math.Max(S(40), Height - listY - S(4)));
            LayoutDropButtons();
        }

        void LayoutDropButtons()
        {
            int gap = S(8);
            int w = btnFolder.Width + gap + btnFile.Width;
            var o = drop.ButtonsOrigin(w, btnFolder.Height);
            btnFolder.Location = o;
            btnFile.Location = new Point(btnFolder.Right + gap, o.Y);
            drop.Invalidate();
        }

        void LayoutBar()
        {
            int y = (bar.Height - btnInstall.Height) / 2;
            btnInstall.Location = new Point(0, y);
            btnClear.Location = new Point(btnInstall.Right + S(8), y);
            int x = btnClear.Right + S(16);
            summary.SetBounds(x, 0, Math.Max(S(40), bar.Width - x - S(4)), bar.Height);
        }

        /// <summary>The drop area shrinks to a strip once there is a list, and grows back when it is cleared.</summary>
        void SetCompact(bool on)
        {
            float target = on ? 1 : 0;
            if (Math.Abs(compact - target) < 0.001f) return;
            float from = compact;
            if (on)
            {
                bar.Visible = listHost.Visible = true;
                btnInstall.Appear();
            }
            Anim.Run(compactKey, 420, t =>
            {
                compact = Anim.Lerp(from, target, t);
                // The drop area lays its content out for this value, so it glides along.
                drop.Compact = compact;
                LayoutPage();
            }, () =>
            {
                if (!on) bar.Visible = listHost.Visible = false;
            }, Anim.EaseInOut);
        }

        public override void ApplyScale()
        {
            btnFolder.ApplyScale();
            btnFile.ApplyScale();
            LayoutBar();
            LayoutPage();
        }

        // ---------- language / theme ----------

        public override void ApplyTheme()
        {
            base.ApplyTheme();
            foreach (Control c in Controls) c.BackColor = Theme.Current.Bg;
            listHost.BackColor = Theme.Current.Border;
            lv.ApplyTheme();
            drop.Invalidate();
        }

        public override void ApplyLanguage()
        {
            drop.Title = L.T("Перетащите сюда драйвер", "Drop a driver here");
            drop.Subtitle = L.T("Папку, .inf, .zip или .cab — DriveUP найдёт в них драйверы и подходящие устройства",
                "A folder, .inf, .zip or .cab — DriveUP finds the drivers in it and the devices they are for");
            drop.BusyText = L.T("Ищем драйверы…", "Looking for drivers…");
            SetButton(btnFolder, L.T("Выбрать папку…", "Choose a folder…"));
            SetButton(btnFile, L.T("Выбрать файл…", "Choose a file…"));
            SetButton(btnClear, L.T("Очистить список", "Clear the list"));
            tip.SetToolTip(btnFile, L.T(".inf, .zip или .cab (например, из каталога Центра обновления Майкрософт)",
                ".inf, .zip or .cab (for example from the Microsoft Update Catalog)"));
            tip.SetToolTip(btnInstall, L.T("Драйвер добавляется в хранилище Windows и ставится на устройства, которым подходит лучше текущего",
                "The driver is added to the Windows driver store and installed on devices it suits better than the current one"));
            string[] cols = { L.T("Драйвер", "Driver"), L.T("Тип", "Type"), L.T("Поставщик", "Provider"), L.T("Версия", "Version"),
                              L.T("Дата", "Date"), L.T("Подходит для", "Suits"), L.T("Состояние", "Status") };
            for (int i = 0; i < cols.Length; i++) lv.Columns[i].Text = cols[i];
            lv.EmptyText = L.T("Драйверов не найдено", "No drivers found");
            lv.EmptyGlyph = "\uE8A5";
            Render();
            LayoutDropButtons();
            LayoutBar();
            drop.Invalidate();
        }

        // ---------- adding files ----------

        void PickFolder()
        {
            string path = FolderPicker.Pick(Host.Window, L.T("Папка с драйвером", "Folder with a driver"), lastFolder);
            if (path != null) AddPaths(new[] { path });
        }

        void PickFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = L.T("Файл драйвера", "Driver file"),
                Filter = L.T("Драйверы", "Drivers") + " (*.inf;*.zip;*.cab)|*.inf;*.zip;*.cab|" + L.T("Все файлы", "All files") + " (*.*)|*.*",
                Multiselect = true,
                // Keep the current directory where it was (programs started by name never look in a picked folder).
                RestoreDirectory = true,
                InitialDirectory = lastFolder ?? "",
            })
            {
                if (dlg.ShowDialog(Host.Window) == DialogResult.OK) AddPaths(dlg.FileNames);
            }
        }

        /// <summary>
        /// A driver the download queue fetched: a .cab / .zip is unpacked into the list like a dropped file
        /// (without a toast of its own), a vendor's installer becomes a row of its own. It waits while
        /// another read or an install runs.
        /// </summary>
        void Receive(DownloadJob job)
        {
            if (drivers.Any(d => d.Job == job)) return;
            if (reading || installing)
            {
                if (!waiting.Contains(job)) waiting.Add(job);
                return;
            }
            if (!job.IsInstaller)
            {
                AddPaths(new[] { job.File }, job);
                return;
            }
            var v = job.Update;
            Version ver;
            var row = new InfDriver
            {
                Path = job.File, Name = v.Title, ClassName = L.T("Установщик производителя", "Vendor's installer"), Provider = v.Vendor,
                VersionText = v.VersionText, Date = v.Date, IsDevice = true, IsChecked = true, Job = job,
                ClassGuid = v.Device != null ? v.Device.ClassGuid : "",
                Version = Version.TryParse(v.VersionText, out ver) ? ver : null,
            };
            if (v.Device != null) row.Matches.Add(v.Device);
            Inventory.EnsureClassIcon(row.ClassGuid);
            drivers.Add(row);
            if (compact < 0.01f) Render();
            else Transition.CrossFade(listHost, Render, 260);
            SetCompact(true);
            UpdateButtons();
        }

        /// <summary>Takes the downloads that waited for a read or an install to end.</summary>
        void TakeWaiting()
        {
            while (waiting.Count > 0 && !reading && !installing)
            {
                var job = waiting[0];
                waiting.RemoveAt(0);
                Receive(job);
            }
        }

        /// <summary>Loads drivers from folders/files; called by drops, pickers and "restore from backup".</summary>
        public void AddPaths(string[] paths)
        {
            AddPaths(paths, null);
        }

        /// <summary>from: the download it came from (no toast of its own then: the queue reports).</summary>
        void AddPaths(string[] paths, DownloadJob from)
        {
            if (paths.Length == 0) return;
            if (reading || installing)
            {
                Host.ShowToast(installing ? L.T("Дождитесь окончания установки — потом добавьте файлы", "Wait for the installation to finish, then add the files")
                                          : L.T("Ещё читаем предыдущие файлы — повторите через пару секунд", "Still reading the previous files — try again in a moment"), ToastKind.Info);
                return;
            }
            reading = true;
            try { lastFolder = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0]); } catch { }
            drop.Busy = true;
            Host.BeginBusy();
            UpdateButtons();
            var known = new HashSet<string>(drivers.Select(d => d.Path), StringComparer.OrdinalIgnoreCase);
            var th = new Thread(() =>
            {
                var installers = new List<string>();
                var errors = new List<string>();
                var infs = InfFiles.Collect(paths, installers, errors);
                if (!Inventory.DevicesLoaded)
                {
                    Dictionary<string, Bitmap> icons;
                    var devs = Inventory.LoadDevices(out icons);
                    RunUI(() => { if (!Inventory.DevicesLoaded) Inventory.SetDevices(devs, icons); });
                }
                List<Device> devices = null;
                RunUI(() => devices = Inventory.Devices);
                var found = new List<InfDriver>();
                foreach (var f in infs)
                {
                    if (known.Contains(f)) continue;
                    try
                    {
                        var parsed = InfFiles.Parse(f, devices);
                        parsed.Job = from;
                        found.Add(parsed);
                    }
                    catch (Exception ex) { errors.Add(Path.GetFileName(f) + ": " + ex.Message); }
                }
                Post(() => Added(found, installers, errors, from != null));
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        void Added(List<InfDriver> found, List<string> installers, List<string> errors, bool quiet)
        {
            reading = false;
            drop.Busy = false;
            Host.EndBusy();
            foreach (var d in found) Inventory.EnsureClassIcon(d.ClassGuid);
            drivers.AddRange(found);
            if (drivers.Count > 0)
            {
                // First time the list is uncovered by the shrinking drop area: fill it before it shows.
                if (compact < 0.01f) Render();
                else Transition.CrossFade(listHost, Render, 260);
                SetCompact(true);
            }
            UpdateButtons();

            int fits = found.Count(d => d.Matches.Count > 0);
            if (quiet)
            {
                if (found.Count == 0) Host.ShowToast(L.T("В скачанном пакете нет драйверов (.inf)", "The downloaded package has no drivers (.inf)"), ToastKind.Info);
            }
            else if (found.Count > 0)
                Host.ShowToast(L.T("Найдено ", "Found ") + L.Count(found.Count, "драйвер", "драйвера", "драйверов", "driver", "drivers") +
                    (fits > 0 ? L.T(" · подходят устройствам: ", " · suit devices: ") + fits : L.T(" · подходящих устройств нет", " · no matching devices")),
                    fits > 0 ? ToastKind.Success : ToastKind.Info);
            else if (installers.Count == 0)
                Host.ShowToast(errors.Count > 0 ? errors[0] : L.T("Драйверов (.inf) здесь нет", "No drivers (.inf) here"), ToastKind.Info);

            if (installers.Count > 0)
            {
                string path = installers[0];
                string others = installers.Count > 1
                    ? "\n\n" + L.T("Ещё программы установки (добавьте их по одной): ", "More installers (add them one at a time): ") +
                      string.Join(", ", installers.Skip(1).Take(5).Select(System.IO.Path.GetFileName)) + (installers.Count > 6 ? "…" : "")
                    : "";
                if (ThemedDialog.Confirm(Host.Window, L.T("Программа установки", "Installer"),
                    path + others + "\n\n" + L.T("Это программа установки, а не файл драйвера. Запустить её?\nОна запустится как при двойном щелчке и сама запросит права администратора — проверьте издателя в окне Windows.",
                        "This is an installer, not a driver file. Run it?\nIt starts as on a double-click and asks for administrator rights itself — check the publisher in the Windows prompt."),
                    L.T("Запустить", "Run"), "\uE768", false))
                {
                    try { UserShell.OpenAsUser(path); }
                    catch (Exception ex) { Host.ShowToast(ex.Message, ToastKind.Error); }
                }
            }
            TakeWaiting();
        }

        /// <summary>The user chose not to install without a restore point: everything stays as it was.</summary>
        void InstallCancelled(List<InfDriver> queue)
        {
            installing = false;
            Host.EndBusy();
            foreach (var d in queue) { d.State = InfState.Ready; UpdateRow(d); }
            UpdateButtons();
            Host.ShowToast(L.T("Установка отменена — ничего не изменено", "Install cancelled — nothing changed"), ToastKind.Info);
            TakeWaiting();
        }

        void ClearList()
        {
            if (installing) return;
            drivers.Clear();
            lv.Items.Clear();
            SetCompact(false);
            UpdateButtons();
            Host.UpdateStatus();
        }

        string[] Cells(InfDriver d)
        {
            return new[]
            {
                d.Name, d.ClassName, d.Provider, d.VersionText, d.Date.HasValue ? L.Date(d.Date.Value) : "", d.FitText, d.StateText
            };
        }

        void Render()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            lv.ResetRowStates();
            foreach (var d in drivers) lv.Items.Add(new ListViewItem(Cells(d)) { Tag = d });
            lv.Sort();
            lv.EndUpdate();
            lv.FitLastColumn();
            UpdateButtons();
        }

        ListViewItem RowOf(InfDriver d)
        {
            foreach (ListViewItem it in lv.Items) if (it.Tag == d) return it;
            return null;
        }

        void UpdateRow(InfDriver d)
        {
            var row = RowOf(d);
            if (row == null) return;
            var cells = Cells(d);
            for (int i = 0; i < cells.Length; i++)
                if (row.SubItems[i].Text != cells[i]) row.SubItems[i].Text = cells[i];
            lv.Invalidate(row.Bounds);
        }

        void UpdateButtons()
        {
            int sel = drivers.Count(d => d.IsChecked && d.Checkable);
            string text = L.T("Установить отмеченные", "Install ticked") + (sel > 0 ? " (" + sel + ")" : "");
            if (btnInstall.Text != text) SetButton(btnInstall, text);
            btnInstall.Enabled = sel > 0 && !installing && !reading;
            btnClear.Enabled = drivers.Count > 0 && !installing;
            btnFolder.Enabled = btnFile.Enabled = !installing && !reading;
            int fits = drivers.Count(d => d.Matches.Count > 0);
            summary.SetText(drivers.Count == 0 ? "" :
                L.Count(drivers.Count, "драйвер", "драйвера", "драйверов", "driver", "drivers") +
                L.T("  ·  подходят устройствам: ", "  ·  suit devices: ") + fits +
                L.T("  ·  Ctrl+A — отметить все", "  ·  Ctrl+A ticks all"));
            LayoutBar();
            Host.UpdateStatus();
        }

        void OpenFolderOf()
        {
            var row = lv.FocusedItem;
            var d = row != null ? row.Tag as InfDriver : null;
            if (d == null) return;
            try { UserShell.Explore("/select,\"" + d.Path + "\""); }
            catch { }
        }

        // ---------- installing ----------

        void DoInstall()
        {
            var queue = drivers.Where(d => d.IsChecked && d.Checkable).ToList();
            if (queue.Count == 0 || installing || !Host.CanStartOperation()) return;
            // Drivers run in the kernel: say what is about to be installed and from where.
            var sources = queue.Select(d => d.Provider.Length > 0 ? d.Provider : L.T("поставщик не указан", "no provider")).Distinct().Take(4).ToList();
            var folders = queue.Where(d => d.Job == null).Select(d => System.IO.Path.GetDirectoryName(d.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var setups = queue.Where(d => d.IsInstaller).ToList();
            int fromQueue = queue.Count(d => d.Job != null && !d.IsInstaller);
            string where = (folders.Count > 0 ? L.T("Папка: ", "Folder: ") + (folders.Count == 1 ? folders[0] : folders[0] + L.T(" и ещё ", " and ") + (folders.Count - 1)) + "\n" : "") +
                (fromQueue > 0 ? L.T("Скачано с Microsoft Update и сайтов производителей: ", "Downloaded from Microsoft Update and vendors' sites: ") + fromQueue + "\n" : "") +
                (setups.Count > 0 ? L.T("Установщики производителей (подпись проверена): ", "Vendors' installers (signature checked): ") +
                    string.Join(", ", setups.Select(d => d.Job.Signer.Length > 0 ? d.Job.Signer : d.Provider).Distinct()) + "\n" +
                    (setups.Any(d => d.Job.Update.SilentArgs == null) ? L.T("Некоторые откроют своё окно — пройдите его шаги.\n", "Some open their own window — follow its steps.\n") : "") : "");
            if (!ThemedDialog.Confirm(Host.Window, L.T("Установить драйверы?", "Install the drivers?"),
                    L.Count(queue.Count, "драйвер", "драйвера", "драйверов", "driver", "drivers") + L.T(" от: ", " from: ") + string.Join(", ", sources) + "\n" +
                    where + "\n" +
                    L.T("Устанавливайте драйверы только из источников, которым доверяете.", "Install drivers only from sources you trust."),
                    L.T("Установить", "Install"), "\uE896", false))
                return;
            installing = true;
            foreach (var d in queue) { d.State = InfState.Ready; d.Error = ""; UpdateRow(d); }
            UpdateButtons();
            Host.BeginBusy();
            IntPtr owner = Host.Window.Handle;
            bool wantPoint = Settings.RestorePoint;
            var th = new Thread(() =>
            {
                string rpError = null;
                bool rpOk = false;
                if (wantPoint)
                {
                    Post(() => summary.SetText(L.T("Создаём точку восстановления…", "Creating a restore point…")));
                    rpError = SysRestore.Create("DriveUP: " + L.T("установка драйвера", "driver install"), out rpOk);
                    if (!SysRestore.Resolve(AskUI, Host.Window, "DriveUP: " + L.T("установка драйвера", "driver install"), ref rpError, ref rpOk))
                    {
                        Post(() => InstallCancelled(queue));
                        return;
                    }
                    if (rpOk) rpError = null;
                }
                bool reboot = false;
                var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in queue)
                {
                    var drv = d;
                    RunUI(() =>
                    {
                        drv.State = InfState.Installing;
                        UpdateRow(drv);
                        summary.SetText(L.T("Установка: ", "Installing: ") + drv.Name);
                    });
                    bool rb = false;
                    string prepError = null;
                    int code;
                    if (drv.IsInstaller) code = VendorInstall.RunInstaller(drv.Job.Update, drv.Path, out rb, out prepError) ? 0 : -1;
                    else
                    {
                        string path = InfFiles.Prepare(drv, copies, out prepError);
                        code = path != null ? InfFiles.Install(path, owner, out rb) : -1;
                    }
                    if (rb) reboot = true;
                    RunUI(() =>
                    {
                        if (code == -1)
                        {
                            drv.State = InfState.Failed;
                            drv.Error = prepError;
                        }
                        else if (code == 0) drv.State = InfState.Installed;
                        else if (code == 259) drv.State = InfState.Staged;
                        else
                        {
                            drv.State = InfState.Failed;
                            drv.Error = InfFiles.ErrorText(code);
                        }
                        drv.IsChecked = drv.State == InfState.Failed;
                        UpdateRow(drv);
                        var row = RowOf(drv);
                        if (row != null) lv.Flash(new[] { row }, drv.State == InfState.Failed ? Theme.Current.Danger : Theme.Current.Success);
                    });
                }
                // Versions changed: read the devices again.
                Dictionary<string, Bitmap> icons;
                var devs = Inventory.LoadDevices(out icons);
                Post(() => InstallDone(queue, reboot, rpError, devs, icons));
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }

        void InstallDone(List<InfDriver> queue, bool reboot, string rpError, List<Device> devs, Dictionary<string, Bitmap> icons)
        {
            installing = false;
            Host.EndBusy();
            Inventory.SetDevices(devs, icons);
            int ok = queue.Count(d => d.State == InfState.Installed), staged = queue.Count(d => d.State == InfState.Staged);
            var failed = queue.Where(d => d.State == InfState.Failed).ToList();
            Journal.Add(JournalKind.Install, ok + staged, failed.Count,
                string.Join(", ", queue.Select(d => d.Name + " " + d.VersionText + " — " + d.StateText)));
            UpdateButtons();
            TakeWaiting();

            string text;
            if (ok > 0) text = L.T("Установлено драйверов: ", "Drivers installed: ") + ok + (staged > 0 ? L.T(" · в хранилище: ", " · stored: ") + staged : "");
            else if (staged > 0) text = L.T("Добавлено в хранилище: ", "Added to the store: ") + staged + L.T(" — подходящих устройств сейчас нет или драйвер не новее", " — no device needs it now or it is not newer");
            else text = L.T("Не удалось установить: ", "Could not install: ") + (failed.Count > 0 ? failed[0].Error : "");
            if (failed.Count > 0 && (ok > 0 || staged > 0)) text += L.T(" · ошибок: ", " · failed: ") + failed.Count;
            if (rpError != null && ok > 0) text += L.T(" · без точки восстановления", " · no restore point");
            Host.ShowToast(text, ok + staged == 0 ? ToastKind.Error : (failed.Count > 0 ? ToastKind.Info : ToastKind.Success));

            if (reboot)
                Anim.Delay(800, () =>
                {
                    if (ThemedDialog.Confirm(Host.Window, L.T("Нужна перезагрузка", "Restart needed"),
                        L.T("Драйвер начнёт работать после перезагрузки.\nСохраните открытые документы и перезагрузите компьютер.",
                            "The driver starts working after a restart.\nSave your open documents and restart the PC."),
                        L.T("Перезагрузить", "Restart now"), "\uE777", false))
                        try
                        {
                            UserShell.RestartPc();
                        }
                        catch (Exception ex) { Host.ShowToast(ex.Message, ToastKind.Error); }
                });
        }

        public bool Installing { get { return installing; } }

        public override string Status
        {
            get
            {
                if (installing) return L.T("Установка драйверов…", "Installing drivers…");
                if (reading) return L.T("Поиск драйверов в выбранном…", "Looking for drivers in the selection…");
                if (drivers.Count == 0) return L.T("Перетащите папку с драйвером, .inf, .zip или .cab — или выберите их кнопками",
                    "Drop a driver folder, .inf, .zip or .cab — or choose them with the buttons");
                return string.Format(L.T("Драйверов: {0}  ·  отмечено: {1}  ·  установлено: {2}", "Drivers: {0}  ·  ticked: {1}  ·  installed: {2}"),
                    drivers.Count, drivers.Count(d => d.IsChecked && d.Checkable), drivers.Count(d => d.State == InfState.Installed));
            }
        }
    }
}
