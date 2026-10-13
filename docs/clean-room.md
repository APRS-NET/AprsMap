# Clean-room process

AprsMap reimplements the *functionality* of APRSIS32 (APRSISCE/32, by Lynn Deffenbaugh KJ4ERJ) as a new,
modern .NET 10 / Avalonia application. It is a redesign, not a port: we study **what the program does and how a user
works with it**, then design our own engine and UI with modern patterns. No APRSIS32 code is read, translated or used.

> Not legal advice. Have the process reviewed before any commercial distribution or relicensing.

## Why the wall matters here

- APRSISCE source code was published under **AGPL-3.0** by the APRS Foundation
  (github.com/APRSFoundation/APRSISCE, 2025-02-06). AGPL covers the code's *expression*. Functionality, workflows
  and the purpose of features, learned from user documentation and observation, are not that expression.
- **APRS.NET and AprsMap's engine (`Contracts`, `Core`, `Client.Remote`) are MIT.** They stay MIT only if nothing in
  them is derived from AGPL code. The UIs are AGPL-3.0 by choice (see [LICENSING.md](../LICENSING.md)), but they come out of the
  same clean process: the AGPL choice is not permission to read the APRSISCE source.

## Stages

The same agent (Claude, with the maintainer reviewing) does every stage, so the wall is enforced by **which sources may
be consulted**.

| Stage | Input allowed | Output |
| --- | --- | --- |
| Reference analysis | The allowed sources below | `docs/reference/*.md`: what the reference does, in our words, with citations |
| Scope and UX design | Reference analysis, protocol specs, modern UI practice | `docs/scope.md`, `docs/ux.md` |
| Feature spec | All of the above | `docs/features/F*.md` |
| Implementation | Feature specs, protocol specs, APRS.NET source and docs | Code in `src/` |

Implementation reads the feature specs, not the reference material. If a spec is unclear, fix the spec first (citing a
source).

## Allowed sources

- Protocol specifications: APRS101, APRS 1.1 / 1.2 addenda, AX.25 2.2, KISS, APRS-IS documentation (aprs-is.net), the
  aprs-deviceid list. APRS.NET mirrors many under `docs/spec/`.
- APRSIS32's **public user documentation**: aprsisce.wikidot.com, the groups.io APRSISCE group, published user guides
  (for example G4ILO's), and videos showing the program in use.
- Black-box observation of APRSIS32 running (by the maintainer, or by Claude driving the UI with the maintainer's
  permission; screenshots stay out of the repo): what appears on screen, which options exist,
  what goes on the wire (packet captures of its transmissions are protocol behavior).
- Other APRS software's **user documentation** for general domain knowledge.

## Documentation licenses: read freely, copy nothing

Reading is never a license issue. Licenses and copyright control *copying*. The sources above have their own terms:

| Source | Terms | Consequence |
| --- | --- | --- |
| aprsisce.wikidot.com | **CC BY-SA 3.0** (site footer) | Copied or closely paraphrased text would be a derivative, which must be CC BY-SA with attribution. That is incompatible with our MIT docs, so we copy none |
| groups.io posts, guides, videos | All rights reserved by each author (no license) | Read and watch only |
| Protocol specs | Various, published for implementers | Implement freely; quote sparingly with attribution, as APRS.NET already does |

Rules for reference notes (`docs/reference/`) and feature specs:

1. **Facts and functions only**: which features exist, what they do, defaults, numbers, protocol behavior. These are
   not copyrightable; the author's sentences, examples, tables, page structure and screenshots are.
2. **Write from understanding, not from the page.** Read, close the page, then write in our own structure (grouped by
   user goal, not the wiki's page order).
3. **Quotes**: only a short name where it is needed to identify something (for example a menu name), marked as a quote.
   Never sentences.
4. **No images** from any source, including the wiki, in the repo.
5. **Skip** wiki pages that document the configuration XML (for example "Editing XML Configuration") or that contain
   code, and don't follow links to the source repository.
6. **Attribute anyway**: each reference file lists the pages it drew facts from, and notes "facts only, no text
   copied". Attribution for facts isn't required, but it is honest and costs nothing.
7. **Check overlap** before committing reference docs: no run of 8 or more consecutive words may match the source
   text, except protocol tokens, numbers and names (see `tools/overlap-check.py`).

## Prohibited

- **The APRSISCE source repository in any form**: github.com/APRSFoundation/APRSISCE and any fork or mirror,
  including its `.c`/`.cpp`/`.h` files, resource scripts (`.rc`), the dated `* APRSISCE Notes.txt` files, and bundled
  assets. Do not fetch, search inside, quote, or summarize them. This includes code snippets quoted in forum posts or
  search results; if one appears, stop reading and don't use it.
- Decompiling, disassembling, or debugging APRSIS32 binaries, or inspecting their resources or memory.
- Reading or reproducing its configuration file (XML) schema.
- Copying APRSIS32 wording (help text, dialog strings, verbatim menu labels), icons, images or screen layouts.
  Inventorying *which options exist* is fine; our UI organizes and names them its own way.
- Using its names or trademarks for our product or features (no "APRSIS", no "GeniusBeaconing"™; we name our own).

If prohibited material is seen by accident, record it in the affected spec's **Sources** section ("exposure:
<what>, <date>"). Don't use anything learned from it, and have someone else re-derive that area from allowed sources.

## Provenance

- Every reference analysis and feature spec ends with **Sources**: URL plus access date, or "maintainer observation,
  YYYY-MM-DD". A feature with no reference source says "none: original design".
- Commits that implement a feature reference its spec ID.
- Each source file carries an SPDX license header matching its project (`MIT` or `AGPL-3.0-only`).
