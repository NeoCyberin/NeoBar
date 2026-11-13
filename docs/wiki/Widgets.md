# Widgets

Widgets sit at the top of the panel; click one to open its window. Order them with ▲ ▼ or drag; the eye in the list hides a widget from the panel (it can still be shown on the desktop).

| Widget | What it does |
|---|---|
| **Time & date** | Clock, calendar, public holidays (from date.nager.at) and world days, your own dates, alarms, timers, stopwatch, desktop clocks (digital, analog, calendar). Clock accuracy can be checked against a time server. |
| **Weather** | Current weather, hourly and weekly forecast, weather themes, European/American units. |
| **System monitor** | CPU, GPU, RAM, with graphs; specs; *Tasks* - processes and services; *Analysis* - one process's load over time. Temperatures can be shown in the tray. |
| **Network** | Download/upload speed, ping, adapters, Wi-Fi, firewall switch. |
| **Storage** | Drives with free space; health (SMART) and maintenance; formatting; file recovery. |
| **Battery** | Charge and power state (shown only on devices with a battery). |
| **Notes** | Notes on the desktop; they can be locked in place and saved to a file (.txt or .rtf - header menu). |
| **Palette** | Eyedropper, colour models (HEX and others), swatches, one click copies HEX. |
| **Dice** | d4-d20, several dice at once, your own names for the sides, history. |
| **QR code** | Text, link, Wi-Fi, contact, e-mail, SMS, phone, location; error correction level; logo in the centre; save as PNG or SVG. |
| **Security** | Checks processes, autostart, services and settings. See [[Tools]]. |
| **Encryption** | Encrypt text and files, hashes. |
| **Cleanup** | Windows junk cleanup. |
| **Recovery** | Recover deleted files and folders from a drive or USB stick. |

## Widgets on the desktop

Most widgets can be put on the desktop (the screen button in the widget's window). Drag a widget to move it, the corner to resize it: the size has limits, so the small captions always stay readable. The lock pins it in place (clicks go through to the desktop); the slider sets transparency. Large captions (time, temperature, totals) use the theme's notes font - Settings → Appearance → *Notes font in desktop widgets* turns this off.

## Process analysis (System monitor → Analysis)

Pick a process in *Tasks* and press **Analysis**. A window in the middle asks how often to sample (0.5-10 s) and where to save the log, then **Start recording**. While recording you see a live graph; afterwards a player: drag along the graph or press play (speed is a slider, ×1 - ×600), and the table shows the value at that moment and the minimum, maximum, average and 95th percentile for every metric. The player row tells you what you are looking at: a red **Live** button with a blinking dot - recording is running and the graph moves; a plain button with a red ring and "viewing the recording" - you scrubbed back (click to go back live); when not recording - *Recording finished* or *Saved log*.

Metrics (each has a tooltip explaining it in plain words):

| Metric | Meaning |
|---|---|
| **CPU** | share of the whole processor (all cores) used by the process |
| **GPU** | graphics card load from this process, as in Task Manager |
| **Video memory** | graphics card memory used by the process |
| **RAM** | memory the process occupies right now |
| **Committed** | memory the process reserved just for itself (including the page file); steady growth means a memory leak |
| **Read / Write** | data per second the process reads and writes (disks, network, devices) |
| **Handles** | open files, registry keys, threads; steady growth means a leak |
| **GDI / USER** | drawing objects and windows; Windows allows up to 10,000 per process |

Every sample is written to a CSV log right away (it opens in Excel), and a summary goes next to it as a .txt file. Recording continues when the monitor window is closed and stops by itself when the process exits. *Open log…* plays back an earlier recording; the ‹ › arrows step through logs in the same folder, the bin deletes the shown log with its summary (to the Windows Recycle Bin), and the cross removes the results from the window (the files stay).

Hover the graph to see the exact value and time. Minimum and maximum are marked when the line really changes (a zero minimum is not marked). Old logs are deleted automatically by the common setting **Settings → Diagnostics → Log retention**; the same page has *Open* and *Clear* for all analysis recordings.

## Location and weather (Settings → Location & weather)

NeoBar can detect your location with the Windows location service or, if that is off, by IP address (city-level accuracy). Without permission you set the city by hand: type 3+ letters, or click on the map (OpenStreetMap). The time zone can follow the location. Downloaded data (weather, holidays, map tiles, icons) is cached; without internet the saved data is shown.

Back: [[Home]]
