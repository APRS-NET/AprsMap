# Spike 5: docking layout with Dock

Status: done, 2026-10-15. Code: `src/AprsMap.UI/Shell/` and `tests/AprsMap.UI.Tests/ShellSpikeTests.cs` on branch
`spike/dock` (throwaway, never merged).

Question: can panels such as messages, direct-message threads, stations and packets be docked, tabbed, split and
floated like Visual Studio, with layouts that persist, can be named and recalled, and survive a monitor being
unplugged? It has to work in the desktop app and the web app.

**Answer: yes, with Dock 12.1 (MIT)** and five pieces of our own:
- a placement check for saved window positions;
- tokens mapped onto Dock's theme;
- text-size scaling for Dock's chrome;
- wrapping tab strips;
- a reviewed exception for Dock's library trim warnings.

Desktop gets native floating windows. The web app gets the same docking with floating panels inside the page. Nothing
in the spike needed an `IEngineService` change: the layout is per-viewer UI state.

## Dock

- **Package:** <https://github.com/wieslawsoltes/Dock>, MIT. Version `12.1.0.9` targets Avalonia 12.1 and net10.0,
  published 2026-10-13. The repository is very active (commit on 2026-10-14).
- **Packages used:** `Dock.Avalonia`, `Dock.Avalonia.Themes.Fluent`, `Dock.Model.Mvvm` (CommunityToolkit.Mvvm, which we
  already use) and `Dock.Serializer.SystemTextJson`, which ships a source generator.
- **Features we'd use:** tool and document docks, splits, tabs, auto-hide, and floating windows that are either OS
  windows or "managed" in-app windows. Also workspaces (named layout snapshots), automation peers for every docking
  control, and Ctrl+Tab selectors for documents and tools.

## Results

| Question | Result |
| --- | --- |
| Builds with our analyzers, `TreatWarningsAsErrors`, Release | Yes, no warnings in our code |
| Layout save and load | Yes, with the source-generated System.Text.Json serializer. Every Dock model type has to be registered by hand (`DockJsonSerializable`), because they live in another assembly. A missed type fails loudly at save time. |
| Panel state in the saved layout | None. Only ids, kind, title and geometry are saved; a `ContextLocator` reattaches view models on load. |
| Floating windows on desktop (Linux, XWayland) | Native `HostWindow`s open. Positions and sizes are stored in the layout. |
| Window saved on a monitor that's gone | **Dock does no check**: it restores raw `X`/`Y`. With our check, a window saved at x = 9000 on a 2256-pixel screen was moved to 1956 before showing; the window manager put it at 1938, on screen. |
| Moving panels without dragging | `MoveDockable`, `FloatDockable`, `PinDockable`, `CloseDockable` and `RestoreDockable(id)` all work from code (tested). |
| Keyboard | Every tab and each panel's menu and pin buttons are in the Tab order. The panel menu offers Float, Dock, Auto Hide, Dock as Tabbed Document and Close. "Move to Left/Right/Bottom" would be our own entries. |
| Screen readers | Dock ships automation peers for docks, tabs, chrome, windows and selectors, with names from `AutomationProperties` or the panel title. |
| Our palettes | Follow, once Dock's semantic tokens are mapped onto ours (`DockTokens.axaml`). Dock's Fluent theme has 8 literal colours, all overridable. |
| 200 % text | **Not by default.** Dock's chrome uses a fixed 12 px (`DockFontSizeNormal`) and fixed tab, header and button sizes. Driving those from our text scale fixed it. Tabs then ran off the panel; a `WrapPanel` for `ToolTabStrip` fixed that. Every 200 % check passes again. |
| Translation | 33 menu strings are resources we can set per language. 4 literal English strings are in the MDI document menu (a mode we don't need), plus a "Reload" button. |
| Browser trim check | **Fails as is:** 13 IL2026 warnings from reflection bindings in Dock (splitter templates, the MDI window menu, ReactiveUI overlay services). Passing `IL2104` through as a warning publishes. The splitter works after trimming (dragged in Firefox). |
| Browser size | +2.27 MB raw, +381 KB Brotli (+6.7 %): 6.08 MB Brotli in all. |
| Browser docking | Dragging a tab onto another dock moves it. Dragging it off floats it as an in-page window. Needs `DockSettings.UseManagedWindows = true` in the browser; without it, drags try to open native windows and fail. |

## Findings

1. **Window placement must be ours.**
   - `WindowPlacement.Fit` is a pure function: saved position and size, plus the current screens, give a fitted
     position and size.
   - **When the saved spot is kept:** if at least 120 px of the 32 px title strip lands in a screen's working area.
   - **Otherwise:** the window moves to the nearest screen and is clamped inside it, shrinking to fit when it's larger.
   - **When it runs:** on every loaded layout before any window shows, and again on `Screens.Changed` for open windows.
   - **Tested cases:** an unplugged monitor, negative coordinates, the title bar above the screen, an oversized window,
     and mixed scaling.
2. **The monitor fingerprint is cheap:** each screen's bounds and scale, sorted. It can key "the layout I used with
   this arrangement".
3. **Under XWayland the screen reported scale 1 on a 2256 × 1504 panel.** Positions still worked, but per-monitor
   scaling can't be trusted on Linux. Saved positions are in pixels, sizes in device-independent pixels, and the check
   uses each screen's own scale.
4. **Native Wayland:** Avalonia 12 has no native Wayland backend, so it runs under XWayland, where positions work.
5. **Floating a panel from code places it at (0,0).** The app should pass a position: near the panel's dock, or the
   pointer.
6. **Trimming:** the clean fix is upstream. Dock's splitter and MDI templates could use compiled bindings, a small
   change to propose to Dock. Until then, either:
   - allow `IL2104` as a warning (library warnings stay visible; our own code's trim warnings stay errors), or
   - keep Dock out of the browser build.
   An ILLink attributes file couldn't suppress the warnings, and keeping the Dock assemblies untrimmed exposed more.
7. **Dock's `DockWorkspaceManager`** captures and restores named layouts. It stores the layout as text, so named
   layouts and the monitor fingerprint fit in our preference store as they are.

## Not tested

- Real hot-plugging of a monitor: the handler is wired, but only one display was available.
- Windows and macOS.
- Memory and performance with real map and list content.
- A second browser tab as a pop-out, which needs no docking support.

## Sources

- Dock documentation in its repository (`docfx/articles`, read 2026-10-15): serialization, workspaces, floating and
  managed windows, host window locators, accessibility, semantic theme tokens.
- Avalonia `Screens`, `Screen.WorkingArea` and `Window.Position` API documentation.
