using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Windows.Forms;

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
            // Once the device has another driver (it was just updated), the old finding no longer applies.
            Vendors = Vendors.Where(v =>
            {
                if (v.Device == null) return true;
                var d = Devices.FirstOrDefault(x => x.InstanceId == v.Device.InstanceId);
                if (d == null) return true;
                if (d != v.Device && d.VersionText != v.Device.VersionText) return false;
                d.Vendor = v;
                v.Device = d;
                return true;
            }).ToList();
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
        /// <summary>Set once Create found that this Windows has no System Restore (Server): nothing to ask about then.</summary>
        public static bool Unsupported;

        const string Policy = @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";
        /// <summary>The volumes System Protection is on for: "\\?\Volume{…}\:(C%3A)" per volume, under the System Restore client.</summary>
        const string Clients = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients", SrClient = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";

        static string SystemDrive { get { return System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)); } }

        static object Machine(string path, string name)
        {
            try
            {
                using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(path))
                    return k == null ? null : k.GetValue(name);
            }
            catch { return null; }
        }

        /// <summary>Turned off by an organisation's policy: nothing DriveUP should change.</summary>
        static bool OffByPolicy
        {
            get { return Convert.ToInt32(Machine(Policy, "DisableSR") ?? 0) == 1 || Convert.ToInt32(Machine(Policy, "DisableConfig") ?? 0) == 1; }
        }

        /// <summary>System Protection is known to be off for the system drive (false when it is on or cannot be told).</summary>
        static bool ProtectionOff
        {
            get
            {
                string[] list;
                try
                {
                    using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                    using (var k = hklm.OpenSubKey(Clients))
                    {
                        // No such key (Windows Server): cannot be told.
                        if (k == null) return false;
                        list = k.GetValue(SrClient) as string[];
                    }
                }
                catch { return false; }
                if (list == null || list.All(string.IsNullOrWhiteSpace)) return true;
                // Entries end with the drive letter, "(C%3A)"; a format without it cannot be told apart.
                if (!list.Any(x => x.IndexOf("%3A)", StringComparison.OrdinalIgnoreCase) >= 0)) return false;
                string letter = SystemDrive.Substring(0, 1);
                return !list.Any(x => x.IndexOf("(" + letter + "%3A)", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        static bool ServiceDisabled(string name)
        {
            return Convert.ToInt32(Machine(@"SYSTEM\CurrentControlSet\Services\" + name, "Start") ?? 3) == 4;
        }

        /// <summary>A failure in words, with the code to look up when it is not a known one.</summary>
        static string Explain(uint code)
        {
            switch (code)
            {
                case 1058: case 0x8007041D:
                    return ServiceDisabled("VSS") ? VssOff() : L.T("защита системы выключена", "System Protection is turned off");
                case 0x80070422: return VssOff();
                case 1460: case 0x800705B4: return L.T("Windows не успела создать точку — диск занят, попробуйте ещё раз", "Windows ran out of time creating the point — the disk is busy, try again");
                case 112: case 0x80070070: return L.T("на системном диске не хватает места", "the system drive is out of space");
                case 0x8004231F: case 0x80042317:
                    return L.T("не хватает места, отведённого под точки восстановления (Свойства системы → Защита системы → Настроить)",
                               "not enough space set aside for restore points (System Properties → System Protection → Configure)");
                case 0x80042316: return L.T("Windows уже создаёт другую точку — попробуйте через пару минут", "Windows is already creating another point — try again in a couple of minutes");
                case 0x80042302: case 0x80042306: case 0x8004230F:
                    return L.T("служба теневого копирования (VSS) сообщила об ошибке 0x", "the Volume Shadow Copy service (VSS) reported error 0x") + code.ToString("X8") +
                           L.T(" — обычно помогает перезагрузка", " — a restart usually helps");
            }
            return L.T("Windows вернула ошибку 0x", "Windows returned error 0x") + code.ToString("X8");
        }

        static string VssOff()
        {
            return L.T("служба «Теневое копирование тома» отключена — без неё Windows не создаёт точки", "the Volume Shadow Copy service is disabled — Windows needs it for restore points");
        }

        /// <summary>WMI's own "Generic failure" says nothing: look for the usual reasons first.</summary>
        static string Explain(ManagementException ex)
        {
            if (OffByPolicy) return L.T("восстановление системы отключено политикой организации", "System Restore is turned off by your organisation's policy");
            if (ProtectionOff) return L.T("защита системы выключена", "System Protection is turned off");
            if (ServiceDisabled("VSS") || ServiceDisabled("swprv")) return VssOff();
            if (ex.ErrorCode == ManagementStatus.AccessDenied) return L.T("нужны права администратора", "administrator rights are needed");
            return L.T("сбой службы восстановления системы (WMI ", "System Restore service failure (WMI ") + ex.ErrorCode + ")";
        }

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
                    // At most 64 characters are kept.
                    args["Description"] = description.Length > 64 ? description.Substring(0, 64) : description;
                    args["RestorePointType"] = 10;   // DEVICE_DRIVER_INSTALL
                    args["EventType"] = 100;         // BEGIN_SYSTEM_CHANGE, as Checkpoint-Computer
                    var outp = mc.InvokeMethod("CreateRestorePoint", args, null);
                    uint rv = Convert.ToUInt32(outp["ReturnValue"]);
                    if (rv != 0) return Explain(rv);
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
                if (ProtectionOff) return L.T("защита системы выключена", "System Protection is turned off");
                return L.T("Windows не создала точку восстановления", "Windows did not create a restore point");
            }
            catch (ManagementException ex)
            {
                if (ex.ErrorCode == ManagementStatus.InvalidClass || ex.ErrorCode == ManagementStatus.InvalidNamespace || ex.ErrorCode == ManagementStatus.NotFound)
                {
                    Unsupported = true;
                    return L.T("в этой Windows нет восстановления системы", "this Windows has no System Restore");
                }
                return Explain(ex);
            }
            catch (UnauthorizedAccessException) { return L.T("нужны права администратора", "administrator rights are needed"); }
            catch (System.Runtime.InteropServices.COMException ex) { return Explain(unchecked((uint)ex.ErrorCode)); }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Turns System Protection on for the system drive (as Enable-ComputerRestore does). Null when done.</summary>
        static string EnableProtection()
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\default");
                using (var mc = new ManagementClass(scope, new ManagementPath("SystemRestore"), null))
                {
                    var args = mc.GetMethodParameters("Enable");
                    args["Drive"] = SystemDrive;
                    args["WaitTillEnabled"] = true;
                    uint rv = Convert.ToUInt32(mc.InvokeMethod("Enable", args, null)["ReturnValue"]);
                    return rv == 0 ? null : Explain(rv);
                }
            }
            catch (ManagementException ex) { return Explain(ex); }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// After Create failed. Where System Protection is simply off, offers to turn it on and makes the point;
        /// otherwise (or if the user says no) asks whether to go on without one. True = go on;
        /// <paramref name="ok"/> and <paramref name="why"/> then tell how it ended.
        /// </summary>
        public static bool Resolve(Func<Func<bool>, bool> onUi, IWin32Window owner, string description, ref string why, ref bool ok)
        {
            if (ok || Unsupported) return true;
            if (!OffByPolicy && ProtectionOff)
            {
                string drive = SystemDrive.TrimEnd('\\');
                bool yes = onUi(() => ThemedDialog.Confirm(owner, L.T("Защита системы выключена", "System Protection is off"),
                    L.T("Windows не создаёт точки восстановления, потому что для диска ", "Windows makes no restore points because System Protection is off for drive ") + drive +
                    L.T(" выключена защита системы (так бывает на новых компьютерах).", " (common on new computers).") + "\n\n" +
                    L.T("Включить её и создать точку? Windows будет хранить точки на диске ", "Turn it on and create the point? Windows keeps the points on drive ") + drive +
                    L.T(" и сама удалять старые. Выключить можно в «Свойства системы → Защита системы».", " and removes old ones by itself. It can be turned off under System Properties → System Protection."),
                    L.T("Включить и создать", "Turn on and create"), "\uE72E", false));
                if (yes)
                {
                    string error = EnableProtection();
                    if (error == null)
                    {
                        bool made;
                        string note = Create(description, out made);
                        if (made) { ok = true; why = note; return true; }
                        why = note;
                    }
                    else why = error;
                }
            }
            else if (OffByPolicy) why = L.T("восстановление системы отключено политикой организации", "System Restore is turned off by your organisation's policy");
            return ContinueWithout(onUi, owner, why);
        }

        /// <summary>
        /// When a wanted restore point could not be made: asks (on the UI thread, waits) whether to go on
        /// without one. True = go on. Windows without System Restore at all goes on without asking.
        /// </summary>
        public static bool ContinueWithout(Func<Func<bool>, bool> onUi, IWin32Window owner, string why)
        {
            if (Unsupported) return true;
            return onUi(() => ThemedDialog.Confirm(owner, L.T("Точка восстановления не создана", "No restore point"),
                L.T("Не удалось создать точку восстановления", "Could not create a restore point") + (string.IsNullOrEmpty(why) ? "" : ": " + why) + ".\n\n" +
                L.T("Без неё изменения нельзя будет откатить через «Восстановление системы». Продолжить без точки?",
                    "Without it the changes cannot be rolled back with System Restore. Go on without one?"),
                L.T("Продолжить", "Go on"), "\uE768", true));
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
