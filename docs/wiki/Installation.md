# Installation

Download the latest files from [Releases](https://github.com/NeoCyberin/NeoBar/releases):

- `NeoBar-x.y.z-setup.exe` - installer. Needs no administrator rights and installs for the current user.
- `NeoBar-x.y.z-portable.zip` - portable version. Extract the **whole archive** to any folder you can write to and run `NeoBar.exe`. Profiles and settings are kept next to the exe.
- `SHA256SUMS.txt` - checksums to verify the downloads.

## Updating

At startup NeoBar checks GitHub for a newer release (Settings → Diagnostics → Updates) and offers to update. Only after you agree it downloads the matching file (installer or portable archive), verifies its SHA-256 against `SHA256SUMS.txt` and its digital signature (the same certificate as the installed version), closes, installs the new version and starts again. Profiles and settings are kept. If the portable folder is write-protected, Windows asks for administrator confirmation once.

Requirements: Windows 10 or 11 (64- or 32-bit) and .NET Framework 4.x, which is built into Windows.

## First start

A **Quick setup** window appears: choose the screen edge, behaviour (docked or auto-hide), theme, which widgets to put at the top, and whether to start with Windows, enable the game overlay and detect your location. Everything can be changed later in Settings.

## SmartScreen

A new program can trigger "Windows protected your PC". Check the file's signature (Properties → Digital Signatures), then **More info → Run anyway**. See [[FAQ]].

## Removing

Use the installer's uninstaller, or for the portable version delete the folder. `NeoBar.exe --uninstall` removes the traces NeoBar left in the system (autostart entry and the like).

Back: [[Home]]
