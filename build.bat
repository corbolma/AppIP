@echo off
rem Compila AppIP.exe usando il compilatore C# incluso in Windows (.NET Framework 4.x)
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Compilatore csc.exe non trovato.
  exit /b 1
)
for %%I in ("%CSC%") do set WPF=%%~dpIWPF
cd /d "%~dp0"
if not exist appip.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1
rem Traduzioni: ogni lang\*.txt (tranne _template.txt) viene incorporato nell'exe
set LANGRES=
for %%F in (lang\*.txt) do if /i not "%%~nF"=="_template" call set LANGRES=%%LANGRES%% /resource:"%%F",AppIP.lang.%%~nF.txt
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:AppIP.exe ^
  /win32icon:appip.ico /resource:appip.ico,AppIP.appip.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" /r:System.Xaml.dll ^
  AppIP.cs SpeedTest.cs Version.cs Lang.cs %LANGRES%
if errorlevel 1 (
  echo Compilazione fallita.
  exit /b 1
)
echo Creato AppIP.exe
