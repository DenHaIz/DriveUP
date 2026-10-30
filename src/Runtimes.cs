using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DriveUp
{
    /// <summary>A library programs and games need (Visual C++, .NET, DirectX 9) and where Microsoft publishes it.</summary>
    class Runtime
    {
        public string Name = "", Url = "", Args = "";
        public Func<bool> Installed;
        /// <summary>Installed, but older than this needs (offered as an update).</summary>
        public Func<bool> Outdated = () => false;
        /// <summary>DirectX: the download unpacks itself, then DXSETUP installs.</summary>
        public bool Unpack;
        /// <summary>.NET Framework 3.5: a Windows feature (DISM), nothing to download here.</summary>
        public bool Feature;
        public long Size;
    }

    /// <summary>
    /// Libraries that programs and games expect. Found missing from the registry and system files (no
    /// network), downloaded only from Microsoft, run only when signed by Microsoft Corporation, silently.
    /// </summary>
    static class Runtimes
    {
        static readonly bool X64 = Environment.Is64BitOperatingSystem;

        static IEnumerable<string> Installed()
        {
            var names = new List<string>();
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                        if (k != null)
                            foreach (string sub in k.GetSubKeyNames())
                                using (var e = k.OpenSubKey(sub))
                                {
                                    string n = e != null ? Convert.ToString(e.GetValue("DisplayName")) : "";
                                    if (n.Length > 0) names.Add(n);
                                }
                }
                catch { }
            return names;
        }

        static bool Vc(IEnumerable<string> names, string year, string arch)
        {
            var re = new Regex(@"Visual C\+\+ " + year + @"\b.*\b" + arch + @"\b", RegexOptions.IgnoreCase);
            // The x86 package of 2005 names no architecture at all.
            var plain = new Regex(@"Visual C\+\+ " + year + @" Redistributable\s*$", RegexOptions.IgnoreCase);
            return names.Any(n => re.IsMatch(n) || (arch == "x86" && plain.IsMatch(n)));
        }

        /// <summary>Visual C++ 2015–2022 from its own key ("v14.44.35211.00"); null if absent.</summary>
        static Version Vc14(string arch)
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + arch))
                {
                    if (k == null || Convert.ToInt32(k.GetValue("Installed", 0)) != 1) return null;
                    Version v;
                    return Version.TryParse(Convert.ToString(k.GetValue("Version")).TrimStart('v'), out v) ? v : new Version(14, 0);
                }
            }
            catch { return null; }
        }

        static bool DotNet(string major)
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var k = hklm.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\" + (X64 ? "x64" : "x86") + @"\sharedfx\Microsoft.WindowsDesktop.App"))
                    return k != null && k.GetValueNames().Any(n => n.StartsWith(major + ".", StringComparison.Ordinal));
            }
            catch { return false; }
        }

        static bool NetFx35()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5"))
                    return k != null && Convert.ToInt32(k.GetValue("Install", 0)) == 1;
            }
            catch { return false; }
        }

        const string Vc5 = "https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_";
        const string Vc8 = "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_";
        const string Vc10 = "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_";
        const string Vc11 = "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_";

        /// <summary>Everything that is missing (or too old) on this computer.</summary>
        public static List<Runtime> Missing()
        {
            var names = Installed().ToList();
            var all = new List<Runtime>();
            foreach (var arch in X64 ? new[] { "x64", "x86" } : new[] { "x86" })
            {
                string a = arch;
                all.Add(new Runtime
                {
                    Name = "Visual C++ 2015–2022 (" + a + ")", Url = "https://aka.ms/vs/17/release/vc_redist." + a + ".exe", Args = "/install /quiet /norestart",
                    Installed = () => Vc14(a) != null, Outdated = () => { var v = Vc14(a); return v != null && v < new Version(14, 40); },
                });
                all.Add(new Runtime { Name = "Visual C++ 2013 (" + a + ")", Url = "https://aka.ms/highdpimfc2013" + a + "enu", Args = "/install /quiet /norestart", Installed = () => Vc(names, "2013", a) });
                all.Add(new Runtime { Name = "Visual C++ 2012 (" + a + ")", Url = Vc11 + a + ".exe", Args = "/install /quiet /norestart", Installed = () => Vc(names, "2012", a) });
                all.Add(new Runtime { Name = "Visual C++ 2010 (" + a + ")", Url = Vc10 + a + ".exe", Args = "/q /norestart", Installed = () => Vc(names, "2010", a) });
                all.Add(new Runtime { Name = "Visual C++ 2008 (" + a + ")", Url = Vc8 + a + ".exe", Args = "/q", Installed = () => Vc(names, "2008", a) });
                all.Add(new Runtime { Name = "Visual C++ 2005 (" + a + ")", Url = Vc5 + a + ".EXE", Args = "/Q", Installed = () => Vc(names, "2005", a) });
            }
            if (X64)
            {
                all.Add(new Runtime { Name = ".NET Desktop Runtime 8", Url = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe", Args = "/install /quiet /norestart", Installed = () => DotNet("8") });
                all.Add(new Runtime { Name = ".NET Desktop Runtime 10", Url = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe", Args = "/install /quiet /norestart", Installed = () => DotNet("10") });
            }
            all.Add(new Runtime
            {
                Name = ".NET Framework 3.5", Feature = true, Installed = NetFx35,
            });
            all.Add(new Runtime
            {
                Name = "DirectX 9.0c (" + L.T("июнь 2010", "June 2010") + ")", Unpack = true,
                Url = "https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe",
                Installed = () => File.Exists(Path.Combine(Environment.SystemDirectory, "d3dx9_43.dll")),
            });
            return all.Where(r => { try { return !r.Installed() || r.Outdated(); } catch { return false; } }).ToList();
        }

        // ---------- Authenticode ----------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO { public int cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public int cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public int dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile; public int dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference; public int dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

        /// <summary>A valid Authenticode signature (whole chain, revocation checked) by Microsoft Corporation.</summary>
        public static bool SignedByMicrosoft(string file)
        {
            string subject = TrustedSigner(file);
            return subject != null && HasPart(subject, "O", "Microsoft Corporation") && HasPart(subject, "CN", "Microsoft Corporation");
        }

        /// <summary>True when a certificate subject has this part exactly ("O=Intel Corporation"), case aside.</summary>
        public static bool HasPart(string subject, string key, string value)
        {
            return Regex.IsMatch(subject, @"(^|,\s*)" + key + "=" + Regex.Escape(value) + @"\s*(,|$)", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(subject, @"(^|,\s*)" + key + "=\"" + Regex.Escape(value) + "\"\\s*(,|$)", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// The signer's certificate subject when WinVerifyTrust accepts the file's Authenticode signature
        /// (whole chain, revocation checked); null when it is unsigned, tampered with or not trusted.
        /// </summary>
        public static string TrustedSigner(string file)
        {
            var info = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = file };
            IntPtr pInfo = Marshal.AllocHGlobal(Marshal.SizeOf(info));
            try
            {
                Marshal.StructureToPtr(info, pInfo, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = Marshal.SizeOf(typeof(WINTRUST_DATA)), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 1 /* WHOLECHAIN */,
                    dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = pInfo, dwStateAction = 0, dwProvFlags = 0x80 /* WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT */,
                };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
                if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return null;
            }
            finally
            {
                Marshal.DestroyStructure(pInfo, typeof(WINTRUST_FILE_INFO));
                Marshal.FreeHGlobal(pInfo);
            }
            try { return new X509Certificate2(X509Certificate.CreateFromSignedFile(file)).Subject; }
            catch { return null; }
        }

        // ---------- installing ----------

        static int busy;

        /// <summary>Components are being installed (an operation: nothing else installs meanwhile, the window waits).</summary>
        public static bool Busy { get { return busy != 0; } }

        /// <summary>Downloads, checks and installs the libraries one after another; done(installed, errors) on the UI thread.</summary>
        public static void Install(IHost host, Control ui, List<Runtime> items, Action<int, List<string>> done)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            {
                host.ShowToast(L.T("Компоненты уже устанавливаются", "Components are already being installed"), ToastKind.Info);
                return;
            }
            host.BeginBusy();
            var th = new Thread(() =>
            {
                int ok = 0;
                bool reboot = false;
                var errors = new List<string>();
                foreach (var r in items)
                {
                    var rt = r;
                    Post(ui, () => host.ShowToast(L.T("Устанавливаю: ", "Installing: ") + rt.Name, ToastKind.Info));
                    try
                    {
                        int code = rt.Feature ? EnableNetFx35() : DownloadAndRun(rt);
                        if (code == 0 || code == 1638 /* a newer one is there */ || code == 3010) ok++;
                        else errors.Add(rt.Name + ": " + L.T("код ", "code ") + code);
                        if (code == 3010) reboot = true;
                    }
                    catch (Exception ex) { errors.Add(rt.Name + ": " + ex.Message); }
                }
                Journal.Add(JournalKind.Install, ok, errors.Count, string.Join(", ", items.Select(x => x.Name)));
                if (reboot) errors.Add(L.T("Для части компонентов нужна перезагрузка", "Some components need a restart"));
                Post(ui, () =>
                {
                    Interlocked.Exchange(ref busy, 0);
                    host.EndBusy();
                    done(ok, errors);
                });
            }) { IsBackground = true, Name = "runtimes" };
            th.Start();
        }

        static void Post(Control ui, Action a)
        {
            try { ui.BeginInvoke((MethodInvoker)delegate { a(); }); }
            catch (InvalidOperationException) { }
        }

        static int DownloadAndRun(Runtime r)
        {
            string dir = InfFiles.NewDownloadDir();
            string file = Path.Combine(dir, r.Unpack ? "directx_redist.exe" : "setup.exe");
            VendorCheck.DownloadFile(r.Url, file, 400L << 20);
            if (!SignedByMicrosoft(file)) throw new IOException(L.T("файл не подписан Microsoft — не запускаю", "the file is not signed by Microsoft — not running it"));
            if (!r.Unpack) return Run(file, r.Args, 900000);
            // DirectX: unpack quietly into the protected folder, then run its own setup (checked as well).
            string unpacked = Path.Combine(dir, "dx");
            Directory.CreateDirectory(unpacked);
            int code = Run(file, "/Q /T:\"" + unpacked + "\"", 600000);
            if (code != 0) return code;
            string setup = Path.Combine(unpacked, "DXSETUP.exe");
            if (!File.Exists(setup) || !SignedByMicrosoft(setup)) throw new IOException(L.T("установщик DirectX не прошёл проверку", "the DirectX setup did not pass the check"));
            return Run(setup, "/silent", 900000);
        }

        static int EnableNetFx35()
        {
            // Windows downloads the files itself from Windows Update.
            return Run(Path.Combine(Environment.SystemDirectory, "dism.exe"), "/Online /Enable-Feature /FeatureName:NetFx3 /All /NoRestart /Quiet", 1800000);
        }

        static int Run(string exe, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) };
            using (var p = Process.Start(psi))
            {
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException(L.T("установка идёт слишком долго", "the install takes too long"));
                }
                return p.ExitCode;
            }
        }
    }
}
