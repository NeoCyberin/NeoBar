# Tools

Every tool that changes the system shows what it will change first and, where possible, keeps a way back (backup/undo).

- **Cleanup** (Storage → Cleanup) - finds Windows junk and shows how much will be freed; the Recycle Bin is only cleaned if you select it. NeoBar does not clean the registry: it brings no speed-up and can break programs.
- **File recovery** (Storage → Recovery) - pick a drive or USB stick and find deleted files and folders. Recover to a *different* drive than the one you scan.
- **Disks** - free space, health (SMART), maintenance and formatting. Some actions need administrator rights.
- **Firewall and Wi-Fi** - switch Windows Firewall on/off (it asks first; turn it off only briefly) and look up the current network. Windows shows a Wi-Fi password only to an administrator.
- **Security check** - looks at running processes, autostart, services and settings, and marks items as suspicious or worth checking. It tells you what it found; fixing anything is your decision.
- **Encryption** (Security → Encryption) - hashes (MD5/SHA/CRC32, compare with the sum from a website), text and file encryption (AES-256, key from the password via PBKDF2, 600,000 iterations; `.nbenc` or a ZIP that 7-Zip and WinRAR open); you can drag a file into the window.
- **Safe** (Security → Encryption → Safe) - a folder where files are kept only encrypted (AES-256 + HMAC-SHA256, names too); unlocked with a password, which can't be recovered. Add files by dragging, with the buttons or via *Add to safe* in a file's context menu; optionally delete the originals after the copy is verified. *Open* decrypts a temporary copy that is erased when the safe locks; the safe locks itself when the window closes, the screen turns off or after 10 min without activity.
- **Tasks** (System monitor) - processes and services in groups, what controls them, search by name or PID (after three letters it suggests processes and services; ↑ ↓ and Enter jump to them); ending with their dependencies (the whole group is frozen first, so programs like Steam cannot relaunch what was just ended), restart (Explorer too - open programs keep running), service start type.
- **Analysis** (System monitor) - records one process's load over time with a full CSV log, graph player and min/max/average; see [[Widgets]].
- **Devices and drivers** (System monitor → Devices) - NeoBar's own device manager: a list by class without duplicates (identical devices are folded into one "×N" row) and without service clutter; filters Main, Need attention (no driver, won't start, stopped by Windows - with an explanation and advice), Disabled, Disconnected, All. Actions: disable/enable, remove device (the driver stays, Windows installs it again), delete driver, remove all disconnected, Scan for hardware changes. The Driver store tab lists third-party driver packages (size, who uses them) and deletes outdated versions no connected device uses. The Updates tab finds drivers in Windows Update and installs them. Changes are made by the built-in pnputil; devices critical for Windows startup are never disabled or removed, and drivers built into Windows are never deleted.

Back: [[Home]]
