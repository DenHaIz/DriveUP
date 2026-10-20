using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DriveUp
{
    /// <summary>A device that is present now, with its driver as Device Manager shows it.</summary>
    class Device : IRowInfo, ISortable
    {
        public string InstanceId = "", Name = "", ClassName = "", ClassKey = "", ClassGuid = "";
        public string Manufacturer = "", Provider = "", VersionText = "", InfName = "";
        public DateTime? Date;
        public Version Version;
        public string[] HardwareIds = new string[0], CompatibleIds = new string[0];
        /// <summary>Configuration Manager problem code (CM_PROB_*); 0 = working.</summary>
        public int Problem;
        /// <summary>The update Windows Update offers for this device, if any (set after a check).</summary>
        public DriverUpdate Update;
        /// <summary>A newer driver on the vendor's site, if a check found one.</summary>
        public VendorUpdate Vendor;

        public bool Disabled { get { return Problem == 22; } }
        public bool NeedsAttention { get { return Problem != 0 && !Disabled; } }
        public bool HasDriver { get { return VersionText.Length > 0; } }
        public bool ThirdParty
        {
            get { return Provider.Length > 0 && Provider.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) < 0; }
        }

        public string StatusText
        {
            get
            {
                if (Problem != 0) return Devices.ProblemText(Problem);
                if (Update != null)
                    return L.T("Есть обновление", "Update available") + (Update.VersionText.Length > 0 ? " · " + Update.VersionText : "");
                if (Vendor != null)
                    return L.T("На сайте ", "On the ") + Vendor.Vendor + L.T(": ", " site: ") + Vendor.VersionText;
                return L.T("Работает", "Working");
            }
        }

        public RowKind RowKind { get { return NeedsAttention ? RowKind.Danger : (Disabled ? RowKind.Dim : RowKind.Normal); } }
        public string IconPath { get { return InstanceId; } }
        public int Dot { get { return NeedsAttention ? 3 : (Disabled ? 2 : (Update != null || Vendor != null ? 4 : 1)); } }

        public IComparable SortKey(int column)
        {
            switch (column)
            {
                case 3: return Version ?? new Version(0, 0);
                case 4: return Date ?? DateTime.MinValue;
                // Problems first, then updates, then the rest.
                case 5: return (NeedsAttention ? 0 : (Update != null || Vendor != null ? 1 : (Disabled ? 3 : 2))).ToString(CultureInfo.InvariantCulture) + Name;
                default: return null;
            }
        }

        public bool MatchesId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            foreach (var h in HardwareIds) if (h.Equals(id, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var h in CompatibleIds) if (h.Equals(id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>Devices and their drivers through SetupAPI / Configuration Manager (no WMI: fast and language-neutral).</summary>
    static class Devices
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DEVPROPKEY
        {
            public Guid fmtid;
            public int pid;
            public DEVPROPKEY(Guid g, int p) { fmtid = g; pid = p; }
        }

        const int DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
        const int SPDRP_DEVICEDESC = 0x0, SPDRP_HARDWAREID = 0x1, SPDRP_COMPATIBLEIDS = 0x2, SPDRP_CLASS = 0x7,
                  SPDRP_MFG = 0xB, SPDRP_FRIENDLYNAME = 0xC;
        const int DN_HAS_PROBLEM = 0x400;
        static readonly IntPtr InvalidHandle = new IntPtr(-1);

        static readonly Guid DriverProps = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
        static DEVPROPKEY DriverDate = new DEVPROPKEY(DriverProps, 2);
        static DEVPROPKEY DriverVersion = new DEVPROPKEY(DriverProps, 3);
        static DEVPROPKEY DriverInfPath = new DEVPROPKEY(DriverProps, 5);
        static DEVPROPKEY DriverProvider = new DEVPROPKEY(DriverProps, 9);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property, out int regType, byte[] buffer, int size, out int required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out int propType, byte[] buffer, int size, out int required, int flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetClassDescriptionW(ref Guid classGuid, StringBuilder desc, int size, out int required);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiLoadDeviceIcon(IntPtr set, ref SP_DEVINFO_DATA data, int cx, int cy, int flags, out IntPtr icon);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiLoadClassIcon(ref Guid classGuid, out IntPtr largeIcon, out int miniIconIndex);

        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_DevNode_Status(out int status, out int problem, int devInst, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out int devInst, string id, int flags);

        [DllImport("cfgmgr32.dll")]
        static extern int CM_Reenumerate_DevNode(int devInst, int flags);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);

        static readonly Dictionary<Guid, string> classNames = new Dictionary<Guid, string>();

        /// <summary>The present devices; icons (keyed by instance ID) go to <paramref name="icons"/> when given.</summary>
        public static List<Device> Load(Dictionary<string, Bitmap> icons)
        {
            var list = new List<Device>();
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == InvalidHandle) return list;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    var d = new Device();
                    d.InstanceId = InstanceId(set, ref data);
                    d.Name = RegString(set, ref data, SPDRP_FRIENDLYNAME);
                    if (d.Name.Length == 0) d.Name = RegString(set, ref data, SPDRP_DEVICEDESC);
                    if (d.Name.Length == 0) d.Name = d.InstanceId;
                    d.ClassKey = RegString(set, ref data, SPDRP_CLASS);
                    d.ClassGuid = data.ClassGuid.ToString("B");
                    d.ClassName = ClassDescription(data.ClassGuid);
                    if (d.ClassName.Length == 0) d.ClassName = d.ClassKey.Length > 0 ? d.ClassKey : L.T("Другие устройства", "Other devices");
                    d.Manufacturer = RegString(set, ref data, SPDRP_MFG);
                    d.HardwareIds = RegMulti(set, ref data, SPDRP_HARDWAREID);
                    d.CompatibleIds = RegMulti(set, ref data, SPDRP_COMPATIBLEIDS);
                    d.VersionText = PropString(set, ref data, ref DriverVersion);
                    d.Provider = PropString(set, ref data, ref DriverProvider);
                    d.InfName = PropString(set, ref data, ref DriverInfPath);
                    d.Date = PropDate(set, ref data, ref DriverDate);
                    d.Version = ParseVersion(d.VersionText);

                    int status, problem;
                    if (CM_Get_DevNode_Status(out status, out problem, data.DevInst, 0) == 0 && (status & DN_HAS_PROBLEM) != 0)
                        d.Problem = problem;

                    if (icons != null && !icons.ContainsKey(d.InstanceId))
                    {
                        var bmp = DeviceIcon(set, ref data);
                        if (bmp != null) icons[d.InstanceId] = bmp;
                    }
                    list.Add(d);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        /// <summary>INF names (oemNN.inf) used by any device Windows knows, connected or not: such packages are in use.</summary>
        public static HashSet<string> InfsInUse()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
            // Never answer "nothing is used" when the list could not be read: callers remove unused packages.
            if (set == InvalidHandle) throw new System.ComponentModel.Win32Exception();
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    string inf = PropString(set, ref data, ref DriverInfPath);
                    if (inf.Length > 0) used.Add(inf);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return used;
        }

        /// <summary>"Scan for hardware changes" of Device Manager.</summary>
        public static bool Rescan()
        {
            int root;
            if (CM_Locate_DevNodeW(out root, null, 0) != 0) return false;
            return CM_Reenumerate_DevNode(root, 0) == 0;
        }

        /// <summary>Opens the device's Properties window of Device Manager.</summary>
        public static void ShowProperties(string instanceId)
        {
            // Instance IDs never contain quotes; one that does is not passed on to a command line.
            if (string.IsNullOrEmpty(instanceId) || instanceId.IndexOf('"') >= 0) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    System.IO.Path.Combine(Environment.SystemDirectory, "rundll32.exe"),
                    "devmgr.dll,DeviceProperties_RunDLL /DeviceID \"" + instanceId + "\"") { UseShellExecute = false });
            }
            catch { }
        }

        public static string ClassDescription(Guid guid)
        {
            lock (classNames)
            {
                string name;
                if (classNames.TryGetValue(guid, out name)) return name;
                var sb = new StringBuilder(256);
                int req;
                name = SetupDiGetClassDescriptionW(ref guid, sb, sb.Capacity, out req) ? sb.ToString() : "";
                classNames[guid] = name;
                return name;
            }
        }

        public static string ClassDescription(string guid)
        {
            Guid g;
            return Guid.TryParse(guid, out g) ? ClassDescription(g) : "";
        }

        /// <summary>The class icon of Device Manager (for rows that are packages rather than devices).</summary>
        public static Bitmap ClassIcon(string guid)
        {
            Guid g;
            if (!Guid.TryParse(guid, out g)) return null;
            IntPtr h;
            int mini;
            if (!SetupDiLoadClassIcon(ref g, out h, out mini) || h == IntPtr.Zero) return null;
            try
            {
                using (var ic = Icon.FromHandle(h)) return ic.ToBitmap();
            }
            catch { return null; }
            finally { DestroyIcon(h); }
        }

        static Bitmap DeviceIcon(IntPtr set, ref SP_DEVINFO_DATA data)
        {
            IntPtr h;
            if (!SetupDiLoadDeviceIcon(set, ref data, 32, 32, 0, out h) || h == IntPtr.Zero) return null;
            try
            {
                using (var ic = Icon.FromHandle(h)) return ic.ToBitmap();
            }
            catch { return null; }
            finally { DestroyIcon(h); }
        }

        static string InstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
        {
            var sb = new StringBuilder(512);
            int req;
            return SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out req) ? sb.ToString() : "";
        }

        static byte[] RegBytes(IntPtr set, ref SP_DEVINFO_DATA data, int prop)
        {
            int type, req;
            var buf = new byte[1024];
            if (SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out type, buf, buf.Length, out req)) return Trim(buf, req);
            if (req <= buf.Length || req > 1 << 20) return null;
            buf = new byte[req];
            return SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out type, buf, buf.Length, out req) ? Trim(buf, req) : null;
        }

        static byte[] Trim(byte[] buf, int len)
        {
            if (len >= buf.Length) return buf;
            var r = new byte[Math.Max(0, len)];
            Array.Copy(buf, r, r.Length);
            return r;
        }

        static string RegString(IntPtr set, ref SP_DEVINFO_DATA data, int prop)
        {
            var b = RegBytes(set, ref data, prop);
            return b == null ? "" : Encoding.Unicode.GetString(b).TrimEnd('\0').Trim();
        }

        static string[] RegMulti(IntPtr set, ref SP_DEVINFO_DATA data, int prop)
        {
            var b = RegBytes(set, ref data, prop);
            if (b == null) return new string[0];
            return Encoding.Unicode.GetString(b).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static byte[] PropBytes(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key)
        {
            int type, req;
            var buf = new byte[512];
            if (SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buf, buf.Length, out req, 0)) return Trim(buf, req);
            if (req <= buf.Length || req > 1 << 20) return null;
            buf = new byte[req];
            return SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buf, buf.Length, out req, 0) ? Trim(buf, req) : null;
        }

        static string PropString(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key)
        {
            var b = PropBytes(set, ref data, ref key);
            return b == null ? "" : Encoding.Unicode.GetString(b).TrimEnd('\0').Trim();
        }

        static DateTime? PropDate(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key)
        {
            var b = PropBytes(set, ref data, ref key);
            if (b == null || b.Length < 8) return null;
            long ft = BitConverter.ToInt64(b, 0);
            if (ft <= 0) return null;
            try { return DateTime.FromFileTimeUtc(ft).Date; }
            catch { return null; }
        }

        public static Version ParseVersion(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Trim().Split('.');
            var n = new int[4];
            int count = 0;
            for (int i = 0; i < parts.Length && i < 4; i++)
            {
                int v;
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < 0) break;
                n[i] = v;
                count++;
            }
            if (count < 2) return null;
            return new Version(n[0], n[1], n[2], n[3]);
        }

        /// <summary>What Device Manager says about a problem code, in plain words.</summary>
        public static string ProblemText(int code)
        {
            switch (code)
            {
                case 1: return L.T("Не настроено", "Not configured");
                case 3: return L.T("Драйвер повреждён или не хватает памяти", "Driver corrupted or out of memory");
                case 10: return L.T("Не удаётся запустить", "Cannot start");
                case 12: return L.T("Не хватает ресурсов", "Not enough resources");
                case 14: return L.T("Нужна перезагрузка", "Restart needed");
                case 18: return L.T("Переустановите драйвер", "Reinstall the driver");
                case 19: return L.T("Повреждены данные в реестре", "Registry data corrupted");
                case 21: return L.T("Удаляется", "Being removed");
                case 22: return L.T("Отключено", "Disabled");
                case 24: return L.T("Нет устройства или драйвера", "Not present or no driver");
                case 28: return L.T("Драйвер не установлен", "No driver installed");
                case 29: return L.T("Отключено в прошивке", "Disabled by firmware");
                case 31: return L.T("Драйвер не загружается", "Driver does not load");
                case 32: return L.T("Служба драйвера отключена", "Driver service disabled");
                case 37: return L.T("Ошибка инициализации драйвера", "Driver failed to initialize");
                case 39: return L.T("Драйвер повреждён или отсутствует", "Driver missing or corrupted");
                case 43: return L.T("Устройство сообщило о сбое", "Device reported a problem");
                case 45: return L.T("Не подключено", "Not connected");
                case 47: return L.T("Подготовлено к извлечению", "Prepared for removal");
                case 48: return L.T("Драйвер заблокирован", "Driver blocked");
                case 52: return L.T("Подпись драйвера не проверена", "Driver signature not verified");
                default: return L.T("Ошибка (код ", "Problem (code ") + code + ")";
            }
        }
    }
}
