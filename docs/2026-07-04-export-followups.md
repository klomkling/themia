# Follow-ups: Themia.Modules.Export (deferred from the 0.6.9 PR #139 review + code review)

Items surfaced during the PR #139 review and the follow-up multi-agent code review of the Export
module, judged real but out of scope for 0.6.9 (or deliberately declined). Captured so they aren't
lost. None block 0.6.9.

## 1. MySQL support is deferred (the plan intended it; there is no EF Core 10 provider)

**Where:**
- `src/modules/Themia.Modules.Export/Migrations/ExportSchemaMigration.cs` — `Up()` whitelists only
  `postgresql`/`sqlserver`, and the unsupported-provider guard throws for anything else.
- `src/modules/Themia.Modules.Export/DependencyInjection/ExportModuleServiceCollectionExtensions.cs`
  — the EF provider switch accepts only `Postgres`/`SqlServer`.
- `CHANGELOG.md` — the 0.6.9 entry states "PostgreSQL and SQL Server".

The Export spec/plan originally intended SQL Server + MySQL + PostgreSQL (the plan carries a
`case DatabaseProviderNames.MySql: db.UseMySql(...)` branch). Implementation dropped MySQL because
there is **no EF Core 10 MySQL provider** (Pomelo has no EF Core 10 build), matching the sibling
`Themia.Modules.Scheduling`, whose migration + DI are also PostgreSQL + SQL Server only. Migration
guard, DI switch, and CHANGELOG now agree on the two engines.

**Risk if untouched:** divergence from the repo-wide "Multi-DB Phase 1: SQL Server, MySQL, PostgreSQL"
statement; an adopter who configures the export module against MySQL gets a `NotSupportedException`
at startup rather than a working schema.

**Suggested fix:** bundle the Export MySQL leg with the framework-wide **EF MySQL provider** work — the
same Pomelo/EF-10 blocker already tracked in `docs/themia-architecture-overview.md` (line ~351,
"Deferred: EF MySQL provider") and `docs/2026-06-12-scheduling-followups.md` §1. When it lands: add the
`mysql` migration branch back, the DI provider `case`, the EF MySQL package reference, **and** the
per-provider concurrency mapping the `ThemiaDbContext.ApplyConcurrencyTokens` "LANDMINE for future
providers" remark calls out (MySQL has no `rowversion`, so the `byte[]` token would silently never fire).

## 2. (Not pursued — intentional) Full-column UPDATE in `ExportRunStore.UpdateAsync`

The code review flagged that `db.Runs.Update(run)` marks every column modified, emitting a full-column
`UPDATE` (including the large `ParametersJson`) even when only a few lifecycle fields changed.

**Not pursued**, because the 0.6.9 cleanup-poison fix made `FindExpiredAcrossTenantsAsync` /
`FindStaleRunningAcrossTenantsAsync` use `AsNoTracking()`, so their results are **detached** — and
`Update()` is then *required* to attach the entity before `SaveChanges`. Dropping `Update()` (relying
on change-tracking) would silently no-op for a detached entity and re-open the tracking poison. The
write amplification lands on a handful of small lifecycle rows (`ParametersJson` is the only large
column and never changes after creation), so it is negligible.

## 3. (Cross-reference) Delivery deferrals already documented in the spec

Recorded here for discoverability; both are deliberate v1 scope decisions in
`docs/superpowers/specs/2026-06-27-themia-modules-export-design.md` ("Out of scope"):

- **Email-attachment delivery** — v1 is signed-download-link only; the Notifications stack carries no
  attachment channel (`NotificationRequest`/`NotificationMessage` hold no bytes).
- **Streaming for very large exports** — superseded/deferred (see the async export design spec).

## Note — a Dapper peer for the Export stores is *not* a gap

The Export module's own stores (`ExportRunStore`/`ExportScheduleStore`) are EF-only **by design**
(the spec: "EF entities map to the tables for reads/writes"). The framework-level
`IDataFilterScope.BypassSoftDeleteFilter()` **does** work on both the EF and Dapper peers (a Dapper
conformance fact was added in 0.6.9). An adopter picks a data peer for their *app*; the module owning
EF stores for its own two tables is intentional, not an omission.

## Resolved in the 0.6.9 hardening (for traceability, not outstanding)

These review findings were fixed in the 0.6.9 hardening pass (commit `52eb4f7`), not deferred:

- CleanupJob shared-context **tracking poison** — `AsNoTracking` on the bulk find + detach-on-failure
  in `UpdateAsync`.
- **Notify-after-success** flipping a stored `Succeeded` run to `Failed` — the completion notification
  moved outside the failure `try` (best-effort).
- `MarkFailed` retaining `StorageKey`/`ExpiresAt` (**orphaned-blob leak**) — now cleared.
- **Non-atomic create+enqueue** — compensation to `Failed` / schedule soft-delete on scheduling failure.
- **Runs orphaned in `Running`** by a host restart — `StartedAt` + startup reconciliation gated by
  `ExportModuleOptions.StaleRunGracePeriod`.
- Null `CleanupCron` validator crash, swallowed malformed job-data parse, implicit `Pending` entry
  state, and disabled-schedule log visibility.
