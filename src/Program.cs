using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DriveUp
{
    static class Program
    {
        /// <summary>
        /// Name of the single-instance lock and of the "show" message. The test build
        /// (DriveUP.dev.exe) has its own, so it never touches a running release copy; so does DriveUP Pro.
        /// </summary>
        public static readonly string Instance =
            Path.GetFileNameWithoutExtension(Application.ExecutablePath).EndsWith(".dev", StringComparison.OrdinalIgnoreCase)
                ? Edition.Id + ".Dev" : Edition.Id;

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        static extern bool SetDefaultDllDirectories(uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref COPYDATASTRUCT data);

        /// <summary>Marks the WM_COPYDATA that carries paths to a running DriveUP.</summary>
        public const int PathsTag = 0x44555031;   // "DUP1"

        /// <summary>
        /// DriveUP.exe              - open the window
        /// DriveUP.exe /tab:N       - open on tab N (1..4); brings up a running instance on that tab
        /// DriveUP.exe path...      - open the Install tab with these folders / .inf / .zip / .cab
        /// DriveUP_Pro.exe /restore - put every setting changed by the Tuning tab back, then exit
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            // Elevated process: DLLs that LoadLibrary is asked for by name come from System32 only
            // (a DLL planted next to the exe, e.g. in Downloads, is never picked up).
            try { SetDefaultDllDirectories(0x800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */); } catch (EntryPointNotFoundException) { }

            int tab = 0;
            foreach (string a in args)
                if (a.StartsWith("/tab:", StringComparison.OrdinalIgnoreCase)) int.TryParse(a.Substring(5), out tab);
            var paths = args.Where(a => !a.StartsWith("/") && (File.Exists(a) || Directory.Exists(a))).Select(Path.GetFullPath).ToArray();

            Settings.Load();
            L.Init();
#if PRO
            // DriveUP_Pro.exe /restore: puts every changed setting back without opening the window
            // (for when something went wrong and the tab is out of reach).
            if (args.Any(a => a.Equals("/restore", StringComparison.OrdinalIgnoreCase))) return Tuner.ReturnAll();
#endif

            bool first;
            using (var mutex = new Mutex(true, @"Local\" + Instance + ".SingleInstance", out first))
            {
                if (!first)
                {
                    // Already running: bring that window up instead (and hand it the files, if any).
                    if (paths.Length > 0) SendPaths(paths);
                    else PostMessage((IntPtr)0xFFFF /* HWND_BROADCAST */, MainForm.ShowMessage, (IntPtr)tab, IntPtr.Zero);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => ThemedDialog.Info(null, L.T("Ошибка", "Error"), e.Exception.Message, true);

                float scale;
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
                Theme.Init(scale);

                var form = new MainForm();
                if (tab >= 1 && tab <= (Edition.Pro ? 5 : 4)) form.StartTab = tab - 1;
                if (paths.Length > 0) form.StartPaths = paths;
                Application.Run(form);
            }
            return 0;
        }

        /// <summary>Gives the running copy the paths this start was asked to open (it shows them on the Install tab).</summary>
        static void SendPaths(string[] paths)
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                string text = string.Join("\n", paths);
                IntPtr mem = Marshal.StringToHGlobalUni(text);
                try
                {
                    var cds = new COPYDATASTRUCT { dwData = (IntPtr)PathsTag, cbData = (text.Length + 1) * 2, lpData = mem };
                    SendMessage(p.MainWindowHandle, 0x4A /* WM_COPYDATA */, IntPtr.Zero, ref cds);
                }
                finally { Marshal.FreeHGlobal(mem); }
                return;
            }
        }
    }
}
