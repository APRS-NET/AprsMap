# AprsMap

[![CI](https://github.com/APRS-NET/AprsMap/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/APRS-NET/AprsMap/actions/workflows/ci.yml)
[![Packages](https://github.com/APRS-NET/AprsMap/actions/workflows/packages.yml/badge.svg?branch=main)](https://github.com/APRS-NET/AprsMap/actions/workflows/packages.yml)
[![Line coverage](https://raw.githubusercontent.com/APRS-NET/AprsMap/badges/badge_linecoverage.svg)](#code-coverage)
[![AI-DECLARATION: pair](https://img.shields.io/badge/䷼%20AI--DECLARATION-pair-ffedd5?labelColor=ffedd5)](https://ai-declaration.md)

An APRS map client for amateur radio, built on .NET 10 and Avalonia: a native desktop app for Windows, Linux and
macOS, and a web app served by a Docker gateway, both running the same UI on the same engine. APRS protocol work comes
from [APRS.NET](https://github.com/APRS-NET/APRS.NET). The feature set is a clean-room reimplementation of the
functionality of APRSIS32, redesigned with a modern UI (see [docs/clean-room.md](docs/clean-room.md)).

Status: Phase 0, foundations. The app connects to APRS-IS, has a first-run setup wizard and station profiles, and lists
live packets; **there is no map yet**. See [docs/status.md](docs/status.md) for what to expect when you run it, and
[docs/scope.md](docs/scope.md) for the plan.

> [!WARNING]
> **Alpha software.** Every release is an alpha pre-release (`0.x.y-alpha.N`) until at least v1.0.0. Behaviour and
> settings can change between any two releases. It depends on APRS.NET alpha packages, which change often too.

## Download

| App | Get it |
| --- | --- |
| Desktop | [GitHub releases](https://github.com/APRS-NET/AprsMap/releases): `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` archives. Self-contained, no .NET install needed. Unsigned for now. |
| Web (gateway) | `docker run -p 8080:8080 ghcr.io/aprs-net/aprsmap:alpha`, then open <http://localhost:8080>. Multi-arch (`linux/amd64`, `linux/arm64`). See [docs/deployment.md](docs/deployment.md). |

Without a passcode the app logs in to APRS-IS receive-only, and nothing is transmitted.

## Layout

| Path | Contents | Licence |
| --- | --- | --- |
| `src/AprsMap.Contracts` | `IEngineService`, the data types the UIs see, product identity (`Brand`) | MIT |
| `src/AprsMap.Core` | The engine: APRS-IS connection, packet handling (APRS.NET), recent history, telemetry | MIT |
| `src/AprsMap.Client.Remote` | `IEngineService` over SignalR, used by the browser app | MIT |
| `src/AprsMap.UI` | Shared Avalonia views, view models, theme tokens and appearance settings | AGPL-3.0 |
| `src/AprsMap.Desktop` | Desktop app; runs the engine in-process | AGPL-3.0 |
| `src/AprsMap.Browser` | Web app (WebAssembly); talks to the gateway | AGPL-3.0 |
| `src/AprsMap.Gateway` | ASP.NET Core gateway: engine, SignalR hub, browser app, `/healthz`, `/source` | AGPL-3.0 |
| `src/AprsMap.Hosting` | Host wiring shared by the desktop app and the gateway (engine config, OpenTelemetry) | AGPL-3.0 |
| `tests/` | xUnit v3 tests: engine, gateway end to end over SignalR, headless UI | as tested project |
| `docs/` | Scope, architecture, UX and mockups, clean-room process, APRS.NET gaps | MIT |

## Build

```sh
dotnet tool restore
dotnet build
dotnet test
```

Run the desktop app with `dotnet run --project src/AprsMap.Desktop`, or the gateway with
`dotnet run --project src/AprsMap.Gateway` (APRS-IS settings are in each project's `appsettings.json`, section
`Engine`).

The browser app builds with any .NET 10 SDK, but only a **publish** links SkiaSharp into WebAssembly, which needs the
`wasm-tools` workload (`dotnet workload install wasm-tools`, with Microsoft's SDK). The Dockerfile does this, so the
easiest way to try the web app is `docker compose up --build`. Running `src/AprsMap.Browser` on its own doesn't work (the page says so): it needs a
gateway to serve it (see [docs/status.md](docs/status.md#web-app)).

Code is formatted with [CSharpier](https://csharpier.com), and CI fails on unformatted files. Enable the pre-commit hook
once per clone so staged files are formatted when you commit:

```sh
git config core.hooksPath .githooks
```

### Conventions the build enforces

- **Warnings are errors**, with the latest recommended analyzers and code style enforced in the build.
- **Licence headers**: every C# file starts with its project's SPDX line (`.editorconfig`, IDE0073), and an MIT project
  that references an AGPL project fails the build (`Directory.Build.targets`). See [LICENSING.md](LICENSING.md).
- **Banned APIs** ([BannedApiAnalyzers](https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.BannedApiAnalyzers/BannedApiAnalyzers.Help.md)),
  shared with APRS.NET: `BannedSymbols.txt` (real clocks, blocking waits) for all shipped code,
  `BannedSymbols.Engine.txt` for the MIT engine (no console, no UI, no raw `HttpClient`), and `BannedSymbols.UI.txt` for
  Avalonia projects (no literal colours: theme tokens only, see [docs/ux/README.md](docs/ux/README.md)).
- **Central package versions** in `Directory.Packages.props`; packages come only from nuget.org (`nuget.config`).
- **Product name in one place**: `build/Branding.props`; see [docs/branding.md](docs/branding.md).

## Telemetry

Nothing is sent anywhere by default. Set `OTEL_EXPORTER_OTLP_ENDPOINT` (and any standard `OTEL_*` variables) to export
logs, metrics and traces over OTLP from the desktop app or the gateway. The engine's meter and activity source are both
named `AprsMap.Core`.

## Code coverage

The CI `coverage` job fails if the merged line or branch coverage of the MIT engine (`AprsMap.Contracts`,
`AprsMap.Core`, `AprsMap.Client.Remote`) drops below `MIN_LINE` / `MIN_BRANCH` in `.github/workflows/ci.yml`
(currently 85% line, 80% branch). To see the same numbers locally:

```sh
dotnet tool restore
dotnet test --collect:"XPlat Code Coverage" --results-directory coverage
dotnet reportgenerator -reports:"coverage/**/coverage.cobertura.xml" -targetdir:coverage/report \
  -reporttypes:"Html;TextSummary" -assemblyfilters:"+AprsMap.Contracts;+AprsMap.Core;+AprsMap.Client.Remote"
```

## Package checks

The `packages` workflow lists vulnerable NuGet packages (direct and transitive) and available updates, posts the report
to the run summary and to pull requests, and fails on any known vulnerability. Dependabot proposes updates daily for
NuGet (so each APRS.NET alpha arrives quickly) and weekly for GitHub Actions, the Docker base images and the SDK.

## Releases

[release-please](https://github.com/googleapis/release-please) keeps a release PR open on `main`; merging it tags
`vX.Y.Z`, and the `release` workflow attaches the desktop archives and pushes the container image. Versions come from
the tag through [MinVer](https://github.com/adamralph/minver). The series starts at `0.1.0-alpha.1`. See
[docs/versioning.md](docs/versioning.md).

## Documentation

| Document | Contents |
| --- | --- |
| [docs/clean-room.md](docs/clean-room.md) | Which sources may and may not be used, and how provenance is recorded |
| [docs/architecture.md](docs/architecture.md) | Projects, the desktop/web split, the gateway, the APRS.NET ownership rule |
| [docs/reference/](docs/reference/) | Behavioural analysis of the reference app from public docs and observation |
| [docs/ux/](docs/ux/) | UX direction, visual style, appearance options, and the approved mockups |
| [docs/scope.md](docs/scope.md) | Phased feature inventory |
| [docs/aprsnet/gaps.md](docs/aprsnet/gaps.md) | Capabilities to be built in APRS.NET, one issue each |
| [docs/features/](docs/features/) | Per-feature specs (the implementation's only behavioural input) |
| [docs/branding.md](docs/branding.md) | How the product name is kept in one place and renamed |
| [docs/status.md](docs/status.md) | What works today, what you'll see when you run it, and known issues |
| [docs/deployment.md](docs/deployment.md) | Running the gateway container and the desktop archives |
| [docs/versioning.md](docs/versioning.md) | Versions, commit types and the release flow |

## AI assistance

AprsMap is developed with an AI coding assistant (Claude Code) working alongside the maintainer, who understands,
reviews and commits every change. See [AI-DECLARATION.md](AI-DECLARATION.md).

## Licence

The repository is AGPL-3.0-only ([LICENSE](LICENSE)). The engine (`AprsMap.Contracts`, `AprsMap.Core`,
`AprsMap.Client.Remote`) and the docs are MIT so other tools can reuse them. Details: [LICENSING.md](LICENSING.md).

APRSIS32 and APRSISCE are the work of Lynn Deffenbaugh, KJ4ERJ. This project is unaffiliated and uses none of its code
or assets. Its published AGPL source is deliberately not consulted; see [docs/clean-room.md](docs/clean-room.md).
