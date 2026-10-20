using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Management;

namespace DriveUp
{
    /// <summary>
    /// What the tabs share: the present devices (with icons) and the last Windows Update result.
    /// Changed only on the UI thread; Changed tells the other tabs to redraw.
    /// </summary>
    static class Inventory
    {
        public static List<Device> Devices = new List<Device>();
        public static bool DevicesLoaded;
        /// <summary>null until the first check is done.</summary>
        public static List<DriverUpdate> Updates;
        /// <summary>Newer drivers found on vendors' sites by the last check.</summary>
        public static List<VendorUpdate> Vendors = new List<VendorUpdate>();
        /// <summary>The PC itself (maker, model), read once per check.</summary>
        public static PcInfo Pc = new PcInfo();
        public static readonly Dictionary<string, Bitmap> Icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        public static event Action Changed;

        public static int Attention { get { return Devices.Count(d => d.NeedsAttention); } }

        /// <summary>Loads devices on the calling (background) thread; hand the result to SetDevices on the UI thread.</summary>
        public static List<Device> LoadDevices(out Dictionary<string, Bitmap> icons)
        {
            icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            try { return DriveUp.Devices.Load(icons); }
            catch { return new List<Device>(); }
        }

        public static void SetDevices(List<Device> list, Dictionary<string, Bitmap> icons)
        {
            foreach (var kv in icons)
            {
                Bitmap old;
                if (Icons.TryGetValue(kv.Key, out old) && old != kv.Value) old.Dispose();
                Icons[kv.Key] = kv.Value;
            }
            Devices = list;
            DevicesLoaded = true;
            Match();
            if (Changed != null) Changed();
        }

        public static void SetUpdates(List<DriverUpdate> list, List<VendorUpdate> vendors, PcInfo pc)
        {
            Vendors = vendors ?? new List<VendorUpdate>();
            if (pc != null) Pc = pc;
            Updates = list;
            Match();
            if (Changed != null) Changed();
        }

        /// <summary>Removes installed updates from the device rows.</summary>
        public static void UpdatesInstalled(IEnumerable<DriverUpdate> done)
        {
            if (Updates == null) return;
            var set = new HashSet<DriverUpdate>(done);
            Updates = Updates.Where(u => !set.Contains(u)).ToList();
            Match();
            if (Changed != null) Changed();
        }

        /// <summary>Ties each update to the device it is for (by hardware ID).</summary>
        static void Match()
        {
            foreach (var d in Devices) { d.Update = null; d.Vendor = null; }
            // Vendor results point at the device objects of the list they were checked with: match by instance.
            foreach (var v in Vendors)
            {
                if (v.Device == null) continue;
                var d = Devices.FirstOrDefault(x => x.InstanceId == v.Device.InstanceId);
                if (d != null) { d.Vendor = v; v.Device = d; }
            }
            if (Updates == null) return;
            foreach (var u in Updates)
            {
                u.Device = null;
                foreach (var d in Devices)
                    if (d.MatchesId(u.HardwareId))
                    {
                        u.Device = d;
                        if (d.Update == null) d.Update = u;
                        break;
                    }
            }
        }

        /// <summary>Class icon for package rows, loaded once ("class:{guid}").</summary>
        public static void EnsureClassIcon(string guid)
        {
            string key = "class:" + guid;
            if (string.IsNullOrEmpty(guid) || Icons.ContainsKey(key)) return;
            var bmp = DriveUp.Devices.ClassIcon(guid);
            if (bmp != null) Icons[key] = bmp;
        }
    }

    /// <summary>System Restore points (client Windows only; Windows Server has no System Restore).</summary>
    static class SysRestore
    {
        /// <summary>
        /// Asks for a restore point. <paramref name="ok"/> is true when one protects the change (made now,
        /// or made earlier today: Windows creates at most one a day). The text explains anything unusual.
        /// </summary>
        public static string Create(string description, out bool ok)
        {
            ok = false;
            try
            {
                var scope = new ManagementScope(@"\\.\root\default");
                DateTime lastBefore;
                uint before = Newest(scope, out lastBefore);
                using (var mc = new ManagementClass(scope, new ManagementPath("SystemRestore"), null))
                {
                    var args = mc.GetMethodParameters("CreateRestorePoint");
                    args["Description"] = description;
                    args["RestorePointType"] = 10;   // DEVICE_DRIVER_INSTALL
                    args["EventType"] = 100;         // BEGIN_SYSTEM_CHANGE
                    var outp = mc.InvokeMethod("CreateRestorePoint", args, null);
                    uint rv = Convert.ToUInt32(outp["ReturnValue"]);
                    if (rv == 1058) return L.T("защита системы выключена", "System Protection is turned off");
                    if (rv != 0) return L.T("код ", "code ") + rv;
                }
                DateTime lastAfter;
                uint after = Newest(scope, out lastAfter);
                if (after > before) { ok = true; return null; }
                if (before > 0 && DateTime.Now - lastBefore < TimeSpan.FromHours(24))
                {
                    ok = true;
                    return L.T("новую точку Windows не создала — действует точка от ", "Windows made no new point — the one from ") + lastBefore.ToString("g") +
                           L.T(" (не чаще раза в сутки)", " still applies (one a day at most)");
                }
                return L.T("Windows не создала точку восстановления", "Windows did not create a restore point");
            }
            catch (ManagementException ex)
            {
                if (ex.ErrorCode == ManagementStatus.InvalidClass || ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.NotFound)
                    return L.T("в этой Windows нет восстановления системы", "this Windows has no System Restore");
                if (ex.ErrorCode == ManagementStatus.AccessDenied) return L.T("нужны права администратора", "administrator rights are needed");
                return ex.Message;
            }
            catch (UnauthorizedAccessException) { return L.T("нужны права администратора", "administrator rights are needed"); }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Sequence number and time of the newest restore point (0 when there is none).</summary>
        static uint Newest(ManagementScope scope, out DateTime when)
        {
            when = DateTime.MinValue;
            uint max = 0;
            try
            {
                using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT SequenceNumber, CreationTime FROM SystemRestore")))
                    foreach (ManagementObject o in q.Get())
                        using (o)
                        {
                            uint n = Convert.ToUInt32(o["SequenceNumber"]);
                            if (n <= max) continue;
                            max = n;
                            try { when = ManagementDateTimeConverter.ToDateTime((string)o["CreationTime"]); } catch { }
                        }
            }
            catch (ManagementException) { }
            return max;
        }
    }
}
