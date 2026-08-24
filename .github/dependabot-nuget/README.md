# Dependabot NuGet manifest

Dependabot's NuGet updater re-runs full project discovery once per dependency it analyses. Against
this repository — 145 projects, central package management, 36 of them multi-targeted — a single
discovery took 10m46s and each re-run 4–17s, so the weekly job hit Dependabot's fixed 55-minute
cap having looked at 11–18 of 78 packages. Every run from 2026-07-27 to 2026-08-21 was cancelled
that way and no NuGet update PR was produced. Splitting the job by directory made it worse
(discovery is per directory) — see PRs #215–#219.

The projects here are what Dependabot discovers instead of the real solution. Together they
reference every `PackageVersion` in the root `Directory.Packages.props`, which is still the file
Dependabot edits — NuGet finds it by walking up from this directory. Discovery of three tiny
projects takes seconds, and Dependabot's per-dependency re-discovery is proportionally cheap.

- `Manifest.csproj` — everything, via `@(PackageVersion)`, minus the two packages below.
- `Manifest.Tooling.csproj` — Roslyn 4.x, which cannot share a restore graph with the Roslyn 5
  consumers. Mirrors the netstandard2.0 tooling projects.
- `Manifest.Net8.csproj` — the one package whose version is conditioned per TFM.

**Nothing builds these.** They are not in `Themia.sln` and CI never restores them, except
`verify-coverage.sh`, which fails CI if the union of their references no longer covers every
`PackageVersion` — the only way a package can silently fall out of Dependabot's view is an
explicit `Remove` with no mirror.
