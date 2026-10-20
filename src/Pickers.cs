using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DriveUp
{
    /// <summary>
    /// The modern "Select folder" dialog (IFileDialog with FOS_PICKFOLDERS) instead of the old
    /// folder tree of FolderBrowserDialog; falls back to that tree if the shell refuses.
    /// </summary>
    static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);   // IModalWindow
            void SetFileTypes(uint count, IntPtr filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr bc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

        const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        /// <summary>The chosen folder, or null when cancelled.</summary>
        public static string Pick(IWin32Window owner, string title, string start)
        {
            try
            {
                var dlg = (IFileDialog)new FileOpenDialogCom();
                try
                {
                    // Add to the defaults (they include FOS_NOCHANGEDIR) rather than replace them.
                    uint defaults;
                    dlg.GetOptions(out defaults);
                    dlg.SetOptions(defaults | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
                    dlg.SetTitle(title);
                    if (!string.IsNullOrEmpty(start) && System.IO.Directory.Exists(start))
                    {
                        IShellItem folder;
                        SHCreateItemFromParsingName(start, IntPtr.Zero, typeof(IShellItem).GUID, out folder);
                        dlg.SetFolder(folder);
                    }
                    if (dlg.Show(owner != null ? owner.Handle : IntPtr.Zero) != 0) return null;
                    IShellItem result;
                    dlg.GetResult(out result);
                    string path;
                    result.GetDisplayName(SIGDN_FILESYSPATH, out path);
                    return path;
                }
                finally { Marshal.ReleaseComObject(dlg); }
            }
            catch (COMException) { }
            catch (InvalidCastException) { }
            using (var fb = new FolderBrowserDialog { Description = title, SelectedPath = start ?? "" })
                return fb.ShowDialog(owner) == DialogResult.OK ? fb.SelectedPath : null;
        }
    }
}
