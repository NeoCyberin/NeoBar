# Panel and items

## Position and behaviour (Settings → Panel)

- **Screen edge:** left, right, top or bottom, on any monitor. Width of a left/right panel: 56-240 px.
- **Docked:** other windows adapt to the panel. **Auto-hide:** the panel lies over the windows and appears when the mouse reaches the edge. The *compact panel* option (sized to the content, centred, rounded) is for a top/bottom panel in auto-hide mode.
- **Games and fullscreen programs:** the panel hides, search is disabled and everything else pauses; only the game overlay keeps working.
- Start with Windows; run always as administrator (no UAC prompt at start).

## Items (Settings → Items)

Programs, folders, sites, groups, separators and widgets. Add them with the buttons under the list, or **drag a shortcut, folder or link onto the panel**. *Shortcuts…* imports from your desktop, *System…* adds This PC, Recycle Bin, your user folders and Windows settings.

- **Order:** ▲ ▼ buttons or drag. Widgets always stay at the top of the panel.
- **Icon:** automatic (from file or folder), drawn in the theme's style, downloaded once from the site, or your own file (.ico, .png, .jpg, .bmp, .exe, .dll).
- **Groups:** a header with the items below it, either expanded or as a compact 2×2 tile with thumbnails; each group can have its own colour.
- **Search tags:** comma-separated words that help you find the item.

## Appearance (Settings → Appearance)

Icon size 24-96 px, labels below/above/hidden, hover effect (zoom, lift, glow), smooth animations, label font, theme. See [[Wallpapers and themes]].

## Search

A search box on the panel and a global hotkey (Settings → Search). The *Computer* tab finds all files and folders: an NTFS index when NeoBar runs as administrator, otherwise Windows Search. The NTFS index keeps itself up to date from the drive's change journal and frees its memory after 10 minutes without searching. NeoBar can also suggest programs and games that are not on the panel yet.

In the *Panel* tab the search box is also a calculator: `2+2*3`, `sqrt(2)`, `15% of 80`, and unit conversion: `10 km in mi`, `100 f to c`, `2 gb to mb`, `90 km/h to m/s`. Enter copies the result.

## Profiles

Each profile has its own items, settings, notes, game list and theme. A profile may be protected with a PIN (deleting it asks for the PIN). Settings → Data: export a profile to a `.nbpack` file, import it, or reset to defaults (the old copy is kept as `neobar.ini.old`).

Back: [[Home]]
