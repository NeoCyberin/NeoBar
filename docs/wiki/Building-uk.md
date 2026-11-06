# Збірка

- Windows 10/11. Запустіть `build.bat`: він компілює `csc.exe` з .NET Framework 4 (уже є у Windows), Visual Studio не потрібна. Програма з'являється в `dist\NeoBar`.
- Нативні бібліотеки C++: `native\build-native.bat` (w64devkit) або `native/build.sh` (mingw-w64 під Linux). Зібрані DLL лежать у `bin/` у репозиторії, тож перезбирайте їх, лише змінивши `native/`.
- `release.bat`: чиста збірка, підпис (якщо є `sign-config.bat`), встановлювач (Inno Setup) і портативний zip.
- Правила коду: лише C# 5, усі тексти через `L.T("…")`, безпека понад усе, нічого не вбудовувати в процеси з античітом, мале споживання ресурсів. Деталі: [CONTRIBUTING](https://github.com/NeoCyberin/NeoBar/blob/main/CONTRIBUTING.md).

Назад: [[Home-uk]]
