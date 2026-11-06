@echo off
rem NeoBar: sign files with your code signing certificate (signtool from the Windows SDK).
rem   sign.bat file1 [file2 ...]
rem Settings are read from sign-config.bat (copy sign-config.example.bat and fill it in).
setlocal
cd /d "%~dp0"
if not exist sign-config.bat (
  echo sign-config.bat not found - copy sign-config.example.bat to sign-config.bat and fill it in.
  exit /b 1
)
call sign-config.bat
if "%SIGNTOOL%"=="" call :findsigntool
if not exist "%SIGNTOOL%" (
  echo signtool.exe not found. Install "Windows SDK Signing Tools for Desktop Apps" or set SIGNTOOL in sign-config.bat.
  exit /b 1
)
if "%SIGN_TIMESTAMP%"=="" set "SIGN_TIMESTAMP=http://time.certum.pl"
:next
if "%~1"=="" exit /b 0
echo Signing %~nx1
if not "%SIGN_THUMBPRINT%"=="" (
  "%SIGNTOOL%" sign /sha1 %SIGN_THUMBPRINT% /fd sha256 /tr "%SIGN_TIMESTAMP%" /td sha256 /d "NeoBar" "%~1"
) else (
  "%SIGNTOOL%" sign /n "%SIGN_SUBJECT%" /fd sha256 /tr "%SIGN_TIMESTAMP%" /td sha256 /d "NeoBar" "%~1"
)
if errorlevel 1 (
  echo SIGNING FAILED: %~1
  exit /b 1
)
shift
goto next

:findsigntool
for /f "delims=" %%d in ('dir /b /ad /o-n "%ProgramFiles(x86)%\Windows Kits\10\bin\10.*" 2^>nul') do (
  if exist "%ProgramFiles(x86)%\Windows Kits\10\bin\%%d\x64\signtool.exe" (
    set "SIGNTOOL=%ProgramFiles(x86)%\Windows Kits\10\bin\%%d\x64\signtool.exe"
    exit /b 0
  )
)
exit /b 0
