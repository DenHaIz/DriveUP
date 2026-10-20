using System.Reflection;
using System.Runtime.InteropServices;

// Shown in Explorer → Properties → Details. Keep AssemblyVersion in step with the release tag (vX.Y.Z).
[assembly: AssemblyTitle("DriveUP")]
[assembly: AssemblyDescription("Driver updates and installs for Windows")]
[assembly: AssemblyProduct("DriveUP")]
[assembly: AssemblyCopyright("© 2026 DenHaIz, MIT License")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
// The app runs as administrator: system DLLs are loaded from System32 only, never from the exe's folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
