#!/usr/bin/env bash
# Fails when a centrally managed package is referenced by none of the Dependabot manifests.
# A package Dependabot cannot see is a package that is never updated — silently.
set -euo pipefail
cd "$(dirname "$0")"

union=$(for p in Manifest.csproj Manifest.Tooling.csproj Manifest.Net8.csproj; do
  dotnet msbuild "$p" -getItem:PackageReference -nologo | python3 -c \
    'import json,sys; [print(i["Identity"]) for i in json.load(sys.stdin)["Items"]["PackageReference"]]'
done | sort -u)

ids=$(grep -oE 'PackageVersion Include="[^"]+"' ../../Directory.Packages.props | sed 's/.*="//;s/"//' | sort -u)

missing=$(comm -23 <(echo "$ids") <(echo "$union"))
if [ -n "$missing" ]; then
  echo "::error::These PackageVersion entries are referenced by no Dependabot manifest, so Dependabot will never update them:"
  echo "$missing" | sed 's/^/  /'
  exit 1
fi
echo "Dependabot manifests cover all $(echo "$ids" | wc -l | tr -d ' ') centrally managed packages."
