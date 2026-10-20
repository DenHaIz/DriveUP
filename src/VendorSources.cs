using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Microsoft.Win32;

namespace DriveUp
{
    /// <summary>
    /// NVIDIA graphics: the driver lookup NVIDIA's own download page uses, asked by device ID
    /// (DEV_xxxx). Windows shows NVIDIA drivers as 32.0.15.6094; NVIDIA calls that 560.94.
    /// </summary>
    class NvidiaSource : IVendorSource
    {
        public string Name { get { return "NVIDIA"; } }

        public List<VendorUpdate> Check(IList<Device> devices, PcInfo pc)
        {
            var result = new List<VendorUpdate>();
            foreach (var d in devices.Where(x => VendorCheck.VendorId(x) == "10DE" && VendorCheck.IsDisplay(x) && x.HasDriver))
            {
                string dev = VendorCheck.DeviceId(d);
                if (dev == null) continue;
                string url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup" +
                             "&deviceID=" + dev + "&osID=" + (pc.IsWindows11 ? "135" : "57") +
                             "&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&sort1=0&numberOfResults=1";
                var json = VendorCheck.Json(VendorCheck.Get(url, null, 30000));
                if (VendorCheck.Str(json, "Success") != "1") continue;
                var info = VendorCheck.At(VendorCheck.Arr(VendorCheck.At(json, "IDS")).FirstOrDefault(), "downloadInfo");
                string latest = VendorCheck.Str(info, "Version");
                string installed = Marketing(d.VersionText);
                if (latest.Length == 0 || installed == null || VendorCheck.Compare(latest, installed) <= 0) continue;
                string name = Uri.UnescapeDataString(VendorCheck.Str(info, "Name").Replace('+', ' '));
                string page = VendorCheck.Str(info, "DetailsURL");
                result.Add(new VendorUpdate
                {
                    Vendor = "NVIDIA", Device = d, VersionText = latest, CurrentText = installed,
                    Title = d.Name + (name.Length > 0 ? " — " + name : ""),
                    Date = VendorCheck.ParseDate(VendorCheck.Str(info, "ReleaseDateTime"), "ddd MMM dd, yyyy", "ddd MMM d, yyyy"),
                    Url = page.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? page : "https://www.nvidia.com/en-us/drivers/",
                });
            }
            return result;
        }

        /// <summary>32.0.15.6094 → 560.94: the last five digits of the last two parts, a dot before the final two.</summary>
        public static string Marketing(string windowsVersion)
        {
            var p = (windowsVersion ?? "").Split('.');
            if (p.Length < 4) return null;
            string digits = p[2] + p[3].PadLeft(4, '0');
            if (digits.Length < 5 || !Regex.IsMatch(digits, @"^\d+$")) return null;
            digits = digits.Substring(digits.Length - 5);
            return int.Parse(digits.Substring(0, 3)) + "." + digits.Substring(3);
        }
    }

    /// <summary>
    /// AMD Radeon graphics: the release index AMD's own installer reads, then the list of chips
    /// (DEV-REV) each release supports. The site only answers with a Referer from amd.com.
    /// </summary>
    class AmdSource : IVendorSource
    {
        public string Name { get { return "AMD"; } }
        const string Referer = "https://www.amd.com/";

        // Current releases first: RDNA3/4, then RDNA1/2, then the older chips, then the combined list.
        static readonly string[] Branches =
        {
            "Consumer_Whql_Win10_productionASICs", "Consumer_Maintenance_Win10", "Consumer_Legacy_Win10", "Consumer_Whql_Win10_AllAsics",
        };

        public List<VendorUpdate> Check(IList<Device> devices, PcInfo pc)
        {
            var result = new List<VendorUpdate>();
            var gpus = devices.Where(x => VendorCheck.VendorId(x) == "1002" && VendorCheck.IsDisplay(x) && x.HasDriver).ToList();
            if (gpus.Count == 0) return result;
            string indexText = VendorCheck.Get("https://drivers.amd.com/drivers/installer/json/version.json", Referer);
            if (!indexText.TrimStart().StartsWith("{")) return result;   // the "download not complete" page: no Referer accepted
            var index = VendorCheck.Json(indexText);
            var releases = new List<object>();
            foreach (var b in Branches)
            {
                string url = VendorCheck.Str(VendorCheck.At(index, b), "url");
                if (url.Length == 0) continue;
                // The installer's host only redirects to a web page; the same files are on drivers.amd.com.
                url = Regex.Replace(url, @"^https?://www2\.ati\.com/", "https://drivers.amd.com/", RegexOptions.IgnoreCase);
                try
                {
                    string text = VendorCheck.Get(url, Referer);
                    if (text.TrimStart().StartsWith("[")) releases.AddRange(VendorCheck.Arr(VendorCheck.Json(text)));
                }
                catch { }
            }
            string installedInternal = InstalledInternal();
            foreach (var d in gpus)
            {
                string dev = VendorCheck.DeviceId(d), rev = VendorCheck.RevisionId(d);
                if (dev == null || rev == null) continue;
                string sku = dev + "-" + rev;
                var rel = releases.FirstOrDefault(r => Skus(VendorCheck.At(r, "skus")).Contains(sku));
                if (rel == null) continue;
                string ext = VendorCheck.Str(rel, "externalbuildversion"), intern = VendorCheck.Str(rel, "internalbuildversion");
                bool newer;
                string current;
                if (installedInternal != null)
                {
                    // AMD's own number for the installed package ("26.10.41.01-...") against the release.
                    newer = VendorCheck.Compare(intern, installedInternal) > 0;
                    current = installedInternal.Split('-')[0];
                }
                else
                {
                    // Otherwise by date: the release's build date is in its number ("...-260811a-...").
                    var m = Regex.Match(intern, @"-(\d{6})[a-z]?-");
                    DateTime built;
                    if (!m.Success || !d.Date.HasValue ||
                        !DateTime.TryParseExact(m.Groups[1].Value, "yyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out built))
                        continue;
                    newer = d.Date.Value < built.AddDays(-1);
                    // Windows' number for AMD drivers (32.0.x) says nothing next to 26.8.1: show the date instead.
                    current = L.T("драйвер от ", "driver of ") + L.Date(d.Date.Value);
                }
                if (!newer) continue;
                string notes = VendorCheck.Str(rel, "releasenotes");
                result.Add(new VendorUpdate
                {
                    Vendor = "AMD", Device = d, VersionText = ext, CurrentText = current,
                    Title = d.Name + " — " + (VendorCheck.Str(rel, "edition").Length > 0 ? VendorCheck.Str(rel, "edition") : "AMD Software"),
                    Date = VendorCheck.ParseDate(VendorCheck.Str(rel, "releasedate"), "MM/dd/yyyy"),
                    Url = notes.StartsWith("https://www.amd.com/", StringComparison.OrdinalIgnoreCase) ? notes : "https://www.amd.com/en/support/download/drivers.html",
                });
            }
            return result;
        }

        static HashSet<string> Skus(object v)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var arr = v as object[];
            if (arr != null) foreach (var x in arr) set.Add(Convert.ToString(x).Trim());
            else foreach (var x in Regex.Split(Convert.ToString(v) ?? "", @"[\s,'\[\]]+")) if (x.Length > 0) set.Add(x);
            return set;
        }

        /// <summary>The installed AMD Software package number, as AMD's installer records it (when it does).</summary>
        static string InstalledInternal()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\AMD\CN"))
                {
                    string v = k != null ? k.GetValue("DriverVersion") as string : null;
                    return !string.IsNullOrEmpty(v) && Regex.IsMatch(v, @"^\d+\.\d+") ? v.Trim() : null;
                }
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Intel: the catalog Intel Driver &amp; Support Assistant downloads (a zip with a JSON list of
    /// packages, each with the device IDs it is for and a version mask saying which parts count).
    /// </summary>
    class IntelSource : IVendorSource
    {
        public string Name { get { return "Intel"; } }

        public List<VendorUpdate> Check(IList<Device> devices, PcInfo pc)
        {
            var result = new List<VendorUpdate>();
            var candidates = devices.Where(x => x.HasDriver && (VendorCheck.VendorId(x) == "8086" || VendorCheck.VendorId(x) == "8087")).ToList();
            if (candidates.Count == 0) return result;
            var zip = VendorCheck.GetBytes("https://dsadata.intel.com/data/en", null, 30000);
            string json = null;
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("software-configurations.json", StringComparison.OrdinalIgnoreCase));
                if (entry == null || entry.Length > 32 << 20) return result;
                using (var r = new StreamReader(entry.Open())) json = r.ReadToEnd();
            }
            // Every catalog entry that matches a device: (device, package, component).
            var matches = new List<Tuple<Device, object, object>>();
            foreach (var pkg in VendorCheck.Arr(VendorCheck.Json(json)))
            {
                string name = VendorCheck.Str(pkg, "Name");
                if (VendorCheck.Str(pkg, "IsBeta").Equals("True", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.IndexOf("Historical", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!ForThisWindows(pkg)) continue;
                foreach (var comp in VendorCheck.Arr(VendorCheck.At(pkg, "Components")))
                {
                    string version = VendorCheck.Str(comp, "Version");
                    if (version.Length == 0) continue;
                    var patterns = VendorCheck.Arr(VendorCheck.At(comp, "DetectionValues")).Select(x => Pattern(Convert.ToString(x))).Where(x => x != null).ToList();
                    if (patterns.Count == 0) continue;
                    foreach (var d in candidates)
                        if (VendorCheck.BareIds(d).Any(id => patterns.Any(p => p.IsMatch(id))))
                            matches.Add(Tuple.Create(d, pkg, comp));
                }
            }
            foreach (var g in matches.GroupBy(m => m.Item1))
            {
                var d = g.Key;
                var list = g.ToList();
                // Several driver lines for one chip (e.g. 1.x and 2.x network drivers): stay on the installed line
                // when the catalog has it; otherwise the newest is the update (e.g. Wi-Fi 22.x -> 24.x).
                if (!VendorCheck.IsDisplay(d))
                {
                    long major = VendorCheck.Parts(d.VersionText).FirstOrDefault();
                    var same = list.Where(m => VendorCheck.Parts(VendorCheck.Str(m.Item3, "Version")).FirstOrDefault() == major).ToList();
                    if (same.Count > 0) list = same;
                }
                Tuple<Device, object, object> top = null;
                string topVersion = null;
                foreach (var m in list)
                {
                    string mask = VendorCheck.Str(m.Item3, "VersionComparisonMask");
                    string a1 = Masked(VendorCheck.Str(m.Item3, "Version"), mask, d), b1 = Masked(d.VersionText, mask, d);
                    if (a1 == null || b1 == null || VendorCheck.Compare(a1, b1) <= 0) continue;
                    if (topVersion != null && VendorCheck.Compare(a1, topVersion) <= 0) continue;
                    top = m;
                    topVersion = a1;
                }
                if (top == null) continue;
                string page = VendorCheck.Str(top.Item2, "Url");
                result.Add(new VendorUpdate
                {
                    Vendor = "Intel", Device = d, VersionText = VendorCheck.Str(top.Item3, "Version"), CurrentText = d.VersionText,
                    Title = Clean(VendorCheck.Str(top.Item2, "Name")),
                    Date = VendorCheck.ParseDate(VendorCheck.Str(top.Item2, "DisplayReleaseDate"), "yyyy-MM-dd'T'HH:mm:ss'Z'"),
                    Url = page.StartsWith("https://www.intel.com/", StringComparison.OrdinalIgnoreCase) ? page : "https://www.intel.com/content/www/us/en/support/detect.html",
                });
            }
            return result;
        }
        /// <summary>Has a file for 64-bit Windows 10 or 11 (the catalog also keeps Windows 7 packages).</summary>
        static bool ForThisWindows(object pkg)
        {
            foreach (var f in VendorCheck.Arr(VendorCheck.At(pkg, "Files")))
                foreach (var os in VendorCheck.Arr(VendorCheck.At(f, "OperatingSystems")))
                {
                    string s = Convert.ToString(os) ?? "";
                    if ((s.StartsWith("windows-10") || s.StartsWith("windows-11")) && s.EndsWith("-64")) return true;
                }
            return false;
        }

        /// <summary>"VEN_8086&amp;DEV_2725&amp;SUBSYS_*8086" → a regex for bare hardware IDs ('*' = anything).</summary>
        static Regex Pattern(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string re = "^" + Regex.Escape(value.Trim()).Replace(@"\*", "[^&]*") + "(&|$)";
            return new Regex(re, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// The parts of a version that count: the mask's 'O' parts ("X.X.O.O" = the last two).
        /// Without a mask graphics compare their last two parts (31.0.101.x and 32.0.101.x are the same line).
        /// </summary>
        static string Masked(string version, string mask, Device d)
        {
            var parts = VendorCheck.Parts(version);
            if (parts.Length == 0) return null;
            if (mask.Length == 0)
            {
                if (VendorCheck.IsDisplay(d) && parts.Length >= 4) return parts[2] + "." + parts[3];
                return string.Join(".", parts);
            }
            var m = mask.Split('.');
            var keep = new List<long>();
            for (int i = 0; i < m.Length && i < parts.Length; i++)
                if (m[i].Equals("O", StringComparison.OrdinalIgnoreCase)) keep.Add(parts[i]);
            return keep.Count > 0 ? string.Join(".", keep) : string.Join(".", parts);
        }

        static string Clean(string s)
        {
            return Regex.Replace(s.Replace("®", "").Replace("™", "").Replace("*", ""), @"\s+", " ").Trim();
        }
    }

    /// <summary>
    /// Lenovo PCs: the catalog Lenovo System Update reads for this machine type, then each package's
    /// description (which device IDs it is for and which driver version it brings).
    /// </summary>
    class LenovoSource : IVendorSource
    {
        public string Name { get { return "Lenovo"; } }

        public List<VendorUpdate> Check(IList<Device> devices, PcInfo pc)
        {
            var result = new List<VendorUpdate>();
            string mt = pc.LenovoType;
            if (!pc.IsLenovo || mt == null) return result;
            XmlDocument catalog = null;
            foreach (var os in pc.IsWindows11 ? new[] { "Win11", "Win10" } : new[] { "Win10", "Win11" })
            {
                try
                {
                    catalog = Xml(VendorCheck.GetBytes("https://download.lenovo.com/catalog/" + mt + "_" + os + ".xml"));
                    break;
                }
                catch { }
            }
            if (catalog == null) return result;
            var locations = catalog.SelectNodes("//package/location").Cast<XmlNode>()
                .Select(n => n.InnerText.Trim())
                .Where(u => u.StartsWith("https://download.lenovo.com/", StringComparison.OrdinalIgnoreCase))
                .Distinct().Take(150).ToList();

            // Package descriptions are small: read them a few at a time.
            var found = new List<VendorUpdate>();
            int next = -1;
            var workers = Enumerable.Range(0, 6).Select(_ => new Thread(() =>
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < locations.Count)
                {
                    try
                    {
                        var v = Package(locations[i], devices);
                        if (v != null) lock (found) found.AddRange(v);
                    }
                    catch { }
                }
            }) { IsBackground = true }).ToList();
            foreach (var w in workers) w.Start();
            foreach (var w in workers) w.Join(30000);
            // One line per device: the newest package.
            lock (found)
                foreach (var g in found.GroupBy(v => v.Device))
                {
                    var top = g.First();
                    foreach (var v in g) if (VendorCheck.Compare(v.VersionText, top.VersionText) > 0) top = v;
                    result.Add(top);
                }
            return result;
        }

        static List<VendorUpdate> Package(string url, IList<Device> devices)
        {
            var doc = Xml(VendorCheck.GetBytes(url));
            var root = doc.DocumentElement;
            if (root == null) return null;
            var list = new List<VendorUpdate>();
            string title = Text(root.SelectSingleNode("Title/Desc[@id='EN']")) ?? Text(root.SelectSingleNode("Title/Desc")) ?? "";
            string folder = url.Substring(0, url.LastIndexOf('/') + 1);
            string readme = Text(root.SelectSingleNode("Files/Readme/File/Name"));
            string page = readme != null && Regex.IsMatch(readme, @"^[\w.-]+$") ? folder + readme : null;
            var date = VendorCheck.ParseDate(Text(root.SelectSingleNode("ReleaseDate")), "yyyy-MM-dd");
            foreach (XmlNode drv in root.SelectNodes("DetectInstall//_Driver"))
            {
                string version = (Text(drv.SelectSingleNode("Version")) ?? (root.Attributes["version"] != null ? root.Attributes["version"].Value : "")).TrimEnd('^').Trim();
                if (VendorCheck.Parts(version).Length < 2) continue;
                foreach (XmlNode hw in drv.SelectNodes("HardwareID"))
                {
                    string id = hw.InnerText.Trim();
                    foreach (var d in devices.Where(x => x.HasDriver && VendorCheck.HasId(x, id)))
                    {
                        if (VendorCheck.Compare(version, d.VersionText) <= 0 || list.Any(v => v.Device == d)) continue;
                        list.Add(new VendorUpdate
                        {
                            Vendor = "Lenovo", Device = d, Title = title, VersionText = version, CurrentText = d.VersionText, Date = date,
                            Url = page ?? "https://pcsupport.lenovo.com/",
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>Lenovo's files start with a byte-order mark: load from bytes, not from a string.</summary>
        static XmlDocument Xml(byte[] data)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var ms = new MemoryStream(data))
            using (var r = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                doc.Load(r);
            return doc;
        }

        static string Text(XmlNode n)
        {
            return n == null ? null : n.InnerText.Trim();
        }
    }

    /// <summary>
    /// Asus PCs and boards: the driver list of Asus' support site for this model. Laptop entries name
    /// the hardware IDs they are for; motherboard entries do not, so those only get the page link.
    /// </summary>
    class AsusSource : IVendorSource
    {
        public string Name { get { return "ASUS"; } }

        public List<VendorUpdate> Check(IList<Device> devices, PcInfo pc)
        {
            var result = new List<VendorUpdate>();
            string model = pc.AsusModel;
            if (!pc.IsAsus || model.Length == 0) return result;
            string url = "https://www.asus.com/support/api/product.asmx/GetPDDrivers?website=global&model=" + Uri.EscapeDataString(model) +
                         "&osid=" + (pc.IsWindows11 ? "52" : "45") + "&pdhashedid=&pdid=";
            var json = VendorCheck.Json(VendorCheck.Get(url, null, 45000));
            string page = "https://www.asus.com/supportonly/" + Uri.EscapeDataString(model) + "/helpdesk_download/";
            foreach (var group in VendorCheck.Arr(VendorCheck.At(VendorCheck.At(json, "Result"), "Obj")))
                foreach (var file in VendorCheck.Arr(VendorCheck.At(group, "Files")))   // newest first in each group
                {
                    string version = VendorCheck.Str(file, "Version").TrimStart('V', 'v');
                    if (VendorCheck.Parts(version).Length < 2) continue;
                    foreach (var hw in VendorCheck.Arr(VendorCheck.At(file, "HardwareInfoList")))
                    {
                        string id = VendorCheck.Str(hw, "hardwareid");
                        foreach (var d in devices.Where(x => x.HasDriver && VendorCheck.HasId(x, id)))
                        {
                            if (result.Any(v => v.Device == d) || VendorCheck.Compare(version, d.VersionText) <= 0) continue;
                            result.Add(new VendorUpdate
                            {
                                Vendor = "ASUS", Device = d, Title = VendorCheck.Str(file, "Title").TrimEnd('.'), VersionText = version,
                                CurrentText = d.VersionText, Date = VendorCheck.ParseDate(VendorCheck.Str(file, "ReleaseDate"), "yyyy/MM/dd"),
                                Url = page,
                            });
                        }
                    }
                }
            return result;
        }
    }
}
