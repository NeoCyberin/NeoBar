# FAQ

**Windows says "Windows protected your PC" (SmartScreen).**
A new program has no reputation yet. Check Properties → Digital Signatures, then More info → Run anyway. Releases include `SHA256SUMS.txt`. The signature is made with the author's own certificate, so Windows shows the publisher as unknown on other PCs - that is expected. Never import the certificate into your system.

**Does NeoBar need administrator rights?**
No for everyday use. FPS in games, CPU temperature, disk maintenance, the NTFS file index and time sync need them; NeoBar asks only when needed, or can always start elevated (Settings → Panel). In that mode the program folder is protected: only an administrator can change NeoBar's files, otherwise any program could replace NeoBar and gain rights without a prompt.

**Is it safe to use in online games?**
By default NeoBar does not inject into anti-cheat games; the overlay is then a window above the game. See [[Games and overlay]].

**How much does it use?**
It is designed to idle: no busy timers, slow work in the background, logs only when you turn them on.

**Where do I find logs?**
Settings → Diagnostics. A switched-on log writes files up to 2 MB; a switched-off one writes nothing. The error log is always written - send it together with a description of the problem. *Keep logs* sets how long all logs (troubleshooting, errors, process analysis) are kept: all, 7/30/90 days or the last 10/50/200 launches; 30 days by default.

**The PC froze, then "NeoBarWall: Application Error" appeared.**
That happens after a graphics driver hang (Windows restarts the driver - TDR): the live wallpaper engine lost the GPU and crashed. Since 1.8.0 the engine notices the GPU reset, quits quietly without an error window and starts again a few seconds later; GPU sensors reconnect too. Sensor polling runs in the background, so a hung driver no longer stops the panel. Settings → Diagnostics → "System stability" shows what happened (graphics driver hangs, memory shortages, sudden shutdowns over the past week), and the error log records when the system or the panel stood still. Frequent driver hangs are most often overheating, overclocking or an unstable driver.

**How do I reset or move my settings?**
Settings → Data: export/import a profile (`.nbpack`) or reset it; the old copy stays as `neobar.ini.bak`.

**The panel does not appear in a fullscreen game.**
That is intentional (Settings → Panel → Games and fullscreen programs).

**How do I report a bug?**
Open an [issue](https://github.com/NeoCyberin/NeoBar/issues) with your Windows version, what you did, and the error log.

Back: [[Home]]
