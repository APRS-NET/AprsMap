# Licensing

The repository as a whole is licensed under the **GNU Affero General Public License v3.0 only** ([LICENSE](LICENSE)),
with the exceptions below, which are **MIT** ([LICENSES/MIT.txt](LICENSES/MIT.txt)) so that other APRS tools can reuse
the engine the way they reuse APRS.NET.

| Path | License |
| --- | --- |
| `src/AprsMap.Contracts`, `src/AprsMap.Core`, `src/AprsMap.Client.Remote`, `src/AprsMap.Tiles` and their tests | MIT |
| `docs/` | MIT |
| `tools/` | MIT (each file says so in its SPDX header) |
| Everything else, including `src/AprsMap.UI`, `src/AprsMap.Desktop`, `src/AprsMap.Browser`, `src/AprsMap.Gateway`, `src/AprsMap.Server` | AGPL-3.0-only |

Rules:

- Every source file carries an SPDX header (`// SPDX-License-Identifier: MIT` or `AGPL-3.0-only`), and every project
  states its license in `PackageLicenseExpression`. The header is authoritative for a single file.
- MIT projects must never reference AGPL projects. AGPL projects may reference MIT ones.
- The license texts live in [LICENSES/](LICENSES/) using SPDX identifiers as file names (REUSE layout); `LICENSE` at the
  root repeats the AGPL text so that GitHub and tooling detect the repository's default license.
- A network-facing deployment (the gateway) must offer its source to users; the web UI's "Source" link does this.

Copyright (c) 2026 Derek Rowland
