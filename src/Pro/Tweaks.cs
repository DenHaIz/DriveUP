using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DriveUp
{
    /// <summary>What the computer is, as far as choosing settings goes.</summary>
    class Machine
    {
        public bool Laptop, OnBattery, Server;
        /// <summary>Modern Standby (S0 low power idle): Windows supports only the Balanced plan there.</summary>
        public bool ModernStandby;
        /// <summary>
        /// Run by an organisation: domain, Entra ID (Azure AD) join or MDM (Intune) enrolment. Their
        /// management and reporting rely on the push routing service, telemetry and its tasks.
        /// </summary>
        public bool Managed;
        /// <summary>Windows Hello Enhanced Sign-in Security: face / fingerprint sign-in relies on virtualization-based security.</summary>
        public bool SecureBiometrics;
        public int Build;
        public readonly List<string> Gpus = new List<string>();
        /// <summary>A discrete graphics card (NVIDIA, AMD Radeon RX / Pro, Intel Arc A/B), or empty.</summary>
        public string GamingGpu = "";
        /// <summary>Hardware-accelerated GPU scheduling: the driver offers it / it is on now.</summary>
        public bool HagsSupported, HagsEnabled;
        public string Cpu = "";
        public int Cores, Threads;
        public double RamGb;
        /// <summary>The drive Windows is on: "NVMe", "SSD", "HDD" or "" (unknown, e.g. a virtual disk).</summary>
        public string SystemDisk = "";
        /// <summary>Any spinning disk in the computer (its idle spin-down matters).</summary>
        public bool AnyHdd;

        public bool Windows11 { get { return Build >= 22000; } }
        /// <summary>
        /// Laptops and Modern Standby machines keep the Balanced plan and get the power mode of the
        /// Settings app instead of the classic plans, and their battery side is never changed.
        /// </summary>
        public bool Mobile { get { return Laptop || ModernStandby; } }
        public bool SystemSsd { get { return SystemDisk == "NVMe" || SystemDisk == "SSD"; } }

        /// <summary>"Laptop · Intel Core i7-12700H, 14 cores · 16 GB · NVMe SSD · RTX 3060 · Windows 11".</summary>
        public string Summary()
        {
            var parts = new List<string>();
            parts.Add(Laptop ? L.T("Ноутбук", "Laptop") + (OnBattery ? L.T(" от батареи", " on battery") : "") : L.T("Настольный ПК", "Desktop PC"));
            if (Cpu.Length > 0) parts.Add(ShortCpu(Cpu) + (Cores > 0 ? ", " + L.Count(Cores, "ядро", "ядра", "ядер", "core", "cores") : ""));
            if (RamGb > 0) parts.Add(Math.Round(RamGb).ToString(CultureInfo.InvariantCulture) + L.T(" ГБ", " GB"));
            if (SystemDisk.Length > 0) parts.Add(SystemDisk == "NVMe" ? "NVMe SSD" : SystemDisk);
            string gpu = GamingGpu.Length > 0 ? GamingGpu : (Gpus.Count > 0 ? Gpus[0] : "");
            if (gpu.Length > 0) parts.Add(Regex.Replace(gpu, @"\((R|TM)\)|NVIDIA\s+|AMD\s+|Intel\s+", "", RegexOptions.IgnoreCase).Trim());
            parts.Add((Server ? "Windows Server" : "Windows " + (Windows11 ? "11" : "10")));
            if (Managed) parts.Add(L.T("управляется организацией", "managed by an organisation"));
            return string.Join(" · ", parts);
        }

        static string ShortCpu(string name)
        {
            string s = Regex.Replace(name, @"\((R|TM)\)|\bCPU\b|\bProcessor\b|\d+(th|st|nd|rd)\s+Gen\s+|@.*$|\s+with\s+Radeon.*$|\d+-Core", "", RegexOptions.IgnoreCase);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        public static Machine Read()
        {
            var m = new Machine { Build = WindowsBuild() };
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    m.Server = k != null && Convert.ToString(k.GetValue("InstallationType")).IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { }
            int chassis = 0;
            Try(() => Query("SELECT ChassisTypes FROM Win32_SystemEnclosure", o =>
            {
                var types = o["ChassisTypes"] as ushort[];
                if (types != null && types.Length > 0 && chassis == 0) chassis = types[0];
            }));
            var ps = SystemInformation.PowerStatus;
            bool battery = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryChargeStatus != BatteryChargeStatus.Unknown;
            // The case says it best; a battery alone may be a UPS on a desktop.
            int[] portable = { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };
            int[] desktop = { 3, 4, 5, 6, 7, 13, 15, 16, 17, 23, 24, 35, 36 };
            m.Laptop = portable.Contains(chassis) || (battery && !desktop.Contains(chassis));
            m.OnBattery = m.Laptop && ps.PowerLineStatus == PowerLineStatus.Offline;
            Try(() => Query("SELECT Name, PNPDeviceID FROM Win32_VideoController", o =>
            {
                string name = Convert.ToString(o["Name"]).Trim(), id = Convert.ToString(o["PNPDeviceID"]).ToUpperInvariant();
                if (name.Length == 0) return;
                m.Gpus.Add(name);
                if (m.GamingGpu.Length == 0 && IsGaming(name, id)) m.GamingGpu = name;
            }));
            Try(() => Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor", o =>
            {
                if (m.Cpu.Length == 0) m.Cpu = Convert.ToString(o["Name"]).Trim();
                m.Cores += Convert.ToInt32(o["NumberOfCores"]);
                m.Threads += Convert.ToInt32(o["NumberOfLogicalProcessors"]);
            }));
            Try(() => Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", o =>
                m.RamGb = Convert.ToUInt64(o["TotalPhysicalMemory"]) / (1024.0 * 1024 * 1024)));
            Try(() => ReadDisks(m));
            Gpu.ReadScheduling(out m.HagsSupported, out m.HagsEnabled);
            m.ModernStandby = PowerApi.ModernStandby();
            Try(() => Query("SELECT PartOfDomain FROM Win32_ComputerSystem", o => { if (Convert.ToBoolean(o["PartOfDomain"])) m.Managed = true; }));
            Try(() => { if (Enrolled()) m.Managed = true; });
            Try(() =>
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\SecureBiometrics"))
                    m.SecureBiometrics = k != null && Convert.ToInt32(k.GetValue("Enabled", 0)) == 1;
            });
            return m;
        }

        /// <summary>Joined to Entra ID, or enrolled in MDM with a work account.</summary>
        static bool Enrolled()
        {
            using (var j = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo"))
                if (j != null && j.SubKeyCount > 0) return true;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments"))
            {
                if (k == null) return false;
                foreach (string n in k.GetSubKeyNames())
                    using (var e = k.OpenSubKey(n))
                    {
                        if (e == null) continue;
                        string provider = Convert.ToString(e.GetValue("ProviderID")), upn = Convert.ToString(e.GetValue("UPN"));
                        if (provider.Length > 0 && upn.Length > 0) return true;
                    }
            }
            return false;
        }

        /// <summary>System drive type from the Storage WMI provider (Windows 8+): media type, NVMe by bus.</summary>
        static void ReadDisks(Machine m)
        {
            char sys = char.ToUpperInvariant(Environment.GetFolderPath(Environment.SpecialFolder.Windows)[0]);
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            int sysDisk = -1;
            using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition")))
                foreach (ManagementBaseObject o in q.Get())
                    using (o)
                    {
                        object dl = o["DriveLetter"];
                        if (dl != null && char.ToUpperInvariant(Convert.ToChar(dl)) == sys) sysDisk = Convert.ToInt32(o["DiskNumber"]);
                    }
            using (var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk")))
                foreach (ManagementBaseObject o in q.Get())
                    using (o)
                    {
                        int media = Convert.ToInt32(o["MediaType"]), bus = Convert.ToInt32(o["BusType"]);
                        int id;
                        int.TryParse(Convert.ToString(o["DeviceId"]), out id);
                        if (media == 3) m.AnyHdd = true;
                        if (id != sysDisk) continue;
                        if (bus == 17) m.SystemDisk = "NVMe";
                        else if (media == 4) m.SystemDisk = "SSD";
                        else if (media == 3) m.SystemDisk = "HDD";
                    }
        }

        /// <summary>The real build number (Environment.OSVersion depends on the exe's manifest).</summary>
        static int WindowsBuild()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int b;
                    if (k != null && int.TryParse(Convert.ToString(k.GetValue("CurrentBuildNumber")), out b)) return b;
                }
            }
            catch { }
            return Environment.OSVersion.Version.Build;
        }

        public static bool IsGaming(string name, string id)
        {
            if (id.Contains("VEN_10DE")) return true;
            if (id.Contains("VEN_1002"))
                return Regex.IsMatch(name, @"\bRX\s?\d|Radeon\s+Pro|Vega\s+(56|64)|Radeon\s+VII", RegexOptions.IgnoreCase);
            // "Intel(R) Arc(TM) Graphics" is the built-in one of Core Ultra; the cards are A380, A770, B580...
            if (id.Contains("VEN_8086")) return Regex.IsMatch(name, @"Arc(\(TM\))?\s+[AB]\d{3}", RegexOptions.IgnoreCase);
            return false;
        }

        static void Try(Action a)
        {
            try { a(); } catch { }
        }

        static void Query(string wql, Action<ManagementBaseObject> each)
        {
            using (var q = new ManagementObjectSearcher(wql))
                foreach (ManagementBaseObject o in q.Get())
                    using (o) each(o);
        }
    }

    /// <summary>Registry access for the settings; tests redirect it into a scratch key.</summary>
    static class Reg
    {
        /// <summary>Tests: every key goes under HKCU\TestRoot\HKLM|HKCU\... instead of the real place.</summary>
        public static string TestRoot;

        static RegistryKey Key(bool machine, string path, bool write, bool create)
        {
            if (TestRoot != null)
            {
                string p = TestRoot + "\\" + (machine ? "HKLM" : "HKCU") + "\\" + path;
                return create ? Registry.CurrentUser.CreateSubKey(p) : Registry.CurrentUser.OpenSubKey(p, write);
            }
            using (var root = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64))
                return create ? root.CreateSubKey(path) : root.OpenSubKey(path, write);
        }

        public static object Get(bool machine, string path, string name)
        {
            using (var k = Key(machine, path, false, false))
                return k == null ? null : k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        public static bool KeyExists(bool machine, string path)
        {
            using (var k = Key(machine, path, false, false)) return k != null;
        }

        public static string[] SubKeyNames(bool machine, string path)
        {
            using (var k = Key(machine, path, false, false))
                return k == null ? new string[0] : k.GetSubKeyNames();
        }

        public static string[] ValueNames(bool machine, string path)
        {
            using (var k = Key(machine, path, false, false))
                return k == null ? new string[0] : k.GetValueNames();
        }

        public static int? Dword(bool machine, string path, string name)
        {
            object v = Get(machine, path, name);
            return v is int ? (int?)(int)v : null;
        }

        public static void SetDword(bool machine, string path, string name, int value)
        {
            using (var k = Key(machine, path, true, true)) k.SetValue(name, value, RegistryValueKind.DWord);
        }

        public static void SetString(bool machine, string path, string name, string value)
        {
            using (var k = Key(machine, path, true, true)) k.SetValue(name, value, RegistryValueKind.String);
        }

        public static void SetBytes(bool machine, string path, string name, byte[] value)
        {
            using (var k = Key(machine, path, true, true)) k.SetValue(name, value, RegistryValueKind.Binary);
        }

        public static void Delete(bool machine, string path, string name)
        {
            using (var k = Key(machine, path, true, false))
                if (k != null) k.DeleteValue(name, false);
        }

        /// <summary>The exact state of a value: "-" (no value), "d:5" (DWORD), "s:text" (string) or "b:0A1B" (binary).</summary>
        public static string Capture(bool machine, string path, string name)
        {
            object v = Get(machine, path, name);
            if (v == null) return "-";
            if (v is int) return "d:" + ((int)v).ToString(CultureInfo.InvariantCulture);
            if (v is string) return "s:" + v;
            var b = v as byte[];
            if (b != null) return "b:" + BitConverter.ToString(b).Replace("-", "");
            throw new InvalidOperationException(L.T("Неожиданный тип значения ", "Unexpected value type ") + name);
        }

        public static void Restore(bool machine, string path, string name, string raw)
        {
            int n;
            if (raw == "-") Delete(machine, path, name);
            else if (raw.StartsWith("d:") && int.TryParse(raw.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) SetDword(machine, path, name, n);
            else if (raw.StartsWith("s:")) SetString(machine, path, name, raw.Substring(2));
            else if (raw.StartsWith("b:")) SetBytes(machine, path, name, Hex(raw.Substring(2)));
        }

        public static byte[] Hex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(s.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b;
        }

        public static int? RawDword(string raw)
        {
            int n;
            return raw != null && raw.StartsWith("d:") && int.TryParse(raw.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? (int?)n : null;
        }
    }

    /// <summary>Groups in the list, in the order they are shown and applied.</summary>
    enum TuneCat { Power, Gaming, Interface, Services, Background, Tasks, Startup, Privacy, System, Updates }

    /// <summary>
    /// One system setting DriveUP Pro can change and put back exactly as it was. Values are short
    /// strings ("1"/"0", "perf", "100,*"); <see cref="Capture"/> returns the exact old state, which
    /// may hold more than the value (e.g. that a registry value did not exist at all).
    /// </summary>
    abstract class Tweak
    {
        public string Id, Glyph = "";
        public TuneCat Category;
        public Func<string> Name, Hint;
        /// <summary>Takes effect after a restart.</summary>
        public bool NeedsReboot;
        /// <summary>Process names (without .exe) this setting keeps from running in the background, to show what runs now.</summary>
        public string[] Processes = new string[0];

        public abstract string Capture();
        public abstract void Restore(string raw);
        /// <summary>The value a captured state stands for.</summary>
        public abstract string ValueOf(string raw);
        public abstract void Set(string value);
        public abstract string Describe(string value);
        public virtual bool Available(Machine m) { return true; }
        /// <summary>Whether the setting already is as the target asks (targets may leave parts as they are).</summary>
        public virtual bool Matches(string now, string target) { return now == target; }

        public string Current() { return ValueOf(Capture()); }
    }

    /// <summary>An on/off setting kept in one registry DWORD.</summary>
    class RegSwitch : Tweak
    {
        readonly bool machine;
        readonly string path, name;
        readonly int on, off;
        /// <summary>Whether the setting is on while the value does not exist.</summary>
        public Func<bool> OnWhenAbsent = () => false;
        public Func<string> OnText = () => L.T("включено", "on"), OffText = () => L.T("выключено", "off");
        public Func<Machine, bool> When = m => true;
        /// <summary>Tells Windows about the change (e.g. a settings broadcast) after it is written.</summary>
        public Action Changed;

        public RegSwitch(bool machine, string path, string name, int on, int off)
        {
            this.machine = machine;
            this.path = path;
            this.name = name;
            this.on = on;
            this.off = off;
        }

        public override string Capture() { return Reg.Capture(machine, path, name); }

        public override void Restore(string raw)
        {
            Reg.Restore(machine, path, name, raw);
            if (Changed != null) Changed();
        }

        public override string ValueOf(string raw)
        {
            int? v = Reg.RawDword(raw);
            if (v == null) return OnWhenAbsent() ? "1" : "0";
            if (v == on) return "1";
            if (v == off) return "0";
            return OnWhenAbsent() ? "1" : "0";
        }

        public override void Set(string value)
        {
            Reg.SetDword(machine, path, name, value == "1" ? on : off);
            if (Changed != null) Changed();
        }

        public override string Describe(string value) { return value == "1" ? OnText() : OffText(); }
        public override bool Available(Machine m) { return When(m); }
    }

    /// <summary>Several registry DWORDs that go together ("Windows suggestions and ads": six switches).</summary>
    class RegSet : Tweak
    {
        readonly List<Tuple<bool, string, string, int>> values = new List<Tuple<bool, string, string, int>>();
        public Func<string> OnText = () => L.T("как в Windows", "Windows default"), OffText = () => L.T("выключено", "off");
        public Func<Machine, bool> When = m => true;

        /// <summary>Adds a value and what it becomes ("set").</summary>
        public RegSet Add(bool machine, string path, string name, int value)
        {
            values.Add(Tuple.Create(machine, path, name, value));
            return this;
        }

        public override string Capture() { return string.Join("|", values.Select(v => Reg.Capture(v.Item1, v.Item2, v.Item3))); }

        public override void Restore(string raw)
        {
            var parts = raw.Split('|');
            if (parts.Length != values.Count) return;
            for (int i = 0; i < values.Count; i++) Reg.Restore(values[i].Item1, values[i].Item2, values[i].Item3, parts[i]);
        }

        /// <summary>"1" = all set as this setting wants, "0" = not (yet).</summary>
        public override string ValueOf(string raw)
        {
            var parts = raw.Split('|');
            if (parts.Length != values.Count) return "0";
            for (int i = 0; i < values.Count; i++)
                if (Reg.RawDword(parts[i]) != values[i].Item4) return "0";
            return "1";
        }

        public override void Set(string value)
        {
            foreach (var v in values)
                if (value == "1") Reg.SetDword(v.Item1, v.Item2, v.Item3, v.Item4);
                else Reg.Delete(v.Item1, v.Item2, v.Item3);
        }

        public override string Describe(string value) { return value == "1" ? OffText() : OnText(); }
        public override bool Available(Machine m) { return When(m); }
    }

    /// <summary>
    /// One key=value pair inside the DirectX user settings string (Windows graphics settings:
    /// "Optimizations for windowed games", "Variable refresh rate"); the other pairs are kept.
    /// </summary>
    class WindowedGames : Tweak
    {
        const string Path = @"Software\Microsoft\DirectX\UserGpuPreferences", Value = "DirectXUserGlobalSettings";
        readonly string Pair;
        public Func<Machine, bool> When = m => m.Windows11;

        public WindowedGames(string pair = "SwapEffectUpgradeEnable")
        {
            Pair = pair;
        }

        public override string Capture() { return Reg.Capture(false, Path, Value); }
        public override void Restore(string raw) { Reg.Restore(false, Path, Value, raw); }

        static Dictionary<string, string> Parse(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in (s ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) d[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
            return d;
        }

        public override string ValueOf(string raw)
        {
            string v;
            return raw.StartsWith("s:") && Parse(raw.Substring(2)).TryGetValue(Pair, out v) && v == "1" ? "1" : "0";
        }

        public override void Set(string value)
        {
            var d = Parse(Reg.Get(false, Path, Value) as string);
            d[Pair] = value == "1" ? "1" : "0";
            Reg.SetString(false, Path, Value, string.Join("", d.Select(kv => kv.Key + "=" + kv.Value + ";")));
        }

        public override string Describe(string value) { return value == "1" ? L.T("включены", "on") : L.T("выключены", "off"); }
        public override bool Available(Machine m) { return When(m); }
    }

    /// <summary>How long a submenu waits before it opens (ms, default 400).</summary>
    class MenuDelay : Tweak
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int param, ref int value, int winIni);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int param, IntPtr value, int winIni);

        public override string Capture()
        {
            if (Reg.TestRoot != null) return Convert.ToString(Reg.Get(false, "Menu", "Delay") ?? "400");
            int v = 400;
            SystemParametersInfo(0x006A /* SPI_GETMENUSHOWDELAY */, 0, ref v, 0);
            return v.ToString(CultureInfo.InvariantCulture);
        }

        public override void Restore(string raw) { Set(raw); }
        public override string ValueOf(string raw) { return raw; }

        public override void Set(string value)
        {
            int v;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return;
            if (Reg.TestRoot != null) { Reg.SetString(false, "Menu", "Delay", value); return; }
            if (!SystemParametersInfo(0x006B /* SPI_SETMENUSHOWDELAY */, v, IntPtr.Zero, 1 | 2)) throw new System.ComponentModel.Win32Exception();
        }

        public override string Describe(string value) { return value + L.T(" мс", " ms"); }
    }

    /// <summary>Hibernation (and the hiberfil.sys it keeps on the system drive), switched by powercfg as Windows does it.</summary>
    class Hibernation : Tweak
    {
        const string Path = @"SYSTEM\CurrentControlSet\Control\Power";

        public override string Capture() { return Reg.Capture(true, Path, "HibernateEnabled"); }

        public override void Restore(string raw) { Set(Reg.RawDword(raw) == 0 ? "0" : "1"); }

        public override string ValueOf(string raw) { return Reg.RawDword(raw) == 0 ? "0" : "1"; }

        public override void Set(string value)
        {
            if (Reg.TestRoot != null) { Reg.SetDword(true, Path, "HibernateEnabled", value == "1" ? 1 : 0); return; }
            string exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "/hibernate " + (value == "1" ? "on" : "off")) { UseShellExecute = false, CreateNoWindow = true };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } throw new TimeoutException("powercfg"); }
                if (p.ExitCode != 0) throw new InvalidOperationException("powercfg /hibernate: " + p.ExitCode);
            }
        }

        public override string Describe(string value) { return value == "1" ? L.T("включена", "on") : L.T("выключена", "off"); }

        /// <summary>Size of hiberfil.sys now (bytes, 0 if none).</summary>
        public static long FileSize()
        {
            try
            {
                var f = new System.IO.FileInfo(System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), "hiberfil.sys"));
                return f.Exists ? f.Length : 0;
            }
            catch { return 0; }
        }
    }

    /// <summary>Windows Update active hours: no restarts for updates between these hours.</summary>
    class ActiveHours : Tweak
    {
        const string Path = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
        static readonly string[] Names = { "ActiveHoursStart", "ActiveHoursEnd", "SmartActiveHoursState" };

        public override string Capture() { return string.Join("|", Names.Select(n => Reg.Capture(true, Path, n))); }

        public override void Restore(string raw)
        {
            var parts = raw.Split('|');
            if (parts.Length != Names.Length) return;
            for (int i = 0; i < Names.Length; i++) Reg.Restore(true, Path, Names[i], parts[i]);
        }

        public override string ValueOf(string raw)
        {
            var parts = raw.Split('|');
            if (parts.Length != Names.Length) return "auto";
            int? start = Reg.RawDword(parts[0]), end = Reg.RawDword(parts[1]), smart = Reg.RawDword(parts[2]);
            if (smart == 1) return "auto";
            return (start ?? 8) + "-" + (end ?? 17);
        }

        public override void Set(string value)
        {
            var se = value.Split('-');
            Reg.SetDword(true, Path, Names[0], int.Parse(se[0], CultureInfo.InvariantCulture));
            Reg.SetDword(true, Path, Names[1], int.Parse(se[1], CultureInfo.InvariantCulture));
            Reg.SetDword(true, Path, Names[2], 0);
        }

        public override string Describe(string value)
        {
            if (value == "auto") return L.T("подбираются сами", "set automatically");
            var se = value.Split('-');
            return L.T("с ", "") + se[0] + ":00" + L.T(" до ", " – ") + se[1] + ":00";
        }
    }

    /// <summary>"Enhance pointer precision" (mouse acceleration).</summary>
    class MouseAcceleration : Tweak
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SystemParametersInfo(int action, int param, int[] value, int winIni);

        int[] Read()
        {
            if (Reg.TestRoot != null)
            {
                string s = Reg.Get(false, "Mouse", "Speed") as string;
                return (s ?? "6,10,1").Split(',').Select(int.Parse).ToArray();
            }
            var v = new int[3];
            if (!SystemParametersInfo(3 /* SPI_GETMOUSE */, 0, v, 0)) throw new System.ComponentModel.Win32Exception();
            return v;
        }

        void Write(int[] v)
        {
            if (Reg.TestRoot != null) { Reg.SetString(false, "Mouse", "Speed", string.Join(",", v)); return; }
            // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE: saved for the user and applied right away.
            if (!SystemParametersInfo(4 /* SPI_SETMOUSE */, 0, v, 1 | 2)) throw new System.ComponentModel.Win32Exception();
        }

        public override string Capture() { return string.Join(",", Read()); }

        public override void Restore(string raw)
        {
            var v = raw.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            if (v.Length == 3) Write(v);
        }

        public override string ValueOf(string raw)
        {
            var v = raw.Split(',');
            return v.Length == 3 && v[2] != "0" ? "1" : "0";
        }

        public override void Set(string value) { Write(value == "1" ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 }); }
        public override string Describe(string value) { return value == "1" ? L.T("включена", "on") : L.T("выключена", "off"); }
    }

    // ---------------------------------------------------------------------------------------------
    // Power: the plan / power mode, and the settings inside the active plan.

    static class PowerApi
    {
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, ref uint size);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        // The power mode slider of Windows 10/11 ("Best performance" ...): not in the SDK, exported since 1709.
        [DllImport("powrprof.dll")] static extern uint PowerGetActualOverlayScheme(out Guid overlay);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid overlay);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);
        [DllImport("powrprof.dll")] static extern bool GetPwrCapabilities(byte[] caps);

        /// <summary>SYSTEM_POWER_CAPABILITIES.AoAc (byte 20): the machine uses Modern Standby.</summary>
        public static bool ModernStandby()
        {
            if (Fake) return Reg.Dword(false, "Power", "AoAc") == 1;
            try
            {
                var caps = new byte[256];
                return GetPwrCapabilities(caps) && caps[20] != 0;
            }
            catch { return false; }
        }

        public static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        public static readonly Guid HighPerformance = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635b");
        public static readonly Guid Ultimate = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");
        public static readonly Guid PowerSaver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
        public static readonly Guid ModeBestPerformance = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
        public static readonly Guid ModeBestEfficiency = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
        public static readonly Guid ModeBalanced = Guid.Empty;

        public static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        public static readonly Guid BoostMode = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        public static readonly Guid MinState = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
        public static readonly Guid MinCores = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
        public static readonly Guid EnergyPreference = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        public static readonly Guid SubPciExpress = new Guid("501a4d13-42af-4429-9fd1-a8139f2c9c2b");
        public static readonly Guid LinkPower = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");
        public static readonly Guid SubUsb = new Guid("2a737441-1930-4402-8d77-b2bebba308a3");
        public static readonly Guid UsbSuspend = new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        public static readonly Guid SubDisk = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
        public static readonly Guid DiskIdle = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");

        static void Check(uint err)
        {
            if (err != 0) throw new System.ComponentModel.Win32Exception((int)err);
        }

        // Tests (Reg.TestRoot set): plans and values live in the scratch registry, the real ones are never touched.
        static bool Fake { get { return Reg.TestRoot != null; } }

        public static Guid Active
        {
            get
            {
                if (Fake) return new Guid(Reg.Get(false, "Power", "Active") as string ?? Balanced.ToString());
                IntPtr p;
                Check(PowerGetActiveScheme(IntPtr.Zero, out p));
                try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
                finally { LocalFree(p); }
            }
        }

        public static void Activate(Guid scheme)
        {
            if (Fake) { Reg.SetString(false, "Power", "Active", scheme.ToString()); return; }
            Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
        }

        public static bool Exists(Guid scheme)
        {
            if (Fake) return scheme != Ultimate;
            uint size = 0;
            return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) == 0;
        }

        public static string FriendlyName(Guid scheme)
        {
            uint size = 0;
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0) return "";
            var buf = new byte[size];
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return "";
            return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
        }

        static bool? overlay;

        /// <summary>The power mode overlay, or null where Windows has none.</summary>
        public static Guid? Overlay
        {
            get
            {
                if (Fake) return new Guid(Reg.Get(false, "Power", "Overlay") as string ?? ModeBalanced.ToString());
                if (overlay == false) return null;
                try
                {
                    Guid g;
                    uint err = PowerGetActualOverlayScheme(out g);
                    overlay = true;
                    return err == 0 ? (Guid?)g : null;
                }
                catch (EntryPointNotFoundException) { overlay = false; return null; }
            }
        }

        public static bool HasOverlay { get { return Overlay.HasValue; } }

        public static void SetOverlay(Guid mode)
        {
            if (Fake) { Reg.SetString(false, "Power", "Overlay", mode.ToString()); return; }
            Check(PowerSetActiveOverlayScheme(mode));
        }

        public static uint Read(Guid scheme, Guid sub, Guid setting, bool ac)
        {
            if (Fake) return (uint)(Reg.Dword(false, @"Power\" + scheme, setting + (ac ? "ac" : "dc")) ?? 1);
            uint v;
            Check(ac ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v)
                     : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v));
            return v;
        }

        public static void Write(Guid scheme, Guid sub, Guid setting, uint ac, uint dc)
        {
            if (Fake)
            {
                Reg.SetDword(false, @"Power\" + scheme, setting + "ac", (int)ac);
                Reg.SetDword(false, @"Power\" + scheme, setting + "dc", (int)dc);
                return;
            }
            Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, ac));
            Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, dc));
        }
    }

    /// <summary>
    /// Power plan / power mode: "perf", "balanced" or "saver". Where Windows has the classic plans
    /// they are used (High performance, Power saver); laptops that only have Balanced get the power
    /// mode of the Settings app instead (Best performance / Best power efficiency).
    /// </summary>
    class PowerPlan : Tweak
    {
        public override string Capture()
        {
            Guid? o = PowerApi.Overlay;
            return PowerApi.Active.ToString() + "|" + (o.HasValue ? o.Value.ToString() : "");
        }

        public override void Restore(string raw)
        {
            var parts = raw.Split('|');
            Guid scheme, mode;
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out scheme)) return;
            PowerApi.Activate(scheme);
            if (parts[1].Length > 0 && Guid.TryParse(parts[1], out mode) && PowerApi.HasOverlay) PowerApi.SetOverlay(mode);
        }

        public override string ValueOf(string raw)
        {
            var parts = raw.Split('|');
            Guid scheme, mode;
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out scheme)) return "balanced";
            if (scheme == PowerApi.HighPerformance || scheme == PowerApi.Ultimate) return "perf";
            if (scheme == PowerApi.PowerSaver) return "saver";
            if (scheme != PowerApi.Balanced) return "plan:" + scheme;
            if (parts[1].Length > 0 && Guid.TryParse(parts[1], out mode))
            {
                if (mode == PowerApi.ModeBestPerformance) return "perf";
                if (mode == PowerApi.ModeBestEfficiency) return "saver";
            }
            return "balanced";
        }

        public override void Set(string value)
        {
            Guid plan = value == "perf" ? PowerApi.HighPerformance : (value == "saver" ? PowerApi.PowerSaver : PowerApi.Balanced);
            // Laptops and Modern Standby: never a classic plan (hidden or unsupported there, and the
            // maker's power software expects Balanced), only the power mode of the Settings app.
            bool standby = Tuner.Pc != null ? Tuner.Pc.ModernStandby : PowerApi.ModernStandby();
            bool mobile = Tuner.Pc == null || Tuner.Pc.Mobile;
            if (plan != PowerApi.Balanced && !standby && (!mobile || !PowerApi.HasOverlay) && PowerApi.Exists(plan))
            {
                PowerApi.Activate(plan);
                return;
            }
            PowerApi.Activate(PowerApi.Balanced);
            if (PowerApi.HasOverlay)
                PowerApi.SetOverlay(value == "perf" ? PowerApi.ModeBestPerformance : (value == "saver" ? PowerApi.ModeBestEfficiency : PowerApi.ModeBalanced));
        }

        public override string Describe(string value)
        {
            switch (value)
            {
                case "perf": return L.T("максимальная производительность", "best performance");
                case "saver": return L.T("энергосбережение", "power saving");
                case "balanced": return L.T("сбалансированный", "balanced");
            }
            Guid g;
            string name = value.StartsWith("plan:") && Guid.TryParse(value.Substring(5), out g) ? PowerApi.FriendlyName(g) : "";
            return name.Length > 0 ? L.T("схема «", "plan \"") + name + L.T("»", "\"") : L.T("своя схема", "custom plan");
        }
    }

    /// <summary>
    /// One setting inside the active power plan, for mains (AC) and battery (DC): value "ac,dc";
    /// a target may leave a side as it is with "*" (laptops: tune on mains, keep the battery side).
    /// </summary>
    class PowerSetting : Tweak
    {
        readonly Guid sub, setting;
        /// <summary>Text for one raw value of the setting.</summary>
        public Func<uint, string> Text = v => v.ToString(CultureInfo.InvariantCulture);

        public PowerSetting(Guid sub, Guid setting)
        {
            this.sub = sub;
            this.setting = setting;
        }

        public override string Capture()
        {
            Guid s = PowerApi.Active;
            return s + "|" + PowerApi.Read(s, sub, setting, true) + "|" + PowerApi.Read(s, sub, setting, false);
        }

        public override void Restore(string raw)
        {
            var parts = raw.Split('|');
            Guid scheme;
            uint ac, dc;
            if (parts.Length != 3 || !Guid.TryParse(parts[0], out scheme) || !uint.TryParse(parts[1], out ac) || !uint.TryParse(parts[2], out dc)) return;
            PowerApi.Write(scheme, sub, setting, ac, dc);
            // A changed plan takes effect once it is set active again.
            if (PowerApi.Active == scheme) PowerApi.Activate(scheme);
        }

        public override string ValueOf(string raw)
        {
            var parts = raw.Split('|');
            return parts.Length == 3 ? parts[1] + "," + parts[2] : "";
        }

        public override void Set(string value)
        {
            Guid s = PowerApi.Active;
            var t = value.Split(',');
            uint ac = t[0] == "*" ? PowerApi.Read(s, sub, setting, true) : uint.Parse(t[0], CultureInfo.InvariantCulture);
            uint dc = t.Length < 2 || t[1] == "*" ? PowerApi.Read(s, sub, setting, false) : uint.Parse(t[1], CultureInfo.InvariantCulture);
            PowerApi.Write(s, sub, setting, ac, dc);
            PowerApi.Activate(s);
        }

        public override bool Matches(string now, string target)
        {
            var n = now.Split(',');
            var t = target.Split(',');
            if (n.Length != 2 || t.Length != 2) return false;
            return (t[0] == "*" || t[0] == n[0]) && (t[1] == "*" || t[1] == n[1]);
        }

        public override string Describe(string value)
        {
            var v = value.Split(',');
            if (v.Length != 2) return "?";
            uint ac, dc;
            bool hasAc = uint.TryParse(v[0], out ac), hasDc = uint.TryParse(v[1], out dc);
            bool laptop = Tuner.Pc != null && Tuner.Pc.Laptop;
            if (hasAc && (!laptop || (hasDc && ac == dc))) return Text(ac);
            if (hasAc && !hasDc) return Text(ac) + L.T(" от сети", " on mains");
            if (!hasAc && hasDc) return Text(dc) + L.T(" от батареи", " on battery");
            return L.T("от сети ", "mains ") + Text(ac) + L.T(", от батареи ", ", battery ") + Text(dc);
        }

        public override bool Available(Machine m)
        {
            try
            {
                PowerApi.Read(PowerApi.Active, sub, setting, true);
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>Hardware-accelerated GPU scheduling as the graphics driver reports it.</summary>
    static class Gpu
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        struct ADAPTERINFO { public uint hAdapter; public LUID Luid; public uint NumOfSources; public int PrecisePresentRegionsPreferred; }

        [StructLayout(LayoutKind.Sequential)]
        struct ENUMADAPTERS2 { public uint NumAdapters; public IntPtr Adapters; }

        [StructLayout(LayoutKind.Sequential)]
        struct QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr Data; public uint DataSize; }

        [StructLayout(LayoutKind.Sequential)]
        struct CLOSEADAPTER { public uint hAdapter; }

        [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 e);
        [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO q);
        [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER c);

        const int KMTQAITYPE_WDDM_2_7_CAPS = 70;

        public static void ReadScheduling(out bool supported, out bool enabled)
        {
            supported = enabled = false;
            try
            {
                var e = new ENUMADAPTERS2();
                if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return;
                int size = Marshal.SizeOf(typeof(ADAPTERINFO));
                e.Adapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
                IntPtr caps = Marshal.AllocHGlobal(4);
                try
                {
                    if (D3DKMTEnumAdapters2(ref e) != 0) return;
                    for (int i = 0; i < e.NumAdapters; i++)
                    {
                        var a = (ADAPTERINFO)Marshal.PtrToStructure(e.Adapters + i * size, typeof(ADAPTERINFO));
                        Marshal.WriteInt32(caps, 0);
                        var q = new QUERYADAPTERINFO { hAdapter = a.hAdapter, Type = KMTQAITYPE_WDDM_2_7_CAPS, Data = caps, DataSize = 4 };
                        if (D3DKMTQueryAdapterInfo(ref q) == 0)
                        {
                            int bits = Marshal.ReadInt32(caps);
                            if ((bits & 1) != 0) supported = true;
                            if ((bits & 2) != 0) enabled = true;
                        }
                        var c = new CLOSEADAPTER { hAdapter = a.hAdapter };
                        D3DKMTCloseAdapter(ref c);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(e.Adapters);
                    Marshal.FreeHGlobal(caps);
                }
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }
    }
}
