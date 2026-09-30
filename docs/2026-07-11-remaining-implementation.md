# Remaining implementation — roadmap-level (not yet built)

Snapshot as of 2026-10-01. Current shipped version: **0.30.1**. Originally written 2026-07-11 at
0.7.2; every Phase 0–3 deliverable it listed as unbuilt (Notifications, Geo, AI, Audit, Sequences,
SourceGenerator/analyzer merge, Phase 0 rename) has since shipped. This note indexes the *bigger,
not-yet-built* items; smaller per-module hardening/consistency follow-ups live in their own docs
(linked at the bottom).

Source of truth for phases and per-package status: `docs/themia-architecture-overview.md` →
"Phase roadmap" and "Specs index".

## Roadmap-level (not yet built)

| Item | Notes |
|---|---|
| **EMVCo TLV/CRC extraction out of `Themia.PromptPay`** | `⬜ 2026-09-22-themia-emvcoqr-design.md` — spec written, no plan. **Deferred**: builds only on the trigger in the spec's header. |
| **`Themia.Analyzers.CodeFixes`** | Never built; the shipped tooling family is diagnostic-only (overview §E). |
| **Serenity adapters** (`Idevs.Net.CoreLib.*`) | `[DEFERRED]` — YAGNI. Built only if/when PowerACC actually migrates; PowerACC is not a design driver. |

## Cross-cutting deferred (framework-wide — each warrants its own spec)

1. **EF Core 10 MySQL provider.** No Pomelo/EF-10 build exists, so Export **and** Scheduling
   migrations + DI are PostgreSQL + SQL Server only; configuring MySQL throws `NotSupportedException`
   at startup. Diverges from the repo-wide "Multi-DB Phase 1: SQL Server, MySQL, PostgreSQL." When it
   lands it also needs the **per-provider concurrency token** work (MySQL has no `rowversion`, so the
   `byte[]` token in `ThemiaDbContext.ApplyConcurrencyTokens` would silently never fire).
   Refs: `docs/2026-07-04-export-followups.md` §1, `docs/2026-06-12-scheduling-followups.md` §1,
   `docs/themia-architecture-overview.md` ("Deferred: … concurrency").
2. **Sanctioned global-record write path + `IncludeGlobalRecordsForTenants` peer alignment.**
   Framework owns the *read* side (EF default `true`, Dapper default `false`) but has no symmetric
   *write* path; modules hand-roll `if (TenantId is null) BypassTenantFilter()`. Changing defaults
   alters tenant-isolation semantics for every adopter → own spec/plan/review cycle.
   Ref: `docs/2026-06-14-identity-followups.md` (Architecture).
3. **EF audit-user bridge.** EF audit reads `ThemiaDbContext.CurrentUserId` (virtual, defaults null),
   not `ICurrentUserAccessor`; adopters must override. A framework bridge would make audit
   correct-by-default on both peers (Dapper already reads `ICurrentUserAccessor`).

**Resolved since the 2026-07-11 snapshot:** the DI descriptor-scan is centralized —
`ContributeDapperMappings` lives in `Themia.Framework.Data.Dapper` (`Mapping/DapperMappingRegistration.cs`)
and Storage, Notifications and Messaging share it.

Other items deferred in the overview (MariaDB support, FluentMigrator 6→8) are tracked in
`docs/themia-architecture-overview.md` and are not repeated here.

## Per-module follow-ups (already documented — hardening/consistency, not roadmap gaps)

- Identity — `docs/2026-06-14-identity-followups.md`
- Scheduling — `docs/2026-06-12-scheduling-followups.md`
- Export — `docs/2026-07-04-export-followups.md`
- Isolation analyzers — `docs/2026-06-13-isolation-analyzer-followups.md` (all items resolved in 0.4.9/0.4.10)
