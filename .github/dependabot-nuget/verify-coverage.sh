#!/usr/bin/env bash
# Fails when a centrally managed package is referenced by none of the Dependabot manifests.
# A package Dependabot cannot see is a package that is never updated — silently.
set -euo pipefail
cd "$(dirname "$0")"

# Restore at TOP LEVEL, not inside the command substitution below. `dotnet msbuild -getItem`
# EVALUATES without restoring, so it reports items for a manifest NuGet would reject: a probe adding a
# PackageReference with no matching PackageVersion left every manifest failing NU1010 while this script
# still printed "covers all 82". A restore inside `union=$(...)` does not fix that -- `set -e` does not
# abort the script from a command-substitution subshell, and the subshell's status is `sort`'s anyway,
# so the failure is swallowed twice over.
for p in Manifest.csproj Manifest.Tooling.csproj Manifest.Net8.csproj; do
  dotnet restore "$p" --nologo >/dev/null
done

union=$(for p in Manifest.csproj Manifest.Tooling.csproj Manifest.Net8.csproj; do
  dotnet restore "$p" --nologo >/dev/null
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
