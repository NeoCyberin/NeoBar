# Translating

1. In NeoBar: Settings → Appearance → **Create template**. A `lang\template.lang` file appears next to `NeoBar.exe`, with every text in Ukrainian and an English hint above it.
2. Copy it to `lang\<code>.lang` (for example `nl.lang`) and set `@name`, `@code`, `@flag` and `@quotes` at the top:
   - `@flag h #FFFFFF #888888` - horizontal stripes top to bottom (`v` = vertical), colours as `#RRGGBB`;
   - `@quotes « »` - the quote marks of your language; `«»` in the texts are replaced by them.
3. Write the translation after each `>` line. Empty lines fall back to English. Keep `\n`, `{0}` and symbols such as `•` and `%`.
4. Restart NeoBar and pick the language in the settings.
5. To ship it with NeoBar, put the file in `res/lang/` and open a pull request; `build.bat` embeds every file from that folder.

Letters with diacritics are supported by the bundled fonts for Latin-script languages.

Back: [[Home]]
