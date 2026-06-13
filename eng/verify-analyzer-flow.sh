#!/usr/bin/env bash
#
# Verifies the Themia isolation analyzers (THEMIA103/104) flow transitively to an adopter that
# references a Themia.Framework.Data.* package. Transitive analyzer flow is the part of this feature
# most likely to silently break in a future packaging change (e.g. re-adding DevelopmentDependency or
# flipping an asset flag), and a green analyzer unit test does NOT prove it. This guards it.
#
#   1. Packs the whole solution to a temporary local feed.
#   2. Fast structural checks: the EFCore package's nuspec depends on Themia.Analyzers WITHOUT excluding
#      the Analyzers asset, and the analyzer package co-locates both DLLs (Roslyn load-context needs it).
#   3. End-to-end: a throwaway consumer referencing Themia.Framework.Data.EFCore calls DbSet<T>.Find and
#      must surface THEMIA104 at build time.
#
# Run from anywhere (resolves the repo root from its own location). Used by .github/workflows/ci.yml.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

VERSION="$(grep -oE '<Version>[^<]+</Version>' Directory.Build.props | head -1 | sed -E 's#</?Version>##g')"
[ -n "$VERSION" ] || { echo "ERROR: could not read <Version> from Directory.Build.props"; exit 1; }
echo "Themia version under test: $VERSION"

WORK="$(mktemp -d)"
FEED="$WORK/feed"
CONSUMER="$WORK/consumer"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$FEED" "$CONSUMER"

echo "==> Packing solution to local feed (this also builds)..."
# Capture pack output so a failure (compile error, RS0016, NETSDK1085, …) shows its MSBuild diagnostics
# instead of a bare non-zero exit. set -e would abort with no context if we sent this to /dev/null.
if ! dotnet pack Themia.sln --configuration Release --output "$FEED" > "$WORK/pack.log" 2>&1; then
  echo "ERROR: 'dotnet pack' failed — see output below:"; cat "$WORK/pack.log"; exit 1
fi

echo "==> Structural checks..."
EFCORE_NUPKG="$FEED/Themia.Framework.Data.EFCore.$VERSION.nupkg"
ANALYZER_NUPKG="$FEED/Themia.Analyzers.$VERSION.nupkg"
[ -f "$EFCORE_NUPKG" ]   || { echo "ERROR: $EFCORE_NUPKG was not produced"; exit 1; }
[ -f "$ANALYZER_NUPKG" ] || { echo "ERROR: $ANALYZER_NUPKG was not produced"; exit 1; }

DEP_LINE="$(unzip -p "$EFCORE_NUPKG" '*.nuspec' | grep -i 'id="Themia.Analyzers"' || true)"
echo "  EFCore -> Analyzers dependency: ${DEP_LINE:-<MISSING>}"
echo "$DEP_LINE" | grep -q 'id="Themia.Analyzers"' \
  || { echo "ERROR: the EFCore package does not depend on Themia.Analyzers — analyzers won't flow."; exit 1; }
# The Analyzers asset must NOT be excluded, or the analyzers won't reach consumers.
if echo "$DEP_LINE" | grep -qiE 'exclude="[^"]*Analyzers'; then
  echo "ERROR: the Themia.Analyzers dependency excludes the Analyzers asset — it will NOT flow to consumers."; exit 1
fi
# Both DLLs must sit together in analyzers/dotnet/cs (Roslyn isolates each analyzer DLL's load context;
# Themia.Analyzers.dll needs Themia.Generators.Abstractions.dll beside it or it fails to load).
for dll in Themia.Analyzers.dll Themia.Generators.Abstractions.dll; do
  unzip -l "$ANALYZER_NUPKG" | grep -q "analyzers/dotnet/cs/$dll" \
    || { echo "ERROR: $dll missing from analyzers/dotnet/cs in the analyzer package"; exit 1; }
done
echo "  OK: dependency flows the Analyzers asset; both DLLs co-located."

echo "==> Building a throwaway adopter that references Themia.Framework.Data.EFCore..."
cat > "$CONSUMER/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="themia-local" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <config>
    <!-- Isolated global-packages folder so a stale same-version package already in ~/.nuget/packages
         (CI caches it across runs) cannot shadow the freshly-packed one — the gate must test THIS build. -->
    <add key="globalPackagesFolder" value="$WORK/gpf" />
  </config>
</configuration>
EOF
cat > "$CONSUMER/consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <!-- Microsoft.EntityFrameworkCore (and Themia.Analyzers) arrive transitively. -->
    <PackageReference Include="Themia.Framework.Data.EFCore" Version="$VERSION" />
  </ItemGroup>
</Project>
EOF
cat > "$CONSUMER/Probe.cs" <<'EOF'
using Microsoft.EntityFrameworkCore;

// DbSet<T>.Find must raise THEMIA104 here — the analyzer flowed transitively from the data package.
public static class Probe
{
    public static void Use()
    {
        var set = default(DbSet<string>)!;
        _ = set.Find("x");
    }
}
EOF

BUILD_LOG="$WORK/consumer-build.log"
# A library build (no entry point); THEMIA104 is a Warning, so the build itself should SUCCEED — we then
# observe the warning. Capture the build's true exit code (PIPESTATUS[0], not tee's) so a restore or
# analyzer-load failure is reported as such, rather than misattributed to "transitive flow broken".
set +e
dotnet build "$CONSUMER/consumer.csproj" --configuration Release 2>&1 | tee "$BUILD_LOG"
build_rc=${PIPESTATUS[0]}
set -e
if [ "$build_rc" -ne 0 ]; then
  echo "ERROR: the consumer build failed (exit $build_rc) — a restore or analyzer-load problem, NOT"
  echo "       necessarily a missing THEMIA104. See the build output above."
  exit 1
fi

# Match the MSBuild warning line specifically (not the bare rule ID, which could appear in an
# analyzer-load error or a doc URL without the analyzer actually firing).
if grep -q "warning THEMIA104" "$BUILD_LOG"; then
  echo "==> PASS: an adopter of Themia.Framework.Data.EFCore receives THEMIA104."
else
  echo "==> FAIL: consumer built cleanly but THEMIA104 did not fire — transitive analyzer flow is broken."
  exit 1
fi
