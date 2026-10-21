#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Writes a CycloneDX SBOM (JSON) for each app a release ships (docs/features/F4.17-sbom.md): the desktop app, the
# gateway and the browser app. Each lists every NuGet package in that app, transitive ones included, with its version,
# licence and hash, from the same restore the build uses. One desktop SBOM covers every platform: the packages are the
# same for each, only which native files are copied differs. The container image's own SBOM (with its OS packages) is
# made by the release workflow from the image.
#
#   tools/sbom.sh <version> <output folder>
set -euo pipefail

version=${1:?version, such as 0.6.0-alpha.1}
out=${2:?output folder}
slug=$(dotnet msbuild src/AprsMap.Desktop/AprsMap.Desktop.csproj -getProperty:ProductSlug)
# The .NET runtime the apps are built and published with: shipped inside the desktop app (self-contained), the base
# of the gateway's image, and in the browser app as WebAssembly.
runtime=$(dotnet msbuild src/AprsMap.Desktop/AprsMap.Desktop.csproj -getProperty:BundledNETCoreAppPackageVersion)
mkdir -p "$out"

framework() { # file, framework names...: adds the shared frameworks, which aren't packages, as components
  python3 - "$out/$1" "$runtime" "${@:2}" <<'PY'
import json, sys
path, version, names = sys.argv[1], sys.argv[2], sys.argv[3:]
with open(path, encoding="utf-8") as f:
    bom = json.load(f)
for name in names:
    bom.setdefault("components", []).append(
        {
            "type": "framework",
            "bom-ref": f"pkg:nuget/{name}.Ref@{version}",
            "name": name,
            "version": version,
            "supplier": {"name": "Microsoft"},
            "licenses": [{"license": {"id": "MIT"}}],
            "purl": f"pkg:nuget/{name}.Ref@{version}",
            "scope": "required",
        }
    )
with open(path, "w", encoding="utf-8") as f:
    json.dump(bom, f, indent=2)
PY
}

sbom() { # project, file name, BOM name
  dotnet dotnet-CycloneDX "src/$1/$1.csproj" --configuration Release --output-format Json \
    --include-license-text --spec-version 1.6 \
    --set-name "$3" --set-version "$version" --set-type Application \
    --output "$out" --filename "$2" > /dev/null
  echo "$out/$2"
}

sbom AprsMap.Desktop "$slug-v$version-desktop.cdx.json" "$slug"
framework "$slug-v$version-desktop.cdx.json" Microsoft.NETCore.App
sbom AprsMap.Gateway "$slug-v$version-gateway.cdx.json" "$slug-gateway"
framework "$slug-v$version-gateway.cdx.json" Microsoft.NETCore.App Microsoft.AspNetCore.App
sbom AprsMap.Browser "$slug-v$version-browser.cdx.json" "$slug-browser"
framework "$slug-v$version-browser.cdx.json" Microsoft.NETCore.App
