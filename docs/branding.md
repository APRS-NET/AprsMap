# Branding and renaming

"zedMap" is a working name. Everything is set up so the product can be renamed with one script run and a review, not
a hunt through the code.

## Where the name may appear

| Place | Form | How it changes |
| --- | --- | --- |
| `build/Branding.props` | `ProductName`, `ProductDisplayName`, `ProductSlug`, `ProductTocall`, URLs | Edit by hand, or let the script do it |
| Namespaces, project and folder names | `ZedMap.*` prefix only | `tools/rename-product.py` |
| Docs, README, CLAUDE.md | Free text | `tools/rename-product.py` |
| User-visible strings | **Never literal.** Bound to `Brand.DisplayName` and friends | Follows `Branding.props` automatically |
| Window titles, installer names, web manifest | Generated from `Branding.props` | Follows automatically |
| App icon (AGPL-3.0-only) | `build/branding/icon.svg`, rendered by `tools/make-icons.sh` into the window icon (`icon-256.png`), the Windows executable icon (`icon.ico`) and the browser's `favicon.svg`, `favicon.png` and `apple-touch-icon.png` | Not renamed: it is the APRS.NET icon, a map pin broadcasting radio waves. To change it, replace `icon.svg` and run the script; a test checks that the web copy matches |
| Linux desktop entry (AGPL-3.0-only) | `tools/install-linux-desktop.sh` writes `~/.local/share/applications/<slug>.desktop` and the icon as `<slug>.svg`, from `build/Branding.props` | GNOME and other freedesktop shells show a window's icon only through a desktop entry whose `StartupWMClass` is the window class: the executable's name, the slug. `--uninstall` removes both |

## Rules

1. **Display strings come from one place.** At build time, `build/Branding.props` emits `AssemblyMetadata` items, and a
   small `Brand` class in `ZedMap.Contracts` reads them (`Brand.DisplayName`, `Brand.Slug`, `Brand.Tocall`,
   `Brand.SourceUrl`). XAML uses `{x:Static brand:Brand.DisplayName}`, and the gateway's HTML title and web manifest are
   generated from the same values. No view, dialog, log message or packet comment contains the product name as a literal.
2. **The name is a namespace prefix, never part of a type name.** `ZedMap.Core.StationStore` is fine;
   `ZedMapStationStore` and `IZedMapService` are not. That keeps a rename a prefix swap: `IEngineService`, not
   `IZedMapService`.
3. **One spelling per form**, so a rename is a fixed set of replacements:
   `ZedMap` (the code prefix), `aprsmap` (slugs, paths, config folder, container image), `APRSMAP`
   (environment-variable prefix); `aprs-map` is not used. The brand itself is written **`zedMap`** (lower-case z,
   maintainer decision, 2026-11-19): `ProductDisplayName` and the spelling in prose, titles and installers. `ZedMap` is
   only the code prefix (`ZedMap.Core`), since .NET namespaces are PascalCase. The slug becomes `zedmap` and the env
   prefix `ZEDMAP` in the second step of the rename (below).
4. **The TOCALL is a separate decision.** The transmitted device ID is the `APZAMP` placeholder for now, in the
   experimental `APZ` range. The plan is to request **`APMAPZ`**, exactly, with no version wildcard, in the
   aprs-deviceid list after the rename to zedMap, and switch to it once it's assigned (maintainer decision, 2026-11-19,
   replacing `APNETM`; [#547](https://github.com/APRS-NET/AprsMap/issues/547)). It is otherwise not tied to the product
   name: renaming the product never changes the TOCALL, because receivers identify existing installs by it.
5. **User data survives a rename.** Settings and caches are found through `Brand.Slug`; the app looks for the previous
   slug's folder on first start and migrates it (`Branding.props` keeps a `PreviousSlugs` list).

## Renaming

```sh
python3 tools/rename-product.py ZedMap NewName            # dry run: lists every file and path that would change
python3 tools/rename-product.py ZedMap NewName --apply    # rewrites contents and git-moves files and folders
```

The script replaces the three spellings above in file contents and paths, skips `.git/`, `LICENSES/`, binary files and
the clean-room exposure log, and prints a summary. Afterwards: build, run the tests, grep for the old name, review the
diff, and update `Branding.props` values the script cannot guess (display name with spaces, URLs, TOCALL).

### Renaming in two steps

The slug names every file an install has: the desktop and gateway executables (`AssemblyName` is the slug), the
gateway's folders and units, the release assets and the container image. An install's update check looks for the
release files under its own slug, and the gateway's updater downloads `<its executable>-v<version>-<rid>.tar.gz`. A
release that changes the slug in one go would be invisible to every install. So a rename goes in two steps
([#545](https://github.com/APRS-NET/AprsMap/issues/545)):

1. **The code and the display name**, slug unchanged: `rename-product.py OLD NEW --code-only` replaces only the
   PascalCase form, leaves the GitHub links and the changelog alone, and doesn't touch `PreviousSlugs`. Set
   `ProductDisplayName` by hand, fix the prose the script wrote in PascalCase, and set `NextSlug` to the new slug: the
   update check then also takes releases named by it (`Brand.ReleaseSlugs`). Saved window layouts name their panels'
   types, so `LayoutFile` reads `<Old>.UI.Shell.*` as this version's.
2. **Once installs have step 1**, with the repository rename: the slug, `PreviousSlugs` (the old slug), `NextSlug`
   cleared, `ProductSourceUrl`, the gateway service ([#546](https://github.com/APRS-NET/AprsMap/issues/546)) and the
   container image.

For zedMap: the brand is **zedMap** (lower-case z) in prose and `ProductDisplayName`; `ZedMap` is the code prefix,
`zedmap` the next slug, `ZEDMAP` the next environment-variable prefix. Until step 2, the slug, the environment-variable
prefix (`APRSMAP_`) and the repository (`APRS-NET/AprsMap`) keep the old name.
