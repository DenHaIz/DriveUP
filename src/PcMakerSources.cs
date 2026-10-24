using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;

namespace DriveUp
{
    /// <summary>
    /// HP: the reference file HP Image Assistant uses for one system board and Windows release
    /// (hpia.hpcloud.hp.com/ref/{board}/{board}_64_{11.0}.{24h2}.cab). Each device entry has the
    /// hardware ID, the driver version and date and the SoftPaq it comes in.
    /// </summary>
    class HpSource : IVendorSource
    {
        public string Name { get { return "HP"; } }
        const string Root = "https://hpia.hpcloud.hp.com/ref/";

        public List<VendorUpdate> Check(IList<Device> devices, PcInfo pc)
        {
            var result = new List<VendorUpdate>();
            if (pc == null || !pc.IsHp || pc.BoardProduct.Length == 0) return result;
            string board = pc.BoardProduct.Trim().ToLowerInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(board, "^[0-9a-f]{4}$")) return result;
            XmlDocument doc = null;
            foreach (string release in Releases(board))
            {
                try { doc = VendorCheck.CabXml(VendorCheck.GetBytes(Root + board + "/" + board + "_64_" + release + ".cab", null, 30000)); break; }
                catch (System.Net.WebException) { }
            }
            return doc == null ? result : Match(doc, devices);
        }

        /// <summary>Newer drivers in an HP reference file for these devices (by hardware ID).</summary>
        public static List<VendorUpdate> Match(XmlDocument doc, IList<Device> devices)
        {
            var result = new List<VendorUpdate>();
            var packs = new Dictionary<string, XmlElement>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlElement u in doc.SelectNodes("/ImagePal/Solutions/UpdateInfo"))
            {
                string id = Text(u, "Id");
                if (id.Length > 0) packs[id] = u;
            }
            var best = new Dictionary<Device, Tuple<XmlElement, string>>();
            foreach (XmlElement e in doc.SelectNodes("/ImagePal/Devices/Device"))
            {
                string hwid = Text(e, "DeviceId"), version = Text(e, "DriverVersion");
                if (hwid.Length == 0 || version.Length == 0) continue;
                foreach (var d in devices)
                {
                    if (!d.HasDriver || !VendorCheck.HasId(d, hwid) || VendorCheck.Compare(version, d.VersionText) <= 0) continue;
                    Tuple<XmlElement, string> was;
                    if (!best.TryGetValue(d, out was) || VendorCheck.Compare(version, was.Item2) > 0) best[d] = Tuple.Create(e, version);
                }
            }
            foreach (var kv in best)
            {
                var e = kv.Value.Item1;
                var refNode = e.SelectSingleNode("Solutions/UpdateInfo") as XmlElement;
                XmlElement pack = null;
                if (refNode != null) packs.TryGetValue(refNode.GetAttribute("IdRef"), out pack);
                string page = pack != null ? Text(pack, "ReleaseNotesUrl") : "";
                result.Add(new VendorUpdate
                {
                    Vendor = "HP", Device = kv.Key, VersionText = kv.Value.Item2, CurrentText = kv.Key.VersionText,
                    Title = pack != null ? Text(pack, "Name") : Text(e, "FriendlyName"),
                    Date = VendorCheck.ParseDate(Text(e, "DriverDate"), "MM/dd/yyyy"),
                    Url = page.Length > 0 ? "https://" + page.Replace("https://", "").Replace("http://", "") : "https://support.hp.com/drivers",
                });
            }
            return result;
        }

        /// <summary>This Windows release first, then the ones HP lists for the board (newest first).</summary>
        static IEnumerable<string> Releases(string board)
        {
            string major = WindowsRelease.Is11 ? "11.0" : "10.0";
            string now = WindowsRelease.Name;
            if (now.Length > 0) yield return major + "." + now;
            List<string> listed = new List<string>();
            try
            {
                var pl = VendorCheck.CabXml(VendorCheck.GetBytes(Root + "platformList.cab", null, 30000));
                foreach (XmlElement p in pl.SelectNodes("//Platform"))
                {
                    if (!Text(p, "SystemID").Equals(board, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (XmlElement os in p.SelectNodes("OS"))
                    {
                        bool w11 = Text(os, "IsWindows11").Equals("true", StringComparison.OrdinalIgnoreCase);
                        string rel = Text(os, "OSReleaseIdFilename").ToLowerInvariant();
                        if (w11 == WindowsRelease.Is11 && rel.Length > 0 && WindowsRelease.Key(rel) <= Math.Max(WindowsRelease.Current, 1)) listed.Add(rel);
                    }
                }
            }
            catch { }
            foreach (var rel in listed.Distinct().OrderByDescending(WindowsRelease.Key))
                if (rel != now) yield return major + "." + rel;
        }

        static string Text(XmlElement e, string child)
        {
            var n = e.SelectSingleNode(child);
            return n != null ? n.InnerText.Trim() : "";
        }
    }

    /// <summary>
    /// Dell: the Dell Command Update catalogs. The index (CatalogIndexPC.cab) names the catalog of each
    /// model by its system ID (= SystemSKUNumber); in it, driver packages list the PCI / USB devices
    /// they are for with the driver version of each. Only those (matched by ID) are used, never names.
    /// </summary>
    class DellSource : IVendorSource
    {
        public string Name { get { return "Dell"; } }
        const string Host = "https://downloads.dell.com/";

        public List<VendorUpdate> Check(IList<Device> devices, PcInfo pc)
        {
            var result = new List<VendorUpdate>();
            if (pc == null || !pc.IsDell || pc.Sku.Length == 0) return result;
            string sku = pc.Sku.Trim();
            var index = VendorCheck.CabXml(VendorCheck.GetBytes(Host + "catalog/CatalogIndexPC.cab", null, 40000));
            string path = ModelPath(index, sku);
            if (string.IsNullOrEmpty(path) || path.Contains("..")) return result;
            return Match(VendorCheck.CabXml(VendorCheck.GetBytes(Host + path.TrimStart('/'), null, 40000)), devices);
        }

        /// <summary>The model catalog's path in the index for a system ID (SystemSKUNumber), or null.</summary>
        public static string ModelPath(XmlDocument index, string sku)
        {
            foreach (XmlElement gm in index.GetElementsByTagName("GroupManifest"))
            {
                bool mine = false;
                foreach (XmlElement m in gm.GetElementsByTagName("Model"))
                    if (m.GetAttribute("systemID").Equals(sku, StringComparison.OrdinalIgnoreCase)) mine = true;
                if (!mine) continue;
                foreach (XmlElement mi in gm.GetElementsByTagName("ManifestInformation")) return mi.GetAttribute("path");
            }
            return null;
        }

        /// <summary>Newer drivers in a Dell model catalog for these devices (by PCI / USB ID only).</summary>
        public static List<VendorUpdate> Match(XmlDocument cat, IList<Device> devices)
        {
            var result = new List<VendorUpdate>();
            var best = new Dictionary<Device, Tuple<XmlElement, string>>();
            foreach (XmlElement sc in cat.GetElementsByTagName("SoftwareComponent"))
            {
                bool driver = false;
                foreach (XmlElement ct in sc.GetElementsByTagName("ComponentType")) driver = ct.GetAttribute("value") == "DRVR";
                if (!driver) continue;
                foreach (XmlElement dev in sc.GetElementsByTagName("Device"))
                {
                    string version = dev.GetAttribute("version");
                    if (version.Length == 0) version = sc.GetAttribute("vendorVersion");
                    foreach (string id in Ids(dev))
                        foreach (var d in devices)
                        {
                            if (!d.HasDriver || !VendorCheck.HasId(d, id) || VendorCheck.Compare(version, d.VersionText) <= 0) continue;
                            Tuple<XmlElement, string> was;
                            if (!best.TryGetValue(d, out was) || VendorCheck.Compare(version, was.Item2) > 0) best[d] = Tuple.Create(sc, version);
                        }
                }
            }
            foreach (var kv in best)
            {
                var sc = kv.Value.Item1;
                string page = "";
                foreach (XmlElement ii in sc.GetElementsByTagName("ImportantInfo")) { page = ii.GetAttribute("URL"); break; }
                string name = "";
                foreach (XmlElement n in sc.GetElementsByTagName("Name")) { name = n.InnerText.Trim(); break; }
                DateTime date;
                result.Add(new VendorUpdate
                {
                    Vendor = "Dell", Device = kv.Key, VersionText = kv.Value.Item2, CurrentText = kv.Key.VersionText, Title = name,
                    Date = DateTime.TryParse(sc.GetAttribute("releaseDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ? (DateTime?)date : null,
                    Url = page.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && new Uri(page).Host.EndsWith("dell.com", StringComparison.OrdinalIgnoreCase)
                        ? page : "https://www.dell.com/support/home/?app=drivers",
                });
            }
            return result;
        }

        /// <summary>PCI\VEN_x&amp;DEV_y[&amp;SUBSYS_ss] and USB\VID_x&amp;PID_y the package names.</summary>
        static IEnumerable<string> Ids(XmlElement dev)
        {
            foreach (XmlElement p in dev.GetElementsByTagName("PCIInfo"))
            {
                string ven = p.GetAttribute("vendorID"), id = p.GetAttribute("deviceID"), sv = p.GetAttribute("subVendorID"), sd = p.GetAttribute("subDeviceID");
                if (ven.Length != 4 || id.Length != 4) continue;
                yield return "PCI\\VEN_" + ven + "&DEV_" + id + (sv.Length == 4 && sd.Length == 4 ? "&SUBSYS_" + sd + sv : "");
            }
            foreach (XmlElement p in dev.GetElementsByTagName("PnPInfo"))
            {
                string vid = "", pid = "";
                foreach (XmlElement v in p.GetElementsByTagName("PNPID")) vid = v.InnerText.Trim();
                foreach (XmlElement v in p.GetElementsByTagName("PnPProductID")) pid = v.InnerText.Trim();
                if (vid.Length == 4 && pid.Length == 4) yield return "USB\\VID_" + vid + "&PID_" + pid;
            }
        }
    }
}
