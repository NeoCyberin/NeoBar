# Tools

Every tool that changes the system shows what it will change first and, where possible, keeps a way back (backup/undo).

- **Cleanup** - finds Windows junk and shows how much will be freed; the Recycle Bin is only cleaned if you select it. NeoBar does not clean the registry: it brings no speed-up and can break programs.
- **File recovery** - pick a drive or USB stick and find deleted files and folders. Recover to a *different* drive than the one you scan.
- **Disks** - free space, health (SMART), maintenance and formatting. Some actions need administrator rights.
- **Firewall and Wi-Fi** - switch Windows Firewall on/off (it asks first; turn it off only briefly) and look up the current network. Windows shows a Wi-Fi password only to an administrator.
- **Security check** - looks at running processes, autostart, services and settings, and marks items as suspicious or worth checking. It tells you what it found; fixing anything is your decision.
- **Encryption** - encrypt text and files (AES-256, key from the password via PBKDF2, 600,000 iterations), MD5/SHA/CRC32 hashes; you can drag a file into the window.
- **Tasks** (System monitor) - processes and services in groups, what controls them, search by name or PID; ending with their dependencies, restart (Explorer too - open programs keep running), service start type.
- **Analysis** (System monitor) - records one process's load over time with a full CSV log, graph player and min/max/average; see [[Widgets]].

Back: [[Home]]
