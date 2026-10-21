@echo off
rem Builds both editions with the C# compiler that ships with Windows (.NET Framework 4.x):
rem   bin\DriveUP.exe      - drivers: updates, devices, install, backups
rem   bin\DriveUP_Pro.exe  - the same plus system tuning (src\Pro, compiled with /define:PRO)
rem   build.cmd       - release exes (ask for administrator rights on start: installing drivers needs them)
rem   build.cmd dev   - also DriveUP.dev.exe and DriveUP_Pro.dev.exe that run without elevation (for testing)
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
set "ICONSRC=tools\MakeIcon.cs src\Logo.cs src\Theme.cs src\Settings.cs src\Edition.cs"

rem 1. The .exe icons are drawn by the same code as the in-app logo (src\Logo.cs): violet, and crimson for Pro.
"%CSC%" %OPTS% /target:exe /out:obj\MakeIcon.exe %REFS% %ICONSRC%
if errorlevel 1 goto failed
obj\MakeIcon.exe obj\app.ico
if errorlevel 1 goto failed
"%CSC%" %OPTS% /define:PRO /target:exe /out:obj\MakeIconPro.exe %REFS% %ICONSRC%
if errorlevel 1 goto failed
obj\MakeIconPro.exe obj\app_pro.ico
if errorlevel 1 goto failed

rem 2. The applications: each one self-contained exe, nothing else is needed next to it.
"%CSC%" %OPTS% /target:winexe /out:bin\DriveUP.exe /win32manifest:app.manifest /win32icon:obj\app.ico %REFS% src\*.cs
if errorlevel 1 goto failed
"%CSC%" %OPTS% /define:PRO /target:winexe /out:bin\DriveUP_Pro.exe /win32manifest:app.manifest /win32icon:obj\app_pro.ico %REFS% src\*.cs src\Pro\*.cs
if errorlevel 1 goto failed

if /i "%~1"=="dev" (
  powershell -NoProfile -Command "(Get-Content app.manifest) -replace 'requireAdministrator','asInvoker' | Set-Content obj\dev.manifest -Encoding UTF8"
  "%CSC%" %OPTS% /target:winexe /out:bin\DriveUP.dev.exe /win32manifest:obj\dev.manifest /win32icon:obj\app.ico %REFS% src\*.cs
  if errorlevel 1 goto failed
  "%CSC%" %OPTS% /define:PRO /target:winexe /out:bin\DriveUP_Pro.dev.exe /win32manifest:obj\dev.manifest /win32icon:obj\app_pro.ico %REFS% src\*.cs src\Pro\*.cs
  if errorlevel 1 goto failed
)

echo Build OK: %CD%\bin\DriveUP.exe, %CD%\bin\DriveUP_Pro.exe
exit /b 0

:failed
echo Build FAILED
exit /b 1
