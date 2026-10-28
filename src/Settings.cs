using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DriveUp
{
    /// <summary>
    /// Portable settings: DriveUP.ini next to the exe, or %LOCALAPPDATA%\DriveUP when that folder is
    /// read-only (e.g. Program Files). Only the old states of tuned settings go to the registry (below).
    /// Until 1.3 there were two editions; DriveUP Pro's DriveUP_Pro.ini, journal and registry key are
    /// picked up once and carried over.
    /// </summary>
    static class Settings
    {
        public static string Theme = "dark";
        public static string Language = "";
        /// <summary>Create a System Restore point before installing drivers (where Windows has System Restore).</summary>
        public static bool RestorePoint = true;
        /// <summary>Look for driver updates as soon as the window opens.</summary>
        public static bool CheckOnStart = true;
        public static DateTime LastCheck = DateTime.MinValue;
        /// <summary>Where driver backups go; empty = Documents\DriveUP Backup.</summary>
        public static string BackupRoot = "";
        /// <summary>The tuning mode applied last ("games", "work", "quiet"); empty = none.</summary>
        public static string TuneMode = "";
        /// <summary>
        /// What each setting was before DriveUP first changed it (setting id → its exact old state),
        /// so any of them can be put back. Kept until the setting is returned.
        /// </summary>
        public static readonly System.Collections.Generic.Dictionary<string, string> TuneSaved =
            new System.Collections.Generic.Dictionary<string, string>();
        /// <summary>The "before" measurement (Measure.ToString), taken before the first change; empty = none.</summary>
        public static string TuneBaseline = "";
        /// <summary>When each setting was changed and by which mode: id → "mode|ISO time".</summary>
        public static readonly System.Collections.Generic.Dictionary<string, string> TuneWhen =
            new System.Collections.Generic.Dictionary<string, string>();

        static string dir;

        public static string ExePath
        {
            get { return System.Reflection.Assembly.GetEntryAssembly().Location; }
        }

        /// <summary>Folder for the .ini and the journal.</summary>
        public static string Dir
        {
            get
            {
                if (dir == null) dir = PickDir();
                return dir;
            }
        }

        public static string IniPath { get { return Path.Combine(Dir, "DriveUP.ini"); } }
        public static string LogPath { get { return Path.Combine(Dir, "DriveUP.log"); } }
        /// <summary>Where DriveUP Pro (until 1.3) kept its settings and journal, next to the same exe folder.</summary>
        static string LegacyPath(string ext) { return Path.Combine(Dir, "DriveUP_Pro" + ext); }

        public static string DefaultBackupRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DriveUP Backup"); }
        }

        public static string BackupDir
        {
            get { return BackupRoot.Length > 0 ? BackupRoot : DefaultBackupRoot; }
        }

        static string PickDir()
        {
            string exeDir = Path.GetDirectoryName(ExePath);
            try
            {
                string probe = Path.Combine(exeDir, ".driveup-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return exeDir;
            }
            catch
            {
                string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveUP");
                try { Directory.CreateDirectory(local); } catch { }
                return local;
            }
        }

        public static void Load()
        {
            // DriveUP Pro's settings first, so this folder's DriveUP.ini wins where both have one.
            if (File.Exists(LegacyPath(".ini"))) LoadIni(LegacyPath(".ini"));
            LoadIni(IniPath);
            // Its journal, when there is none of DriveUP's own yet.
            try
            {
                if (!File.Exists(LogPath) && File.Exists(LegacyPath(".log"))) File.Copy(LegacyPath(".log"), LogPath);
            }
            catch { }
            // Old states kept by 1.1 / 1.2 in the .ini are kept (moved to the registry on the next save);
            // the registry wins where both have one.
            LoadTuning();
        }

        static void LoadIni(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '[') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    Apply(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
            }
            catch { }
        }

        static void Apply(string key, string val)
        {
            switch (key)
            {
                case "Theme": Theme = val; break;
                case "Language": Language = val; break;
                case "RestorePoint": RestorePoint = val == "1"; break;
                case "CheckOnStart": CheckOnStart = val == "1"; break;
                case "LastCheck": LastCheck = ParseDate(val); break;
                case "BackupRoot": BackupRoot = val; break;
                case "TuneMode": TuneMode = val; break;
                default:
                    if (key.StartsWith("Saved.", StringComparison.Ordinal) && key.Length > 6) TuneSaved[key.Substring(6)] = val;
                    else if (key.StartsWith("When.", StringComparison.Ordinal) && key.Length > 5) TuneWhen[key.Substring(5)] = val;
                    break;
            }
        }

        static DateTime ParseDate(string s)
        {
            DateTime d;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d) ? d : DateTime.MinValue;
        }

        static string FormatDate(DateTime d)
        {
            return d == DateTime.MinValue ? "" : d.ToString("o", CultureInfo.InvariantCulture);
        }

        /// <summary>Saving can come from a background thread (tuning): one at a time.</summary>
        public static readonly object Sync = new object();

        /// <summary>Writes the settings; false when they could not be stored (tuning then changes nothing).</summary>
        public static bool Save()
        {
            lock (Sync)
            {
                bool ok = SaveLocked();
                ok &= SaveTuning();
                // Carried over: DriveUP Pro's .ini must not bring back old states that have been returned since.
                if (ok)
                    try { File.Delete(LegacyPath(".ini")); } catch { }
                return ok;
            }
        }

        /// <summary>
        /// The old states of changed settings live in HKLM\SOFTWARE\DriveUP: only administrators can
        /// change them, they survive deleting or moving the exe, and text is kept exactly. Older versions
        /// kept them in the .ini (1.1, 1.2) or in HKLM\SOFTWARE\DriveUP Pro (1.2, 1.3); those are read and
        /// moved here on the next save.
        /// </summary>
        /// <summary>Tests: the old states go to this HKCU key instead.</summary>
        public static string TestStore;

        const string TuningPath = @"SOFTWARE\DriveUP", LegacyTuningPath = @"SOFTWARE\DriveUP Pro";

        static Microsoft.Win32.RegistryKey TuningKey(bool write, string path = TuningPath)
        {
            if (TestStore != null)
            {
                string p = path == TuningPath ? TestStore : TestStore + ".Legacy";
                return write ? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(p) : Microsoft.Win32.Registry.CurrentUser.OpenSubKey(p);
            }
            using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                return write ? hklm.CreateSubKey(path) : hklm.OpenSubKey(path);
        }

        static void LoadTuning()
        {
            // DriveUP Pro's key first: DriveUP's own wins where both have one.
            LoadTuning(LegacyTuningPath);
            LoadTuning(TuningPath);
        }

        static void LoadTuning(string path)
        {
            try
            {
                using (var k = TuningKey(false, path))
                {
                    if (k == null) return;
                    foreach (string name in k.GetValueNames())
                    {
                        string val = Convert.ToString(k.GetValue(name, "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames));
                        if (name == "TuneMode") TuneMode = val;
                        else if (name == "Baseline") TuneBaseline = val;
                        else if (name.StartsWith("Saved.", StringComparison.Ordinal)) TuneSaved[name.Substring(6)] = val;
                        else if (name.StartsWith("When.", StringComparison.Ordinal)) TuneWhen[name.Substring(5)] = val;
                    }
                }
            }
            catch { }
        }

        static bool SaveTuning()
        {
            try
            {
                using (var k = TuningKey(true))
                {
                    foreach (string name in k.GetValueNames())
                    {
                        bool keep = name == "TuneMode" || name == "Baseline" ||
                                    (name.StartsWith("Saved.", StringComparison.Ordinal) && TuneSaved.ContainsKey(name.Substring(6))) ||
                                    (name.StartsWith("When.", StringComparison.Ordinal) && TuneSaved.ContainsKey(name.Substring(5)));
                        if (!keep) k.DeleteValue(name, false);
                    }
                    k.SetValue("TuneMode", TuneMode, Microsoft.Win32.RegistryValueKind.String);
                    k.SetValue("Baseline", TuneBaseline ?? "", Microsoft.Win32.RegistryValueKind.String);
                    foreach (var kv in TuneSaved) k.SetValue("Saved." + kv.Key, kv.Value, Microsoft.Win32.RegistryValueKind.String);
                    foreach (var kv in TuneWhen) if (TuneSaved.ContainsKey(kv.Key)) k.SetValue("When." + kv.Key, kv.Value, Microsoft.Win32.RegistryValueKind.String);
                }
                // Everything from DriveUP Pro's key is in DriveUP's now.
                try
                {
                    if (TestStore != null) Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(TestStore + ".Legacy", false);
                    else
                        using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                            hklm.DeleteSubKeyTree(LegacyTuningPath, false);
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        static bool SaveLocked()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; DriveUP settings");
            sb.AppendLine("[Settings]");
            sb.AppendLine("Theme=" + Theme);
            sb.AppendLine("Language=" + Language);
            sb.AppendLine("RestorePoint=" + (RestorePoint ? "1" : "0"));
            sb.AppendLine("CheckOnStart=" + (CheckOnStart ? "1" : "0"));
            sb.AppendLine("LastCheck=" + FormatDate(LastCheck));
            sb.AppendLine("BackupRoot=" + BackupRoot);
            // Written aside and swapped in: a crash mid-write never leaves a half file (DriveUP
            // keeps the old values of changed settings here).
            string tmp = IniPath + ".tmp";
            try
            {
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(IniPath)) File.Replace(tmp, IniPath, null);
                else File.Move(tmp, IniPath);
                return true;
            }
            catch
            {
                try
                {
                    File.WriteAllText(IniPath, sb.ToString(), new UTF8Encoding(false));
                    File.Delete(tmp);
                    return true;
                }
                catch { return false; }
            }
        }
    }
}
