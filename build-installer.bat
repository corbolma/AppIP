@echo off
rem Crea dist\AppIP-Setup-X.Y.Z.exe: compila AppIP.exe e lo incorpora nell'installer.
rem   build-installer.bat            -> usa la versione attuale di Version.cs
rem   build-installer.bat patch      -> incrementa 1.0.0 -> 1.0.1 (anche: minor, major)
rem   build-installer.bat 2.0.0      -> imposta una versione esplicita
setlocal
cd /d "%~dp0"

if not "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bump-version.ps1" %1 || exit /b 1
)
for /f "delims=" %%V in ('powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bump-version.ps1" show') do set VER=%%V

call "%~dp0build.bat" || exit /b 1

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist dist mkdir dist

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:dist\AppIP-Setup-%VER%.exe ^
  /win32icon:appip.ico /win32manifest:setup.manifest /resource:AppIP.exe,AppIP.payload ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.CSharp.dll ^
  Setup.cs Version.cs
if errorlevel 1 (
  echo Compilazione installer fallita.
  exit /b 1
)
echo Creato dist\AppIP-Setup-%VER%.exe
