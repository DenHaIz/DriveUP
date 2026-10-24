using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DriveUp
{
    enum InfState { Ready, Installing, Installed, Staged, Failed }

    /// <summary>A driver .inf found in a folder, archive or file the user gave us.</summary>
    class InfDriver : IRowInfo, ICheckRow, ISortable
    {
        public string Path = "", Name = "", ClassName = "", ClassGuid = "", Provider = "", VersionText = "";
        /// <summary>SHA-256 of the .inf when it was read: it must be the same file that gets installed.</summary>
        public string Hash = "";
        public DateTime? Date;
        public Version Version;
        public readonly List<string> HardwareIds = new List<string>();
        public readonly List<Device> Matches = new List<Device>();
        /// <summary>Has a [Manufacturer] section, i.e. is a device driver at all.</summary>
        public bool IsDevice;
        /// <summary>Built only for another processor architecture (e.g. 32-bit only on 64-bit Windows).</summary>
        public bool WrongArch;
        public bool IsChecked;
        public InfState State;
        public string Error = "";

        public string FitText
        {
            get
            {
                if (WrongArch) return L.T("Не для этой системы", "Not for this system");
                if (!IsDevice) return L.T("Не драйвер устройства", "Not a device driver");
                if (Matches.Count == 0) return L.T("Нет подходящих устройств", "No matching devices");
                string first = Matches[0].Name;
                return Matches.Count == 1 ? first : first + L.T(" и ещё ", " and ") + (Matches.Count - 1);
            }
        }

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case InfState.Installing: return L.T("Установка…", "Installing…");
                    case InfState.Installed: return L.T("Установлен", "Installed");
                    case InfState.Staged: return L.T("Добавлен в хранилище", "Added to the store");
                    case InfState.Failed: return Error;
                    default: return Matches.Count > 0 ? L.T("Готов к установке", "Ready to install") : L.T("Добавится в хранилище", "Goes to the store");
                }
            }
        }

        public bool Checked { get { return IsChecked; } set { IsChecked = value; } }
        public bool Checkable { get { return !WrongArch && IsDevice && State != InfState.Installing; } }
        public RowKind RowKind
        {
            get { return State == InfState.Failed ? RowKind.Danger : (WrongArch || !IsDevice ? RowKind.Dim : RowKind.Normal); }
        }
        public string IconPath { get { return "class:" + ClassGuid; } }
        public int Dot
        {
            get
            {
                switch (State)
                {
                    case InfState.Installed: return 1;
                    case InfState.Staged: return 4;
                    case InfState.Failed: return 3;
                    case InfState.Installing: return 4;
                    default: return Matches.Count > 0 ? 4 : 2;
                }
            }
        }

        public IComparable SortKey(int column)
        {
            switch (column)
            {
                case 3: return Version ?? new Version(0, 0);
                case 4: return Date ?? DateTime.MinValue;
                // Drivers that suit a device first.
                case 5: return (Matches.Count > 0 ? "0" : (IsDevice && !WrongArch ? "1" : "2")) + FitText;
                default: return null;
            }
        }
    }

    /// <summary>Finds, reads and installs driver .inf files.</summary>
    static class InfFiles
    {
        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DiInstallDriverW(IntPtr hwndParent, string infPath, int flags, out bool needReboot);

        const int MaxInfs = 400;
        static readonly List<string> tempDirs = new List<string>();

        /// <summary>
        /// .inf files in a folder (recursively), an .inf itself, or inside a .zip / .cab (unpacked to
        /// %TEMP%). Installers (.exe / .msi) are reported in <paramref name="installers"/>.
        /// </summary>
        public static List<string> Collect(IEnumerable<string> paths, List<string> installers, List<string> errors)
        {
            var found = new List<string>();
            foreach (string raw in paths)
            {
                string path = raw.Trim().Trim('"');
                try
                {
                    if (Directory.Exists(path)) Walk(path, found, 0);
                    else if (File.Exists(path))
                    {
                        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                        if (ext == ".inf") found.Add(path);
                        else if (ext == ".zip") Walk(Unzip(path), found, 0);
                        else if (ext == ".cab") Walk(Uncab(path), found, 0);
                        else if (ext == ".exe" || ext == ".msi") installers.Add(path);
                        else errors.Add(System.IO.Path.GetFileName(path) + ": " + L.T("не драйвер (нужна папка, .inf, .zip или .cab)", "not a driver (a folder, .inf, .zip or .cab is needed)"));
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(System.IO.Path.GetFileName(path) + ": " + ex.Message);
                }
                if (found.Count >= MaxInfs) break;
            }
            return found.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxInfs).ToList();
        }

        static void Walk(string dir, List<string> found, int depth)
        {
            if (depth > 12 || found.Count >= MaxInfs) return;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.inf")) found.Add(f);
                foreach (var d in Directory.GetDirectories(dir))
                {
                    // Junctions and symbolic links are skipped; OneDrive folders are not links and are walked.
                    if (SafePath.IsLink(d)) continue;
                    Walk(d, found, depth + 1);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        /// <summary>
        /// Archives are unpacked into a folder only administrators can change (not the user's TEMP):
        /// nothing running without elevation can swap the files before they are installed.
        /// </summary>
        static string NewTemp()
        {
            string dir = SafePath.NewStaging();
            lock (tempDirs) tempDirs.Add(dir);
            return dir;
        }

        /// <summary>A protected folder for a downloaded driver package (removed on exit like unpacked archives).</summary>
        public static string NewDownloadDir() { return NewTemp(); }

        static bool InStaging(string path)
        {
            lock (tempDirs)
                foreach (var d in tempDirs)
                    if (path.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// The .inf to hand to Windows. A driver from an unpacked archive is already in a protected folder;
        /// one from a folder the user dropped is copied into a protected folder first (once per folder per
        /// install run), so it cannot be swapped while it installs. Either way the .inf must still be the
        /// one that was read when it was added: its catalog signature then covers the other files.
        /// </summary>
        public static string Prepare(InfDriver d, Dictionary<string, string> copies, out string error)
        {
            error = null;
            try
            {
                string path = d.Path;
                if (!InStaging(path))
                {
                    string dir = System.IO.Path.GetDirectoryName(path);
                    string copy;
                    if (!copies.TryGetValue(dir, out copy))
                    {
                        // Up to 4 GB / 20 000 files (a whole Downloads folder is not copied: checked in place).
                        copy = SafePath.StageCopy(dir, 4L << 30, 20000);
                        if (copy != null) lock (tempDirs) tempDirs.Add(copy);
                        copies[dir] = copy;
                    }
                    if (copy != null) path = System.IO.Path.Combine(copy, System.IO.Path.GetFileName(path));
                }
                if (d.Hash.Length > 0 && SafePath.Sha256(path) != d.Hash)
                {
                    error = L.T("Файл драйвера изменился после добавления — добавьте его снова", "The driver file changed after it was added — add it again");
                    return null;
                }
                return path;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        static string Unzip(string zip)
        {
            string dir = NewTemp();
            // .NET 4.6.1+ refuses entries that would land outside the folder ("zip slip").
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir);
            return dir;
        }

        static string Uncab(string cab)
        {
            string dir = NewTemp();
            var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "expand.exe"),
                "\"" + cab + "\" -F:* \"" + dir + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                if (!p.WaitForExit(180000))
                {
                    try { p.Kill(); } catch { }
                    throw new IOException(L.T("архив .cab распаковывается слишком долго", "the .cab archive takes too long to unpack"));
                }
                // expand.exe also refuses entries with "..\" in their names (non-zero exit code).
                if (p.ExitCode != 0) throw new IOException(L.T("не удалось распаковать архив .cab", "could not unpack the .cab archive"));
            }
            return dir;
        }

        /// <summary>Removes what archives were unpacked into (on exit).</summary>
        public static void CleanTemp()
        {
            lock (tempDirs)
            {
                foreach (var d in tempDirs)
                    try { Directory.Delete(d, true); } catch { }
                tempDirs.Clear();
            }
        }

        // ---------- reading an .inf ----------

        /// <summary>Reads [Version], [Strings] and the models, and finds which present devices it is for.</summary>
        public static InfDriver Parse(string path, IList<Device> devices)
        {
            var d = new InfDriver { Path = path, Name = System.IO.Path.GetFileName(path) };
            try { d.Hash = SafePath.Sha256(path); } catch { }
            var sections = ReadSections(path);
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in sections.Keys.Where(k => k.Equals("strings", StringComparison.OrdinalIgnoreCase) || k.StartsWith("strings.", StringComparison.OrdinalIgnoreCase))
                                              .OrderBy(k => k.Length))
                foreach (var line in sections[key])
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    if (!strings.ContainsKey(k)) strings[k] = Unquote(line.Substring(eq + 1).Trim());
                }
            Func<string, string> resolve = s =>
            {
                s = Unquote(s.Trim());
                if (s.Length > 2 && s[0] == '%' && s[s.Length - 1] == '%')
                {
                    string v;
                    if (strings.TryGetValue(s.Substring(1, s.Length - 2), out v)) return v;
                }
                return s;
            };

            List<string> version;
            if (sections.TryGetValue("version", out version))
                foreach (var line in version)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (k.Equals("Class", StringComparison.OrdinalIgnoreCase)) d.ClassName = resolve(v);
                    else if (k.Equals("ClassGuid", StringComparison.OrdinalIgnoreCase)) d.ClassGuid = Unquote(v).ToLowerInvariant();
                    else if (k.Equals("Provider", StringComparison.OrdinalIgnoreCase)) d.Provider = resolve(v);
                    else if (k.Equals("DriverVer", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = v.Split(',');
                        DateTime date;
                        if (DateTime.TryParseExact(parts[0].Trim(), new[] { "MM/dd/yyyy", "M/d/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                            d.Date = date;
                        if (parts.Length > 1)
                        {
                            d.VersionText = parts[1].Trim();
                            d.Version = Devices.ParseVersion(d.VersionText);
                        }
                    }
                }
            string described = Devices.ClassDescription(d.ClassGuid);
            if (described.Length > 0) d.ClassName = described;

            // Models: [Manufacturer] lists "name = section[, decorations]"; each section lists "desc = install, hwid, compatible ids".
            List<string> mfg;
            if (sections.TryGetValue("manufacturer", out mfg) && mfg.Count > 0)
            {
                d.IsDevice = true;
                string arch = Environment.Is64BitOperatingSystem ? "ntamd64" : "ntx86";
                if (string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "ARM64", StringComparison.OrdinalIgnoreCase))
                    arch = "ntarm64";
                bool anyForUs = false;
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in mfg)
                {
                    int eq = line.IndexOf('=');
                    string rhs = eq >= 0 ? line.Substring(eq + 1) : line;
                    var parts = rhs.Split(',').Select(x => Unquote(x.Trim())).Where(x => x.Length > 0).ToList();
                    if (parts.Count == 0) continue;
                    string baseName = parts[0];
                    var names = new List<string>();
                    if (parts.Count == 1)
                    {
                        // Undecorated models are only for 32-bit Windows.
                        if (arch == "ntx86") { names.Add(baseName); anyForUs = true; }
                    }
                    else
                        foreach (var deco in parts.Skip(1))
                        {
                            string dl = deco.ToLowerInvariant();
                            bool ours = dl.StartsWith(arch) || (arch == "ntx86" && (dl == "nt" || dl.StartsWith("nt.")));
                            if (!ours) continue;
                            anyForUs = true;
                            names.Add(baseName + "." + deco);
                        }
                    foreach (var name in names)
                    {
                        List<string> models;
                        if (!sections.TryGetValue(name.ToLowerInvariant(), out models)) continue;
                        foreach (var m in models)
                        {
                            int e2 = m.IndexOf('=');
                            if (e2 < 0) continue;
                            var items = m.Substring(e2 + 1).Split(',');
                            for (int i = 1; i < items.Length; i++)
                            {
                                string id = Unquote(items[i].Trim());
                                if (id.Length > 0 && ids.Add(id)) d.HardwareIds.Add(id);
                            }
                        }
                    }
                }
                d.WrongArch = !anyForUs;
                if (devices != null && !d.WrongArch)
                    foreach (var dev in devices)
                        foreach (var id in d.HardwareIds)
                            if (dev.MatchesId(id)) { d.Matches.Add(dev); break; }
            }
            d.IsChecked = d.Matches.Count > 0;
            return d;
        }

        static string Unquote(string s)
        {
            return s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;
        }

        /// <summary>Section name (lower case) → its lines, without comments, continuations joined.</summary>
        static Dictionary<string, List<string>> ReadSections(string path)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string text;
            // INF files are UTF-16 (with a BOM) or in the ANSI code page.
            using (var r = new StreamReader(path, Encoding.Default, true)) text = r.ReadToEnd();
            List<string> current = null;
            var pending = new StringBuilder();
            foreach (string raw in text.Split('\n'))
            {
                string line = StripComment(raw.TrimEnd('\r')).Trim();
                if (line.EndsWith("\\"))
                {
                    pending.Append(line, 0, line.Length - 1);
                    continue;
                }
                if (pending.Length > 0)
                {
                    line = pending + line;
                    pending.Clear();
                }
                if (line.Length == 0) continue;
                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    string name = (close > 0 ? line.Substring(1, close - 1) : line.Substring(1)).Trim();
                    if (!map.TryGetValue(name, out current)) map[name] = current = new List<string>();
                    continue;
                }
                if (current != null) current.Add(line);
            }
            return map;
        }

        static string StripComment(string s)
        {
            bool quoted = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"') quoted = !quoted;
                else if (s[i] == ';' && !quoted) return s.Substring(0, i);
            }
            return s;
        }

        // ---------- installing ----------

        /// <summary>
        /// Adds the package to the driver store and installs it on the devices it suits best.
        /// Returns 0 when installed on a device, 259 when only added to the store, else a Win32 error.
        /// </summary>
        public static int Install(string inf, IntPtr owner, out bool reboot)
        {
            reboot = false;
            if (DiInstallDriverW(owner, inf, 0, out reboot)) return 0;
            return Marshal.GetLastWin32Error();
        }

        public static string ErrorText(int code)
        {
            switch ((uint)code)
            {
                case 5: return L.T("Нужны права администратора", "Administrator rights are needed");
                case 2:
                case 3: return L.T("Не хватает файлов драйвера", "Driver files are missing");
                case 1223:
                case 0x800704C7: return L.T("Отменено", "Cancelled");
                case 0xE000022F: return L.T("Драйвер не подписан (нет каталога .cat)", "The driver is not signed (no .cat catalog)");
                case 0xE0000242:
                case 0xE0000244:
                case 0xE0000245:
                case 0x800B0100:
                case 0x800B0109:
                case 0x800B010A: return L.T("Подпись драйвера не прошла проверку", "The driver signature did not verify");
                case 0xE0000235: return L.T("Драйвер для другой архитектуры", "The driver is for another architecture");
                case 0xE0000203:
                case 0xE000020B: return L.T("Подходящее устройство не найдено", "No matching device found");
                case 0xE0000248: return L.T("Установка этого драйвера запрещена политикой", "Installing this driver is blocked by policy");
                case 0xE0000101:
                case 0xE0000100: return L.T("Файл .inf повреждён", "The .inf file is damaged");
                default:
                    string msg = (code & 0x20000000) == 0 ? new Win32Exception(code).Message : "";
                    return (msg.Length > 0 && !msg.StartsWith("Unknown error") ? msg.TrimEnd('.') : L.T("Ошибка", "Error")) +
                           " (0x" + ((uint)code).ToString("X8", CultureInfo.InvariantCulture) + ")";
            }
        }
    }
}
