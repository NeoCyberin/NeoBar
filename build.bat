@echo off
rem NeoBar: compile NeoBar.exe and collect everything it needs into one folder.
rem   build.bat                 -> dist\NeoBar (for your own use; profiles there are kept)
rem   build.bat <folder> nopause -> clean build into <folder> (used by release.bat)
setlocal
cd /d "%~dp0"
set "OUT=%~1"
if "%OUT%"=="" set "OUT=dist\NeoBar"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%OUT%" mkdir "%OUT%"
rem translations: every res\lang\*.lang is embedded as resource lang.<file>
set "LANGRES="
for %%f in (res\lang\*.lang) do call set "LANGRES=%%LANGRES%% /resource:res\lang\%%~nxf,lang.%%~nxf"

"%CSC%" /nologo /optimize+ /target:winexe /codepage:65001 /out:"%OUT%\NeoBar.exe" ^
  /win32manifest:res\NeoBar.manifest /win32icon:res\NeoBar.ico ^
  /resource:res\NeoBar.ico,NeoBar.ico /resource:res\sbfonts.bin,sbfonts.bin /resource:res\emoji.bin,emoji.bin %LANGRES% ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Data.dll /r:Microsoft.CSharp.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /recurse:src\*.cs
if errorlevel 1 goto fail

copy /y bin\NeoBarFps32.dll "%OUT%\" >nul || goto fail
copy /y bin\NeoBarFps64.dll "%OUT%\" >nul || goto fail
copy /y bin\NeoBarWall32.dll "%OUT%\" >nul || goto fail
copy /y bin\NeoBarWall64.dll "%OUT%\" >nul || goto fail
copy /y bin\NeoBarDrop.exe "%OUT%\" >nul || goto fail
if not exist "%OUT%\d3d8to9" mkdir "%OUT%\d3d8to9"
copy /y third-party\d3d8to9\d3d8.dll "%OUT%\d3d8to9\" >nul || goto fail
copy /y third-party\d3d8to9\LICENSE.md "%OUT%\d3d8to9\" >nul
copy /y third-party\d3d8to9\README.txt "%OUT%\d3d8to9\" >nul
if not exist "%OUT%\cnc-ddraw" mkdir "%OUT%\cnc-ddraw"
copy /y third-party\cnc-ddraw\ddraw.dll "%OUT%\cnc-ddraw\" >nul || goto fail
copy /y third-party\cnc-ddraw\LICENSE "%OUT%\cnc-ddraw\" >nul
copy /y third-party\cnc-ddraw\README.txt "%OUT%\cnc-ddraw\" >nul
copy /y THIRD-PARTY-NOTICES.txt "%OUT%\" >nul
if exist LICENSE copy /y LICENSE "%OUT%\LICENSE.txt" >nul

echo.
echo OK: %OUT%\NeoBar.exe
if /i not "%~2"=="nopause" pause
exit /b 0

:fail
echo.
echo BUILD FAILED
if /i not "%~2"=="nopause" pause
exit /b 1
