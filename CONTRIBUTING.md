# Contributing to NeoBar

Thank you for helping! Issues and pull requests are welcome in English or Ukrainian.

## Build

- Windows 10/11. `build.bat` compiles with `csc.exe` from .NET Framework 4 (already in Windows) - no Visual Studio needed.
  The program appears in `dist\NeoBar` (run it from there; profiles are created next to the exe).
- Native libraries (`native/`, C++): `native\build-native.bat` with [w64devkit](https://github.com/skeeto/w64devkit),
  or `native/build.sh` on Linux with mingw-w64. Built DLLs go to `bin/` and are committed, so you only need this when you change `native/`.
- Every push and pull request is built by GitHub Actions; the unsigned build is attached to the run as an artifact.

## Code rules

- **C# 5 only**: no `?.`, no string interpolation `$"…"`, no newer features - `build.bat` uses the old compiler on purpose (works on every Windows without installing anything).
- **Every user-visible text goes through `L.T("…")`** (the Ukrainian text is the translation key). For values inside a sentence use `L.F("… {0} …", value)`.
- **Safety first**: nothing that changes the system (registry, files, services, firewall, drivers…) without the user's explicit confirmation; show what will change and keep a way back (backup / undo) where possible.
- **Games**: never inject into a process protected by anti-cheat unless the user explicitly turned on integration for that game.
- **Low resource usage**: no busy timers, do slow work in the background, logs only when the user enabled them.
- Code comments are mostly in Ukrainian; new comments may be in English.

## Translations

1. In NeoBar: Settings → Appearance → **Create template**. The `lang\template.lang` file appears next to `NeoBar.exe`,
   with every text in Ukrainian and an English hint above it.
2. Copy it to `lang\<code>.lang` (e.g. `nl.lang`), set `@name`, `@code`, `@flag` and `@quotes` at the top,
   and write the translation after each `>` line. Empty lines fall back to English.
3. Restart NeoBar and pick the language in the settings. Keep `\n`, `{0}` and symbols like `•`, `%`, `«»` (they are replaced by your `@quotes`).
4. To include it in NeoBar, put the file in `res/lang/` and open a pull request (`build.bat` embeds every file from that folder).

## Pull requests

- One topic per pull request; describe what and why, and how you tested it (Windows version).
- Make sure `build.bat` builds without errors.
- By contributing you agree that your contribution is licensed under the GPL-3.0, like the rest of NeoBar.
