using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>The running Windows: "11"/"10" and its release as a number (24H2 → 242, 1903 → 191).</summary>
    static class WindowsRelease
    {
        static volatile int key = -1;
        static bool is11;
        static readonly object sync = new object();

        static void Read()
        {
            if (key >= 0) return;
            lock (sync)
            {
                if (key >= 0) return;
                is11 = false;
                key = ReadKey();
            }
        }

        static int ReadKey()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build;
                    int.TryParse(Convert.ToString(k.GetValue("CurrentBuildNumber")), out build);
                    is11 = build >= 22000;
                    string dv = Convert.ToString(k.GetValue("DisplayVersion"));
                    if (dv.Length == 0) dv = Convert.ToString(k.GetValue("ReleaseId"));
                    return Math.Max(0, Key(dv));
                }
            }
            catch { return 0; }
        }

        public static bool Is11 { get { Read(); return is11; } }
        public static int Current { get { Read(); return key; } }

        /// <summary>"24H2" → 242, "1903" → 191 (first half), "1909" → 192; 0 when unknown.</summary>
        public static int Key(string release)
        {
            var m = Regex.Match(release ?? "", @"(\d\d)H([12])", RegexOptions.IgnoreCase);
            if (m.Success) return int.Parse(m.Groups[1].Value) * 10 + int.Parse(m.Groups[2].Value);
            m = Regex.Match(release ?? "", @"\b(\d\d)(\d\d)\b");
            if (m.Success) return int.Parse(m.Groups[1].Value) * 10 + (int.Parse(m.Groups[2].Value) <= 6 ? 1 : 2);
            return 0;
        }

        /// <summary>The release name the way vendors spell it in file names ("24h2").</summary>
        public static string Name
        {
            get
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        string dv = Convert.ToString(k.GetValue("DisplayVersion"));
                        return (dv.Length > 0 ? dv : Convert.ToString(k.GetValue("ReleaseId"))).ToLowerInvariant();
                    }
                }
                catch { return ""; }
            }
        }
    }

    /// <summary>A driver in the Microsoft Update Catalog.</summary>
    class CatalogHit
    {
        public string Id = "", Title = "", Version = "", Provider = "";
        public DateTime? Date;
        public long Size;
        public string Page { get { return CatalogSource.Base + "ScopedViewInline.aspx?updateid=" + Id; } }
    }

    /// <summary>
    /// The Microsoft Update Catalog: drivers of every chip maker (Realtek, MediaTek, Qualcomm, Broadcom,
    /// AMD chipset, Intel storage...) as published to Windows Update, searchable by hardware ID. A driver
    /// counts only when its own list of hardware IDs has one of this device's IDs exactly (many entries
    /// are made for one PC maker's boards), it is for this Windows release or older, and it is newer.
    /// </summary>
    class CatalogSource : IVendorSource
    {
        public const string Base = "https://www.catalog.update.microsoft.com/";
        public string Name { get { return "Microsoft"; } }

        // Chip makers whose drivers the catalog carries well (graphics have their own makers' checks).
        static readonly HashSet<string> Makers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "10EC", "0BDA", "14C3", "168C", "17CB", "14E4", "1022", "8086", "1B21", "1912", "1217", "06CB", "04F3", "14F1", "111D", "1057", "1179", "1C5C",
        };

        static bool Wanted(Device d)
        {
            if (d.HardwareIds.Length == 0 || VendorCheck.IsDisplay(d) || d.Disabled || Firmware.Is(d)) return false;
            if (d.NeedsAttention) return true;
            if (!d.HasDriver) return false;
            string first = d.HardwareIds[0];
            if (first.StartsWith("ACPI\\AMDI", StringComparison.OrdinalIgnoreCase) || first.StartsWith("ACPI\\VEN_QCOM", StringComparison.OrdinalIgnoreCase) ||
                first.StartsWith("ACPI\\QCOM", StringComparison.OrdinalIgnoreCase)) return true;
            string ven = VendorCheck.VendorId(d);
            return ven != null && Makers.Contains(ven);
        }

        public List<VendorUpdate> Check(IList<Device> devices, PcInfo pc)
        {
            var result = new List<VendorUpdate>();
            var deadline = DateTime.UtcNow.AddSeconds(33);
            // Devices with problems first; at most a couple of dozen, four at a time.
            var queue = new Queue<Device>(devices.Where(Wanted).OrderByDescending(d => d.NeedsAttention).Take(24));
            var workers = new List<Thread>();
            for (int w = 0; w < 4; w++)
            {
                var th = new Thread(() =>
                {
                    while (DateTime.UtcNow < deadline)
                    {
                        Device d;
                        lock (queue)
                        {
                            if (queue.Count == 0) return;
                            d = queue.Dequeue();
                        }
                        CatalogHit hit;
                        try { hit = Lookup(d, !d.HasDriver || d.NeedsAttention, deadline); }
                        catch { continue; }
                        if (hit == null) continue;
                        lock (result)
                            result.Add(new VendorUpdate
                            {
                                Vendor = "Microsoft", Device = d, Title = hit.Title, VersionText = hit.Version, CurrentText = d.VersionText,
                                Date = hit.Date, Url = hit.Page, CatalogId = hit.Id, Size = hit.Size,
                            });
                    }
                }) { IsBackground = true, Name = "catalog" };
                th.Start();
                workers.Add(th);
            }
            foreach (var th in workers)
            {
                var left = deadline.AddSeconds(2) - DateTime.UtcNow;
                if (left > TimeSpan.Zero) th.Join(left);
            }
            lock (result) return result.ToList();
        }

        static readonly Regex RowRe = new Regex(@"<tr id=""([0-9a-f\-]{36})_R\d+""", RegexOptions.IgnoreCase);

        static string Cell(string html, string id, int col)
        {
            var m = Regex.Match(html, "id=\"" + Regex.Escape(id) + "_C" + col + @"_R\d+"">(.*?)</td>", RegexOptions.Singleline);
            if (!m.Success) return "";
            return Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ")), @"\s+", " ").Trim();
        }

        /// <summary>Whether a row's "Products" fit the Windows running here ("Windows 11 Client, version 24H2 and later").</summary>
        public static bool ForThisWindows(string products)
        {
            string os = WindowsRelease.Is11 ? "Windows 11" : "Windows 10";
            bool any = false;
            foreach (var part in products.Split(','))
            {
                if (part.IndexOf(os, StringComparison.OrdinalIgnoreCase) < 0) continue;
                any = true;
                break;
            }
            if (!any) return false;
            int now = WindowsRelease.Current;
            if (now == 0) return true;
            // "version 26H1 and later": only from that release on.
            foreach (Match m in Regex.Matches(products, os + @"[^,]*?,\s*version\s+([0-9H]{4})\s+and later", RegexOptions.IgnoreCase))
                if (WindowsRelease.Key(m.Groups[1].Value) <= now) return true;
            return !Regex.IsMatch(products, os + @"[^,]*?,\s*version\s+[0-9H]{4}\s+and later", RegexOptions.IgnoreCase);
        }

        /// <summary>IDs to search for: the most specific one without the revision, then the plain vendor+device one.</summary>
        static List<string> Queries(Device d)
        {
            var ids = d.HardwareIds.Where(x => x.IndexOf("&REV_", StringComparison.OrdinalIgnoreCase) < 0 && x.IndexOf("&CC_", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            if (ids.Count == 0) ids = d.HardwareIds.Take(1).ToList();
            var q = new List<string> { ids.OrderByDescending(x => x.Length).First() };
            string shortest = ids.OrderBy(x => x.Length).First();
            if (!q.Contains(shortest, StringComparer.OrdinalIgnoreCase)) q.Add(shortest);
            return q;
        }

        /// <summary>The newest catalog driver for this device that is newer than its own (any, when anyVersion); null if none.</summary>
        public static CatalogHit Lookup(Device d, bool anyVersion, DateTime deadline)
        {
            var mine = new HashSet<string>(d.HardwareIds.Select(x => x.ToLowerInvariant()));
            int details = 0;
            foreach (string q in Queries(d))
            {
                if (DateTime.UtcNow > deadline) return null;
                string html = VendorCheck.Get(Base + "Search.aspx?q=" + Uri.EscapeDataString(q), null, 20000);
                if (html.IndexOf("We did not find any results", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                int scanned = 0;
                foreach (Match row in RowRe.Matches(html))
                {
                    if (++scanned > 25 || details >= 4 || DateTime.UtcNow > deadline) break;
                    string id = row.Groups[1].Value;
                    if (!ForThisWindows(Cell(html, id, 2))) continue;
                    string title = Cell(html, id, 1), version = Cell(html, id, 5);
                    if (version.Length == 0 || version == "n/a")
                    {
                        var vm = Regex.Match(title, @"(\d+\.\d+(\.\d+){1,2})\)?\s*$");
                        version = vm.Success ? vm.Groups[1].Value : "";
                    }
                    if (version.Length == 0) continue;
                    if (!anyVersion && VendorCheck.Compare(version, d.VersionText) <= 0) continue;
                    details++;
                    var hit = Details(id, mine);
                    if (hit == null) continue;
                    hit.Title = title;
                    if (hit.Version.Length == 0) hit.Version = version;
                    long size;
                    var sm = Regex.Match(html, "id=\"" + Regex.Escape(id) + "_originalSize\">(\\d+)<");
                    if (sm.Success && long.TryParse(sm.Groups[1].Value, out size)) hit.Size = size;
                    if (!hit.Date.HasValue) hit.Date = VendorCheck.ParseDate(Cell(html, id, 4), "M/d/yyyy");
                    return hit;
                }
            }
            return null;
        }

        static string Span(string html, string name)
        {
            var m = Regex.Match(html, "id=\"ScopedViewHandler_" + name + "\"[^>]*>(.*?)</span>", RegexOptions.Singleline);
            return m.Success ? Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ")), @"\s+", " ").Trim() : "";
        }

        /// <summary>The update's details; null unless its hardware ID list has one of this device's IDs.</summary>
        static CatalogHit Details(string id, HashSet<string> mine)
        {
            string html = VendorCheck.Get(Base + "ScopedViewInline.aspx?updateid=" + id, null, 20000);
            int box = html.IndexOf("id=\"driverhwIDs\"", StringComparison.OrdinalIgnoreCase);
            if (box < 0) return null;
            // Up to the next section: only exact hardware ID texts can match anyway.
            int end = html.IndexOf("ScopedViewHandler_label", box + 20, StringComparison.Ordinal);
            string list = html.Substring(box, (end > box ? end : html.Length) - box);
            bool match = false;
            foreach (Match m in Regex.Matches(list, @"<div[^>]*>\s*([^<]+?)\s*</div>"))
                if (mine.Contains(WebUtility.HtmlDecode(m.Groups[1].Value).Trim().ToLowerInvariant())) { match = true; break; }
            if (!match) return null;
            return new CatalogHit
            {
                Id = id, Version = Span(html, "version"), Provider = Span(html, "driverProvider"),
                Date = VendorCheck.ParseDate(Span(html, "versionDate"), "M/d/yyyy"),
            };
        }

        /// <summary>The download address of an update's .cab (Microsoft's download servers only).</summary>
        public static string DownloadUrl(string updateId)
        {
            string body = "updateIDs=" + Uri.EscapeDataString("[{\"size\":0,\"languages\":\"\",\"uidInfo\":\"" + updateId + "\",\"updateID\":\"" + updateId + "\"}]");
            string html = VendorCheck.Post(Base + "DownloadDialog.aspx", body);
            var m = Regex.Match(html, @"downloadInformation\[0\]\.files\[0\]\.url\s*=\s*'([^']+)'");
            if (!m.Success) throw new IOException(L.T("каталог не дал ссылку на файл", "the catalog gave no file link"));
            var uri = new Uri(m.Groups[1].Value.Replace("http://", "https://"));
            if (uri.Scheme != "https" || !(uri.Host.EndsWith(".windowsupdate.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase)))
                throw new IOException(L.T("ссылка ведёт не на сервер Microsoft", "the link does not point to a Microsoft server"));
            return uri.AbsoluteUri;
        }

        /// <summary>
        /// Downloads a catalog driver into a new protected folder (progress(done, total)) and returns the .cab;
        /// the Install tab unpacks it, matches it to the devices and installs it after the usual confirmation.
        /// </summary>
        public static string Download(string updateId, Action<long, long> progress)
        {
            string url = DownloadUrl(updateId);
            string dir = InfFiles.NewDownloadDir();
            string file = Path.Combine(dir, Path.GetFileName(new Uri(url).LocalPath));
            if (!file.EndsWith(".cab", StringComparison.OrdinalIgnoreCase)) throw new IOException(L.T("это не пакет драйвера (.cab)", "not a driver package (.cab)"));
            VendorCheck.DownloadFile(url, file, 2L << 30, null, progress);
            return file;
        }

        /// <summary>One driver from the Devices tab: downloaded now and opened on the Install tab.</summary>
        public static void Fetch(IHost host, Control ui, string updateId, string title, long size)
        {
            host.BeginBusy();
            host.ShowToast(L.T("Скачиваю: ", "Downloading: ") + title + (size > 0 ? " (" + L.Size(size) + ")" : ""), ToastKind.Info);
            var th = new Thread(() =>
            {
                string file = null, error = null;
                try { file = Download(updateId, null); }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    ui.BeginInvoke((MethodInvoker)delegate
                    {
                        host.EndBusy();
                        if (error != null) host.ShowToast(L.T("Не удалось скачать: ", "Download failed: ") + error, ToastKind.Error);
                        else host.InstallFrom(new[] { file });
                    });
                }
                catch (InvalidOperationException) { }
            }) { IsBackground = true, Name = "catalog download" };
            th.Start();
        }
    }
}
