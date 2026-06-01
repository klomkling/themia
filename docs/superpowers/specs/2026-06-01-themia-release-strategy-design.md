# Themia Release Strategy & Build-Order Roadmap

**Status:** Accepted (2026-06-01)
**Scope:** How Themia packages are versioned, sequenced, and published. The companion to
[`themia-architecture-overview.md`](../../themia-architecture-overview.md) — that doc says *what*
the packages are; this one says *in what order they are built and at which version they ship*.

---

## 1. Core principle — phase ≠ version

Two independent counters, deliberately kept separate:

- **Phase** — a build-*priority* grouping (Phase 0 rename, Phase 1 cross-cutting, …). Answers
  "what do we work on next." Defined in the architecture overview's Phase roadmap.
- **Version** — a *release-milestone* counter for the shared package set. Answers "what is
  published." A single number shared by every `Themia.*` package.

They do **not** align 1:1 — neutral cores underpin several phases, and Phase 0 is a rename rather
than a "feature." Do not force `Phase N = v0.N`. The build order maps onto versions explicitly
(§3); it is not derived from phase numbers.

## 2. Versioning model (pre-1.0, single shared version)

| Rule | Meaning |
|---|---|
| `0.x` = unstable API | Public surface may change between minors. |
| **MINOR** bump (`0.1`→`0.2`) | One *release milestone* — a coherent batch of new packages/capabilities. |
| **PATCH** bump (`0.1.0`→`0.1.1`) | Bug-fix-only release between milestones. |
| **1.0.0** | First *stable, API-committed* release (framework core + Phase-1 modules solid). |
| Single shared version | Every `Themia.*` carries the same number (`<Version>` in `Directory.Build.props`); unchanged packages re-publish at the new number. Normal for this layout. |

**Cadence:** publish **incrementally — one MINOR per milestone**. Don't batch many milestones
behind a single big release; ship each coherent set as it lands. Bug fixes between milestones go
out as PATCH releases.

Breaking changes during `0.x` are allowed (that's what `0.x` signals) but should still be noted in
`CHANGELOG.md` and `MIGRATION.md`. The `1.0.0` line is where the public API is committed and SemVer
breaking-change discipline begins in earnest.

## 3. Build-order roadmap (Option A — framework next)

Chosen build order: prove the harness on the smallest standalone package (done), publish it, then
land the **framework core** (the bulk of the existing value) into the proven harness, then the
remaining neutral cores, then the Phase-1 modules.

| Version | Deliverable | Layer / Phase |
|---|---|---|
| **0.1.0** *(immediate)* | `Themia.AspNetCore` | neutral core — **done, merged** (`#13`/`#14`) |
| **0.2.0** | Framework core — rename `zenity-v2` → `Themia.Framework.{Core,Data.EFCore,AspNetCore}`, `Themia.MultiTenancy`, `Themia.Mediator`, `Themia.Caching`, `Themia.Logging`, `Themia.Services` | **Phase 0** (framework) |
| **0.3.0** | Remaining neutral cores — `Themia.Quartz`, `Themia.Exceptional(.SqlServer/.MySql/.PostgreSql)` | Phase 1 (neutral) |
| **0.4.0** | Phase-1 modules — `Themia.Modules.{Scheduling,ExceptionLogging,Identity,Storage}` (+ multi-DB baseline) | Phase 1 (modules) |
| **0.5.0+** | Phase 2 (Notifications, Pdf, Export) → Phase 3 (Geo, AI, Audit; Sequences EF-port; SourceGenerator/analyzer merge) | Phase 2 / 3 |
| **1.0.0** | Public API committed across framework core + Phase-1 modules | — |

Rationale for Option A: the release pipeline is the only part of the harness still unproven, so
`0.1.0` proves it on one finished package; the framework is what makes every later module usable,
so it earns the `0.2.0` slot; the remaining neutral cores (`Quartz`, `Exceptional.*`) are already
specced and slot in at `0.3.0` alongside/after the framework rather than blocking it.

## 4. Immediate action — cut `0.1.0`

`Themia.AspNetCore` is merged to `main` but not yet on NuGet. Releasing it is the first concrete
plan (see the implementation plan produced from this spec). Gate items:

- Add the `NUGET_API_KEY` repository secret and the `nuget` deployment environment the release
  workflow expects.
- Reserve the `Themia.` package-ID prefix on nuget.org (prevents ID hijacking; ties packages to
  the owner account).
- Promote `CHANGELOG.md` `[Unreleased]` → a dated `0.1.0` heading.
- Trigger the existing release workflow (reads `<Version>` from `Directory.Build.props`, packs the
  solution, publishes, tags, and creates the GitHub Release). Confirm its trigger mechanism and
  that `<Version>` is `0.1.0` before release.

These steps are detailed in the implementation plan; the exact workflow trigger is verified there
against `.github/workflows/release.yml`.

## 5. Phase 0 (the `0.2.0` framework rename) is its own brainstorm

The framework rename is large and dependency-sequenced (roughly Core → Data.EFCore →
Mediator/Caching/Logging/Services/MultiTenancy → Framework.AspNetCore, then it feeds the modules)
and depends on inspecting the actual `zenity-v2` source tree. It gets its **own** spec → plan cycle
when we start it. This roadmap deliberately does not detail it — it only fixes its slot (`0.2.0`)
and order.

## Open items / future revisions

- The `0.x → 1.0.0` trigger (what "API committed" requires) is left intentionally loose until the
  framework + Phase-1 modules exist; revisit before `0.4.0`.
- Per-package independent versioning is explicitly **not** adopted now (single shared version); the
  architecture overview notes this can be revisited "only if release cadences genuinely diverge."
