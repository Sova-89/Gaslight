@echo off
rem Builds Gaslight.dll for Hollow Knight with the C# compiler that ships with Windows (.NET Framework 4).
rem
rem Usage:  build_hk.bat ["C:\path\to\Hollow Knight"]
rem Without an argument the HOLLOWKNIGHT environment variable is used, then the default Steam folder.
rem Result: out\hk\Gaslight.dll  (copy it, together with src\shared\report_template.html, to Managed\Mods\Gaslight\)
setlocal enabledelayedexpansion
set GAME=%~1
if "%GAME%"=="" set GAME=%HOLLOWKNIGHT%
if "%GAME%"=="" set GAME=%ProgramFiles(x86)%\Steam\steamapps\common\Hollow Knight
set MANAGED=%GAME%\hollow_knight_Data\Managed
if not exist "%MANAGED%\Assembly-CSharp.dll" goto nogame

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" goto nocsc

rem Reference every game DLL we need that exists in this install (the list differs a little between API versions).
set REFS=
for %%F in (mscorlib System System.Core netstandard UnityEngine UnityEngine.CoreModule UnityEngine.IMGUIModule UnityEngine.InputLegacyModule UnityEngine.TextRenderingModule Assembly-CSharp Assembly-CSharp-firstpass PlayMaker MonoMod.RuntimeDetour MonoMod.Utils MonoMod.Core MonoMod.Backports MonoMod.ILHelpers Mono.Cecil) do (
  if exist "!MANAGED!\%%F.dll" set REFS=!REFS! /r:"!MANAGED!\%%F.dll"
)

set SRC=%~dp0src
set SHARED="%SRC%\shared\KeyNames.cs" "%SRC%\shared\Lang.cs" "%SRC%\shared\Raster.cs" "%SRC%\shared\Settings.cs" "%SRC%\shared\Profiler.cs" "%SRC%\shared\HudLayout.cs" "%SRC%\shared\SettingsPanel.cs" "%SRC%\shared\ReportWriter.cs" "%SRC%\shared\ScanGuard.cs" "%SRC%\shared\IlScanner.cs" "%SRC%\shared\Hooker.cs" "%SRC%\shared\App.cs"
if not exist "%~dp0out\hk" mkdir "%~dp0out\hk"

"%CSC%" /nologo /langversion:5 /codepage:65001 /noconfig /nostdlib+ /target:library /optimize+ /out:"%~dp0out\hk\Gaslight.dll" !REFS! %SHARED% "%SRC%\hk\GaslightMod.cs"
if errorlevel 1 goto fail

echo.
echo Done: %~dp0out\hk\Gaslight.dll
echo Copy it together with src\shared\report_template.html to:
echo   %MANAGED%\Mods\Gaslight\
exit /b 0

:nogame
echo Cannot find Hollow Knight at "%GAME%".
echo Pass the game folder:  build_hk.bat "D:\Games\Hollow Knight"
exit /b 1
:nocsc
echo Cannot find csc.exe of .NET Framework 4 in %WINDIR%\Microsoft.NET.
exit /b 1
:fail
echo COMPILATION FAILED (see messages above).
exit /b 1
