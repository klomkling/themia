#!/usr/bin/env bash
#
# Installs the Chromium build PuppeteerSharp expects into a directory, ONCE, and prints the path of its
# executable on stdout (everything else goes to stderr).
#
# Why: Themia.ETax.IntegrationTests renders in several test classes that xunit runs in parallel. Each one
# used to call BrowserFetcher on its own and they raced on the same chrome-linux64.zip
# ("IOException: ... being used by another process"), failing the nightly integration run. The workflow
# runs this once, before the tests, and exports the printed path as PUPPETEER_EXECUTABLE_PATH, which
# the tests pass to ThemiaPdfOptions.ExecutablePath. A re-run into the same directory downloads nothing.
#
# The build is the one the repo's PuppeteerSharp version pins (read from Directory.Packages.props), so a
# bump of that package changes the Chromium the nightly proves, exactly as before.
#
# Usage: eng/fetch-chrome.sh <install-dir>
# Run from anywhere (resolves the repo root from its own location). Needs the dotnet SDK and nuget.org.
set -euo pipefail

INSTALL_DIR="${1:?usage: eng/fetch-chrome.sh <install-dir>}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# grep -m1 rather than `| head -1`: with SIGPIPE ignored (trap '' PIPE) a writer into a closed pipe fails
# with EPIPE instead of exiting quietly.
VERSION="$(grep -m1 -oE '<PackageVersion Include="PuppeteerSharp" Version="[^"]+"' "$REPO_ROOT/Directory.Packages.props" \
  | sed -E 's/.*Version="([^"]+)"/\1/')"
[ -n "$VERSION" ] || { echo "ERROR: could not read the PuppeteerSharp version from Directory.Packages.props" >&2; exit 1; }
echo "Installing the Chromium PuppeteerSharp $VERSION expects into $INSTALL_DIR" >&2

# A throwaway project OUTSIDE the repo, so the repo's Directory.Build.props (warnings as errors, XML docs,
# analyzers) does not apply to it. Same approach as eng/verify-analyzer-flow.sh.
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$INSTALL_DIR"
INSTALL_DIR="$(cd "$INSTALL_DIR" && pwd)"
RESULT="$WORK/executable-path.txt"

cat > "$WORK/FetchChrome.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="PuppeteerSharp" Version="$VERSION" />
  </ItemGroup>
</Project>
CSPROJ

cat > "$WORK/Program.cs" <<'CS'
using PuppeteerSharp;

var fetcher = new BrowserFetcher(new BrowserFetcherOptions { Path = args[0] });
var installed = await fetcher.DownloadAsync();
await File.WriteAllTextAsync(args[1], installed.GetExecutablePath());
CS

# Build output goes to stderr so stdout carries only the executable path.
dotnet run --project "$WORK/FetchChrome.csproj" --configuration Release -- "$INSTALL_DIR" "$RESULT" >&2

EXECUTABLE="$(cat "$RESULT")"
[ -x "$EXECUTABLE" ] || { echo "ERROR: the installed Chromium is not executable: $EXECUTABLE" >&2; exit 1; }
echo "$EXECUTABLE"
