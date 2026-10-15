# Branding and renaming

"AprsMap" is a working name. Everything is set up so the product can be renamed with one script run and a review, not
a hunt through the code.

## Where the name may appear

| Place | Form | How it changes |
| --- | --- | --- |
| `build/Branding.props` | `ProductName`, `ProductDisplayName`, `ProductSlug`, `ProductTocall`, URLs | Edit by hand, or let the script do it |
| Namespaces, project and folder names | `AprsMap.*` prefix only | `tools/rename-product.py` |
| Docs, README, CLAUDE.md | Free text | `tools/rename-product.py` |
| User-visible strings | **Never literal.** Bound to `Brand.DisplayName` and friends | Follows `Branding.props` automatically |
| Window titles, installer names, web manifest | Generated from `Branding.props` | Follows automatically |
| App icon (AGPL-3.0-only) | `build/branding/icon.svg`, rendered by `tools/make-icons.sh` into the window icon (`icon-256.png`), the Windows executable icon (`icon.ico`) and the browser's `favicon.svg`, `favicon.png` and `apple-touch-icon.png` | Not renamed: it is the APRS.NET icon, a map pin broadcasting radio waves. To change it, replace `icon.svg` and run the script; a test checks that the web copy matches |

## Rules

1. **Display strings come from one place.** At build time, `build/Branding.props` emits `AssemblyMetadata` items, and a
   small `Brand` class in `AprsMap.Contracts` reads them (`Brand.DisplayName`, `Brand.Slug`, `Brand.Tocall`,
   `Brand.SourceUrl`). XAML uses `{x:Static brand:Brand.DisplayName}`, and the gateway's HTML title and web manifest are
   generated from the same values. No view, dialog, log message or packet comment contains the product name as a literal.
2. **The name is a namespace prefix, never part of a type name.** `AprsMap.Core.StationStore` is fine;
   `AprsMapStationStore` and `IAprsMapService` are not. That keeps a rename a prefix swap: `IEngineService`, not
   `IAprsMapService`.
3. **One spelling per form**, so a rename is a fixed set of replacements:
   `AprsMap` (code and prose), `aprsmap` (slugs, paths, config folder, container image), `APRSMAP` (environment-variable
   prefix), `aprs-map` is not used.
4. **The TOCALL is a separate decision.** The transmitted device ID (`APZAMP` placeholder, experimental range) is
   registered in the aprs-deviceid list. Renaming the product does not change it unless a new TOCALL is registered,
   because receivers identify existing installs by it.
5. **User data survives a rename.** Settings and caches are found through `Brand.Slug`; the app looks for the previous
   slug's folder on first start and migrates it (`Branding.props` keeps a `PreviousSlugs` list).

## Renaming

```sh
python3 tools/rename-product.py AprsMap NewName            # dry run: lists every file and path that would change
python3 tools/rename-product.py AprsMap NewName --apply    # rewrites contents and git-moves files and folders
```

The script replaces the three spellings above in file contents and paths, skips `.git/`, `LICENSES/`, binary files and
the clean-room exposure log, and prints a summary. Afterwards: build, run the tests, grep for the old name, review the
diff, and update `Branding.props` values the script cannot guess (display name with spaces, URLs, TOCALL).
