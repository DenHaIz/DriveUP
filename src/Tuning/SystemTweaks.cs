using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DriveUp
{
    /// <summary>
    /// How a service starts: "auto", "delayed", "manual", "disabled". Changed through the Service
    /// Control Manager (as services.msc does); a service turned off is also stopped right away.
    /// The old state keeps whether it was running, so putting it back starts it again.
    /// </summary>
    class ServiceTweak : Tweak
    {
        public readonly string Service;
        const string Root = @"SYSTEM\CurrentControlSet\Services\";

        public ServiceTweak(string service)
        {
            Service = service;
            Id = "svc." + service.ToLowerInvariant();
            Category = TuneCat.Services;
            Glyph = "\uE912";
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SERVICE_STATUS { public uint Type, State, Controls, Win32Exit, SpecificExit, CheckPoint, WaitHint; }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenSCManager(string machine, string db, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfig(IntPtr svc, uint type, uint start, uint error, string path, string group, IntPtr tag, string deps, string user, string pwd, string display);
        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
        static extern bool ChangeServiceConfig2(IntPtr svc, uint level, ref int info);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool QueryServiceStatus(IntPtr svc, ref SERVICE_STATUS status);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool ControlService(IntPtr svc, uint control, ref SERVICE_STATUS status);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool StartService(IntPtr svc, int count, string[] args);
        [DllImport("advapi32.dll")]
        static extern bool CloseServiceHandle(IntPtr h);

        const uint NoChange = 0xFFFFFFFF;

        static void Win32(bool ok)
        {
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        void WithService(uint access, Action<IntPtr> action)
        {
            IntPtr scm = OpenSCManager(null, null, 1 /* CONNECT */);
            Win32(scm != IntPtr.Zero);
            try
            {
                IntPtr h = OpenService(scm, Service, access);
                Win32(h != IntPtr.Zero);
                try { action(h); }
                finally { CloseServiceHandle(h); }
            }
            finally { CloseServiceHandle(scm); }
        }

        bool Running()
        {
            if (Reg.TestRoot != null) return false;
            bool running = false;
            try
            {
                WithService(4 /* QUERY_STATUS */, h =>
                {
                    var st = new SERVICE_STATUS();
                    if (QueryServiceStatus(h, ref st)) running = st.State != 1 /* STOPPED */;
                });
            }
            catch { }
            return running;
        }

        public override bool Available(Machine m) { return Reg.Dword(true, Root + Service, "Start") != null; }

        public override string Capture()
        {
            int start = Reg.Dword(true, Root + Service, "Start") ?? 3;
            int delayed = Reg.Dword(true, Root + Service, "DelayedAutostart") ?? 0;
            return start + "|" + delayed + "|" + (Running() ? 1 : 0);
        }

        void Configure(int start, bool delayed)
        {
            if (Reg.TestRoot != null)
            {
                Reg.SetDword(true, Root + Service, "Start", start);
                Reg.SetDword(true, Root + Service, "DelayedAutostart", delayed ? 1 : 0);
                return;
            }
            WithService(2 /* CHANGE_CONFIG */, h =>
            {
                Win32(ChangeServiceConfig(h, NoChange, (uint)start, NoChange, null, null, IntPtr.Zero, null, null, null, null));
                if (start == 2)
                {
                    int flag = delayed ? 1 : 0;
                    Win32(ChangeServiceConfig2(h, 3 /* SERVICE_CONFIG_DELAYED_AUTO_START_INFO */, ref flag));
                }
            });
        }

        void Stop()
        {
            if (Reg.TestRoot != null) return;
            // Best effort: if it will not stop now (a dependent is running), it stays off after a restart.
            try
            {
                WithService(0x20 /* STOP */, h =>
                {
                    var st = new SERVICE_STATUS();
                    ControlService(h, 1 /* SERVICE_CONTROL_STOP */, ref st);
                });
            }
            catch { }
        }

        void Start()
        {
            if (Reg.TestRoot != null) return;
            try { WithService(0x10 /* START */, h => StartService(h, 0, null)); }
            catch { }
        }

        public override void Restore(string raw)
        {
            var p = raw.Split('|');
            int start, delayed;
            if (p.Length != 3 || !int.TryParse(p[0], out start) || !int.TryParse(p[1], out delayed)) return;
            Configure(start, delayed == 1);
            if (p[2] == "1") Start();
        }

        public override string ValueOf(string raw)
        {
            var p = raw.Split('|');
            if (p.Length < 2) return "";
            switch (p[0])
            {
                case "2": return p[1] == "1" ? "delayed" : "auto";
                case "3": return "manual";
                case "4": return "disabled";
                default: return "system";
            }
        }

        public override void Set(string value)
        {
            switch (value)
            {
                case "auto": Configure(2, false); Start(); break;
                case "delayed": Configure(2, true); Start(); break;
                case "manual": Configure(3, false); Stop(); break;
                case "disabled": Configure(4, false); Stop(); break;
            }
        }

        public override string Describe(string value)
        {
            switch (value)
            {
                case "auto": return L.T("запускается сама", "starts by itself");
                case "delayed": return L.T("запускается сама, с задержкой", "starts by itself, delayed");
                case "manual": return L.T("по требованию", "on demand");
                case "disabled": return L.T("отключена", "disabled");
                default: return L.T("системная", "system");
            }
        }
    }

    /// <summary>Scheduled tasks that only collect and send data: all on or all off, each one put back as it was.</summary>
    class TaskSet : Tweak
    {
        readonly string[] paths;

        public TaskSet(params string[] paths)
        {
            this.paths = paths;
        }

        static dynamic Scheduler()
        {
            dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            s.Connect();
            return s;
        }

        static dynamic Find(dynamic s, string path)
        {
            int i = path.LastIndexOf('\\');
            try { return s.GetFolder(path.Substring(0, Math.Max(1, i))).GetTask(path.Substring(i + 1)); }
            catch { return null; }
        }

        /// <summary>"1" on, "0" off, "-" not on this computer.</summary>
        string[] Read()
        {
            if (Reg.TestRoot != null)
                return paths.Select(p => { int? v = Reg.Dword(false, "Tasks", p); return v == null ? "-" : v.ToString(); }).ToArray();
            dynamic s = Scheduler();
            return paths.Select(p =>
            {
                dynamic t = Find(s, p);
                if (t == null) return "-";
                return (bool)t.Enabled ? "1" : "0";
            }).ToArray();
        }

        void Write(string[] states)
        {
            if (Reg.TestRoot != null)
            {
                for (int i = 0; i < paths.Length; i++) if (states[i] != "-") Reg.SetDword(false, "Tasks", paths[i], states[i] == "1" ? 1 : 0);
                return;
            }
            dynamic s = Scheduler();
            Exception first = null;
            for (int i = 0; i < paths.Length; i++)
            {
                if (states[i] == "-") continue;
                dynamic t = Find(s, paths[i]);
                if (t == null) continue;
                try { t.Enabled = states[i] == "1"; }
                catch (Exception ex) { if (first == null) first = ex; }
            }
            if (first != null) throw first;
        }

        public override bool Available(Machine m)
        {
            try { return Read().Any(x => x != "-"); }
            catch { return false; }
        }

        public override string Capture() { return string.Join("|", Read()); }

        public override void Restore(string raw)
        {
            var p = raw.Split('|');
            if (p.Length == paths.Length) Write(p);
        }

        public override string ValueOf(string raw)
        {
            return raw.Split('|').Any(x => x == "1") ? "1" : "0";
        }

        public override void Set(string value)
        {
            Write(Read().Select(x => x == "-" ? "-" : value).ToArray());
        }

        public override string Describe(string value) { return value == "1" ? L.T("работают", "running") : L.T("отключены", "off"); }
    }

    /// <summary>
    /// A program that starts with Windows. Turned off the way Task Manager does it (the
    /// StartupApproved flag), so the entry itself stays and can be turned back on.
    /// </summary>
    class StartupTweak : Tweak
    {
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        public readonly bool Machine;
        public readonly string Sub, Entry;

        public StartupTweak(bool machine, string sub, string entry)
        {
            Machine = machine;
            Sub = sub;
            Entry = entry;
            Id = MakeId(machine, sub, entry);
            Category = TuneCat.Startup;
            Glyph = "\uE7B5";
            string shown = entry.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? entry.Substring(0, entry.Length - 4) : entry;
            Name = () => shown;
        }

        public static string MakeId(bool machine, string sub, string entry)
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(entry)).Replace('=', '_').Replace('+', '-').Replace('/', '~');
            return "run." + (machine ? "m" : "u") + "." + sub + "." + b64;
        }

        public static StartupTweak FromId(string id)
        {
            var p = id.Split('.');
            if (p.Length != 4 || p[0] != "run") return null;
            try
            {
                string entry = Encoding.UTF8.GetString(Convert.FromBase64String(p[3].Replace('_', '=').Replace('-', '+').Replace('~', '/')));
                return new StartupTweak(p[1] == "m", p[2], entry);
            }
            catch { return null; }
        }

        public override string Capture() { return Reg.Capture(Machine, Approved + Sub, Entry); }
        public override void Restore(string raw) { Reg.Restore(Machine, Approved + Sub, Entry, raw); }

        public override string ValueOf(string raw)
        {
            if (!raw.StartsWith("b:") || raw.Length < 4) return "1";
            return (Convert.ToByte(raw.Substring(2, 2), 16) & 1) == 0 ? "1" : "0";
        }

        public override void Set(string value)
        {
            var data = new byte[12];
            data[0] = (byte)(value == "1" ? 2 : 3);
            if (value != "1") BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
            Reg.SetBytes(Machine, Approved + Sub, Entry, data);
        }

        public override string Describe(string value) { return value == "1" ? L.T("запускается с Windows", "starts with Windows") : L.T("не запускается", "does not start"); }
    }

    /// <summary>Which startup programs are clutter, which are heavy but useful, which sync files.</summary>
    class StartupRule
    {
        /// <summary>1 = clutter (helpers, pre-launchers, nags), 2 = heavy, can be started by hand, 3 = syncs files (only by choice).</summary>
        public int Level;
        public Regex Match;
        public Func<string> Reason, Warn;
        public string[] Processes = new string[0];
    }

    class StartupFound
    {
        public Tweak Tweak;
        public StartupRule Rule;
    }

    static class StartupScan
    {
        const string Cv = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Cv32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

        static StartupRule R(int level, string pattern, Func<string> reason, Func<string> warn = null, params string[] processes)
        {
            return new StartupRule { Level = level, Match = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), Reason = reason, Warn = warn, Processes = processes };
        }

        /// <summary>Only programs on this list are ever offered: drivers, security and Windows itself never are.</summary>
        public static readonly List<StartupRule> Rules = new List<StartupRule>
        {
            R(1, @"MicrosoftEdgeAutoLaunch", () => L.T("Edge запускается заранее и ждёт в фоне", "Edge starts in advance and waits in the background")),
            R(1, @"Browser\s?Assistant|browser_assistant\.exe", () => L.T("Помощник Opera: фоновый процесс и реклама", "Opera assistant: a background process and ads"), null, "browser_assistant"),
            R(1, @"AdobeGCInvoker|AGCInvokerUtility", () => L.T("Проверка лицензии Adobe при каждом входе", "Adobe licence check at every sign-in"), null, "AGCInvokerUtility", "AdobeGCClient"),
            R(1, @"iTunesHelper", () => L.T("Нужен только для автосинхронизации iPhone", "Only needed to auto-sync an iPhone"), null, "iTunesHelper"),
            R(1, @"CCleaner.*(monitor|smart|tray)", () => L.T("Мониторинг CCleaner висит в фоне", "CCleaner monitoring sits in the background"), null, "CCleaner64", "CCleaner"),
            R(1, @"YandexBrowserAutoLaunch|browser_manager|BrowserManager", () => L.T("Фоновый менеджер браузера", "Background browser manager"), null, "browser_manager"),
            R(1, @"Mail\.?Ru|Amigo", () => L.T("Фоновые сервисы Mail.ru", "Mail.ru background services"), null, "MailRuUpdater"),
            R(1, @"\bSkype\b", () => L.T("Skype закрыт Microsoft в 2025 году", "Microsoft shut Skype down in 2025"), null, "Skype"),
            R(1, @"Wondershare.*Helper|WSHelper", () => L.T("Фоновый помощник Wondershare", "Wondershare background helper"), null, "WSHelper", "Wondershare Helper Compact"),
            R(1, @"jusched|Java Update", () => L.T("Проверяет обновления Java и держит процесс", "Checks for Java updates and keeps a process"), null, "jusched"),
            R(1, @"AvastBrowser|AVG Secure Browser|CCleaner Browser", () => L.T("Браузер-«нагрузка» от антивируса", "Bundled browser from an antivirus")),
            R(2, @"\bsteam(\.exe)?\b", () => L.T("Steam можно открыть перед игрой", "Open Steam before you play"), null, "steam", "steamwebhelper"),
            R(2, @"discord", () => L.T("Тяжёлый мессенджер — запускайте, когда нужен", "A heavy messenger — start it when you need it"), null, "Discord"),
            R(2, @"spotify", () => L.T("Запускается и висит в фоне", "Starts and sits in the background"), null, "Spotify"),
            R(2, @"EpicGamesLauncher|EADesktop|EADM|\bOrigin\b|Battle\.net|UbisoftConnect|upc\.exe|GalaxyClient|RiotClient", () => L.T("Лаунчер игр можно открыть перед игрой", "Open the game launcher before you play"), null,
                "EpicGamesLauncher", "EpicWebHelper", "EADesktop", "Origin", "Battle.net", "upc", "GalaxyClient", "RiotClientServices"),
            R(2, @"telegram|viber|whatsapp|\bzoom\b|slack|com\.squirrel\.Teams|MSTeams|\bTeams\b", () => L.T("Мессенджер можно запускать вручную", "Start the messenger by hand"), null,
                "Telegram", "Viber", "WhatsApp", "Zoom", "slack", "ms-teams", "Teams"),
            R(2, @"utorrent|bittorrent|qbittorrent", () => L.T("Торрент-клиент раздаёт в фоне и занимает сеть", "The torrent client seeds in the background and takes bandwidth"), null, "uTorrent", "BitTorrent", "qbittorrent"),
            R(2, @"Creative Cloud|CCXProcess|AdobeAAMUpdater", () => L.T("Фоновые процессы Adobe Creative Cloud", "Adobe Creative Cloud background processes"), null,
                "Creative Cloud", "CCXProcess", "AdobeIPCBroker", "CoreSync", "Adobe Desktop Service"),
            R(3, @"OneDrive|GoogleDriveFS|Dropbox|Yandex\.?Disk", () => L.T("Облачный диск: файлы перестанут синхронизироваться, пока его не запустить", "Cloud drive: files stop syncing until you start it"),
                () => L.T("файлы не синхронизируются", "files stop syncing"), "OneDrive", "GoogleDriveFS", "Dropbox", "YandexDisk2"),
        };

        /// <summary>How many programs start with Windows now (any, not only the listed ones): for the measurement.</summary>
        public static int CountEnabled()
        {
            int n = 0;
            const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
            Func<bool, string, string, bool> on = (machine, sub, name) =>
            {
                var b = Reg.Get(machine, Approved + sub, name) as byte[];
                return b == null || b.Length == 0 || (b[0] & 1) == 0;
            };
            foreach (var key in new[] { Tuple.Create(false, Cv, "Run"), Tuple.Create(true, Cv, "Run"), Tuple.Create(true, Cv32, "Run32") })
                foreach (string name in Reg.ValueNames(key.Item1, key.Item2))
                    if (name.Length > 0 && on(key.Item1, key.Item3, name)) n++;
            foreach (var folder in new[] { Tuple.Create(false, Environment.SpecialFolder.Startup), Tuple.Create(true, Environment.SpecialFolder.CommonStartup) })
            {
                try
                {
                    string dir = Environment.GetFolderPath(folder.Item2);
                    if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        string name = Path.GetFileName(f);
                        if (!name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) && on(folder.Item1, "StartupFolder", name)) n++;
                    }
                }
                catch { }
            }
            return n;
        }

        /// <summary>Startup programs on the list, as they are now.</summary>
        public static List<StartupFound> Find()
        {
            var found = new List<StartupFound>();
            Action<bool, string, string, string> add = (machine, sub, entry, command) =>
            {
                string text = entry + " " + command;
                var rule = Rules.FirstOrDefault(r => r.Match.IsMatch(text));
                if (rule == null) return;
                var t = new StartupTweak(machine, sub, entry) { Processes = rule.Processes };
                found.Add(new StartupFound { Tweak = t, Rule = rule });
            };
            // Store apps start through their own startup tasks (new Teams, Spotify, WhatsApp, Telegram from the Store).
            foreach (string family in Reg.SubKeyNames(false, PackagedStartupTweak.Root))
                foreach (string task in Reg.SubKeyNames(false, PackagedStartupTweak.Root + "\\" + family))
                {
                    if (Reg.Get(false, PackagedStartupTweak.Root + "\\" + family + "\\" + task, "State") == null) continue;
                    var rule = Rules.FirstOrDefault(r => r.Match.IsMatch(family + " " + task));
                    if (rule != null) found.Add(new StartupFound { Tweak = new PackagedStartupTweak(family, task) { Processes = rule.Processes }, Rule = rule });
                }
            foreach (var key in new[] { Tuple.Create(false, Cv, "Run"), Tuple.Create(true, Cv, "Run"), Tuple.Create(true, Cv32, "Run32") })
                foreach (string name in Reg.ValueNames(key.Item1, key.Item2))
                    if (name.Length > 0) add(key.Item1, key.Item3, name, Convert.ToString(Reg.Get(key.Item1, key.Item2, name)));
            if (Reg.TestRoot == null)
                foreach (var folder in new[] { Tuple.Create(false, Environment.SpecialFolder.Startup), Tuple.Create(true, Environment.SpecialFolder.CommonStartup) })
                {
                    try
                    {
                        string dir = Environment.GetFolderPath(folder.Item2);
                        if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                        foreach (var f in Directory.GetFiles(dir))
                        {
                            string name = Path.GetFileName(f);
                            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                            add(folder.Item1, "StartupFolder", name, "");
                        }
                    }
                    catch { }
                }
            return found;
        }
    }

    /// <summary>A Store app's startup task (as Task Manager turns it off: State 1 = off by the user, 2 = on).</summary>
    class PackagedStartupTweak : Tweak
    {
        public const string Root = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
        public readonly string Family, Task;

        public PackagedStartupTweak(string family, string task)
        {
            Family = family;
            Task = task;
            Id = "pkg." + Convert.ToBase64String(Encoding.UTF8.GetBytes(family + "|" + task)).Replace('=', '_').Replace('+', '-').Replace('/', '~');
            Category = TuneCat.Startup;
            Glyph = "\uE7B5";
            // "SpotifyAB.SpotifyMusic_zpdnekdrzrea0" → "SpotifyMusic", "MSTeams_8wekyb3d8bbwe" → "MSTeams".
            string name = family.Split('_')[0];
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name.Substring(dot + 1);
            Name = () => name + L.T(" (из Microsoft Store)", " (Microsoft Store)");
        }

        public static PackagedStartupTweak FromId(string id)
        {
            if (!id.StartsWith("pkg.")) return null;
            try
            {
                var p = Encoding.UTF8.GetString(Convert.FromBase64String(id.Substring(4).Replace('_', '=').Replace('-', '+').Replace('~', '/'))).Split('|');
                return p.Length == 2 ? new PackagedStartupTweak(p[0], p[1]) : null;
            }
            catch { return null; }
        }

        string Path { get { return Root + "\\" + Family + "\\" + Task; } }

        public override string Capture() { return Reg.Capture(false, Path, "State"); }
        public override void Restore(string raw) { Reg.Restore(false, Path, "State", raw); }

        public override string ValueOf(string raw)
        {
            int? v = Reg.RawDword(raw);
            return v == 2 || v == 4 ? "1" : "0";
        }

        public override void Set(string value) { Reg.SetDword(false, Path, "State", value == "1" ? 2 : 1); }
        public override string Describe(string value) { return value == "1" ? L.T("запускается с Windows", "starts with Windows") : L.T("не запускается", "does not start"); }
    }

    /// <summary>
    /// Whether a Store app may run in the background (Settings → Apps → background permission).
    /// Off, it runs only while it is open.
    /// </summary>
    class BackgroundApp : Tweak
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\";
        const string Families = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Families\";
        public readonly string Family;

        public BackgroundApp(string family)
        {
            Family = family;
            Id = "bg." + family;
            Category = TuneCat.Background;
            Glyph = "\uE71D";
        }

        public override bool Available(Machine m) { return Reg.KeyExists(false, Families + Family); }

        public override string Capture() { return Reg.Capture(false, Key + Family, "Disabled") + "|" + Reg.Capture(false, Key + Family, "DisabledByUser"); }

        public override void Restore(string raw)
        {
            var p = raw.Split('|');
            if (p.Length != 2) return;
            Reg.Restore(false, Key + Family, "Disabled", p[0]);
            Reg.Restore(false, Key + Family, "DisabledByUser", p[1]);
        }

        public override string ValueOf(string raw) { return raw.StartsWith("d:1|") ? "0" : "1"; }

        public override void Set(string value)
        {
            int v = value == "1" ? 0 : 1;
            Reg.SetDword(false, Key + Family, "Disabled", v);
            Reg.SetDword(false, Key + Family, "DisabledByUser", v);
        }

        public override string Describe(string value) { return value == "1" ? L.T("работает в фоне", "runs in the background") : L.T("только когда открыто", "only while open"); }
    }

    /// <summary>What runs now: process count and memory by process name, read once per plan.</summary>
    static class Running
    {
        public static Dictionary<string, Tuple<int, long>> Snapshot()
        {
            var map = new Dictionary<string, Tuple<int, long>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in System.Diagnostics.Process.GetProcesses())
                using (p)
                {
                    long mem = 0;
                    try { mem = p.WorkingSet64; } catch { }
                    Tuple<int, long> was;
                    map.TryGetValue(p.ProcessName, out was);
                    map[p.ProcessName] = Tuple.Create((was != null ? was.Item1 : 0) + 1, (was != null ? was.Item2 : 0) + mem);
                }
            return map;
        }
    }

    /// <summary>Window, menu and taskbar animations.</summary>
    class Animations : Tweak
    {
        [StructLayout(LayoutKind.Sequential)]
        struct ANIMATIONINFO { public int Size; public int MinAnimate; }

        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int param, ref int value, int winIni);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int param, IntPtr value, int winIni);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(int action, int param, ref ANIMATIONINFO value, int winIni);

        const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        public override string Capture()
        {
            string bar = Reg.Capture(false, Adv, "TaskbarAnimations");
            if (Reg.TestRoot != null) return Convert.ToString(Reg.Get(false, "Anim", "State") ?? "1,1") + "," + bar;
            int client = 1;
            SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, ref client, 0);
            var ai = new ANIMATIONINFO { Size = 8 };
            SystemParametersInfo(0x0048 /* SPI_GETANIMATION */, 8, ref ai, 0);
            return (client != 0 ? 1 : 0) + "," + ai.MinAnimate + "," + bar;
        }

        void Write(bool client, bool minimize)
        {
            if (Reg.TestRoot != null) { Reg.SetString(false, "Anim", "State", (client ? 1 : 0) + "," + (minimize ? 1 : 0)); return; }
            SystemParametersInfo(0x1043 /* SPI_SETCLIENTAREAANIMATION */, 0, (IntPtr)(client ? 1 : 0), 1 | 2);
            var ai = new ANIMATIONINFO { Size = 8, MinAnimate = minimize ? 1 : 0 };
            SystemParametersInfo(0x0049 /* SPI_SETANIMATION */, 8, ref ai, 1 | 2);
        }

        public override void Restore(string raw)
        {
            var p = raw.Split(new[] { ',' }, 3);
            if (p.Length != 3) return;
            Write(p[0] == "1", p[1] != "0");
            Reg.Restore(false, Adv, "TaskbarAnimations", p[2]);
        }

        public override string ValueOf(string raw) { return raw.StartsWith("0,") ? "0" : "1"; }

        public override void Set(string value)
        {
            bool on = value == "1";
            Write(on, on);
            Reg.SetDword(false, Adv, "TaskbarAnimations", on ? 1 : 0);
        }

        public override string Describe(string value) { return value == "1" ? L.T("включены", "on") : L.T("выключены", "off"); }
    }

    static class SettingsBroadcast
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);

        /// <summary>Tells running programs (Explorer) that a setting changed, e.g. "ImmersiveColorSet".</summary>
        public static void Send(string area)
        {
            if (Reg.TestRoot != null) return;
            IntPtr r;
            SendMessageTimeout((IntPtr)0xFFFF, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, area, 2 /* SMTO_ABORTIFHUNG */, 1000, out r);
        }
    }
}
