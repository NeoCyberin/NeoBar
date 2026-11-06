@echo off
rem NeoBar: create your own (self-signed) code signing certificate and sign-config.bat.
rem Windows will not trust it on other computers; see docs/RELEASING.md.
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\make-cert.ps1"
echo.
pause
