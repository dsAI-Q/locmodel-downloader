@echo off
rem Build LocModelDownloader.exe with the csc shipped in .NET Framework 4.x
rem No Visual Studio required.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo ERROR: csc.exe not found: %CSC%
  echo Please install .NET Framework 4.x
  pause
  exit /b 1
)

set "ICON="
if exist "app.ico" set "ICON=/win32icon:app.ico"

echo Building...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ %ICON% /out:LocModelDownloader.exe /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll LocModelDownloader.cs

if errorlevel 1 (
  echo.
  echo BUILD FAILED
  pause
  exit /b 1
)

echo.
echo BUILD OK: LocModelDownloader.exe
echo Put aria2c.exe in the same folder before first run.
pause
