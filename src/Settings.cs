using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DriveUp
{
    /// <summary>
    /// Portable settings: DriveUP.ini next to the exe, or %LOCALAPPDATA%\DriveUP when that folder is
    /// read-only (e.g. Program Files). Nothing is written to the registry.
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

        public static void Save()
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
            try { File.WriteAllText(IniPath, sb.ToString(), new UTF8Encoding(false)); }
            catch { }
        }
    }
}
