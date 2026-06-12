# Follow-ups: Themia.Modules.Scheduling (deferred from PR #79 review)

Two findings from the comprehensive review of PR #79 (0.4.8 persistent Quartz) were judged
real but out of scope for that PR. Captured here so they aren't lost. Neither blocks 0.4.8.

## 1. Engine partition is triplicated, kept in sync only by a comment

**Where:**
- `src/modules/Themia.Modules.Scheduling/Migrations/QuartzAdoJobStoreMigration.cs` — `Up()` (positive
  `IfDatabase("postgres")`/`IfDatabase("sqlserver")` branches + the negative unsupported-engine guard)
  and `Down()` (its own inline `IfDatabase` dispatch, plus a hardcoded `"quartz"` literal instead of
  the `SchemaName` const).
- `src/modules/Themia.Modules.Scheduling/SchedulingModule.cs` — the `providerName` switch in
  `ConfigureServices` that selects `UsePostgres`/`UseSqlServer` (and `ToMigrationEngine`).

The supported-engine set (PostgreSQL + SQL Server) is encoded in **three** independent places, held
consistent only by the `LOCKSTEP` comment — adding an engine means three coordinated edits with
nothing (compiler or test) enforcing they agree. `Up()` compounds it by using two different matching
strategies for what must be the same partition: `IfDatabase("postgres")` (exact token) for the
positive branches vs `StartsWith("Postgres", OrdinalIgnoreCase)` for the guard. They agree today but
are not provably the same set.

**Risk if untouched:** a future engine (EF MySQL) added to the positive branches but missed in the
`StartsWith` guard would silently no-op for that engine (no schema, no throw) rather than fail fast.

**Suggested fix:** factor the supported-engine set into one shared source of truth (e.g. a small
`static readonly` set or mapping helper) consumed by all three sites; have `Down()` reuse `SchemaName`
and the same dispatch shape as `Up()`; make both `Up()` branches and the guard use one predicate form.
A unit test asserting an unsupported provider throws from `Up()` would convert the negative branch from
comment-enforced to test-enforced. Natural home: bundle with the EF MySQL provider work (the moment a
third engine actually arrives and the triplication first bites).

## 2. Integration-test flakiness hardening

**Where:** `tests/Themia.Modules.Scheduling.IntegrationTests/SchedulingModuleTests.cs`

- `PersistentScheduler_RecordsExecutionHistory_ToEfStore` polls `GetTotalJobsExecuted() > 0` for a
  fixed 5 s (50 × 100 ms), then calls `Shutdown` + `DisposeAsync` **before** the final
  `Assert.True(recorded, …)`. A slow-but-correct job that completes during/after shutdown still fails
  on a loaded CI host. Fixes: move the assert before shutdown (or re-query once after
  `Shutdown(waitForJobsToComplete: true)` drains), and/or raise the ceiling.
- `PersistentScheduler_SurvivesRestart_ViaAdoJobStore` and the history test dispose the scheduler /
  provider only on the happy path — a failed assert leaks the connection (and the `qrtz_locks` row),
  which can cascade into spurious failures in sibling tests sharing the one container per engine. Wrap
  scheduler/provider lifecycle in `try/finally` so failures stay isolated.

**Risk if untouched:** intermittent CI red that erodes trust in the suite; not a product defect.

## Not pursued (intentional)

- **Per-object existence guards for the qrtz migration** — the qrtz DDL is one atomic `Execute.Sql`
  block, so a partial schema can't arise from a failed replay (only manual intervention). The
  all-or-nothing root-table guard is sufficient and now documented as intentional in
  `QuartzAdoJobStoreMigration.cs`. The cutover-replay path itself is covered by
  `InitializeAsync_AdoptsExistingQuartzSchema_OnCutoverReplay` (both engines).
