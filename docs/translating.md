# Translating

Thank you for helping. The app's interface text is in one English file; a translation is a copy of it in your
language. You don't need to read or change any code.

## Ground rules

- **Your own words.** Translations must be your own work. Never copy text from another APRS program's translation or
  interface, and in particular never from APRSIS32 or its documentation: this project is a clean-room reimplementation
  ([clean-room.md](clean-room.md)), and copied text would break that.
- **The product name** is never written in a string. Where a sentence needs it, the English has `{product}`; keep the
  placeholder and the app fills in the name. This lets the product be renamed without touching translations
  ([branding.md](branding.md)).
- **Data stays as it is.** Callsigns, packet text, locators and coordinates aren't translated; the strings around them
  are.

## The files

- English, the source: `src/AprsMap.UI/Localization/Strings.resx`.
- A translation: `Strings.<language>.resx` in the same folder, for example `Strings.de.resx` (German) or
  `Strings.pt-BR.resx` (Brazilian Portuguese). Use the plain language code unless the regional variant really
  differs.

Each entry has a **key** (`Status.Packets`), the **value** to translate, and a **comment** saying where it appears and
what each placeholder holds:

```xml
<data name="Status.Packets" xml:space="preserve">
  <value>{count, plural, one {# packet} other {# packets}}</value>
  <comment>Status bar: packets received since the engine started. count: a number.</comment>
</data>
```

Translate only the `<value>`. Keep the key and leave the comment in English (or add a note of your own after it).
A translation can be incomplete: anything missing is shown in English.

## Placeholders and plurals

Strings use ICU MessageFormat:

- `{name}`: a value filled in by the app (a callsign, a server name). Keep it exactly, but move it wherever your
  grammar needs it.
- `{count, plural, one {…} other {…}}`: picks a form by number. Inside it, `#` is the number, already formatted for
  your language (`1.234` in German). Use the plural categories **your** language has (`zero`, `one`, `two`, `few`,
  `many`, `other`; see the [CLDR plural rules](https://www.unicode.org/cldr/charts/latest/supplemental/language_plural_rules.html)).
  `other` is always required.
- `{kind, select, station {…} object {…} other {…}}`: picks a form by a word. Keep the words before each `{`, and
  translate the text inside.
- Separators such as ` · ` are inside the strings so you can move or change them.

A test checks that every translation uses the same placeholders as the English and that every string formats, so a
mistake shows up in the pull request's checks.

## Words to keep or translate

| Term | Meaning | Guidance |
| --- | --- | --- |
| APRS | Automatic Packet Reporting System | Never translated. |
| APRS-IS | The internet network of APRS servers | Never translated. |
| callsign | An amateur radio station's licensed identifier | Use your amateur radio community's usual word. |
| SSID | The `-9` after a callsign, telling a person's stations apart | Usually kept as SSID. |
| passcode | The number that lets a callsign send on APRS-IS | Translate; it isn't a password for an account. |
| beacon | A station's regular position or status transmission | Use your community's usual word. |
| digipeater | A station that repeats packets on radio | Usually kept or shortened (digi). |
| IGate | A station that passes packets between radio and APRS-IS | Never translated. |
| Mic-E | A compact position format used by many radios | Never translated. |
| object, item | A position someone places on the map for something else (an event, a hazard) | Translate. |
| filter | What APRS-IS sends you, chosen by area or station | Translate. |
| profile | A saved set of station settings (callsign, server, location) | Translate. |

If your language's community uses a different word, use it and add it to this table in your pull request.

## Testing your translation

1. Build and run the desktop app (`dotnet run --project src/AprsMap.Desktop`), open **Settings › Appearance ›
   Language** and choose your language. Text changes at once.
2. Look for text that is cut off or wraps badly. Your language may be longer than English; the layouts allow for
   about 40 % more, and a test renders every screen at 200 % text size.
3. Run the tests (`dotnet test`); the localization tests check placeholders, plurals and missing keys.

The **pseudo-language** (`qps-ploc`, shown as brackets and accented letters in debug builds) is a tool for developers:
any plain English it reveals is text that can't be translated yet. Please report it as an issue.

## Adding a language

1. Copy `Strings.resx` to `Strings.<language>.resx` and translate the values.
2. Add the language code to `Localizer.Translations` in `src/AprsMap.UI/Localization/Localizer.cs` (one line; a test
   fails until you do), so the Language list offers it.
3. Open a pull request titled `feat(i18n): <language> translation`.

Languages written right to left (Arabic, Hebrew) and languages that need large fonts in the browser (Chinese,
Japanese, Korean) need extra work in the app first; open an issue before translating.

## Keeping up

New English strings arrive with new features, and show in English in your language until translated. To find them,
compare your file's keys with `Strings.resx` (a per-language report comes with the first translation). Translating through a hosted service (Weblate) may come later; until then,
pull requests are the way.
