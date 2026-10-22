using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DriveUp
{
    /// <summary>A third-party driver package in the Windows driver store (oemNN.inf).</summary>
    class DriverPackage : IRowInfo, ICheckRow, ISortable
    {
        public string Oem = "", Original = "", Key = "", Provider = "", Signer = "", ClassGuid = "", ClassName = "";
        public DateTime? Date;
        public Version Version;
        public string VersionText = "";
        public long Size = -1;
        /// <summary>Some device (connected or not) uses this package.</summary>
        public bool InUse;
        /// <summary>A newer package of the same driver is in the store and this one is not used.</summary>
        public bool Outdated;
        public bool IsChecked = true;
        public string Status = "";
        public bool Failed;

        public string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\DriverStore\FileRepository", Key); }
        }

        public string StateText
        {
            get
            {
                if (Status.Length > 0) return Status;
                if (Outdated) return L.T("Старая версия", "Old version");
                return InUse ? L.T("Используется", "In use") : L.T("Не используется", "Not in use");
            }
        }

        public bool Checked { get { return IsChecked; } set { IsChecked = value; } }
        public bool Checkable { get { return true; } }
        public RowKind RowKind { get { return Failed ? RowKind.Danger : (Outdated ? RowKind.Dim : RowKind.Normal); } }
        public string IconPath { get { return "class:" + ClassGuid; } }
        public int Dot { get { return Failed ? 3 : (Outdated ? 5 : (InUse ? 1 : 2)); } }

        public IComparable SortKey(int column)
        {
            switch (column)
            {
                case 3: return Version ?? new Version(0, 0);
                case 4: return Date ?? DateTime.MinValue;
                case 5: return Size;
                default: return null;
            }
        }
    }

    /// <summary>
    /// Reads the driver store from HKLM\SYSTEM\DriverDatabase (what pnputil /enum-drivers shows,
    /// without parsing its localized text), copies packages out and removes unused old ones.
    /// </summary>
    static class DriverStore
    {
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupUninstallOEMInfW(string infFileName, int flags, IntPtr reserved);

        static readonly HashSet<string> NoCleanup = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "{e2f84ce7-8efa-411c-aa69-97454ca4cb57}",   // Extension
            "{5c4c3332-344d-483c-8739-259e934c9cc8}",   // SoftwareComponent
            "{4d36e979-e325-11ce-bfc1-08002be10318}",   // Printer
            "{1ed2bbf9-11f0-4084-b21f-ad83a8e6dcdc}",   // PrintQueue
        };

        public static List<DriverPackage> Load(HashSet<string> inUse)
        {
            var list = new List<DriverPackage>();
            using (var files = Registry.LocalMachine.OpenSubKey(@"SYSTEM\DriverDatabase\DriverInfFiles"))
            using (var packages = Registry.LocalMachine.OpenSubKey(@"SYSTEM\DriverDatabase\DriverPackages"))
            {
                if (files == null || packages == null) return list;
                foreach (string oem in files.GetSubKeyNames())
                {
                    if (!oem.StartsWith("oem", StringComparison.OrdinalIgnoreCase) || !oem.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)) continue;
                    string key = null;
                    try
                    {
                        using (var k = files.OpenSubKey(oem))
                        {
                            if (k == null) continue;
                            key = k.GetValue("Active") as string;
                            if (string.IsNullOrEmpty(key))
                            {
                                var all = k.GetValue("") as string[];
                                if (all != null && all.Length > 0) key = all[all.Length - 1];
                            }
                        }
                    }
                    catch { }
                    if (string.IsNullOrEmpty(key)) continue;
                    var p = new DriverPackage { Oem = oem, Key = key, InUse = inUse.Contains(oem) };
                    try
                    {
                        using (var k = packages.OpenSubKey(key))
                        {
                            if (k == null) continue;
                            p.Provider = (k.GetValue("Provider") as string ?? "").Trim();
                            p.Signer = (k.GetValue("SignerName") as string ?? "").Trim();
                            p.Original = (k.GetValue("InfName") as string ?? "").Trim();
                            ParseVersion(k.GetValue("Version") as byte[], p);
                        }
                    }
                    catch { }
                    if (p.Original.Length == 0)
                    {
                        int i = key.IndexOf(".inf_", StringComparison.OrdinalIgnoreCase);
                        p.Original = i > 0 ? key.Substring(0, i + 4) : oem;
                    }
                    p.ClassName = Devices.ClassDescription(p.ClassGuid);
                    if (p.ClassName.Length == 0) p.ClassName = L.T("Другие", "Other");
                    list.Add(p);
                }
            }

            // Several packages of the same driver: the newest one stays, unused older ones are "old versions".
            // Only when the newest one is actually in use (the devices have moved on to it), and never for
            // kinds whose use is not visible per device (extensions, software components, printers).
            foreach (var grp in list.Where(x => !NoCleanup.Contains(x.ClassGuid)).GroupBy(x => (x.Original + "|" + x.Provider).ToLowerInvariant()))
            {
                if (grp.Count() < 2) continue;
                var newest = grp.OrderByDescending(x => x.Version ?? new Version(0, 0)).ThenByDescending(x => x.Date ?? DateTime.MinValue).First();
                if (!newest.InUse) continue;
                // Only really older ones: the same version (variants for other hardware) or an unknown one stays.
                foreach (var x in grp)
                    x.Outdated = x != newest && !x.InUse && x.Version != null && newest.Version != null &&
                                 (x.Version < newest.Version || (x.Version == newest.Version && x.Date.HasValue && newest.Date.HasValue && x.Date < newest.Date));
            }
            return list;
        }

        /// <summary>
        /// The "Version" value of a package: 8 bytes of flags, the class GUID, the driver date as a
        /// FILETIME and the version as four 16-bit parts (major first in the high word).
        /// </summary>
        static void ParseVersion(byte[] b, DriverPackage p)
        {
            if (b == null || b.Length < 40) return;
            var guidBytes = new byte[16];
            Array.Copy(b, 8, guidBytes, 0, 16);
            p.ClassGuid = new Guid(guidBytes).ToString("B");
            long ft = BitConverter.ToInt64(b, 24);
            if (ft > 0)
                try { p.Date = DateTime.FromFileTimeUtc(ft).Date; } catch { }
            ulong v = BitConverter.ToUInt64(b, 32);
            p.Version = new Version((int)(v >> 48), (int)((v >> 32) & 0xFFFF), (int)((v >> 16) & 0xFFFF), (int)(v & 0xFFFF));
            p.VersionText = p.Version.ToString();
        }

        public static long FolderSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                    try { total += f.Length; } catch { }
            }
            catch { }
            return total;
        }

        /// <summary>A new dated folder under the backup root: "2026-10-20 10-42".</summary>
        public static string NewBackupFolder(string root)
        {
            root = Path.GetFullPath(root);
            // The copy is written with administrator rights: never through a link someone placed on the way.
            for (var d = new DirectoryInfo(root); d != null; d = d.Parent)
                if (d.Exists && d.Parent != null && SafePath.IsLink(d.FullName))
                    throw new IOException(L.T("папка копий ведёт по ссылке в другое место — выберите обычную папку",
                        "the backup folder is a link to another place — choose a regular folder"));
            string name = DateTime.Now.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture);
            string path = Path.Combine(root, name);
            for (int i = 2; Directory.Exists(path); i++) path = Path.Combine(root, name + " (" + i + ")");
            Directory.CreateDirectory(path);
            Protect(path);
            if (SafePath.IsLink(path))
                throw new IOException(L.T("папка копий подменена ссылкой", "the backup folder was replaced by a link"));
            return path;
        }

        /// <summary>
        /// Only administrators, SYSTEM and this user may change the new folder, so nobody else can slip a
        /// link into it while the copy runs (matters when the backup root is on a shared drive).
        /// </summary>
        static void Protect(string path)
        {
            try
            {
                var sec = new System.Security.AccessControl.DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);
                const System.Security.AccessControl.InheritanceFlags inherit =
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
                foreach (var sid in new System.Security.Principal.SecurityIdentifier[]
                {
                    new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                    new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                    System.Security.Principal.WindowsIdentity.GetCurrent().User,
                })
                    sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl,
                        inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                Directory.SetAccessControl(path, sec);
            }
            catch (UnauthorizedAccessException) { }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }   // FAT, some network shares: no permissions to set
            catch (IOException) { }
        }

        /// <summary>
        /// Copies each package's store folder to dest\name_version; progress(done, total) after each.
        /// The copy is a ready-to-install folder: install it back from the Install tab.
        /// </summary>
        public static int Backup(IList<DriverPackage> items, string dest, Action<int, int> progress, List<string> errors, HashSet<DriverPackage> copied = null)
        {
            int ok = 0;
            var index = new StringBuilder();
            index.AppendLine("DriveUP " + L.T("резервная копия драйверов", "driver backup") + " · " + DateTime.Now.ToString("g") + " · " + Environment.MachineName);
            index.AppendLine();
            for (int i = 0; i < items.Count; i++)
            {
                var p = items[i];
                string name = Safe(Path.GetFileNameWithoutExtension(p.Original)) + (p.VersionText.Length > 0 ? "_" + p.VersionText : "");
                string target = Path.Combine(dest, name);
                for (int n = 2; Directory.Exists(target); n++) target = Path.Combine(dest, name + "_" + n);
                try
                {
                    CopyDir(p.Folder, target);
                    ok++;
                    if (copied != null) copied.Add(p);
                    index.AppendLine(Path.GetFileName(target) + "\t" + p.Provider + "\t" + p.ClassName + "\t" + p.VersionText + "\t" + (p.Date.HasValue ? L.Date(p.Date.Value) : ""));
                }
                catch (Exception ex)
                {
                    errors.Add(p.Original + ": " + ex.Message);
                }
                progress(i + 1, items.Count);
            }
            try { File.WriteAllText(Path.Combine(dest, "DriveUP.txt"), index.ToString(), new UTF8Encoding(true)); } catch { }
            return ok;
        }

        static string Safe(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length > 0 ? s : "driver";
        }

        static void CopyDir(string from, string to)
        {
            if (!Directory.Exists(from)) throw new DirectoryNotFoundException(L.T("Папка пакета не найдена в хранилище", "Package folder not found in the store"));
            Directory.CreateDirectory(to);
            if (SafePath.IsLink(to))
                throw new IOException(L.T("папка копии подменена ссылкой", "the copy folder was replaced by a link"));
            // Fresh folders only: never overwrite something that is already there.
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), false);
            foreach (var d in Directory.GetDirectories(from))
            {
                // The store has no links of its own; skip any so the copy never leaves the package.
                if (SafePath.IsLink(d)) continue;
                CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
            }
        }

        /// <summary>Removes a package from the store; refused by Windows while any device uses it.</summary>
        public static bool Remove(DriverPackage p, out string error)
        {
            error = null;
            if (SetupUninstallOEMInfW(p.Oem, 0, IntPtr.Zero)) return true;
            int code = Marshal.GetLastWin32Error();
            error = code == 5 ? L.T("нужны права администратора", "administrator rights are needed")
                : (code == unchecked((int)0xE000023D) /* ERROR_INF_IN_USE_BY_DEVICES */ ? L.T("пакет используется устройством", "the package is used by a device") : new Win32Exception(code).Message);
            return false;
        }
    }
}
