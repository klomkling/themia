# Remaining implementation — roadmap-level (not yet built)

Snapshot as of 2026-07-11. Current shipped version: **0.7.2** (published to NuGet on this date).
No open GitHub issues — all remaining work is tracked in docs. This note indexes the *bigger,
not-yet-built* items; smaller per-module hardening/consistency follow-ups live in their own docs
(linked at the bottom).

Source of truth for phases: `docs/themia-architecture-overview.md` → "Phase roadmap".

## Roadmap-level (not yet built)

| Item | Phase | Notes |
|---|---|---|
| **Notifications module** | Phase 2 | Plans exist (`docs/superpowers/plans/2026-06-22-themia-notifications-core.md`, `…-themia-modules-notifications.md`) but not marked ✅. Pdf ✅ (0.7.0) and Export ✅ (0.6.9) shipped; Notifications is the open Phase-2 deliverable. |
| **Phase 3 — Advanced** | Phase 3 | Geo, AI, Audit; `ISequenceProvider` EF-port into `Themia.Framework.Data`; SourceGenerator/analyzer merge (rename diagnostic IDs `IDEVSGEN1xx → THEMIA1xx`). All unstarted. |
| **Phase 0 rename spec** | Phase 0 | `⬜ Phase 0 framework rename (0.2.0)` — needs its own spec when started (architecture-overview specs index). |
| **Serenity adapters** (`Idevs.Net.CoreLib.*`) | Ongoing / Strangler | `[DEFERRED]` — YAGNI. Built only if/when PowerACC actually migrates; PowerACC is not a design driver. |

## Cross-cutting deferred (framework-wide — each warrants its own spec)

1. **EF Core 10 MySQL provider.** No Pomelo/EF-10 build exists, so Export **and** Scheduling
   migrations + DI are PostgreSQL + SQL Server only; configuring MySQL throws `NotSupportedException`
   at startup. Diverges from the repo-wide "Multi-DB Phase 1: SQL Server, MySQL, PostgreSQL." When it
   lands it also needs the **per-provider concurrency token** work (MySQL has no `rowversion`, so the
   `byte[]` token in `ThemiaDbContext.ApplyConcurrencyTokens` would silently never fire).
   Refs: `docs/2026-07-04-export-followups.md` §1, `docs/2026-06-12-scheduling-followups.md` §1,
   `docs/themia-architecture-overview.md` (~line 249 "Deferred: … concurrency", ~line 351).
2. **Sanctioned global-record write path + `IncludeGlobalRecordsForTenants` peer alignment.**
   Framework owns the *read* side (EF default `true`, Dapper default `false`) but has no symmetric
   *write* path; modules hand-roll `if (TenantId is null) BypassTenantFilter()`. Changing defaults
   alters tenant-isolation semantics for every adopter → own spec/plan/review cycle.
   Ref: `docs/2026-06-14-identity-followups.md` (Architecture).
3. **EF audit-user bridge.** EF audit reads `ThemiaDbContext.CurrentUserId` (virtual, defaults null),
   not `ICurrentUserAccessor`; adopters must override. A framework bridge would make audit
   correct-by-default on both peers (Dapper already reads `ICurrentUserAccessor`).
4. **Centralize the DI descriptor-scan.** `ContributeDapperMappings` /
   `SchedulingModule.GetRegisteredInstance<T>` are duplicated across modules; extract a shared
   `Themia.Framework.Core` helper before a third module copies it.

## Per-module follow-ups (already documented — hardening/consistency, not roadmap gaps)

- Identity — `docs/2026-06-14-identity-followups.md`
- Scheduling — `docs/2026-06-12-scheduling-followups.md`
- Export — `docs/2026-07-04-export-followups.md`
- Isolation analyzers — `docs/2026-06-13-isolation-analyzer-followups.md` (all items resolved in 0.4.9/0.4.10)
