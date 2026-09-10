#!/usr/bin/env bash
#
# Verifies the Themia.Data.Migrations build-time engine guard (THEMIA2001, spec §7 of
# docs/superpowers/specs/2026-09-09-data-migrations-engine-split.md) actually fires from a packed
# .nupkg. A unit test cannot see this: the guard lives in a buildTransitive/ MSBuild asset that only
# NuGet's restore/import pipeline wires up, and the single mistake that would make it a silent no-op —
# shipping it from build/ instead of buildTransitive/ — compiles fine and every unit test still passes.
#
#   1. Packs Themia.Data.Migrations, Themia.Data.Migrations.PostgreSql, Themia.Audit and
#      Themia.Audit.PostgreSql to a temporary local feed.
#   2. Structural check: the buildTransitive/ assets are actually inside the nupkgs (and nothing
#      shipped from build/ instead).
#   3. End-to-end, without an engine: a throwaway Exe project references Themia.Audit ONLY — not
#      Themia.Data.Migrations directly, so this proves the *transitive* case NuGet's build/ assets
#      cannot reach. Build must fail with THEMIA2001.
#   4. End-to-end, with an engine: a throwaway Exe project references Themia.Audit.PostgreSql ONLY,
#      which pulls in Themia.Data.Migrations.PostgreSql transitively. Build must succeed.
#   5. End-to-end, test project without an engine: a throwaway project that is an Exe AND sets
#      IsTestProject (the shape xUnit v3 and MSTest 3 produce — v3's migration guide requires
#      OutputType=Exe) references Themia.Audit ONLY. Build must SUCCEED: a test project never runs
#      migrations, so THEMIA2001 must not fire on it.
#
# Run from anywhere (resolves the repo root from its own location). Not part of `dotnet test
# Themia.sln` — it shells out to `dotnet pack`/`dotnet restore`/`dotnet build` several times over, which
# is slow relative to the unit suite. Wired into CI as its own eng/verify-*.sh step, the same way
# eng/verify-analyzer-flow.sh is (see .github/workflows/ci.yml's "analyzer-flow" job) — add a sibling
# "migrations-engine-guard" job calling this script the same way.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

VERSION="$(grep -oE '<Version>[^<]+</Version>' Directory.Build.props | head -1 | sed -E 's#</?Version>##g')"
[ -n "$VERSION" ] || { echo "ERROR: could not read <Version> from Directory.Build.props"; exit 1; }
echo "Themia version under test: $VERSION"

WORK="$(mktemp -d)"
FEED="$WORK/feed"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$FEED"

echo "==> Packing Themia.Data.Migrations + PostgreSql engine + Themia.Audit + Themia.Audit.PostgreSql..."
PACK_LOG="$WORK/pack.log"
: > "$PACK_LOG"
for proj in \
    src/neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj \
    src/neutral/Themia.Data.Migrations.PostgreSql/Themia.Data.Migrations.PostgreSql.csproj \
    src/neutral/Themia.Audit/Themia.Audit.csproj \
    src/neutral/Themia.Audit.PostgreSql/Themia.Audit.PostgreSql.csproj; do
  if ! dotnet pack "$proj" --configuration Release --output "$FEED" >> "$PACK_LOG" 2>&1; then
    echo "ERROR: 'dotnet pack $proj' failed — see output below:"; cat "$PACK_LOG"; exit 1
  fi
done

CORE_NUPKG="$FEED/Themia.Data.Migrations.$VERSION.nupkg"
PG_NUPKG="$FEED/Themia.Data.Migrations.PostgreSql.$VERSION.nupkg"
AUDIT_NUPKG="$FEED/Themia.Audit.$VERSION.nupkg"
AUDIT_PG_NUPKG="$FEED/Themia.Audit.PostgreSql.$VERSION.nupkg"
for nupkg in "$CORE_NUPKG" "$PG_NUPKG" "$AUDIT_NUPKG" "$AUDIT_PG_NUPKG"; do
  [ -f "$nupkg" ] || { echo "ERROR: $nupkg was not produced"; exit 1; }
done

echo "==> Structural checks (buildTransitive/, not build/)..."
# Listings are captured ONCE and matched with here-strings, never `unzip -l … | grep -q …`. Under
# `set -o pipefail` that pipeline is a coin flip: `grep -q` exits on the first match and closes the pipe,
# `unzip` takes SIGPIPE, and the pipeline reports the SIGPIPE — so a package that DOES contain the asset
# intermittently fails the check. Measured at roughly one run in eight here before the change.
CORE_LISTING="$(unzip -l "$CORE_NUPKG")"
PG_LISTING="$(unzip -l "$PG_NUPKG")"
grep -q "buildTransitive/Themia.Data.Migrations.targets" <<< "$CORE_LISTING" \
  || { echo "ERROR: Themia.Data.Migrations.targets missing from buildTransitive/ in $CORE_NUPKG"; exit 1; }
if grep -qE '^\s*[0-9]+\s+\S+\s+\S+\s+build/Themia\.Data\.Migrations\.targets$' <<< "$CORE_LISTING"; then
  echo "ERROR: Themia.Data.Migrations.targets ALSO shipped from build/ — build/ assets are only imported"
  echo "       for direct PackageReferences, so this alone would not fix the transitive case, but its"
  echo "       presence signals the packaging authored it in the wrong (or an extra) location."
  exit 1
fi
grep -q "buildTransitive/Themia.Data.Migrations.PostgreSql.props" <<< "$PG_LISTING" \
  || { echo "ERROR: Themia.Data.Migrations.PostgreSql.props missing from buildTransitive/ in $PG_NUPKG"; exit 1; }
echo "  OK: both build assets are packed under buildTransitive/."

# make_consumer <dir> <package-id> [extra-properties-xml]
make_consumer() {
  local dir="$1" package_ref="$2" extra_properties="${3:-}"
  mkdir -p "$dir"
  cat > "$dir/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="themia-local" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <config>
    <!-- Isolated global-packages folder so a stale same-version package already in ~/.nuget/packages
         cannot shadow the freshly-packed one — the gate must test THIS build. -->
    <add key="globalPackagesFolder" value="$WORK/gpf" />
  </config>
</configuration>
EOF
  cat > "$dir/consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    $extra_properties
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="$package_ref" Version="$VERSION" />
  </ItemGroup>
</Project>
EOF
  cat > "$dir/Program.cs" <<'EOF'
System.Console.WriteLine("ok");
EOF
}

echo "==> Without an engine: throwaway Exe referencing Themia.Audit only (pulls in Themia.Data.Migrations transitively)..."
WITHOUT="$WORK/without-engine"
make_consumer "$WITHOUT" "Themia.Audit"
WITHOUT_LOG="$WORK/without-engine-build.log"
set +e
dotnet build "$WITHOUT/consumer.csproj" --configuration Release 2>&1 | tee "$WITHOUT_LOG"
without_rc=${PIPESTATUS[0]}
set -e
if [ "$without_rc" -eq 0 ]; then
  echo "ERROR: consumer with NO engine package built successfully (exit 0) — THEMIA2001 did not fire."
  exit 1
fi
if grep -q "THEMIA2001" "$WITHOUT_LOG"; then
  echo "  OK: build failed naming THEMIA2001, as expected."
else
  echo "ERROR: consumer build failed (exit $without_rc) but NOT with THEMIA2001 — see log above."
  echo "       This could be a restore failure, not the guard we're testing."
  exit 1
fi

echo "==> With an engine: throwaway Exe referencing Themia.Audit.PostgreSql only (pulls in Themia.Data.Migrations.PostgreSql transitively)..."
WITH="$WORK/with-engine"
make_consumer "$WITH" "Themia.Audit.PostgreSql"
WITH_LOG="$WORK/with-engine-build.log"
set +e
dotnet build "$WITH/consumer.csproj" --configuration Release 2>&1 | tee "$WITH_LOG"
with_rc=${PIPESTATUS[0]}
set -e
if [ "$with_rc" -ne 0 ]; then
  echo "ERROR: consumer WITH the PostgreSql engine package failed to build (exit $with_rc) — see log above."
  exit 1
fi
if grep -q "THEMIA2001" "$WITH_LOG"; then
  echo "ERROR: THEMIA2001 fired even though the PostgreSql engine package was referenced."
  exit 1
fi
echo "  OK: build succeeded, no THEMIA2001."

echo "==> Test project without an engine: throwaway Exe with IsTestProject=true referencing Themia.Audit only..."
# The shape xUnit v3 and MSTest 3 produce. Before the IsTestProject term was added to the guard's
# condition, this build failed with THEMIA2001 — an adopter's test suite broken by a package it only
# references, never migrates with.
TESTPROJ="$WORK/test-project-no-engine"
make_consumer "$TESTPROJ" "Themia.Audit" "<IsTestProject>true</IsTestProject>"
TESTPROJ_LOG="$WORK/test-project-no-engine-build.log"
set +e
dotnet build "$TESTPROJ/consumer.csproj" --configuration Release 2>&1 | tee "$TESTPROJ_LOG"
testproj_rc=${PIPESTATUS[0]}
set -e
if grep -q "THEMIA2001" "$TESTPROJ_LOG"; then
  echo "ERROR: THEMIA2001 fired on an Exe TEST project (IsTestProject=true) that never runs migrations."
  echo "       xUnit v3 and MSTest 3 test projects are executables, so this would break an adopter's"
  echo "       test suite merely for referencing Themia.Audit."
  exit 1
fi
if [ "$testproj_rc" -ne 0 ]; then
  echo "ERROR: the Exe test project failed to build (exit $testproj_rc) for some other reason — see log above."
  exit 1
fi
echo "  OK: build succeeded, no THEMIA2001."

echo "==> PASS: THEMIA2001 fires transitively with no engine package, is silent once one is referenced, and never fires on a test project."
