using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DriveUp
{
    /// <summary>
    /// Portable settings: DriveUP.ini (DriveUP_Pro.ini) next to the exe, or %LOCALAPPDATA%\DriveUP when that
    /// folder is read-only (e.g. Program Files). Nothing is written to the registry. Both editions share
    /// the backup folder, so either one restores the other's backups.
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
#if PRO
        /// <summary>The tuning mode applied last ("games", "work", "quiet"); empty = none.</summary>
        public static string TuneMode = "";
        /// <summary>
        /// What each setting was before DriveUP Pro first changed it (setting id → its exact old state),
        /// so any of them can be put back. Kept until the setting is returned.
        /// </summary>
        public static readonly System.Collections.Generic.Dictionary<string, string> TuneSaved =
            new System.Collections.Generic.Dictionary<string, string>();
        /// <summary>When each setting was changed and by which mode: id → "mode|ISO time".</summary>
        public static readonly System.Collections.Generic.Dictionary<string, string> TuneWhen =
            new System.Collections.Generic.Dictionary<string, string>();
#endif

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

        public static string IniPath { get { return Path.Combine(Dir, Edition.Id + ".ini"); } }
        public static string LogPath { get { return Path.Combine(Dir, Edition.Id + ".log"); } }

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
                string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Edition.Id);
                try { Directory.CreateDirectory(local); } catch { }
                return local;
            }
        }

        public static void Load()
        {
            string path = IniPath;
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
#if PRO
                case "TuneMode": TuneMode = val; break;
                default:
                    if (key.StartsWith("Saved.", StringComparison.Ordinal) && key.Length > 6) TuneSaved[key.Substring(6)] = val;
                    else if (key.StartsWith("When.", StringComparison.Ordinal) && key.Length > 5) TuneWhen[key.Substring(5)] = val;
                    break;
#endif
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

#if PRO
        static string OneLine(string v)
        {
            return (v ?? "").Replace('\r', ' ').Replace('\n', ' ');
        }

#endif
        /// <summary>Saving can come from a background thread (DriveUP Pro tuning): one at a time.</summary>
        public static readonly object Sync = new object();

        public static void Save()
        {
            lock (Sync) SaveLocked();
        }

        static void SaveLocked()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; " + Edition.Name + " settings");
            sb.AppendLine("[Settings]");
            sb.AppendLine("Theme=" + Theme);
            sb.AppendLine("Language=" + Language);
            sb.AppendLine("RestorePoint=" + (RestorePoint ? "1" : "0"));
            sb.AppendLine("CheckOnStart=" + (CheckOnStart ? "1" : "0"));
            sb.AppendLine("LastCheck=" + FormatDate(LastCheck));
            sb.AppendLine("BackupRoot=" + BackupRoot);
#if PRO
            sb.AppendLine("TuneMode=" + TuneMode);
            if (TuneSaved.Count > 0) sb.AppendLine("[Tuning]");
            foreach (var kv in TuneSaved) sb.AppendLine("Saved." + kv.Key + "=" + OneLine(kv.Value));
            foreach (var kv in TuneWhen) if (TuneSaved.ContainsKey(kv.Key)) sb.AppendLine("When." + kv.Key + "=" + OneLine(kv.Value));
#endif
            // Written aside and swapped in: a crash mid-write never leaves a half file (DriveUP Pro
            // keeps the old values of changed settings here).
            string tmp = IniPath + ".tmp";
            try
            {
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(IniPath)) File.Replace(tmp, IniPath, null);
                else File.Move(tmp, IniPath);
            }
            catch
            {
                try
                {
                    File.WriteAllText(IniPath, sb.ToString(), new UTF8Encoding(false));
                    File.Delete(tmp);
                }
                catch { }
            }
        }
    }
}
