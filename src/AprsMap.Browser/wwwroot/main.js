// SPDX-License-Identifier: AGPL-3.0-only
import { dotnet } from './_framework/dotnet.js';

const runtime = await dotnet.withDiagnosticTracing(false).create();
const config = runtime.getConfig();
// The page URL tells the app which gateway served it.
await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
