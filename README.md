# NeoBar

<p align="center"><img src="docs/images/banner.png" alt="NeoBar" width="100%"></p>

A side panel for Windows 10/11: launch programs and games, widgets, desktop notes, an in-game overlay, and tools for drives, network and security.

[Українська](README.uk.md)

## Features

- **Panel** - programs, folders, sites and groups at any screen edge, docked or auto-hide; drag files onto icons; profiles with a PIN.
- **Search** - panel items and every file on the computer (NTFS index), a calculator and unit conversion.
- **Time & weather** - clock and calendar with holidays, stopwatch, timers, alarms; weather now, hourly and for several days. The clock, weather, monitor, network and drives can also live on the desktop.
- **System** - monitor (CPU, GPU, memory, temperatures), specs, processes and services, app load analysis with a log, daily history; devices and drivers; network, Wi-Fi and firewall; battery; security check. System watch warns about overheating and low memory and writes an after-game report.
- **Drives** - free space, health (SMART), speed, check and format; Windows junk cleanup; recovery of deleted files.
- **Tools** - desktop notes, color picker, QR codes, file encryption and hashes, dice.
- **Games** - overlay with FPS, frame times and sensors, frame limiter, borderless fullscreen. In game mode only the overlay, the limiter and system watch keep running; anti-cheat games are left alone unless you allow it.
- **Wallpapers** - images, slideshows with transitions, live (video / GIF) wallpapers; paused while a game runs.
- **Themes & languages** - 9 themes, a custom one, festive themes; interface in 9 languages (Ukrainian, English, German, French, Spanish, Italian, Czech, Polish, Brazilian Portuguese) and your own translations.

## Screenshots

<p align="center">
  <img src="docs/images/quick-setup.png" alt="Quick setup" width="49%">
  <img src="docs/images/clock-light.png" alt="Clock and calendar" width="49%">
</p>

<p align="center"><img src="docs/images/settings-dark.png" alt="Settings: items of the panel" width="100%"></p>

### Themes

Nine built-in themes (dark, light, glass, cyberpunk, steampunk, urban, glamour, nature, artistic), a custom one and festive themes.

<p align="center"><img src="docs/images/themes.png" alt="Themes" width="100%"></p>

### Languages

Ukrainian, English, German, French, Spanish, Italian, Czech, Polish and Brazilian Portuguese - or add your own.

<p align="center"><img src="docs/images/languages.png" alt="Languages" width="100%"></p>

## Download and install

Get the latest version from the [Releases](../../releases) page:

- `NeoBar-x.y.z-setup.exe` - installer (no administrator rights needed, installs for the current user);
- `NeoBar-x.y.z-portable.zip` - portable version: extract to any writable folder and run `NeoBar.exe`.

`SHA256SUMS.txt` on the release page lets you check the downloaded files.
NeoBar can also update itself: it offers a newer release at startup and, only after your consent, downloads it from GitHub, verifies the checksum, installs it and restarts.
Requirements: Windows 10 or 11 (64- or 32-bit), .NET Framework 4.x (built into Windows).

A new program may trigger a SmartScreen warning ("Windows protected your PC") until it gains reputation:
check that the file is signed by the author (Properties → Digital Signatures), then click **More info → Run anyway**.

Some features (FPS in games, CPU temperature, disk maintenance) need administrator rights;
NeoBar asks for them only when needed, or can always start elevated (Settings → Panel → Startup).

## Build from source

See [CONTRIBUTING.md](CONTRIBUTING.md). In short: run `build.bat` on Windows - the program appears in `dist\NeoBar`.

## Contributing and translations

Bug reports, ideas, code and translations are welcome - see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

NeoBar is free software under the [GNU General Public License v3.0](LICENSE): you can use, study, change and share it;
changed versions must stay under the same license with their source code.
Third-party components and their licenses: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
