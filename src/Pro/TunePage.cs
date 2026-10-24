using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>
    /// DriveUP Pro: system tuning. Left: the smart pick and the three modes. Right: what the
    /// chosen mode changes on this computer (now → new, and why), or everything changed so far
    /// (was → now) with a Return button on each. Every change can be undone, one by one or all.
    /// </summary>
    class TunePage : Page
    {
        readonly Card smartCard = new Card(), listCard = new Card();
        // What the computer is (one dim line) and what suits it (up to two lines).
        readonly StatusText smartText = new StatusText { Flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis };
        readonly StatusText recText = new StatusText { Flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis };
        readonly FlatButton btnSmart, btnMeasure, btnApply, btnReturnAll;
        readonly Segmented view;
        readonly TuneList list = new TuneList();
        readonly StatusText info = new StatusText();
        readonly List<ModeCard> cards = new List<ModeCard>();
        readonly ToolTip tip = new ToolTip();

        Machine machine;
        TuneMode selected, recommended;
        /// <summary>The mode the plan rows on screen were built for (Apply uses this one), and whether a plan is being read.</summary>
        TuneMode rowsMode;
        bool planLoading;
        string reason = "";
        bool loaded, working, showChanged;
        int loadSeq;

        public TunePage(IHost host) : base(host)
        {
            smartCard.TitleBase = 46;
            btnSmart = new FlatButton("", "\uE945", ButtonKind.Primary);
            btnSmart.SetBaseHeight(34);
            btnSmart.Click += (s, e) => SmartPick();
            smartText.Font = Theme.SmallFont;
            smartCard.Controls.Add(smartText);
            smartCard.Controls.Add(recText);
            smartCard.Controls.Add(btnSmart);
            btnMeasure = new FlatButton("", "\uE9D9");
            btnMeasure.SetBaseHeight(34);
            btnMeasure.Click += (s, e) => MeasureNow();
            smartCard.Controls.Add(btnMeasure);
            smartCard.Resize += (s, e) => LayoutSmart();
            Controls.Add(smartCard);

            foreach (var m in Tuner.Modes)
            {
                var c = new ModeCard(m);
                c.Click += (s, e) => { if (!working) Select(((ModeCard)s).Mode, true); };
                cards.Add(c);
                Controls.Add(c);
            }

            view = new Segmented(new[] { "", "" }, new[] { "\uE8FD", "\uE81C" }, 0);
            view.SelectedIndexChanged += (s, e) => ShowView(view.SelectedIndex == 1);
            listCard.TitleBase = 56;
            list.CheckedChanged += (s, e) => UpdateInfo();
            list.ReturnClicked += row => ReturnRows(new List<TuneRow> { row });
            btnApply = new FlatButton("", "\uE73E", ButtonKind.Primary);
            btnApply.SetBaseHeight(38);
            btnApply.Font = Theme.UiBold;
            btnApply.Click += (s, e) => Apply();
            btnReturnAll = new FlatButton("", "\uE7A7");
            btnReturnAll.SetBaseHeight(38);
            btnReturnAll.Visible = false;
            btnReturnAll.Click += (s, e) => ReturnRows(list.Rows.Where(r => r.Kind == TuneKind.Changed && r.Ready).ToList());
            list.ReturnGroup += rows => ReturnRows(rows);
            foreach (Control c in new Control[] { view, list, info, btnApply, btnReturnAll }) listCard.Controls.Add(c);
            listCard.Resize += (s, e) => LayoutList();
            Controls.Add(listCard);

            Resize += (s, e) => LayoutPage();
        }

        /// <summary>Settings are being changed or put back (the window does not close meanwhile).</summary>
        public bool Working { get { return working; } }

        // ---------- layout ----------

        void LayoutPage()
        {
            int pad = S(16);
            int leftW = Math.Max(S(320), Math.Min(S(500), (int)(Width * 0.36)));
            int smartH = S(158);
            smartCard.SetBounds(pad, pad, leftW, smartH);
            int gap = S(10);
            int avail = Height - pad * 2 - smartH - S(14);
            int cardH = Math.Max(S(84), Math.Min(S(118), (avail - gap * (cards.Count - 1)) / Math.Max(1, cards.Count)));
            int y = smartCard.Bottom + S(14);
            foreach (var c in cards)
            {
                c.SetBounds(pad, y, leftW, cardH);
                y += cardH + gap;
            }
            int x = pad + leftW + S(20);
            listCard.SetBounds(x, pad, Math.Max(S(300), Width - x - pad), Height - pad * 2);
            LayoutSmart();
            LayoutList();
        }

        void LayoutSmart()
        {
            int x = S(18);
            btnSmart.Width = btnSmart.PreferredWidth;
            btnSmart.Location = new Point(x, smartCard.Height - btnSmart.Height - S(14));
            btnMeasure.Width = btnMeasure.PreferredWidth;
            btnMeasure.Location = new Point(btnSmart.Right + S(8), btnSmart.Top);
            btnMeasure.Visible = btnMeasure.Right <= smartCard.Width - S(12);
            smartText.SetBounds(x, smartCard.TitleHeight - S(4), smartCard.Width - x * 2, S(18));
            recText.ForeOverride = Theme.Current.Text;
            recText.SetBounds(x, smartText.Bottom + S(6), smartCard.Width - x * 2, Math.Max(S(20), btnSmart.Top - smartText.Bottom - S(12)));
        }

        void LayoutList()
        {
            view.Location = new Point(S(14), (listCard.TitleHeight - view.Height) / 2);
            int footH = S(64);
            int fy = listCard.Height - footH;
            foreach (var b in new[] { btnApply, btnReturnAll })
            {
                b.Width = b.PreferredWidth + S(8);
                b.Location = new Point(listCard.Width - b.Width - S(16), fy + (footH - b.Height) / 2);
            }
            var right = btnApply.Visible ? btnApply : btnReturnAll;
            info.SetBounds(S(20), fy, Math.Max(S(60), right.Left - S(32)), footH);
            list.SetBounds(S(2), listCard.TitleHeight, listCard.Width - S(4), fy - listCard.TitleHeight);
            listCard.Invalidate();
        }

        public override void ApplyScale()
        {
            LayoutPage();
        }

        // ---------- texts ----------

        public override void ApplyLanguage()
        {
            smartCard.Title = L.T("Умный подбор", "Smart pick");
            SetButton(btnSmart, L.T("Подобрать для этого ПК", "Pick for this PC"));
            SetButton(btnMeasure, L.T("Замер", "Measure"));
            tip.SetToolTip(btnMeasure, L.T("Процессы, память, фоновая загрузка, службы, автозагрузка и время загрузки Windows — сейчас и до применения режима",
                "Processes, memory, background load, services, startup and Windows boot time — now and before a mode was applied"));
            tip.SetToolTip(btnSmart, L.T("DriveUP Pro смотрит на компьютер — ноутбук или ПК, батарея, видеокарта, версия Windows —\nи выбирает режим и значения, которые ему подходят",
                "DriveUP Pro looks at the computer — laptop or desktop, battery, graphics card, Windows version —\nand picks the mode and values that suit it"));
            SetButton(btnReturnAll, L.T("Вернуть всё", "Return all"));
            list.EmptyGlyph = "\uE73E";
            UpdateViewTexts();
            UpdateSmart();
            UpdateApplyText();
            UpdateInfo();
            UpdateEmpty();
            LayoutPage();
            foreach (var c in cards) c.Invalidate();
            list.Invalidate();
        }

        public override void ApplyTheme()
        {
            base.ApplyTheme();
            recText.ForeOverride = Theme.Current.Text;
            foreach (var c in cards) c.Invalidate();
            list.Invalidate();
        }

        void UpdateViewTexts()
        {
            int n = Tuner.ChangedCount;
            view.SetTexts(new[]
            {
                L.T("Что изменится", "What changes"),
                L.T("Изменено", "Changed") + (n > 0 ? " · " + n : ""),
            });
            LayoutList();
        }

        void UpdateSmart()
        {
            if (machine == null)
            {
                smartText.SetText(L.T("Смотрю на компьютер…", "Looking at the computer…"));
                recText.SetText("");
                return;
            }
            smartText.SetText(machine.Summary());
            tip.SetToolTip(smartText, machine.Summary().Replace(" · ", "\n"));
            var mode = Tuner.Recommend(machine, out reason);
            recText.SetText(L.T("Рекомендуем «", "Suggested: ") + mode.Name() + L.T("» — ", " — ") + reason);
        }

        void UpdateApplyText()
        {
            if (selected == null) SetButton(btnApply, L.T("Применить", "Apply"));
            else SetButton(btnApply, L.T("Применить «", "Apply ") + selected.Name() + L.T("»", ""));
            LayoutList();
        }

        void UpdateEmpty()
        {
            list.EmptyText = showChanged
                ? L.T("Пока ничего не изменено.\nВыберите режим и нажмите «Применить» — здесь появится каждое изменение с кнопкой «Вернуть».",
                      "Nothing changed yet.\nPick a mode and click Apply — every change shows up here with its own Return button.")
                : L.T("Режиму нечего менять на этом компьютере", "This mode has nothing to change on this computer");
            list.Invalidate();
        }

        void UpdateInfo()
        {
            if (!loaded) { info.SetText(""); return; }
            var rows = list.Rows;
            if (showChanged)
            {
                int n = rows.Count(r => r.Kind == TuneKind.Changed);
                info.SetText(n == 0 ? "" : L.Count(n, "параметр изменён", "параметра изменено", "параметров изменено", "setting changed", "settings changed") +
                    L.T(" · каждый можно вернуть отдельно", " · each one can be returned on its own"));
                btnReturnAll.Enabled = !working && rows.Any(r => r.Kind == TuneKind.Changed && r.Ready);
                return;
            }
            int change = rows.Count(r => r.Kind == TuneKind.Change && r.Checked && r.Ready);
            int back = rows.Count(r => r.Kind == TuneKind.Return && r.Checked && r.Ready);
            int reboot = rows.Count(r => r.Actionable && r.Checked && r.Tweak.NeedsReboot);
            int same = rows.Count(r => r.Kind == TuneKind.Same);
            var parts = new List<string>();
            if (change > 0) parts.Add(L.T("изменится: ", "changes: ") + change);
            if (back > 0) parts.Add(L.T("вернётся: ", "goes back: ") + back);
            if (reboot > 0) parts.Add(L.T("после перезагрузки: ", "after a restart: ") + reboot);
            var stopping = rows.Where(r => r.Actionable && r.Checked && r.Procs > 0).ToList();
            if (stopping.Count > 0)
                parts.Add(L.T("уйдёт из фона: ", "leaves the background: ") + L.Count(stopping.Sum(r => r.Procs), "процесс", "процесса", "процессов", "process", "processes") +
                          " · " + L.Size(stopping.Sum(r => r.ProcMemory)));
            if (parts.Count == 0 && same > 0) parts.Add(L.T("всё уже настроено так", "everything is already set like this"));
            else if (same > 0) parts.Add(L.T("уже так: ", "already set: ") + same);
            info.SetText(string.Join(" · ", parts));
            btnApply.Enabled = !working && !planLoading && change + back > 0;
        }

        public override string Status
        {
            get
            {
                if (!loaded) return L.T("Смотрю настройки Windows…", "Reading Windows settings…");
                var mode = Tuner.Mode(Settings.TuneMode);
                int n = Tuner.ChangedCount;
                if (n == 0) return L.T("Настройки Windows не менялись — всё как было до DriveUP Pro", "Windows settings untouched — all as it was before DriveUP Pro");
                return (mode != null ? L.T("Режим «", "Mode: ") + mode.Name() + L.T("» · ", " · ") : "") +
                       L.Count(n, "параметр изменён", "параметра изменено", "параметров изменено", "setting changed", "settings changed");
            }
        }

        // ---------- loading ----------

        protected override void FirstActivate()
        {
            list.Loading = true;
            UpdateSmart();
            Reload(true);
        }

        public override void RefreshData()
        {
            if (!working) Reload(false);
        }

        /// <summary>Reads the computer and the settings in the background, then shows them.</summary>
        void Reload(bool first)
        {
            int seq = ++loadSeq;
            bool changedView = showChanged;
            var mode = selected;
            Host.BeginBusy();
            new Thread(() =>
            {
                Machine m = null;
                TuneMode rec = null;
                List<TuneRow> rows = null;
                try
                {
                    m = machine ?? Machine.Read();
                    string why;
                    rec = Tuner.Recommend(m, out why);
                    if (mode == null) mode = Tuner.Mode(Settings.TuneMode) ?? rec;
                    rows = changedView ? Tuner.Changed() : Tuner.Plan(mode, m);
                }
                catch { rows = rows ?? new List<TuneRow>(); }
                Post(() =>
                {
                    Host.EndBusy();
                    if (seq != loadSeq) return;
                    machine = m;
                    recommended = rec;
                    loaded = true;
                    if (selected == null) SetSelected(mode);
                    foreach (var c in cards)
                    {
                        c.Recommended = c.Mode == recommended;
                        c.Active = c.Mode.Id == Settings.TuneMode && Tuner.ChangedCount > 0;
                    }
                    UpdateSmart();
                    UpdateViewTexts();
                    UpdateEmpty();
                    if (!changedView) rowsMode = mode;
                    if (first) list.SetRows(rows, true);
                    else Transition.CrossFade(list, () => list.SetRows(rows, false), 200);
                    UpdateInfo();
                    Host.UpdateStatus();
                    // Another mode was picked while this was being read: show that one.
                    if (!changedView && selected != null && selected != mode) Select(selected, false, true);
                });
            }) { IsBackground = true, Name = "Tune read" }.Start();
        }

        void SetSelected(TuneMode m)
        {
            selected = m;
            foreach (var c in cards) c.Selected = c.Mode == m;
            UpdateApplyText();
        }

        /// <summary>Picks a mode: its plan slides in from the side the card is on.</summary>
        void Select(TuneMode m, bool user, bool force = false)
        {
            if (m == null) return;
            if (m == selected && m == rowsMode && !force && !showChanged) return;
            int from = rowsMode != null ? Tuner.Modes.IndexOf(rowsMode) : 0, to = Tuner.Modes.IndexOf(m);
            SetSelected(m);
            if (showChanged)
            {
                view.SelectedIndex = 0;   // ShowView reloads the plan
                return;
            }
            if (!loaded || machine == null) return;
            int seq = ++loadSeq;
            var mc = machine;
            planLoading = true;
            UpdateInfo();
            new Thread(() =>
            {
                List<TuneRow> rows;
                try { rows = Tuner.Plan(m, mc); }
                catch { rows = new List<TuneRow>(); }
                Post(() =>
                {
                    if (seq != loadSeq) return;
                    planLoading = false;
                    rowsMode = m;
                    UpdateEmpty();
                    Transition.CrossFade(list, () => list.SetRows(rows, false), 240, Math.Sign(to - from) * S(28));
                    UpdateInfo();
                });
            }) { IsBackground = true, Name = "Tune plan" }.Start();
        }

        bool measuring;

        /// <summary>Measures the system now and compares with the "before" taken ahead of the first change.</summary>
        void MeasureNow()
        {
            if (measuring) return;
            measuring = true;
            btnMeasure.Enabled = false;
            Host.BeginBusy();
            new Thread(() =>
            {
                Measure now = null;
                try { now = Measure.Take(); } catch { }
                Post(() =>
                {
                    measuring = false;
                    btnMeasure.Enabled = true;
                    Host.EndBusy();
                    if (now == null) return;
                    Measure before;
                    lock (Settings.Sync) before = Measure.Parse(Settings.TuneBaseline);
                    string text = (before != null
                        ? L.T("До (", "Before (") + L.When(before.When) + L.T(") → сейчас:", ") → now:")
                        : L.T("Сейчас (замера «до» ещё нет — он делается сам перед первым применением режима):", "Now (no \"before\" yet — it is taken by itself before a mode is first applied):"))
                        + "\n\n" + Measure.Compare(before, now) + "\n\n" +
                        L.T("Время загрузки меняется после перезагрузки. Запомнить текущее состояние как «до»?", "Boot time changes after a restart. Keep the current state as \"before\"?");
                    if (ThemedDialog.Confirm(Host.Window, L.T("Замер системы", "System measurement"), text, L.T("Запомнить как «до»", "Keep as \"before\""), "\uE74E", false))
                    {
                        lock (Settings.Sync) Settings.TuneBaseline = now.ToString();
                        Settings.Save();
                    }
                });
            }) { IsBackground = true, Name = "measure" }.Start();
        }

        void SmartPick()
        {
            if (working || recommended == null) return;
            // Point at the suggested card, then show its plan.
            var card = cards.FirstOrDefault(c => c.Mode == recommended);
            if (selected == recommended && !showChanged)
            {
                Host.ShowToast(L.T("Режим «", "") + recommended.Name() + L.T("» уже выбран — проверьте список и нажмите «Применить»", " is already picked — check the list and click Apply"), ToastKind.Info);
                return;
            }
            Select(recommended, true);
            if (card != null) card.Focus();
        }

        void ShowView(bool changed)
        {
            if (showChanged == changed && loaded) return;
            showChanged = changed;
            btnApply.Visible = !changed;
            btnReturnAll.Visible = changed;
            if (changed) btnReturnAll.Appear(); else btnApply.Appear();
            LayoutList();
            UpdateEmpty();
            if (loaded) Reload(false);
        }

        // ---------- applying ----------

        void SetWorking(bool on)
        {
            working = on;
            list.Locked = on;
            btnSmart.Enabled = !on;
            view.Enabled = !on;
            foreach (var c in cards) c.Enabled = !on;
            if (on) { btnApply.Enabled = false; btnReturnAll.Enabled = false; }
            if (on) Host.BeginBusy(); else Host.EndBusy();
        }

        void Apply()
        {
            if (working || planLoading || rowsMode == null || showChanged) return;
            var todo = list.Rows.Where(r => r.Actionable && r.Checked && r.Ready).ToList();
            if (todo.Count == 0) return;
            if (!Host.CanStartOperation()) return;
            // Settings with a catch go only with the user's explicit yes, each one named with what it costs.
            var risky = todo.Where(r => r.Kind == TuneKind.Change && r.Risky).ToList();
            if (risky.Count > 0 && !ThemedDialog.Confirm(Host.Window, L.T("Подтвердите рискованные пункты", "Confirm the risky settings"),
                    L.T("Вы отметили пункты, у которых есть последствия:", "You ticked settings that have a catch:") + "\n\n" +
                    string.Join("\n", risky.Select(r => "• " + r.Tweak.Name() + " — " + r.Warn())) + "\n\n" +
                    L.T("Каждый можно вернуть во вкладке «Изменено». Применить их вместе с остальными?",
                        "Each can be returned under Changed. Apply them along with the rest?"),
                    L.T("Применить", "Apply"), "\uE73E", true))
                return;
            var mode = rowsMode;
            SetWorking(true);
            new Thread(() =>
            {
                int done = 0, failed = 0;
                bool reboot = false;
                var names = new List<string>();
                // The "before" measurement, once, ahead of the first change (to compare with later).
                bool noBaseline;
                lock (Settings.Sync) noBaseline = string.IsNullOrEmpty(Settings.TuneBaseline);
                if (noBaseline && Tuner.ChangedCount == 0)
                {
                    Post(() => info.SetText(L.T("Замеряю систему «до»…", "Measuring the system \"before\"…")));
                    try
                    {
                        var before = Measure.Take();
                        lock (Settings.Sync) Settings.TuneBaseline = before.ToString();
                        Settings.Save();
                    }
                    catch { }
                }
                // A System Restore point first: if Windows misbehaves after the changes, it can be rolled back
                // even from the recovery screen. Without one (Server, protection off) nothing stops.
                if (Settings.RestorePoint && todo.Any(r => r.Kind == TuneKind.Change))
                {
                    Post(() => info.SetText(L.T("Создаю точку восстановления…", "Creating a restore point…")));
                    bool ok;
                    string why = SysRestore.Create("DriveUP Pro: " + mode.Name(), out ok);
                    if (!ok && why != null) Post(() => Host.ShowToast(L.T("Точка восстановления не создана: ", "No restore point: ") + why, ToastKind.Info));
                }
                foreach (var row in Tuner.Order(todo))
                {
                    var r = row;
                    Post(() => list.SetState(r, TuneState.Working));
                    Thread.Sleep(70);   // long enough to see each one being done
                    try
                    {
                        if (r.Kind == TuneKind.Return) Tuner.Return(r.Tweak);
                        else Tuner.Change(r.Tweak, r.Target, mode);
                        done++;
                        names.Add(r.Tweak.Name());
                        if (r.Tweak.NeedsReboot) reboot = true;
                        Post(() => list.SetState(r, TuneState.Done));
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        string msg = ex is UnauthorizedAccessException || ex is System.Security.SecurityException
                            ? L.T("Нет доступа — нужны права администратора", "Access denied — administrator rights needed") : ex.Message;
                        Post(() => list.SetState(r, TuneState.Failed, msg));
                    }
                }
                if (done > 0) Tuner.SetMode(mode.Id);
                Journal.Add(JournalKind.Tune, done, failed, mode.Name() + ": " + string.Join(", ", names));
                Thread.Sleep(250);
                Post(() => Applied(mode, done, failed, reboot));
            }) { IsBackground = true, Name = "Tune apply" }.Start();
        }

        void Applied(TuneMode mode, int done, int failed, bool reboot)
        {
            SetWorking(false);
            foreach (var c in cards) c.Active = c.Mode.Id == Settings.TuneMode && Tuner.ChangedCount > 0;
            UpdateViewTexts();
            UpdateInfo();
            Host.UpdateStatus();
            if (failed == 0)
                Host.ShowToast(L.T("Режим «", "") + mode.Name() + L.T("» включён. Что изменено — во вкладке «Изменено»", " mode is on. See what changed under Changed"), ToastKind.Success);
            else
                Host.ShowToast(L.T("Не удалось изменить: ", "Could not change: ") + failed + L.T(" — причина в списке", " — see the list for why"), ToastKind.Error);
            // Show what was changed, each with its Return button (failures stay in view to be read).
            if (failed == 0 && done > 0)
                Anim.Delay(900, () =>
                {
                    if (IsDisposed) return;
                    if (!working && !showChanged) view.SelectedIndex = 1;
                    if (reboot) AskRestart();
                });
            else if (reboot) AskRestart();
        }

        void AskRestart()
        {
            if (ThemedDialog.Confirm(Host.Window, L.T("Нужна перезагрузка", "Restart needed"),
                    L.T("Часть настроек заработает только после перезагрузки компьютера.\nПерезагрузить сейчас? Сохраните открытые файлы.",
                        "Some settings take effect only after the computer restarts.\nRestart now? Save your open files first."),
                    L.T("Перезагрузить", "Restart"), "\uE777", false))
                UserShell.RestartPc();
        }

        /// <summary>Puts settings back as they were: each row turns, gets its tick and folds away.</summary>
        void ReturnRows(List<TuneRow> rows)
        {
            if (working || rows.Count == 0) return;
            if (!Host.CanStartOperation()) return;
            SetWorking(true);
            new Thread(() =>
            {
                int done = 0, failed = 0;
                bool reboot = false;
                var names = new List<string>();
                foreach (var row in Tuner.Order(rows))
                {
                    var r = row;
                    Post(() => list.SetState(r, TuneState.Working));
                    Thread.Sleep(70);
                    try
                    {
                        Tuner.Return(r.Tweak);
                        done++;
                        names.Add(r.Tweak.Name());
                        if (r.Tweak.NeedsReboot) reboot = true;
                        Post(() =>
                        {
                            list.SetState(r, TuneState.Done);
                            Anim.Delay(520, () => list.Remove(r, () => { UpdateInfo(); UpdateEmpty(); }));
                        });
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        string msg = ex.Message;
                        Post(() => list.SetState(r, TuneState.Failed, msg));
                    }
                }
                Journal.Add(JournalKind.TuneReturn, done, failed, string.Join(", ", names));
                Thread.Sleep(300);
                Post(() =>
                {
                    SetWorking(false);
                    foreach (var c in cards) c.Active = c.Mode.Id == Settings.TuneMode && Tuner.ChangedCount > 0;
                    UpdateViewTexts();
                    UpdateInfo();
                    Host.UpdateStatus();
                    if (failed == 0)
                        Host.ShowToast(done == 1 ? L.T("Возвращено как было: ", "Returned: ") + names[0]
                                                 : L.T("Возвращено как было: ", "Returned: ") + L.Count(done, "параметр", "параметра", "параметров", "setting", "settings"), ToastKind.Success);
                    else
                        Host.ShowToast(L.T("Не удалось вернуть: ", "Could not return: ") + failed, ToastKind.Error);
                    if (reboot)
                        Host.ShowToast(L.T("Часть настроек вернётся после перезагрузки", "Some settings go back after a restart"), ToastKind.Info);
                });
            }) { IsBackground = true, Name = "Tune return" }.Start();
        }
    }
}
