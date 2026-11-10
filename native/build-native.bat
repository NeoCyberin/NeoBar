@echo off
rem Збірка NeoBarFps64.dll / NeoBarFps32.dll і NeoBarWall64.dll / NeoBarWall32.dll під Windows без Visual Studio.
rem Потрібен w64devkit (github.com/skeeto/w64devkit): x64-версія збирає 64-бітну DLL,
rem x86-версія - 32-бітну. Запустіть цей файл з консолі w64devkit або додайте його bin у PATH.
cd /d "%~dp0"
for /f %%m in ('gcc -dumpmachine') do set "MACH=%%m"
set "BITS=64"
set "HDE=hde64"
echo %MACH% | find "i686" >nul && (set "BITS=32" & set "HDE=hde32")
if not exist obj mkdir obj
gcc -c -O2 -Iminhook/include minhook/src/buffer.c -o obj/buffer.o || goto fail
gcc -c -O2 -Iminhook/include minhook/src/hook.c -o obj/hook.o || goto fail
gcc -c -O2 -Iminhook/include minhook/src/trampoline.c -o obj/trampoline.o || goto fail
gcc -c -O2 minhook/src/hde/%HDE%.c -o obj/hde.o || goto fail
g++ -c -O2 -std=c++17 -fno-exceptions -fno-rtti -Iminhook/include sbfps.cpp -o obj/sbfps.o || goto fail
g++ -c -O2 -std=c++17 -fno-exceptions -fno-rtti sbd8.cpp -o obj/sbd8.o || goto fail
g++ -c -O2 -std=c++17 -fno-exceptions -fno-rtti -Ivk sbvk.cpp -o obj/sbvk.o || goto fail
g++ -shared -O2 -s -static -static-libgcc -static-libstdc++ obj/*.o -o ../bin/NeoBarFps%BITS%.dll -Wl,--kill-at -lgdi32 || goto fail
rem рушій шпалер: DLL, NeoBar запускає її окремим процесом через rundll32
g++ -shared -O2 -std=c++17 -fno-exceptions -fno-rtti sbwall.cpp -o ../bin/NeoBarWall%BITS%.dll -s -static -static-libgcc -static-libstdc++ -Wl,--kill-at -ld3d11 -ld2d1 -ldxgi -lwindowscodecs -lole32 -loleaut32 -luuid -lgdi32 -luser32 -lshlwapi -lshell32 || goto fail
rem помічник перетягування (NeoBar від адміністратора): 32-бітний, збирається x86-версією w64devkit
if "%BITS%"=="32" g++ -O2 -std=c++17 -fno-exceptions -fno-rtti -municode -mwindows sbdrop.cpp -o ../bin/NeoBarDrop.exe -s -static -static-libgcc -static-libstdc++ -lshell32 -luser32 -lgdi32 || goto fail
rmdir /s /q obj
echo OK: NeoBarFps%BITS%.dll, NeoBarWall%BITS%.dll
pause
exit /b 0
:fail
echo BUILD FAILED
pause
exit /b 1
