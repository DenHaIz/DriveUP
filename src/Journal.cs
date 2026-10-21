using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace DriveUp
{
    enum JournalKind { Update, Install, Backup, Cleanup, Tune, TuneReturn }

    class JournalEntry
    {
        public DateTime When;
        public JournalKind Kind;
        public int Done, Failed;
        public string Details = "";

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case JournalKind.Install: return L.T("Установка из файла", "Install from file");
                    case JournalKind.Backup: return L.T("Резервная копия", "Backup");
                    case JournalKind.Cleanup: return L.T("Удаление старых версий", "Old versions removed");
                    case JournalKind.Tune: return L.T("Настройка системы", "System tuning");
                    case JournalKind.TuneReturn: return L.T("Возврат настроек", "Settings returned");
                    default: return L.T("Обновление", "Update");
                }
            }
        }

        public string ResultText
        {
            get
            {
                string s = L.T("готово: ", "done: ") + Done;
                if (Failed > 0) s += L.T(" · ошибок: ", " · failed: ") + Failed;
                return s;
            }
        }
    }

    /// <summary>What DriveUP changed: one tab-separated line per operation in DriveUP.log next to the exe.</summary>
    static class Journal
    {
        static readonly object sync = new object();

        public static void Add(JournalKind kind, int done, int failed, string details)
        {
            string line = string.Join("\t", new[]
            {
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture), kind.ToString(),
                done.ToString(CultureInfo.InvariantCulture), failed.ToString(CultureInfo.InvariantCulture),
                (details ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')
            });
            lock (sync)
            {
                try { File.AppendAllText(Settings.LogPath, line + Environment.NewLine, new UTF8Encoding(false)); }
                catch { }
            }
        }

        /// <summary>Newest first.</summary>
        public static List<JournalEntry> Load(int max = 500)
        {
            var list = new List<JournalEntry>();
            try
            {
                if (!File.Exists(Settings.LogPath)) return list;
                foreach (string line in File.ReadAllLines(Settings.LogPath, Encoding.UTF8))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 4) continue;
                    var e = new JournalEntry();
                    if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out e.When)) continue;
                    JournalKind k;
                    if (Enum.TryParse(parts[1], out k)) e.Kind = k;
                    int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out e.Done);
                    int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out e.Failed);
                    if (parts.Length > 4) e.Details = parts[4];
                    list.Add(e);
                }
            }
            catch { }
            list.Reverse();
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }
    }

    /// <summary>The journal window: when, what was done, how it went.</summary>
    class JournalForm : ThemedWindow
    {
        JournalForm() : base(L.T("Журнал DriveUP", "DriveUP journal"), new Size(Theme.S(860), Theme.S(520)))
        {
            var entries = Journal.Load();
            var frame = new BufferedPanel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Theme.Current.Border };
            var lv = new ThemedListView { Dock = DockStyle.Fill, MultiSelect = false };
            lv.Columns.Add(L.T("Когда", "When"), S(150));
            lv.Columns.Add(L.T("Действие", "Action"), S(190));
            lv.Columns.Add(L.T("Результат", "Result"), S(150));
            lv.Columns.Add(L.T("Подробности", "Details"), S(330));
            lv.IconColumn = false;
            lv.ListViewItemSorter = null;
            lv.EmptyText = L.T("Пока ничего не устанавливалось", "Nothing installed yet");
            lv.EmptyGlyph = "\uE81C";
            foreach (var e in entries)
            {
                var row = new ListViewItem(new[] { e.When.ToString("g"), e.KindText, e.ResultText, e.Details })
                {
                    Tag = new TextRow { Kind = e.Failed > 0 && e.Done == 0 ? RowKind.Danger : (e.Done > 0 ? RowKind.Normal : RowKind.Dim) }
                };
                lv.Items.Add(row);
            }
            frame.Controls.Add(lv);
            Body.Controls.Add(frame);

            var summary = new StatusText
            {
                Text = L.T("Обновлено драйверов: ", "Drivers updated: ") +
                       entries.Where(x => x.Kind == JournalKind.Update || x.Kind == JournalKind.Install).Sum(x => x.Done) + "   ·   " + Settings.LogPath
            };
            summary.SetBounds(S(20), 0, S(560), Footer.Height);
            summary.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            Footer.Controls.Add(summary);
            AddButton(L.T("Закрыть", "Close"), "", ButtonKind.Normal, DialogResult.OK);
        }

        public static void ShowFor(IWin32Window owner)
        {
            using (var f = new JournalForm()) f.ShowDialog(owner);
        }
    }
}
