#!/bin/sh
# Збірка на Linux (mingw-w64). Під Windows - build-native.bat з w64devkit.
set -e
cd "$(dirname "$0")"
OUT=../bin
for A in 64 32; do
  if [ $A = 64 ]; then CC=x86_64-w64-mingw32-gcc; CXX=x86_64-w64-mingw32-g++; HDE=hde64; else CC=i686-w64-mingw32-gcc; CXX=i686-w64-mingw32-g++; HDE=hde32; fi
  rm -rf obj$A; mkdir obj$A
  for f in buffer hook trampoline; do $CC -c -O2 -Iminhook/include minhook/src/$f.c -o obj$A/$f.o; done
  $CC -c -O2 minhook/src/hde/$HDE.c -o obj$A/hde.o
  $CXX -c -O2 -std=c++17 -fno-exceptions -fno-rtti -Iminhook/include sbfps.cpp -o obj$A/sbfps.o
  $CXX -c -O2 -std=c++17 -fno-exceptions -fno-rtti sbd8.cpp -o obj$A/sbd8.o
  $CXX -c -O2 -std=c++17 -fno-exceptions -fno-rtti -Ivk sbvk.cpp -o obj$A/sbvk.o
  $CXX -shared -O2 -s -static -static-libgcc -static-libstdc++ obj$A/*.o -o $OUT/NeoBarFps$A.dll -Wl,--subsystem,windows -Wl,--kill-at -lgdi32
  rm -rf obj$A
  # рушій шпалер: DLL, що запускається окремим процесом через rundll32 (див. Wallpaper.cs)
  $CXX -shared -O2 -std=c++17 -fno-exceptions -fno-rtti sbwall.cpp -o $OUT/NeoBarWall$A.dll -s -static -static-libgcc -static-libstdc++ -Wl,--kill-at -ld3d11 -ld2d1 -ldxgi -lwindowscodecs -lole32 -loleaut32 -luuid -lgdi32 -luser32 -lshlwapi -lshell32
done
# помічник перетягування для NeoBar від адміністратора: 32-бітний, працює і в 64-бітній Windows
i686-w64-mingw32-g++ -O2 -std=c++17 -fno-exceptions -fno-rtti -municode -mwindows sbdrop.cpp -o $OUT/NeoBarDrop.exe -s -static -static-libgcc -static-libstdc++ -lshell32 -luser32 -lgdi32
echo OK
