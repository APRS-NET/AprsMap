#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Checks the SBOMs tools/sbom.sh writes (docs/features/F4.17-sbom.md, behavior 5).

Each file must be CycloneDX JSON naming the app and its version, with components that each have a name, a version, a
NuGet package URL and a SHA-512 hash, and must include the APRS.NET packages the app uses. Components whose package
states no licence are listed, as a warning: the SBOM says what the package says.

    tools/ci/check-sbom.py <file>...
"""

import json
import sys


def check(path: str) -> list[str]:
    problems: list[str] = []
    try:
        with open(path, encoding="utf-8") as f:
            bom = json.load(f)
    except (OSError, json.JSONDecodeError) as e:
        return [f"can't be read: {e}"]
    if bom.get("bomFormat") != "CycloneDX":
        problems.append("not CycloneDX")
    if not str(bom.get("specVersion", "")).startswith("1."):
        problems.append(f"unexpected specVersion {bom.get('specVersion')!r}")
    app = bom.get("metadata", {}).get("component", {})
    if not app.get("name") or not app.get("version") or app.get("version") == "0.0.0":
        problems.append("the app's name or version is missing")
    components = bom.get("components") or []
    if not components:
        problems.append("no components")
    for c in components:
        name = c.get("name", "?")
        if not c.get("version"):
            problems.append(f"{name}: no version")
        if not str(c.get("purl", "")).startswith("pkg:nuget/"):
            problems.append(f"{name}: no NuGet package URL")
        if c.get("type") != "framework" and not any(h.get("alg") == "SHA-512" for h in c.get("hashes") or []):
            problems.append(f"{name}: no SHA-512 hash")
    if not any(c.get("name", "").startswith("AprsNet.") for c in components):
        problems.append("no AprsNet packages")
    if not any(c.get("type") == "framework" and c.get("name") == "Microsoft.NETCore.App" for c in components):
        problems.append("the .NET runtime isn't listed")
    unlicensed = sorted(c.get("name", "?") for c in components if not c.get("licenses"))
    if unlicensed:
        print(f"{path}: no licence stated by {', '.join(unlicensed)}", file=sys.stderr)
    return problems


def main(paths: list[str]) -> int:
    if not paths:
        print(__doc__, file=sys.stderr)
        return 2
    failed = False
    for path in paths:
        problems = check(path)
        for p in problems:
            print(f"{path}: {p}", file=sys.stderr)
        if problems:
            failed = True
        else:
            with open(path, encoding="utf-8") as f:
                print(f"{path}: {len(json.load(f)['components'])} components")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
