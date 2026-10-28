using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>How "download and install" of a vendor's driver ended.</summary>
    class VendorResult
    {
        public bool Ok, Reboot, Cancelled;
        public string Message = "";
        /// <summary>A .zip package: checked, to be opened on the Install tab (unpacked and confirmed there).</summary>
        public string OpenFile;
    }

    /// <summary>
    /// "Download and install" for drivers found on vendors' sites. The file comes only over HTTPS from the
    /// vendor's own download host (redirects too), into a protected folder; its hash must match the feed's
    /// (when the feed has one) and its Authenticode signature must be valid and made by the vendor itself
    /// (O= of the signer). Anything else is not run. Then a confirmation, a restore point, the installer
    /// (silently only with the command the feed gives), the device list read again and a Journal entry.
    /// </summary>
    static class VendorInstall
    {
        /// <summary>The vendors' own download hosts (exact names; NVIDIA: any *.download.nvidia.com).</summary>
        static readonly Dictionary<string, string[]> Hosts = new Dictionary<string, string[]>
        {
            { "NVIDIA", new[] { "download.nvidia.com", ".download.nvidia.com" } },
            { "Intel", new[] { "downloadmirror.intel.com" } },
            { "Lenovo", new[] { "download.lenovo.com" } },
            { "ASUS", new[] { "dlcdnets.asus.com" } },
            { "HP", new[] { "ftp.hp.com", "ftp.ext.hp.com" } },
            { "Dell", new[] { "downloads.dell.com", "dl.dell.com" } },
        };

        /// <summary>The organisation (O=) in the signer's certificate, exactly (case aside), as each vendor signs today.</summary>
        static readonly Dictionary<string, string[]> Signers = new Dictionary<string, string[]>
        {
            { "NVIDIA", new[] { "NVIDIA Corporation" } },
            { "Intel", new[] { "Intel Corporation" } },
            { "Lenovo", new[] { "Lenovo" } },
            { "ASUS", new[] { "ASUSTeK COMPUTER INC." } },
            { "HP", new[] { "HP Inc.", "HP Inc" } },
            // Dell Update Packages are signed "Dell Technologies Inc." now, "Dell Inc" before.
            { "Dell", new[] { "Dell Technologies Inc.", "Dell Inc.", "Dell Inc" } },
        };

        /// <summary>True when the row can be downloaded and installed from here (the feed named the file).</summary>
        public static bool Offers(VendorUpdate v)
        {
            return v != null && v.CatalogId.Length == 0 && v.FileUrl.Length > 0 && Allowed(v.Vendor, v.FileUrl);
        }

        /// <summary>HTTPS on one of this vendor's download hosts.</summary>
        public static bool Allowed(string vendor, string url)
        {
            Uri u;
            return !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out u) && Allowed(vendor, u);
        }

        public static bool Allowed(string vendor, Uri u)
        {
            string[] hosts;
            if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo.Length > 0 || !Hosts.TryGetValue(vendor, out hosts)) return false;
            string h = u.Host.ToLowerInvariant();
            return hosts.Any(x => x.StartsWith(".") ? h.EndsWith(x) && h.Length > x.Length : h == x);
        }

        /// <summary>The signer's O= is one of the vendor's.</summary>
        public static bool SignerMatches(string vendor, string subject)
        {
            string[] orgs;
            return subject != null && Signers.TryGetValue(vendor, out orgs) && orgs.Any(o => Runtimes.HasPart(subject, "O", o));
        }

        public static string ExpectedSigner(string vendor)
        {
            string[] orgs;
            return Signers.TryGetValue(vendor, out orgs) ? orgs[0] : vendor;
        }

        /// <summary>The file name in a download address ("…/sp174484.exe?x=1" → "sp174484.exe").</summary>
        public static string FileName(string url)
        {
            try { return Path.GetFileName(Uri.UnescapeDataString(new Uri(url).AbsolutePath)); }
            catch { return ""; }
        }

        /// <summary>"1.04 GB", "11.25 MB" → bytes; 0 when unknown.</summary>
        public static long ParseSize(string s)
        {
            var m = Regex.Match(s ?? "", @"^\s*([\d.]+)\s*(KB|MB|GB)\s*$", RegexOptions.IgnoreCase);
            double n;
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return 0;
            int pow = "KMG".IndexOf(char.ToUpperInvariant(m.Groups[2].Value[0])) + 1;
            return (long)(n * Math.Pow(1024, pow));
        }

        /// <summary>
        /// Arguments from a feed that are safe to pass on: switches, names, paths and quotes only — no shell
        /// characters (&amp; | &lt; &gt; ^ % ! and the like). %PACKAGEPATH% (Lenovo) is the only variable.
        /// </summary>
        public static bool SafeArgs(string args)
        {
            if (args == null) return false;
            string bare = Regex.Replace(args, "%PACKAGEPATH%", "", RegexOptions.IgnoreCase);
            return bare.Length < 400 && Regex.IsMatch(bare, @"^[A-Za-z0-9_ \t/\-=.:""\\,+]*\z");   // no line breaks either
        }

        /// <summary>
        /// HP's SilentInstall: a program of the package (no folder) and its arguments — '"Setup.exe" /s', 'HPUP.exe',
        /// 'Install2.bat'. Null when it is not one ("Not support", "SP149450 -s").
        /// </summary>
        public static Tuple<string, string> InnerCommand(string command)
        {
            var m = Regex.Match(command ?? "", @"^\s*(?:""(?<f>[^""]+)""|(?<f>[^\s""]+))(?<a>.*)$");
            if (!m.Success) return null;
            string file = m.Groups["f"].Value.Trim(), args = m.Groups["a"].Value.Trim();
            if (!Regex.IsMatch(file, @"^[\w.\- ]+\.(exe|bat|cmd)$", RegexOptions.IgnoreCase) || file.StartsWith(".") || !SafeArgs(args)) return null;
            return Tuple.Create(file, args);
        }

        /// <summary>%PACKAGEPATH% → the download folder (quoted when it has spaces).</summary>
        public static string Expand(string args, string dir)
        {
            return Regex.Replace(args ?? "", @"(?<pre>[^\s""]*?=)?%PACKAGEPATH%(?<post>[^\s""]*)", m =>
            {
                string value = dir.TrimEnd('\\') + m.Groups["post"].Value;
                if (value.Contains(" ")) value = "\"" + value + "\"";
                return m.Groups["pre"].Value + value;
            }, RegexOptions.IgnoreCase);
        }

        /// <summary>Null when the file's hash matches the feed's (or the feed has none), otherwise why not.</summary>
        public static string CheckHash(VendorUpdate v, string file)
        {
            if (v.FileHash.Length == 0) return null;
            HashAlgorithm h;
            switch (v.HashKind.ToUpperInvariant())
            {
                case "SHA256": h = SHA256.Create(); break;
                case "SHA1": h = SHA1.Create(); break;
                case "MD5": h = MD5.Create(); break;
                default: return L.T("неизвестный вид хеша: ", "unknown hash kind: ") + v.HashKind;
            }
            using (h)
            using (var s = File.OpenRead(file))
            {
                string got = BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
                return got.Equals(v.FileHash.Trim(), StringComparison.OrdinalIgnoreCase) ? null
                    : L.T("хеш файла не совпадает с указанным производителем (", "the file's hash differs from the vendor's (") + v.HashKind + ")";
            }
        }

        /// <summary>Null when the file is signed (valid chain) by the vendor itself; otherwise why not. signer = the O= found.</summary>
        public static string CheckSigner(VendorUpdate v, string file, out string signer)
        {
            signer = null;
            string subject = Runtimes.TrustedSigner(file);
            if (subject == null) return L.T("у файла нет действительной цифровой подписи", "the file has no valid digital signature");
            var m = Regex.Match(subject, @"(?:^|,\s*)O=(""[^""]*""|[^,]*)");
            signer = m.Success ? m.Groups[1].Value.Trim('"', ' ') : subject;
            if (!SignerMatches(v.Vendor, subject))
                return L.T("файл подписан «", "the file is signed by \"") + signer + L.T("», а не «", "\", not \"") + ExpectedSigner(v.Vendor) + L.T("»", "\"");
            return null;
        }

        static bool IsZip(string file) { return file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase); }

        /// <summary>
        /// The whole way, on a worker thread: download (progress(done, total)), checks, confirmation (askUi runs on
        /// the UI thread and waits), restore point, the installer. stage() names each step for the row.
        /// </summary>
        public static VendorResult Run(VendorUpdate v, IWin32Window owner, Func<Func<bool>, bool> askUi, Action<string> stage, Action<long, long> progress)
        {
            var r = new VendorResult();
            if (!Offers(v)) { r.Message = L.T("для этой находки нет файла — только страница", "no file for this finding — only its page"); return r; }
            string dir = InfFiles.NewDownloadDir();
            string name = FileName(v.FileUrl);
            if (!Regex.IsMatch(name, @"^[\w.\-]+\.(exe|zip)$", RegexOptions.IgnoreCase)) name = IsZip(name) ? "package.zip" : "setup.exe";
            string file = Path.Combine(dir, name);
            bool keep = false;
            try
            {
                stage(L.T("скачивание…", "downloading…"));
                VendorCheck.DownloadFile(v.FileUrl, file, 3L << 30, u => Allowed(v.Vendor, u), progress);
                stage(L.T("проверка…", "checking…"));
                string error = CheckHash(v, file);
                if (error != null) { r.Message = error; return r; }
                long size = new FileInfo(file).Length;
                if (IsZip(file))
                {
                    // A zip has no signature of its own: only with the vendor's hash, then the Install tab unpacks it
                    // (Windows checks the drivers' own signatures) and asks as usual.
                    if (v.FileHash.Length == 0) { r.Message = L.T("у архива нет хеша в каталоге производителя — не открываю", "the vendor's feed has no hash for the archive — not opening it"); return r; }
                    r.Ok = true;
                    r.OpenFile = file;
                    keep = true;
                    return r;
                }
                string signer;
                error = CheckSigner(v, file, out signer);
                if (error != null) { r.Message = error + L.T(" — не запускаю", " — not running it"); return r; }

                bool silent = v.SilentArgs != null;
                bool want = Settings.RestorePoint;
                string text =
                    v.Vendor + ": " + v.Title + (v.VersionText.Length > 0 ? " " + v.VersionText : "") + "\n" +
                    L.T("Файл: ", "File: ") + name + " (" + L.Size(size) + ")\n" +
                    L.T("Подпись: ", "Signed by: ") + signer + L.T(" — проверена", " — verified") +
                    (v.FileHash.Length > 0 ? L.T(", хеш совпадает с каталогом производителя", ", hash matches the vendor's feed") : "") + "\n\n" +
                    (silent ? L.T("Установка пройдёт без окон (тихо), как предусмотрел производитель.", "It installs silently, the way the vendor provides.")
                            : L.T("Откроется установщик производителя — пройдите его шаги.", "The vendor's installer opens — follow its steps.")) +
                    (want ? L.T("\nПеред установкой будет создана точка восстановления.", "\nA restore point is made first.") : "");
                if (!askUi(() => ThemedDialog.Confirm(owner, L.T("Установить драйвер?", "Install the driver?"), text, L.T("Установить", "Install"), "", false)))
                {
                    r.Cancelled = true;
                    r.Message = L.T("отменено", "cancelled");
                    return r;
                }
                if (want)
                {
                    stage(L.T("точка восстановления…", "restore point…"));
                    bool rpOk;
                    string rpNote = SysRestore.Create("DriveUP: " + L.T("установка драйвера ", "driver install ") + v.Vendor, out rpOk);
                    if (!rpOk && !SysRestore.ContinueWithout(askUi, owner, rpNote))
                    {
                        r.Cancelled = true;
                        r.Message = L.T("отменено: нет точки восстановления", "cancelled: no restore point");
                        return r;
                    }
                }
                stage(L.T("установка…", "installing…"));
                int code;
                bool finished = Execute(v, file, dir, out code);
                var ok = v.OkCodes.Length > 0 ? v.OkCodes : new[] { 0, 3010, 1641 };
                if (!finished)
                    r.Message = L.T("установщик ещё работает — DriveUP перестал его ждать", "the installer is still running — DriveUP stopped waiting");
                else if (ok.Contains(code))
                {
                    r.Ok = true;
                    r.Reboot = code == 3010 || code == 1641 || (v.Vendor == "Dell" && code == 2);
                }
                else r.Message = L.T("установщик завершился с кодом ", "the installer ended with code ") + code;
                keep = !finished;
                return r;
            }
            catch (Exception ex)
            {
                r.Message = ex.Message;
                return r;
            }
            finally
            {
                if (!keep) try { Directory.Delete(dir, true); } catch { }
                Journal.Add(JournalKind.Install, r.Ok ? 1 : 0, r.Ok || r.Cancelled ? 0 : 1,
                    v.Vendor + ": " + v.Title + " " + v.VersionText + " — " +
                    (r.OpenFile != null ? L.T("скачан, открыт на «Установке»", "downloaded, opened on the Install tab")
                     : r.Ok ? L.T("установлен", "installed") + (r.Reboot ? L.T(", нужна перезагрузка", ", restart needed") : "") : r.Message));
            }
        }

        /// <summary>Runs the checked installer: silently with the feed's command, otherwise with its own windows (waits as long as it takes).</summary>
        static bool Execute(VendorUpdate v, string file, string dir, out int code)
        {
            code = -1;
            if (v.SilentArgs == null) return Start(file, "", dir, -1, out code);
            if (v.Vendor != "HP") return Start(file, Expand(v.SilentArgs, dir), dir, 3600000, out code);
            // HP: unpack the SoftPaq quietly into the protected folder, then run its SilentInstall there
            // (what HP Image Assistant does). The SoftPaq was checked; nobody else can write in that folder.
            var cmd = InnerCommand(v.SilentArgs);
            string unpacked = Path.Combine(dir, "x");
            Directory.CreateDirectory(unpacked);
            if (!Start(file, "/s /e /f \"" + unpacked + "\"", dir, 900000, out code)) return false;
            if (code != 0) return true;
            string inner = Path.Combine(unpacked, cmd.Item1);
            if (!File.Exists(inner) || SafePath.IsLink(inner))
                throw new IOException(L.T("в пакете HP нет ", "the HP package has no ") + cmd.Item1);
            if (inner.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Start(inner, cmd.Item2, unpacked, 3600000, out code);
            return Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c \"\"" + inner + "\"" + (cmd.Item2.Length > 0 ? " " + cmd.Item2 : "") + "\"", unpacked, 3600000, out code);
        }

        /// <summary>Starts and waits (timeoutMs &lt; 0: until it ends). False when it is still running after the timeout (it is not killed).</summary>
        static bool Start(string exe, string args, string workDir, int timeoutMs, out int code)
        {
            code = -1;
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = workDir };
            using (var p = Process.Start(psi))
            {
                if (timeoutMs < 0) p.WaitForExit();
                else if (!p.WaitForExit(timeoutMs)) return false;
                code = p.ExitCode;
                return true;
            }
        }
    }
}
