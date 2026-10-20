using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace DriveUp
{
    /// <summary>What Windows knows about the PC itself (for vendors that publish drivers per PC model).</summary>
    class PcInfo
    {
        public string Manufacturer = "", Model = "", Product = "", ProductVersion = "", Sku = "", BoardMaker = "", BoardProduct = "";
        public int WindowsBuild;

        public bool IsLenovo { get { return Manufacturer.IndexOf("LENOVO", StringComparison.OrdinalIgnoreCase) >= 0; } }
        public bool IsAsus
        {
            get
            {
                return Manufacturer.IndexOf("ASUS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       BoardMaker.IndexOf("ASUS", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        public bool IsDell { get { return Manufacturer.IndexOf("DELL", StringComparison.OrdinalIgnoreCase) >= 0; } }
        public bool IsHp
        {
            get
            {
                return Manufacturer.Equals("HP", StringComparison.OrdinalIgnoreCase) ||
                       Manufacturer.IndexOf("HEWLETT", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        public bool IsSurface
        {
            get { return Manufacturer.IndexOf("MICROSOFT", StringComparison.OrdinalIgnoreCase) >= 0 && Model.IndexOf("SURFACE", StringComparison.OrdinalIgnoreCase) >= 0; }
        }
        public bool IsWindows11 { get { return WindowsBuild >= 22000; } }

        /// <summary>Lenovo machine type: the first four characters of the product name ("20XWCTO1WW" → "20XW").</summary>
        public string LenovoType
        {
            get
            {
                foreach (var s in new[] { Product, Model })
                {
                    var m = Regex.Match(s ?? "", @"^([0-9A-Z]{4})", RegexOptions.IgnoreCase);
                    if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
                }
                return null;
            }
        }

        /// <summary>The model name Asus' support site knows (board product for desktops and laptops alike).</summary>
        public string AsusModel
        {
            get { return BoardProduct.Length > 0 ? BoardProduct : Model; }
        }

        public static PcInfo Read()
        {
            var pc = new PcInfo();
            try
            {
                pc.WindowsBuild = Environment.OSVersion.Version.Build;
                Query("SELECT Manufacturer, Model, SystemSKUNumber FROM Win32_ComputerSystem", o =>
                {
                    pc.Manufacturer = Str(o["Manufacturer"]);
                    pc.Model = Str(o["Model"]);
                    pc.Sku = Str(o["SystemSKUNumber"]);
                });
                Query("SELECT Name, Version FROM Win32_ComputerSystemProduct", o =>
                {
                    pc.Product = Str(o["Name"]);
                    pc.ProductVersion = Str(o["Version"]);
                });
                Query("SELECT Manufacturer, Product FROM Win32_BaseBoard", o =>
                {
                    pc.BoardMaker = Str(o["Manufacturer"]);
                    pc.BoardProduct = Str(o["Product"]);
                });
            }
            catch { }
            return pc;
        }

        static void Query(string wql, Action<ManagementBaseObject> each)
        {
            using (var q = new ManagementObjectSearcher(wql))
                foreach (ManagementBaseObject o in q.Get())
                    using (o) each(o);
        }

        static string Str(object v)
        {
            return v == null ? "" : v.ToString().Trim();
        }
    }

    /// <summary>A newer driver found on a vendor's site: shown as a link (downloaded by the person, then installed on the Install tab).</summary>
    class VendorUpdate
    {
        public string Vendor = "", Title = "", VersionText = "", CurrentText = "", Url = "";
        public DateTime? Date;
        public Device Device;
    }

    /// <summary>One vendor's way of telling the newest driver for the hardware of this PC.</summary>
    interface IVendorSource
    {
        string Name { get; }
        /// <summary>Only the updates newer than what is installed. May throw: the caller ignores that vendor then.</summary>
        List<VendorUpdate> Check(IList<Device> devices, PcInfo pc);
    }

    /// <summary>Checks the vendors' sites in parallel, each on its own, within one time limit.</summary>
    static class VendorCheck
    {
        static readonly IVendorSource[] Sources =
        {
            new NvidiaSource(), new AmdSource(), new IntelSource(), new LenovoSource(), new AsusSource(),
        };

        public static List<VendorUpdate> CheckAll(IList<Device> devices, PcInfo pc, int timeLimitMs = 40000)
        {
            var found = new List<VendorUpdate>();
            var threads = new List<Thread>();
            foreach (var src in Sources)
            {
                var s = src;
                var th = new Thread(() =>
                {
                    List<VendorUpdate> r = null;
                    try { r = s.Check(devices, pc); } catch { }
                    if (r != null) lock (found) found.AddRange(r);
                }) { IsBackground = true, Name = "vendor " + s.Name };
                th.Start();
                threads.Add(th);
            }
            var deadline = DateTime.UtcNow.AddMilliseconds(timeLimitMs);
            foreach (var th in threads)
            {
                var left = deadline - DateTime.UtcNow;
                if (left > TimeSpan.Zero) th.Join(left);
            }
            lock (found)
            {
                // One line per device: a PC maker's package and the chip maker's driver may both be newer;
                // the PC maker's one is meant for this very model, so it wins.
                var result = new List<VendorUpdate>();
                foreach (var v in found.OrderBy(x => x.Vendor == "Lenovo" || x.Vendor == "ASUS" ? 0 : 1))
                    if (v.Device == null || !result.Any(x => x.Device == v.Device)) result.Add(v);
                return result;
            }
        }

        // ---------- "vendor's site" for any device ----------

        static readonly Regex VenId = new Regex(@"(?:VEN_|VID_)([0-9A-F]{4})", RegexOptions.IgnoreCase);
        public const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";

        /// <summary>The PCI / USB vendor ID of a device ("10DE"), or null.</summary>
        public static string VendorId(Device d)
        {
            foreach (var id in d.HardwareIds)
            {
                var m = VenId.Match(id);
                if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            }
            return null;
        }

        public static bool IsDisplay(Device d)
        {
            return d.ClassGuid.Equals(DisplayClass, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Driver pages of chip makers, by PCI / USB vendor ID.</summary>
        static readonly Dictionary<string, string> Pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "10DE", "https://www.nvidia.com/en-us/drivers/" },
            { "1002", "https://www.amd.com/en/support/download/drivers.html" },
            { "1022", "https://www.amd.com/en/support/download/drivers.html" },
            { "8086", "https://www.intel.com/content/www/us/en/support/detect.html" },
            { "8087", "https://www.intel.com/content/www/us/en/support/detect.html" },
            { "10EC", "https://www.realtek.com/Downloads" },
            { "0BDA", "https://www.realtek.com/Downloads" },
            { "168C", "https://www.qualcomm.com/support" },
            { "17CB", "https://www.qualcomm.com/support" },
            { "14E4", "https://www.broadcom.com/support/download-search" },
            { "14C3", "https://www.mediatek.com/products/connectivity-and-networking" },
            { "11AB", "https://www.marvell.com/support/downloads.html" },
            { "1B4B", "https://www.marvell.com/support/downloads.html" },
            { "06CB", "https://www.synaptics.com/products/displaylink-graphics/downloads/windows" },
            { "17E9", "https://www.synaptics.com/products/displaylink-graphics/downloads/windows" },
            { "04F3", "https://www.elan.com.tw/en/" },
        };

        /// <summary>The PC maker's driver page for this model, when the PC maker is known.</summary>
        static string PcPage(PcInfo pc)
        {
            if (pc == null) return null;
            if (pc.IsLenovo && pc.LenovoType != null) return "https://pcsupport.lenovo.com/us/en/products/" + pc.LenovoType + "/downloads/driver-list";
            if (pc.IsAsus && pc.AsusModel.Length > 0) return "https://www.asus.com/supportonly/" + Uri.EscapeDataString(pc.AsusModel) + "/helpdesk_download/";
            if (pc.IsDell) return "https://www.dell.com/support/home/en-us?app=drivers";
            if (pc.IsHp) return "https://support.hp.com/us-en/drivers";
            if (pc.IsSurface) return "https://support.microsoft.com/en-us/surface/download-drivers-and-firmware-for-surface-09bb2e09-2a4b-cb69-0951-078a7739e120";
            return null;
        }

        /// <summary>
        /// Where to look for a driver for this device: the page a check found; for graphics the chip
        /// maker (their drivers suit any PC); otherwise the PC maker's page for this model (they ship
        /// drivers made for it), else the chip maker's page.
        /// </summary>
        public static string PageFor(Device d, PcInfo pc)
        {
            if (d.Vendor != null && d.Vendor.Url.Length > 0) return d.Vendor.Url;
            string ven = VendorId(d), page = null;
            bool chip = ven != null && Pages.TryGetValue(ven, out page);
            if (chip && IsDisplay(d)) return page;
            return PcPage(pc) ?? (chip ? page : null);
        }

        // ---------- HTTP and JSON ----------

        static VendorCheck()
        {
            // csc builds the exe without a target-framework mark, so .NET would offer old TLS only.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }   // TLS 1.3 where Windows has it
        }

        const string Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

        /// <summary>GET; small answers only (a few MB), short timeouts. One retry: some vendors answer slowly at times.</summary>
        public static byte[] GetBytes(string url, string referer = null, int timeoutMs = 20000)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return GetOnce(url, referer, timeoutMs); }
                catch (WebException ex)
                {
                    var resp = ex.Response as HttpWebResponse;
                    bool retry = attempt == 0 && (resp == null || (int)resp.StatusCode >= 500);
                    if (ex.Response != null) ex.Response.Close();
                    if (!retry) throw;
                }
            }
        }

        static byte[] GetOnce(string url, string referer, int timeoutMs)
        {
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = Agent;
            rq.Accept = "application/json, text/xml, application/xml, text/html;q=0.9, */*;q=0.8";
            rq.Headers[HttpRequestHeader.AcceptLanguage] = "en-US,en;q=0.9";
            if (referer != null) rq.Referer = referer;
            rq.Timeout = timeoutMs;
            rq.ReadWriteTimeout = timeoutMs;
            rq.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = (HttpWebResponse)rq.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var buf = new byte[65536];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > 16 << 20) throw new IOException("answer too large");
                }
                return ms.ToArray();
            }
        }

        public static string Get(string url, string referer = null, int timeoutMs = 20000)
        {
            var b = GetBytes(url, referer, timeoutMs);
            string s = Encoding.UTF8.GetString(b);
            return s.Length > 0 && s[0] == (char)0xFEFF ? s.Substring(1) : s;
        }

        public static object Json(string text)
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 }.DeserializeObject(text);
        }

        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }

        public static object[] Arr(object o) { return o as object[] ?? new object[0]; }

        public static object At(object o, string key)
        {
            var d = Obj(o);
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(object o, string key)
        {
            var v = At(o, key);
            return v == null ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture).Trim();
        }

        // ---------- versions and hardware IDs ----------

        /// <summary>Compares dotted versions ("566.36" &gt; "560.94"); missing parts count as 0.</summary>
        public static int Compare(string a, string b)
        {
            var pa = Parts(a);
            var pb = Parts(b);
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                long x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        public static long[] Parts(string v)
        {
            var m = Regex.Match(v ?? "", @"\d+(\.\d+)*");
            if (!m.Success) return new long[0];
            return m.Value.Split('.').Select(x => { long n; long.TryParse(x, out n); return n; }).ToArray();
        }

        /// <summary>Hardware IDs without the bus prefix: "PCI\VEN_8086&amp;DEV_2725&amp;..." → "VEN_8086&amp;DEV_2725&amp;...".</summary>
        public static IEnumerable<string> BareIds(Device d)
        {
            foreach (var id in d.HardwareIds.Concat(d.CompatibleIds))
            {
                int slash = id.IndexOf('\\');
                yield return slash >= 0 ? id.Substring(slash + 1) : id;
            }
        }

        /// <summary>True when the device has the ID itself or a more specific one ("PCI\VEN_10DE&amp;DEV_2438" matches "...&amp;SUBSYS_...").</summary>
        public static bool HasId(Device d, string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            foreach (var h in d.HardwareIds)
                if (h.StartsWith(id, StringComparison.OrdinalIgnoreCase) && (h.Length == id.Length || h[id.Length] == '&'))
                    return true;
            return false;
        }

        static readonly Regex DevPart = new Regex(@"DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        static readonly Regex RevPart = new Regex(@"REV_([0-9A-F]{2})", RegexOptions.IgnoreCase);

        public static string DeviceId(Device d)
        {
            foreach (var id in d.HardwareIds)
            {
                var m = DevPart.Match(id);
                if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            }
            return null;
        }

        public static string RevisionId(Device d)
        {
            foreach (var id in d.HardwareIds)
            {
                var m = RevPart.Match(id);
                if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            }
            return null;
        }

        public static DateTime? ParseDate(string s, params string[] formats)
        {
            DateTime d;
            if (DateTime.TryParseExact((s ?? "").Trim(), formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out d)) return d.Date;
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out d)) return d.Date;
            return null;
        }
    }
}
