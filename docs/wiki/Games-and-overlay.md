# Games and overlay

## Overlay

Stats drawn over a game: FPS, frame times, GPU/CPU load and temperature, VRAM, RAM and more, with optional graphs. Configure it in Settings → Game overlay:

- **General** - the main switch and hotkeys (show/hide overlay; frame limit on/off).
- **Look / Stats** - corner of the screen, vertical or horizontal layout, margin, background opacity, update interval, graph size and period, overlay theme, which stats to show and their order. Every game can have its own look.
- **Games** - the game list (*Find games* scans for installed games, or add an `.exe`).

FPS, CPU temperature and some other readings need administrator rights.

## Game integration and anti-cheat

*Game integration* injects the NeoBar library into the game process (hooks): frame limit, fullscreen replacement, drawing inside the frame. An anti-cheat may see this as tampering, and in an online game it can lead to an account ban.

- Integration is **on by default in games without anti-cheat and off in games with it.**
- NeoBar never injects into an anti-cheat game unless you turn integration on for that game and confirm the warning.
- **Without integration** the overlay is a normal window on top of the game (borderless window mode) and nothing is injected.
- The frame limit is off in all games until you enable it.

If you play only single-player or on a local server there is no risk.

Back: [[Home]]
