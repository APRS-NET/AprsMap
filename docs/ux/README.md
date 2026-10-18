# UX direction

How AprsMap meets the needs in the [functional inventory](../reference/functional-inventory.md) with a modern,
cross-platform UI. The reference's capability set is the bar. Its menu tree is not: it grew on 240×320 Windows Mobile
screens, so it puts nearly everything in one menu bar with 20 Configure submenus. We keep the functions and redesign
how users get to them.

## Principles

1. **Map first.** The map is always the main surface. Everything else is a pane, panel or sheet over or beside it.
2. **Find any command by typing.** A command palette (Ctrl/Cmd+K) reaches every action and every settings page, and
   also searches stations. Deep menus aren't needed.
3. **State is always visible.** Connection health per port, TX armed or not, unread messages, and GPS fix live in the top
   bar, not in a status corner you have to know about.
4. **Transmitting is deliberate and obvious.** A clear TX-armed indicator, a first-beacon interlock, and a gateway login
   are required before anything leaves the station.
5. **Never block the UI.** Every command is an async, cancellable `IAsyncRelayCommand`. Long work shows progress inline,
   not in a modal.
6. **One layout system for every screen size.** The same views reflow from a wide desktop (docked panes) to a phone
   browser (bottom sheets) through Avalonia container queries. There are no separate "mobile" views.
7. **Settings are searchable pages with good defaults**, not dialogs. Changes apply live, with Undo where it's cheap.
8. **Context first.** Right-click (or long-press) on the map, a station or a packet offers the relevant actions.
   Global menus are the fallback.
9. **Appearance is the user's choice** (see [Appearance and accessibility](#appearance-and-accessibility)): dark,
   light or automatic; text and icon size; corner style; standard or accessible colours. Keyboard navigable,
   screen-reader labels, colour never the only signal (the reference's scroller colours become colour + icon + tooltip).
10. **Quiet, utilitarian look** (see [Visual style](#visual-style)). An operating tool, not a showcase.

## Visual style

The look is calm and functional, like good instrument and charting software. It is explicitly **not** the current
"AI-generated app" style.

- **Muted, low-saturation colours.** Neutral grey grounds, one restrained accent (a mid blue) and one reserved colour
  for transmitting (a burnt orange). Status colours are desaturated and always paired with an icon or text.
- **No** gradients, glows, neon, glassmorphism or blur, "HDR" saturated fills, oversized hero type, animated
  backgrounds or decorative illustrations.
- Flat surfaces separated by thin borders; shadows only where something floats (menus, dialogs, map controls) and kept
  faint.
- Typography carries hierarchy through size and weight, not colour. A plain sans for UI text and a monospace for
  callsigns, paths and raw packets.
- Motion is short and functional (a panel sliding in, a list update), and turns off with the OS "reduce motion"
  setting.
- The approved mockups ([mockups/](mockups/)) set the baseline: IBM Plex Sans and Mono, `#F4F5F7` ground, `#1F5FAE`
  accent, `#C2410C` transmit. Final typefaces are open to change; the restraint is not.

## Appearance and accessibility

Every option below is in *Settings › Appearance*, applies live without restart, and works identically on desktop and
web.

| Option | Choices | Default | Notes |
| --- | --- | --- | --- |
| Theme | Light · Dark · Auto | Auto | Auto follows the OS (desktop) or `prefers-color-scheme` (browser) and switches live. Dark mode also dims map tiles or uses a dark tile style where the provider offers one. |
| Colours | Standard · High contrast · Colour-vision-safe | Standard | High contrast: AAA (7:1) text, heavier borders, focus rings 3 px. Colour-vision-safe: a palette that separates states by lightness and blue/orange rather than red/green, plus patterns and shapes on status markers, tracks and map symbols. Can be combined with Light or Dark. |
| Text size | 85 % – 200 %, in steps | 100 % or the OS setting | Scales every text token; layouts reflow rather than clip. |
| Icon size | Small · Medium · Large | Medium | UI icons 16/20/24 px base, scaled with text size if "follow text size" is on. Separate from map symbol size. |
| Map symbol size | 75 % – 200 % | 100 % | Station symbols, labels and track widths on the map. |
| Corners | Rounded · Sharp | Rounded | Rounded: 8 px controls, 12 px panels. Sharp: 2 px and 0 px. |
| Density | Comfortable · Compact | Comfortable | Compact tightens list rows and paddings; touch targets stay ≥ 44 px on touch devices. |
| Motion | Follow OS · Reduced | Follow OS | |
| Language | System default · each translated language, in its own name | System default | OS language on desktop, browser language on the web. Switches live. Numbers, dates and units follow it; callsigns and packet text never change ([F0.9](../features/F0.9-localization.md)). |

How it is built:

- **Design tokens, not literal values.** Colours, font sizes, icon sizes, spacing and corner radii are named tokens
  (`Color.Surface`, `Color.Accent`, `Color.Transmit`, `Text.Body`, `Icon.Size`, `Radius.Control`…) defined in
  `AprsMap.UI/Themes/`. Views use `DynamicResource` only, so every option swaps tokens at runtime with no view changes.
- **Theme = mode × palette.** Light and Dark are Avalonia `ThemeVariant`s; each palette (Standard, High contrast,
  Colour-vision-safe) supplies both variants. Size, corner and density options are independent token sets layered on
  top.
- **Map rendering reads the same tokens**: symbol outline colours, track colours, label halo and range-circle styles,
  so the map follows the theme and palette.
- **Appearance is per viewer, not per station.** It lives in the UI layer (stored through `IPreferenceStore`,
  [F0.6](../features/F0.6-appearance.md)), not in the engine or `IEngineService`. Desktop stores it with the user's
  settings; the browser stores it locally (and, when signed in, in the operator's gateway profile so it follows them
  between devices). Two people viewing one gateway can use different themes.
- **Every screen is checked in all palettes and both modes** in the headless UI tests (contrast and clipping at 200 %
  text), not only in Light/Standard.

## Shell

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ ◉ N0CALL-9 [TX armed]  ● APRS-IS ● KISS-1 ○ GPS   🔍 Search / Ctrl+K   ⚡ 🔔3 │  top bar
├────┬───────────────────────────────────────────────────┬─────────────────────┤
│ 🗺 │                                                   │ Station details     │
│ 💬 │                  MAP                              │  N0ABC-7  🚗        │
│ 📡 │   [layers] [filters]                              │  12.4 km  NE 041°   │
│ 📋 │                                   [+]             │  via WIDE1-1*,qAR   │
│ 📍 │                                   [-]             │  [Message] [Follow] │
│ ☁  │   (my predictive-beacon widget)   [◎ me] [🔒]     │  [Track] [⋯]        │
│ ⚙  │                                                   │                     │
├────┴───────────────────────────────────────────────────┴─────────────────────┤
│ Packets ▾ | Log    (dock: live packet list, collapsible)                     │
└──────────────────────────────────────────────────────────────────────────────┘
 rail: Map · Messages · Stations · Packets · Objects · Weather & Telemetry · Settings
```

- **Top bar**: identity chip (callsign, symbol, TX state; menu: Beacon now, Send status, Disarm TX), one pill per
  connection (green/amber/red, click for details and reconnect), search/command palette, quick toggles (⚡), and a
  notification inbox (🔔: messages, bulletins, alerts, DX).
- **Rail**: top-level pages. On narrow screens it becomes a bottom tab bar.
- **Panels** ([F1.0](../features/F1.0-shell-and-docking.md)): everything around the map is a panel that can be docked
  on any side, tabbed, split, auto-hidden or floated (its own window on desktop, inside the page on the web). The
  layout is saved per viewer, and named layouts can be recalled ([F1.23](../features/F1.23-saved-layouts.md)). The
  drawing above is the default layout.
- **Right pane**: the Details panel for the current selection. It replaces the reference's pop-up station menu, and
  becomes a bottom sheet on narrow screens.
- **Bottom dock**: the Packets panel (and logs).
- **Extra map views** (the reference's "multitrack"): map-view panels, each following a station; on desktop they can
  float onto another monitor.

## Where the reference's menus went

| Reference menu | AprsMap |
| --- | --- |
| Transmit | Identity chip → *Beacon now* (Ctrl+B); palette command |
| Enables | Quick toggles flyout (⚡): APRS-IS, internet access, GPS, beaconing, telemetry, sound, tile fetching, logging. Each toggle is also in Settings |
| Enables → Ports | Connection pills (per-port enable) plus *Settings › Connections*; a global "receive only" toggle in ⚡ |
| Messages | *Messages* page: conversation list + thread, compose (Ctrl+M), pending/unacked filter, bulletins tab, groups (announcement servers) tab, auto-reply in *Settings › Messaging* |
| Screen (labels, tracks, PHG, DF, dead reckoning, tile set) | **Layers** panel on the map |
| Screen → Follow (find, centre, me, lock) | Search box, map buttons (◎ me, 🔒 follow), station context menu |
| View (category, platform, RF, transport, symbol, shriek filters) | **Filters** panel on the map: chips with counts. The station list and packet list share the same filter model, so "what you see" stays consistent |
| Configure (20 submenus) | **Settings** pages, searchable: Station · Beaconing · Messaging · Connections · Map & tiles · Display · Objects & overlays · Weather & NWS · Alerts & sounds · Advanced (DX, AltNet, logging) · Data · About |
| Clear | *Tools › Clear data…*, grouped with confirmation (and Undo where possible); contextual "Clear track" on a station |
| About | *Help › About*, including the AGPL "Source" link required for the web gateway |
| Exit | OS window close; the gateway keeps running headless |

## Key flows

- **First run** (wizard, ≤ 4 steps): callsign-SSID → location (Maidenhead locator, address or ZIP/postal code, typed
  coordinates, GPS or browser geolocation, or a map picker with a crosshair; [scope](../scope.md#settings-storage-and-profiles))
  → APRS-IS server (default rotate pool) → optional passcode (receive-only by default, or typed in; a calculator behind a
  "licensed, own callsign" tick shows it with a Copy button). Units are inferred from the locale and can be changed later. The user is on a live map
  within a minute. The answers become the first settings profile.
- **Profiles**: a profile switcher (callsign shown in the status bar) changes callsign, filters and location together.
  Switching disconnects from APRS-IS and reconnects with the new profile.
- **Messaging**: messenger-style threads. Each outgoing message shows its state: queued → sent → retry *n* → acked /
  rejected / failed / final, with a route badge (RF / IS / best). Long text is split into 64-character parts, with a live
  character counter while typing.
- **Predictive beaconing**: a small widget on the map shows the drift between where others think you are and where you
  are, and how close it is to triggering a beacon. Clicking it explains why the last beacon was sent.
- **Objects**: right-click the map → *Create object here*. Drag to move (confirm before it is transmitted), and the
  object's own pane has *Kill*.
- **Packets**: the list pauses automatically when you scroll up or hover (with a "resume live" pill). Anomalies get icons
  and tooltips, not only colours.

## Avalonia implementation notes

- Avalonia 12, Fluent theme with app-level design tokens (colour, spacing, typography) in one resource dictionary.
- CommunityToolkit.Mvvm: `[ObservableProperty]`, `[RelayCommand]` (async, cancellable), messenger for cross-pane events.
- Virtualized lists (`ItemsRepeater`/`ListBox` virtualization) for packets and stations; batched UI updates
  (coalesce change streams to the frame rate) so a busy feed can't flood the UI thread.
- Adaptive layout through container queries and `SplitView`. Avoid a docking library unless a spike shows it works in
  the browser.
- Command palette and settings search are driven from one registry of commands and settings, so both stay complete
  automatically.
- Notifications: in-app toasts plus the inbox; native OS notifications on desktop where available.
