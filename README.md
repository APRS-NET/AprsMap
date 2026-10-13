# AprsMap (working name)

A clean-room APRS map client in .NET 10 and Avalonia 12, with the functionality of APRSIS32 as its reference, running
as a native desktop app and as a web app from one shared engine. APRS protocol work comes from
[APRS.NET](https://github.com/APRS-NET/APRS.NET) (`AprsNet.*` NuGet packages).

Status: scoping. No code yet.

| Document | Contents |
| --- | --- |
| [docs/clean-room.md](docs/clean-room.md) | What sources may and may not be used, and how provenance is recorded |
| [docs/architecture.md](docs/architecture.md) | Projects, the desktop/web split, the gateway, the APRS.NET ownership rule, spikes |
| [docs/reference/](docs/reference/) | Behavioral analysis of the reference app from public docs |
| [docs/ux.md](docs/ux.md) | Modern UX direction, and where each reference menu went |
| [docs/scope.md](docs/scope.md) | Phased feature inventory |
| [docs/aprsnet/gaps.md](docs/aprsnet/gaps.md) | Capabilities to be built in APRS.NET, one issue each |
| [docs/features/](docs/features/) | Per-feature specs (the implementation's only behavioral input) |

## License

The repository is AGPL-3.0-only ([LICENSE](LICENSE)). The engine (`AprsMap.Contracts`, `AprsMap.Core`,
`AprsMap.Client.Remote`) and the docs are MIT so other tools can reuse them. Details: [LICENSING.md](LICENSING.md).

APRSIS32 and APRSISCE are the work of Lynn Deffenbaugh, KJ4ERJ. This project is unaffiliated and uses none of its code
or assets. Its published AGPL source is deliberately not consulted; see [docs/clean-room.md](docs/clean-room.md).
