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
            if (!IsElevated)
            {
                var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
                if (!wantHandle || p == null) return IntPtr.Zero;
                return OpenProcess(0x00100000 /* SYNCHRONIZE */, false, p.Id);
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
                if (!CreateProcessWithTokenW(primary, 0, null, cmd, 0, IntPtr.Zero, dir, ref si, out pi))
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
}
