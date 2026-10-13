# Clean-room process

This project reimplements the *functionality* of APRSIS32 (APRSISCE/32, by Lynn Deffenbaugh KJ4ERJ) as a new .NET 10 /
Avalonia application. It is not a port. No APRSIS32 code, binaries, assets or text are used. This document defines the
wall and how work crosses it.

> Not legal advice. If the app is ever distributed commercially, have the process reviewed.

## Roles

The same agent (Claude, with the maintainer reviewing) writes specs and code, so the wall is enforced by **what sources
may be consulted**, not by separate people.

| Stage | Input allowed | Output |
| --- | --- | --- |
| Scope | Public feature lists, wiki topic names, the maintainer's description of observed behavior | `docs/scope.md` |
| Feature spec | Allowed sources below, cited by URL | `docs/features/NNN-*.md`, written in our own words |
| Implementation | Feature specs, APRS protocol specs, APRS.NET source and docs | Code in `src/` |

Implementation reads the feature specs, not the reference material. If a spec is unclear, fix the spec (citing a source)
before writing code.

## Allowed sources

- Protocol specifications: APRS101, APRS 1.1 / 1.2 addenda, AX.25 2.2, KISS, APRS-IS documentation (aprs-is.net),
  the aprs-deviceid list. APRS.NET mirrors many of these under `docs/spec/`.
- APRSIS32's **public user documentation** (aprsisce.wikidot.com, the groups.io support group, published getting-started
  guides) for *what a feature does from the user's point of view*.
- Black-box observation of APRSIS32 running, reported by the maintainer: what appears on screen, what goes on the wire
  (packet captures of its transmissions are protocol behavior and are fine).
- Other open-source APRS software's **documentation** for general domain knowledge. Their code is not used either,
  unless its license is compatible and the use is recorded in `THIRD-PARTY.md`.

## Prohibited

- Decompiling, disassembling, or debugging APRSIS32 binaries, or reading memory dumps of it.
- Copying APRSIS32 text (help pages, dialog strings, menu labels verbatim), icons, images or screen layouts pixel for
  pixel. Matching workflows is fine; cloning look and wording is not.
- Using its trademarks for our features: for example, our predictive beaconing gets its own name, not GeniusBeaconing™.
  The product name and namespaces must not use "APRSIS32" or "APRSISCE".
- Reading or reproducing its configuration file schema. If config import is ever wanted, specify it as an
  interoperability feature from the maintainer's own files, scoped separately.

## Provenance

Every feature spec ends with a **Sources** list (URL plus access date, or "maintainer observation, YYYY-MM-DD"). Commits
that implement a feature reference its spec number. A feature with no sources is designed from scratch, and its spec says
so.
