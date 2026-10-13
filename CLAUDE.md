# CLAUDE.md

Clean-room reimplementation of APRSIS32's functionality as AprsMap (.NET 10, Avalonia 12, desktop + WASM web via an
ASP.NET Core gateway). Read `docs/clean-room.md` and `docs/architecture.md` before any work.

## Hard rules

- **Clean room.** Never decompile or inspect APRSIS32 binaries or config schemas, and never copy its text, icons or
  layouts. Only the sources listed in `docs/clean-room.md` are allowed. Code is written from `docs/features/*` specs; every spec cites its sources.
- **Ownership.** Protocol and APRS analysis (packets, encode, AX.25, KISS, TNC, APRS-IS, messaging rules, digi, IGate,
  telemetry math, geo) belongs in APRS.NET (local clone: `../APRS.NET`). If it's missing, add or extend a section in
  `docs/aprsnet/gaps.md`; don't implement it here except as a marked `// SHIM: <gap id>`.
- **UI parity.** `AprsMap.UI` references only `AprsMap.Contracts`. Every UI capability is an `IAprsMapService` member,
  so desktop (in-process) and web (SignalR) stay identical.
- Use APRS.NET via NuGet prereleases (`AprsNet.*`), not project references, unless testing an unreleased change.
- Anything that transmits must be gated behind a verified login/passcode and, on the gateway, an authenticated operator.
