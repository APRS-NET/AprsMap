# Versioning and releases

Versions follow [Semantic Versioning](https://semver.org) and are tagged `vX.Y.Z`. The process mirrors APRS.NET.

**Until 1.0.0 every release is an alpha pre-release**, versioned `0.x.y-alpha.N`. release-please marks each GitHub
release as a pre-release, and the container image is tagged with the exact version plus a floating `alpha`.

## How it works

1. Commits to `main` use [Conventional Commits](https://www.conventionalcommits.org). With squash merges the **PR
   title** is the commit message, so the `pr-title` workflow checks it.
2. `release-please` (the `release` workflow) keeps a release PR open that bumps the version and updates `CHANGELOG.md`.
3. Merging that PR creates the `vX.Y.Z` tag and a GitHub release. The `release` workflow then builds that tag:
   - runs the tests;
   - publishes the desktop app self-contained and single-file for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
     `osx-x64` and `osx-arm64`, and attaches the archives to the release;
   - builds the gateway image for `linux/amd64` and `linux/arm64` and pushes it to `ghcr.io/aprs-net/aprsmap`.
4. [MinVer](https://github.com/adamralph/minver) derives every assembly version from the nearest tag, so no version
   number is stored in the repo. Builds between releases look like `0.1.1-alpha.0.3`. The Docker build, which can't see
   git history, receives the version as a build argument.

## Commit types

| Prefix | Changelog | Bump during the alpha | Bump (1.0 and later) |
| --- | --- | --- | --- |
| `feat:` | Features | alpha number | minor |
| `fix:`, `perf:` | Bug fixes, Performance | alpha number | patch |
| `feat!:`, `fix!:` or a `BREAKING CHANGE:` footer | Breaking changes | alpha number | major |
| `docs:`, `test:`, `chore:`, `ci:`, `build:`, `refactor:` | hidden | none | none |

release-please skips a commit when every file it touches is under `tests/`, `tools/`, `docs/` or `.github/`
(`exclude-paths` in `release-please-config.json`).

A `Release-As:` footer is read only from a commit release-please counts, so it can't go on an empty commit or one that
only touches those folders. AprsMap's minor version follows APRS.NET's: `0.2.0-alpha.1` came with APRS.NET 0.2.0.

## The first release

With no earlier release tag, release-please would propose 1.0.0. Make the first release `0.1.0-alpha.1` by adding this
footer to the squash-merge commit of the first `feat:` PR (or any later commit):

```
Release-As: 0.1.0-alpha.1
```

The footer only counts on a commit that changes a file release-please considers part of the package: an empty
commit, or one that only touches `docs/`, `tests/`, `tools/` or `.github/`, is skipped ("Found 0 files" in the
release job's log).

To start a new milestone later, use the same footer, for example `Release-As: 0.2.0-alpha.1`.

release-please walks `main` back by commit date and stops at the last release's commit, so the footer must also be on
a commit made after that release. A commit written before the open release PR was merged, and merged after it, is
older than the release and is never read (as happened with `0.3.0-alpha.4`): merge the footer's PR first, or put the
footer on a new commit.

## APRS.NET

AprsMap uses the APRS.NET packages from nuget.org, including pre-releases, with versions pinned in
`Directory.Packages.props`. Dependabot checks daily and opens one grouped PR per new APRS.NET release. Work that needs
an unreleased APRS.NET change waits for its alpha; nothing references APRS.NET projects directly.
