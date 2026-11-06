@echo off
rem NeoBar release: clean build -> (signing) -> installer + portable zip in dist\
rem Signing happens only if sign-config.bat exists.
rem Installer needs Inno Setup 6 (https://jrsoftware.org/isdl.php).
setlocal
cd /d "%~dp0"
for /f "tokens=3" %%v in ('findstr /b /c:"#define AppVersion" installer\NeoBar.iss') do set "VER=%%~v"
set "STAGE=dist\release\NeoBar"
if exist dist\release rmdir /s /q dist\release
call build.bat "%STAGE%" nopause || goto fail

set "SIGNED=0"
if exist sign-config.bat (
  call sign.bat "%STAGE%\NeoBar.exe" "%STAGE%\NeoBarFps32.dll" "%STAGE%\NeoBarFps64.dll" "%STAGE%\NeoBarWall32.dll" "%STAGE%\NeoBarWall64.dll" || goto fail
  set "SIGNED=1"
) else (
  echo.
  echo NOTE: sign-config.bat not found - building WITHOUT a signature.
)

set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo Inno Setup 6 not found - installer skipped.
  set "NOSETUP=1"
) else (
  if "%SIGNED%"=="1" (
    "%ISCC%" /Q "/DSIGN" "/Sneobar=cmd.exe /s /c $q$q%~dp0sign.bat$q $f$q" installer\NeoBar.iss || goto fail
  ) else (
    "%ISCC%" /Q installer\NeoBar.iss || goto fail
  )
)

set "ZIP=dist\NeoBar-%VER%-portable.zip"
if exist "%ZIP%" del "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path 'dist\release\NeoBar' -DestinationPath '%ZIP%'" || goto fail
rem checksums for the release page (users can verify downloads)
powershell -NoProfile -Command "Get-ChildItem dist -File -Filter 'NeoBar-%VER%-*' | Get-FileHash -Algorithm SHA256 | ForEach-Object { $_.Hash.ToLower() + '  ' + (Split-Path $_.Path -Leaf) } | Set-Content -Encoding ascii 'dist\SHA256SUMS.txt'"

echo.
if "%NOSETUP%"=="1" (
  echo DONE WITHOUT INSTALLER: %ZIP%, dist\SHA256SUMS.txt - install Inno Setup 6 and run again.
) else (
  echo OK: dist\NeoBar-%VER%-setup.exe, %ZIP%, dist\SHA256SUMS.txt
)
pause
exit /b 0

:fail
echo.
echo RELEASE FAILED
pause
exit /b 1
