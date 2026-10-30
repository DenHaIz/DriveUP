using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace DriveUp
{
    /// <summary>
    /// DriveUP runs as administrator (installing drivers needs it). Whatever it opens for the
    /// person (the browser, Explorer) starts with their normal rights instead — taken from Wisp.
    /// </summary>
    static class UserShell
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr h, out int code);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int level, int type, out IntPtr newToken);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessWithTokenW(IntPtr token, int logonFlags, string app, StringBuilder cmd, int flags,
            IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        public static bool IsElevated
        {
            get
            {
                try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        /// <summary>
        /// Starts a program with the signed-in user's normal rights (the shell's token), so browsers
        /// and per-user uninstallers never run as administrator. When this app is not elevated it is a
        /// plain start. Returns a process handle the caller must close (IntPtr.Zero if not needed / failed).
        /// </summary>
        public static IntPtr StartAsUser(string exe, string args, bool wantHandle)
        {
            return StartAsUser(exe, args, wantHandle, false);
        }

        static IntPtr StartAsUser(string exe, string args, bool wantHandle, bool hidden)
        {
            if (!IsElevated)
            {
                var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = hidden });
                if (!wantHandle || p == null) return IntPtr.Zero;
                return OpenProcess(0x00100000 /* SYNCHRONIZE */ | 0x0001 /* PROCESS_TERMINATE */ | 0x1000 /* QUERY_LIMITED */, false, p.Id);
            }
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) throw new InvalidOperationException(L.T("Проводник Windows не запущен", "Windows Explorer is not running"));
            int pid;
            GetWindowThreadProcessId(shell, out pid);
            IntPtr proc = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (proc == IntPtr.Zero) throw new Win32Exception();
            IntPtr token = IntPtr.Zero, primary = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(proc, 0x0002 /* TOKEN_DUPLICATE */, out token)) throw new Win32Exception();
                // TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID
                if (!DuplicateTokenEx(token, 0x0008 | 0x0001 | 0x0002 | 0x0080 | 0x0100, IntPtr.Zero, 2, 1, out primary)) throw new Win32Exception();
                var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
                PROCESS_INFORMATION pi;
                var cmd = new StringBuilder("\"" + exe + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args));
                string dir = Path.IsPathRooted(exe) ? Path.GetDirectoryName(exe) : null;
                if (!CreateProcessWithTokenW(primary, 0, null, cmd, hidden ? 0x08000000 /* CREATE_NO_WINDOW */ : 0, IntPtr.Zero, dir, ref si, out pi))
                    throw new Win32Exception();
                CloseHandle(pi.hThread);
                if (wantHandle) return pi.hProcess;
                CloseHandle(pi.hProcess);
                return IntPtr.Zero;
            }
            finally
            {
                if (primary != IntPtr.Zero) CloseHandle(primary);
                if (token != IntPtr.Zero) CloseHandle(token);
                CloseHandle(proc);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr process, uint exitCode);

        /// <summary>Runs a console program as the signed-in user, without a window, and waits for it (exit code; -1 when it did not end in time, then it is stopped).</summary>
        public static int RunAsUser(string exe, string args, int timeoutMs)
        {
            IntPtr h = StartAsUser(exe, args, true, true);
            if (h == IntPtr.Zero) return -1;
            try
            {
                if (WaitForSingleObject(h, (uint)timeoutMs) != 0)
                {
                    // Not left running: the caller treats this as a failure and may act on the result.
                    TerminateProcess(h, 1);
                    return -1;
                }
                int code;
                return GetExitCodeProcess(h, out code) ? code : -1;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>The account that owns the desktop (Explorer's token): SID and "DOMAIN\name"; null when Explorer is not running.</summary>
        public static string ShellUser(out string name)
        {
            name = "";
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) return null;
            int pid;
            GetWindowThreadProcessId(shell, out pid);
            IntPtr proc = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (proc == IntPtr.Zero) return null;
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(proc, 0x0008 /* TOKEN_QUERY */, out token)) return null;
                using (var id = new WindowsIdentity(token))
                {
                    name = id.Name ?? "";
                    return id.User != null ? id.User.Value : null;
                }
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                CloseHandle(proc);
            }
        }

        /// <summary>Opens a web page in the default browser, with the user's normal rights.</summary>
        public static void OpenUrl(string url)
        {
            StartAsUser(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), "url.dll,FileProtocolHandler " + url, false);
        }

        /// <summary>Restarts the PC in 5 seconds (by the full path: never a shutdown.exe planted elsewhere).</summary>
        public static void RestartPc()
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 5")
                { UseShellExecute = false, CreateNoWindow = true });
        }

        /// <summary>
        /// Opens a file the way a double-click in Explorer would, with the user's rights: an installer
        /// then shows its own UAC prompt with its own publisher instead of inheriting DriveUP's rights.
        /// </summary>
        public static void OpenAsUser(string path)
        {
            Explore("\"" + path + "\"");
        }

        /// <summary>Opens Explorer (a folder, or "/select,file"), with the user's normal rights.</summary>
        public static void Explore(string args)
        {
            StartAsUser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), args, false);
        }
    }

    /// <summary>
    /// The person signed in at the desktop. DriveUP elevated with another account's password ("over the
    /// shoulder" UAC: a standard user, an administrator types the password) runs as that administrator, so
    /// its HKCU, startup folder and Store apps would be the administrator's. Then per-user settings are read
    /// and changed in the desktop user's own hive (HKEY_USERS\SID) and folders instead.
    /// </summary>
    static class DesktopUser
    {
        static bool read;
        static string sid, name = "", profile;

        static void Read()
        {
            if (read) return;
            read = true;
            try
            {
                string shellName;
                string shellSid = UserShell.ShellUser(out shellName);
                string mine = WindowsIdentity.GetCurrent().User.Value;
                if (shellSid == null || string.Equals(shellSid, mine, StringComparison.OrdinalIgnoreCase)) return;
                using (var k = Microsoft.Win32.Registry.Users.OpenSubKey(shellSid))
                    if (k == null) return;   // the hive is not loaded: nothing to do there
                sid = shellSid;
                name = shellName;
                using (var p = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + shellSid))
                    if (p != null) profile = Environment.ExpandEnvironmentVariables(Convert.ToString(p.GetValue("ProfileImagePath")));
            }
            catch { sid = null; }
        }

        /// <summary>The desktop user's SID when it is not the account DriveUP runs as; null otherwise.</summary>
        public static string OtherSid { get { Read(); return sid; } }
        public static bool Other { get { return OtherSid != null; } }
        /// <summary>"PC\anna" (empty when the same account).</summary>
        public static string Name { get { Read(); return sid != null ? name : ""; } }

        /// <summary>The desktop user's own startup folder (as set in their User Shell Folders).</summary>
        public static string StartupFolder()
        {
            if (!Other) return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string home = profile ?? "";
            string raw = Reg.Get(false, @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "Startup") as string;
            if (string.IsNullOrEmpty(raw)) raw = @"%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup";
            string appData = Reg.Get(false, @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "AppData") as string;
            if (string.IsNullOrEmpty(appData)) appData = @"%USERPROFILE%\AppData\Roaming";
            Func<string, string> expand = s => System.Text.RegularExpressions.Regex.Replace(s, "%USERPROFILE%", home.Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            raw = System.Text.RegularExpressions.Regex.Replace(raw, "%APPDATA%", expand(appData).Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return home.Length == 0 && raw.IndexOf("%USERPROFILE%", StringComparison.OrdinalIgnoreCase) >= 0 ? "" : expand(raw);
        }
    }
}
