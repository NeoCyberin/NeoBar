#!/bin/sh
# Збірка d3d8.dll (32-біт) з вихідників d3d8to9 (Crosire, BSD 2-clause) на Linux з mingw-w64
set -e
cd "$(dirname "$0")"
i686-w64-mingw32-g++ -std=c++17 -O2 -s -shared -DD3D8TO9NOLOG -Wno-delete-non-virtual-dtor -Wno-unknown-pragmas -static -static-libgcc -static-libstdc++ source/*.cpp res/d3d8.def -o d3d8.dll -Wl,--enable-stdcall-fixup -ld3d9 -lgdi32
echo OK
