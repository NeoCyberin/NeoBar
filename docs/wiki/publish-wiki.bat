@echo off
rem Publishes docs\wiki\*.md to the GitHub wiki of this repository.
rem Before the first run: GitHub -> repository -> Wiki -> "Create the first page" (save any text) - this creates the wiki repository.
setlocal
cd /d "%~dp0"
set "WIKI=https://github.com/NeoCyberin/NeoBar.wiki.git"
set "TMPW=%TEMP%\neobar-wiki-publish"
if exist "%TMPW%" rmdir /s /q "%TMPW%"
git clone "%WIKI%" "%TMPW%"
if errorlevel 1 (echo Could not clone the wiki. Create its first page on GitHub first. & pause & exit /b 1)
copy /y "%~dp0*.md" "%TMPW%\" >nul
cd /d "%TMPW%"
git add -A
git commit -m "Update wiki"
git push
if errorlevel 1 (echo Push failed. & pause & exit /b 1)
echo Done: https://github.com/NeoCyberin/NeoBar/wiki
pause
