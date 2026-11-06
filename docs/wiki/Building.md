# Building

- Windows 10/11. Run `build.bat`: it compiles with `csc.exe` from .NET Framework 4 (already in Windows), no Visual Studio needed. The program appears in `dist\NeoBar`.
- Native C++ libraries: `native\build-native.bat` (w64devkit) or `native/build.sh` (mingw-w64 on Linux). The built DLLs are committed in `bin/`, so rebuild them only if you change `native/`.
- `release.bat`: clean build, signing (if `sign-config.bat` exists), installer (Inno Setup) and the portable zip.
- Code rules: C# 5 only, every text through `L.T("…")`, safety first, nothing injected into anti-cheat processes, low resource usage. Details: [CONTRIBUTING](https://github.com/NeoCyberin/NeoBar/blob/main/CONTRIBUTING.md).

Back: [[Home]]
