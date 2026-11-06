# Installation

Download the latest files from [Releases](https://github.com/NeoCyberin/NeoBar/releases):

- `NeoBar-x.y.z-setup.exe` - installer. Needs no administrator rights and installs for the current user.
- `NeoBar-x.y.z-portable.zip` - portable version. Extract the **whole archive** to any folder you can write to and run `NeoBar.exe`. Profiles and settings are kept next to the exe.
- `SHA256SUMS.txt` - checksums to verify the downloads.

Requirements: Windows 10 or 11 (64- or 32-bit) and .NET Framework 4.x, which is built into Windows.

## First start

A **Quick setup** window appears: choose the screen edge, behaviour (docked or auto-hide), theme, which widgets to put at the top, and whether to start with Windows, enable the game overlay and detect your location. Everything can be changed later in Settings.

## SmartScreen

A new program can trigger "Windows protected your PC". Check the file's signature (Properties → Digital Signatures), then **More info → Run anyway**. See [[FAQ]].

## Removing

Use the installer's uninstaller, or for the portable version delete the folder. `NeoBar.exe --uninstall` removes the traces NeoBar left in the system (autostart entry and the like).

Back: [[Home]]
