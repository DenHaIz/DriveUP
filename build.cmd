@echo off
rem Builds DriveUP with the C# compiler that ships with Windows (.NET Framework 4.x):
rem   bin\DriveUP.exe - drivers (updates, devices, install, backups) and system tuning (src\Tuning)
rem   build.cmd       - the release exe (asks for administrator rights on start: installing drivers needs them)
rem   build.cmd dev   - also DriveUP.dev.exe that runs without elevation (for testing)
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)

if not exist bin mkdir bin
if not exist obj mkdir obj

set "OPTS=/nologo /preferreduilang:en-US /codepage:65001 /optimize+ /platform:anycpu"
set "REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll /r:System.Management.dll /r:Microsoft.CSharp.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll"
set "ICONSRC=tools\MakeIcon.cs src\Logo.cs src\Theme.cs src\Settings.cs"

rem 1. The .exe icon is drawn by the same code as the in-app logo (src\Logo.cs).
"%CSC%" %OPTS% /target:exe /out:obj\MakeIcon.exe %REFS% %ICONSRC%
if errorlevel 1 goto failed
obj\MakeIcon.exe obj\app.ico
if errorlevel 1 goto failed

rem 2. The application: one self-contained exe, nothing else is needed next to it.
"%CSC%" %OPTS% /target:winexe /out:bin\DriveUP.exe /win32manifest:app.manifest /win32icon:obj\app.ico %REFS% src\*.cs src\Tuning\*.cs
if errorlevel 1 goto failed

if /i "%~1"=="dev" (
  powershell -NoProfile -Command "(Get-Content app.manifest) -replace 'requireAdministrator','asInvoker' | Set-Content obj\dev.manifest -Encoding UTF8"
  "%CSC%" %OPTS% /target:winexe /out:bin\DriveUP.dev.exe /win32manifest:obj\dev.manifest /win32icon:obj\app.ico %REFS% src\*.cs src\Tuning\*.cs
  if errorlevel 1 goto failed
)

echo Build OK: %CD%\bin\DriveUP.exe
exit /b 0

:failed
echo Build FAILED
exit /b 1
