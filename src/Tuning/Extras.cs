using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DriveUp
{
    /// <summary>
    /// "Pause updates" of Windows Settings for a number of days (the same registry values Settings
    /// writes). The old state — including an earlier pause — comes back exactly.
    /// </summary>
    class PauseUpdates : Tweak
    {
        const string Path = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
        static readonly string[] Names = { "PauseUpdatesExpiryTime", "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime" };
        public int Days = 7;

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
            DateTime until;
            return parts.Length > 0 && parts[0].StartsWith("s:") &&
                   DateTime.TryParse(parts[0].Substring(2), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out until) &&
                   until > DateTime.UtcNow ? "1" : "0";
        }

        public override void Set(string value)
        {
            if (value != "1") { foreach (var n in Names) Reg.Delete(true, Path, n); return; }
            string start = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string end = DateTime.UtcNow.AddDays(Days).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            Reg.SetString(true, Path, Names[0], end);
            Reg.SetString(true, Path, Names[1], start);
            Reg.SetString(true, Path, Names[2], end);
            Reg.SetString(true, Path, Names[3], start);
            Reg.SetString(true, Path, Names[4], end);
        }

        public override string Describe(string value)
        {
            return value == "1" ? L.T("на паузе", "paused") : L.T("приходят как обычно", "arrive as usual");
        }
    }

    /// <summary>
    /// A preinstalled Store app (games, trials, promotions). Removed for this user with
    /// Remove-AppxPackage; "Return" registers it again from the copy Windows keeps for new users,
    /// and where there is none, opens its Microsoft Store page.
    /// </summary>
    class StoreApp : Tweak
    {
        const string Families = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Families";
        /// <summary>Package family name prefix ("king.com.CandyCrushSaga_").</summary>
        public readonly string Prefix;

        public StoreApp(string prefix, string title)
        {
            Prefix = prefix;
            Id = "app." + prefix.TrimEnd('_');
            Category = TuneCat.Apps;
            Glyph = "\uE74D";
            Name = () => title;
        }

        string Family()
        {
            return Reg.SubKeyNames(false, Families).FirstOrDefault(f => f.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
        }

        public override bool Available(Machine m) { return Family() != null; }

        /// <summary>"1|family" while installed, "0|family" after it was removed.</summary>
        public override string Capture()
        {
            string f = Family();
            return (f != null ? "1|" : "0|") + (f ?? Prefix);
        }

        public override void Restore(string raw)
        {
            var p = raw.Split('|');
            if (p.Length != 2 || p[0] != "1" || Family() != null) return;
            if (Reg.TestRoot != null) { Reg.SetDword(false, Families + "\\" + p[1], "x", 1); return; }
            if (!Regex.IsMatch(p[1], @"^[A-Za-z0-9.\-]+_[a-z0-9]+$")) return;
            // A preinstalled app stays on the disk for new users: registered again from there, as it was
            // (for the person at the desktop, by a PowerShell running as them, when DriveUP runs as another account).
            string command = "Add-AppxPackage -RegisterByFamilyName -MainPackage '" + p[1] + "' -ErrorAction Stop";
            try
            {
                if (DesktopUser.Other) UserShell.RunAsUser(PowerShellExe, Args(command), 180000);
                else PowerShell(command);
            }
            catch { }
            // Not on this computer any more: its Store page opens for the user (with the user's rights).
            if (Family() == null) UserShell.OpenUrl("ms-windows-store://pdp/?PFN=" + p[1]);
        }

        public override string ValueOf(string raw) { return raw.StartsWith("1|") ? "1" : "0"; }

        public override void Set(string value)
        {
            if (value == "1") return;
            string f = Family();
            if (f == null) return;
            if (!Regex.IsMatch(f, @"^[A-Za-z0-9.\-]+_[a-z0-9]+$")) throw new InvalidOperationException("package name");
            if (Reg.TestRoot != null)
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Reg.TestRoot + @"\HKCU\" + Families, true)) if (k != null) k.DeleteSubKeyTree(f, false);
                return;
            }
            // Elevated as another account: the person at the desktop's copy, by their SID.
            string who = DesktopUser.OtherSid;
            if (who != null && !Regex.IsMatch(who, @"^S-1-[0-9\-]+$")) throw new InvalidOperationException("SID");
            string err = who == null
                ? PowerShell("Get-AppxPackage | Where-Object { $_.PackageFamilyName -eq '" + f + "' } | Remove-AppxPackage -ErrorAction Stop")
                : PowerShell("Get-AppxPackage -User '" + who + "' | Where-Object { $_.PackageFamilyName -eq '" + f + "' } | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -User '" + who + "' -ErrorAction Stop }");
            if (Family() != null) throw new InvalidOperationException(err.Length > 0 ? err.Split('\n')[0].Trim() : L.T("не удалось удалить", "could not remove"));
        }

        /// <summary>Runs one PowerShell command without a window; returns what it wrote to stderr.</summary>
        static string PowerShellExe { get { return System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"); } }
        static string Args(string command) { return "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command + "\""; }

        static string PowerShell(string command)
        {
            var psi = new ProcessStartInfo(PowerShellExe, Args(command))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(180000)) { try { p.Kill(); } catch { } throw new TimeoutException("PowerShell"); }
                return err;
            }
        }

        public override string Describe(string value) { return value == "1" ? L.T("установлено", "installed") : L.T("удалено", "removed"); }
    }

    /// <summary>A snapshot of how busy the system is, to compare before and after a mode.</summary>
    class Measure
    {
        public DateTime When;
        public int Processes, Services, Startup;
        public long MemoryMb;
        public double Cpu;
        /// <summary>The last boot's duration from the Windows boot performance log (s), 0 when unknown.</summary>
        public double BootSec;

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX { public uint Length, Load; public ulong Total, Avail, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended; }

        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        /// <summary>Takes about two seconds (processor load is averaged over them). Off the UI thread.</summary>
        public static Measure Take()
        {
            var m = new Measure { When = DateTime.Now };
            try { m.Processes = Process.GetProcesses().Length; } catch { }
            try
            {
                var ms = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                if (GlobalMemoryStatusEx(ref ms)) m.MemoryMb = (long)((ms.Total - ms.Avail) >> 20);
            }
            catch { }
            try
            {
                long i1, k1, u1, i2, k2, u2;
                GetSystemTimes(out i1, out k1, out u1);
                System.Threading.Thread.Sleep(2000);
                GetSystemTimes(out i2, out k2, out u2);
                long total = (k2 - k1) + (u2 - u1), idle = i2 - i1;
                m.Cpu = total > 0 ? Math.Round(100.0 * (total - idle) / total, 1) : 0;
            }
            catch { }
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name FROM Win32_Service WHERE State='Running'"))
                    m.Services = q.Get().Count;
            }
            catch { }
            try
            {
                m.Startup = StartupScan.CountEnabled();
            }
            catch { }
            try { m.BootSec = LastBoot(); } catch { }
            return m;
        }

        static double LastBoot()
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational",
                System.Diagnostics.Eventing.Reader.PathType.LogName, "*[System[EventID=100]]") { ReverseDirection = true };
            using (var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q))
            using (var e = r.ReadEvent())
            {
                if (e == null) return 0;
                var mm = Regex.Match(e.ToXml(), @"<Data Name='BootTime'>(\d+)</Data>");
                return mm.Success ? Math.Round(long.Parse(mm.Groups[1].Value) / 1000.0, 1) : 0;
            }
        }

        public override string ToString()
        {
            return string.Join("|", new[]
            {
                When.ToString("s", CultureInfo.InvariantCulture), Processes.ToString(CultureInfo.InvariantCulture), MemoryMb.ToString(CultureInfo.InvariantCulture),
                Cpu.ToString(CultureInfo.InvariantCulture), Services.ToString(CultureInfo.InvariantCulture), Startup.ToString(CultureInfo.InvariantCulture),
                BootSec.ToString(CultureInfo.InvariantCulture),
            });
        }

        public static Measure Parse(string s)
        {
            var p = (s ?? "").Split('|');
            if (p.Length != 7) return null;
            var m = new Measure();
            var ci = CultureInfo.InvariantCulture;
            DateTime.TryParse(p[0], ci, DateTimeStyles.None, out m.When);
            int.TryParse(p[1], NumberStyles.Integer, ci, out m.Processes);
            long.TryParse(p[2], NumberStyles.Integer, ci, out m.MemoryMb);
            double.TryParse(p[3], NumberStyles.Float, ci, out m.Cpu);
            int.TryParse(p[4], NumberStyles.Integer, ci, out m.Services);
            int.TryParse(p[5], NumberStyles.Integer, ci, out m.Startup);
            double.TryParse(p[6], NumberStyles.Float, ci, out m.BootSec);
            return m;
        }

        /// <summary>The comparison as lines of text: "Processes: 182 → 141 (−41)".</summary>
        public static string Compare(Measure before, Measure now)
        {
            var lines = new List<string>();
            Func<double, double, bool, string> diff = (a, b, lowerBetter) =>
            {
                double d = b - a;
                if (Math.Abs(d) < 0.05) return "";
                string sign = d < 0 ? "−" : "+";
                return "  (" + sign + Math.Abs(Math.Round(d, 1)).ToString(CultureInfo.CurrentCulture) + ((d < 0) == lowerBetter ? " ✓" : "") + ")";
            };
            Action<string, double, double, string> line = (name, a, b, unit) =>
                lines.Add(name + ": " + (before != null ? a.ToString("0.#", CultureInfo.CurrentCulture) + unit + " → " : "") + b.ToString("0.#", CultureInfo.CurrentCulture) + unit +
                          (before != null ? diff(a, b, true) : ""));
            var b0 = before ?? now;
            line(L.T("Процессов", "Processes"), b0.Processes, now.Processes, "");
            line(L.T("Занято памяти", "Memory in use"), b0.MemoryMb, now.MemoryMb, L.T(" МБ", " MB"));
            line(L.T("Загрузка процессора в простое", "Processor load at rest"), b0.Cpu, now.Cpu, " %");
            line(L.T("Работающих служб", "Running services"), b0.Services, now.Services, "");
            line(L.T("Программ в автозагрузке", "Startup programs"), b0.Startup, now.Startup, "");
            if (now.BootSec > 0 || b0.BootSec > 0)
                line(L.T("Последняя загрузка Windows", "Last Windows boot"), b0.BootSec, now.BootSec, L.T(" с", " s"));
            return string.Join("\n", lines);
        }
    }
}
