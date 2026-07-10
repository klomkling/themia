# Themia.Framework metapackage + package-selection docs — design

**Date:** 2026-07-11
**Status:** approved
**Problem:** Themia ships 47 NuGet packages. The per-package fan-out is deliberate (driver
isolation, selectable data peers, TFM policy) and stays; but a new adopter faces "which 6–7
core packages do I assemble?" friction. Reduce the confusion without merging packages.

## Decision

One new **assembly-less metapackage** plus a **"Which packages do I reference?"** docs section.
No packages are merged, renamed, or retired.

Two alternatives considered and rejected:

- **Opinionated peer+provider bundles** (e.g. `Themia.Framework.EFCore.SqlServer`, 5 packages):
  saves one reference line but multiplies with every future peer/provider, and baking a peer into
  a bundle name erodes the "EF/Dapper are selectable first-class peers" decision. Additive later
  if adopters still stumble — nothing here blocks it.
- **Bundling both peers per provider**: rejected outright — drags both driver stacks into every
  app, defeating the peer split.
- **`Themia.Modules.Identity.All`**: unnecessary. `Themia.Modules.Identity.AspNetCore` already
  references all four other Identity packages (`.Abstractions`, core, `.Tokens.AspNetCore`,
  `.ExternalAuth.AspNetCore`) — it *is* the umbrella. Solved by docs, not a package.

## 1. `Themia.Framework` metapackage

- **Location:** `src/framework/Themia.Framework/Themia.Framework.csproj`, added to `Themia.sln`
  (release packs the solution, so it rides the existing pipeline and shared version automatically).
- **TFM:** `net10.0` (framework core is net10-only).
- **Shape:** standard assembly-less metapackage — `IncludeBuildOutput=false`,
  `NoWarn NU5128`, no source files, no `lib/` content. Only dependencies:

  | Dependency | Why |
  |---|---|
  | `Themia.Framework.Core` | framework kernel |
  | `Themia.Logging` | cross-cutting |
  | `Themia.Caching` | cross-cutting |
  | `Themia.Services` | cross-cutting |
  | `Themia.MultiTenancy` | tenancy kernel |
  | `Themia.Mediator` | mediator + source-gen |
  | `Themia.MultiTenancy.Mediator` | tenancy⇄mediator bridge |
  | `Themia.Framework.Data.Abstractions` | data seam (no driver) |
  | `Themia.Framework.AspNetCore` | ASP.NET integration — Themia is a web-app framework |

  Some entries are transitively implied (Mediator already pulls Caching/Logging/Core); they are
  listed explicitly anyway — a metapackage's dependency list is its documentation.

- **Deliberately excluded:** any `Themia.Framework.Data.EFCore*` / `Dapper*` package. Picking the
  data peer + DB provider is a conscious architectural choice; the adopter adds exactly **one**
  such package next to the metapackage.
- **Worker services / non-HTTP apps:** the bundle includes `Framework.AspNetCore` (pulls the
  ASP.NET Core shared framework). Non-web adopters hand-pick individual packages instead —
  stated in the docs.
- **Versioning/release:** shared version counter, no workflow changes.

## 2. README — "Which packages do I reference?"

New section in the root `README.md`:

1. **Quickstart** — a running multi-tenant web stack is two references:
   `Themia.Framework` + one of `Themia.Framework.Data.{EFCore|Dapper}.{SqlServer|PostgreSql|MySql}`
   (note: EFCore.MySql not yet available — no EF Core 10 MySQL provider).
2. **Scenario matrix** — scenario → exact package(s) to add:
   - Identity (users/roles/JWT/external login) → `Themia.Modules.Identity.AspNetCore` (umbrella)
   - Scheduling → `Themia.Modules.Scheduling`
   - Storage → `Themia.Modules.Storage` (+ `Themia.Storage.S3` for S3)
   - Export → `Themia.Modules.Export` (+ `Themia.Export.Excel` for xlsx)
   - Pdf → `Themia.Modules.Pdf`
   - Exception logging/dashboard → `Themia.Exceptional` + one `Themia.Exceptional.{engine}` + `Themia.Exceptional.AspNetCore` (dashboard/middleware)
   - Notifications → `Themia.Modules.Notifications` + one `Themia.Modules.Notifications.{engine}`
3. **Why so many packages** — one short paragraph: driver isolation + selectable peers; the
   metapackage never picks the data peer for you.

(Scenario entries are verified against actual csproj references during implementation, not
copied from this spec.)

## Testing

- Pack-level test: `dotnet pack` of `Themia.Framework` produces a nupkg whose nuspec lists
  exactly the 9 dependencies above and contains no `lib/` entries. Implemented in the existing
  test style (a unit test shelling `dotnet pack` is acceptable, or a CI assertion in the pack
  step if a test project is disproportionate).
- Compile smoke: a test (or existing integration project) referencing only the metapackage +
  one peer package builds — proves the bundle is sufficient for the quickstart claim.

## Out of scope

- Merging/retiring any existing package (incl. folding `Identity.Abstractions` into core).
- Opinionated per-peer bundles (`Themia.Framework.EFCore.SqlServer`, …) — revisit on demand.
- Module-level metapackages.
