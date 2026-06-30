# Themia.Modules.Export — Design (async / scheduled export)

**Status:** approved (brainstorm)
**Date:** 2026-06-27
**Phase:** 2 follow-on (Productivity) — the asynchronous/queued layer above the stateless export cores.
**Builds on:** `Themia.Export` + `Themia.Export.Excel` (the shipped neutral writers, unchanged).
**Supersedes for the async path:** the "Out of scope (YAGNI)" items _streaming for very large exports_ and _asynchronous / queued exports written to Storage with a Notifications ping_ in `docs/superpowers/specs/2026-06-24-themia-export-design.md`.

## Goal

A tenant-aware module that runs exports **off the request thread** — on demand or on a cron schedule — writes the produced file to Storage, and notifies the requesting user (email + in-app) when it is ready or has failed. Solves the "large export without an HTTP timeout" problem and the "email me this report every Monday" problem with one execution path.

## Boundary (what it is / is not)

- **Is:** background orchestration of the neutral export writers — request/queue, run, persist bytes, deliver, retain, clean up. A tenant-aware `IThemiaModule`.
- **Is not:** a new file-format engine. CSV/xlsx production stays in the neutral `Themia.Export(.Excel)` cores; additional backends/formats are a **separate spec (B)**, not this one.
- **Is not:** a synchronous streaming API. Exports buffer to `byte[]` inside the job (the neutral writers' existing contract) and are streamed to Storage, not to an HTTP response.
- **Is not:** persisted export _templates_ (dropped as YAGNI) or an ASP.NET package (hosts call `IExportRequestService` directly).

## Decided behavior (brainstorm outcomes — do not relitigate)

1. **Triggers:** both **on-demand** and **recurring (cron)**. Both converge on one execution path; only the trigger differs.
2. **Data source:** keyed **`IExportDefinition`** registered in app DI. The persisted job stores only `{ definitionKey, parametersJson, format }`; the definition reconstructs rows + columns at run time. No delegates/in-memory rows are ever serialized.
3. **Scope / filter:** three layers — **global** (tenant always; definition-level invariants such as "always `ctx.UserId`"), **per-schedule** (fixed `TParams` stored on the schedule, with _relative_ values resolved at fire-time), **per-request** (`TParams` on each submission). Per-request params can only narrow _within_ what the definition allows.
4. **Delivery:** persist to Storage, then notify (email + in-app) with a **signed download link** (`GetDownloadUrlAsync`). **Link-only** — the Notifications stack has no attachment channel today (`NotificationRequest` / `NotificationMessage` carry no bytes), so size-threshold attachment delivery is deferred to a later spec. Delivery is **eventual** (Notifications outbox drain).
5. **Retention:** fixed default (**7 days**), configurable; a recurring cleanup job deletes expired bytes and marks the run `Expired`.
6. **Failure:** any exception → `Failed` + stored error + failure notification. **No retry.**
7. **Soft-delete:** infra records soft-delete (recoverable/audited); produced bytes hard-delete at retention. Exports can **opt in to include soft-deleted business rows** via a new, sanctioned data-layer bypass (below).
8. **Execution host:** reuse `Themia.Modules.Scheduling` (Quartz) as the engine — one-shot job for on-demand, cron trigger for recurring, recurring cleanup job. The module owns its own domain tables for run/schedule state.

## Architecture & packaging

```
Themia.Export / Themia.Export.Excel   (neutral writers — shipped, unchanged)
                  ▲
Themia.Modules.Export  (net10.0, IThemiaModule)  ──► Themia.Modules.Storage      (persist bytes, signed URL)
   • IExportDefinition registry                  ──► Themia.Modules.Scheduling    (Quartz: one-shot + cron)
   • IExportRequestService (submit/schedule/list)──► Themia.Modules.Notifications (email + in-app dispatch)
   • ExportJob : IJob, CleanupJob : IJob         ──► Themia.Framework.Data.*       (tenant + audit + soft-delete + UoW)
   • export_runs / export_schedules store (EF + FluentMigrator DDL)
```

- **Single package** `Themia.Modules.Export` (`net10.0` — module TFM policy). References both neutral writers so it produces CSV or xlsx out of the box. (The "CSV-only never pulls ClosedXML" rule is a neutral-core concern; a host installing async-export wants full export.)
- **Dependencies:** `Themia.Export`, `Themia.Export.Excel`, `Themia.Modules.Storage`, `Themia.Modules.Scheduling`, `Themia.Modules.Notifications`, framework core + data.
- **Scheduler seam + precondition:** the module schedules through the standard Quartz `ISchedulerFactory` / `IScheduler` registered by `SchedulingModule`. But Scheduling can be configured to register **no** scheduler (host-supplied). The module's `InitializeAsync` must assert an `ISchedulerFactory` is resolvable and fail fast at startup — never at first submit.
- **Persistence:** the module owns `export_runs` and `export_schedules`. DDL is owned by **FluentMigrator** (one migration, `IfDatabase(...)` per engine — SQL Server / MySQL / PostgreSQL), the single schema authority for both layers — **no `dotnet ef migrations add`**, mirroring `Themia.Modules.Scheduling`. EF entities map to the tables for reads/writes. **No per-engine packages** (the single migration covers all three engines).
- **Tenant isolation:** both tables carry `TenantId`; the data layer's tenant filter enforces isolation on every read/write. A tenant only ever sees its own runs/schedules.

## Public contract

### Authoring (app side)

```csharp
namespace Themia.Modules.Export;

/// What the registry holds — keyed, non-generic.
public interface IExportDefinition
{
    string Key { get; }                                   // e.g. "sales-report"
    bool AllowsIncludeSoftDeleted { get; }                // gate for the soft-delete opt-in (default false)
    Task<ExportResult> ExportAsync(ExportContext context, CancellationToken ct);
}

/// Convenience base: typed rows + typed, validated filter params. All delegate/generic code
/// stays in app code; the base deserializes + validates params, then dispatches to the right
/// neutral writer (ICsvExporter / IExcelExporter) by context.Format.
public abstract class ExportDefinition<TRow, TParams> : IExportDefinition
    where TParams : new()
{
    public abstract string Key { get; }
    public virtual bool AllowsIncludeSoftDeleted => false;

    protected abstract IReadOnlyList<ExportColumn<TRow>> Columns(TParams p, ExportContext ctx);
    protected abstract Task<IReadOnlyList<TRow>> RowsAsync(TParams p, ExportContext ctx, CancellationToken ct);
    protected virtual IEnumerable<ReportHeader> Headers(TParams p, ExportContext ctx) => [];
    // base ExportAsync: ParametersJson → TParams (System.Text.Json) → validate → RowsAsync → writer(Format).
}
```

- `ExportContext` = `{ TenantId, UserId, string? ParametersJson, ExportFormat Format, string? FileName, bool IncludeSoftDeleted }`.
- A params-free, non-generic base (`ExportDefinition<TRow>`) is also provided for exports with no filter.
- `TParams` validation at the boundary uses DataAnnotations / explicit guards (matches "validate all external input").
- **Relative params:** schedules store `TParams` with relative markers (e.g. a `RelativeRange` value object); `ExportJob` resolves them against fire-time before calling the definition, so recurring exports stay current.

### Submitting / managing (caller side)

```csharp
public interface IExportRequestService
{
    Task<ExportRun> SubmitAsync(ExportSubmission submission, CancellationToken ct);          // on-demand → one-shot Quartz job
    Task<ExportSchedule> ScheduleAsync(ExportScheduleRequest request, CancellationToken ct); // recurring → Quartz cron trigger
    Task<IReadOnlyList<ExportRun>> ListRunsAsync(ExportRunQuery query, CancellationToken ct); // tenant-scoped history
    Task<ExportRun?> GetRunAsync(Guid runId, CancellationToken ct);
}

public sealed record ExportSubmission(
    string DefinitionKey, string? ParametersJson, ExportFormat Format,
    string? FileName = null, bool IncludeSoftDeleted = false);

public enum ExportFormat { Csv, Xlsx }
public enum ExportRunStatus { Pending, Running, Succeeded, Failed, Expired }
```

`ExportRun` (row + API model): `{ Guid Id, string DefinitionKey, ExportFormat Format, ExportRunStatus Status, string? StorageKey, string? FileName, long? SizeBytes, DateTimeOffset? ExpiresAt, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt }`.

`ExportSchedule` / `ExportScheduleRequest`: `{ Guid Id, string DefinitionKey, string? ParametersJson, ExportFormat Format, string Cron, bool Enabled, bool IncludeSoftDeleted, … }`.

## Data flow

**On-demand**
1. `SubmitAsync` → reject if `IncludeSoftDeleted && !definition.AllowsIncludeSoftDeleted` → persist `ExportRun` (`Pending`, TenantId, UserId, key, paramsJson, format, includeSoftDeleted) → enqueue a **one-shot Quartz job** carrying `{ runId }` → return the run.
2. `ExportJob` fires → **open a DI scope and establish tenant context from the run's `TenantId`** (see _Background execution context_ below) → load run → `Running` → resolve `IExportDefinition` by key → build `ExportContext` → (if `IncludeSoftDeleted`) open `BypassSoftDeleteFilter()` scope → `definition.ExportAsync` → bytes.
3. `ITenantStorage.PutAsync` at `exports/{tenantId}/{runId}.{ext}` → set `StorageKey`, `SizeBytes`, `ExpiresAt = now + Retention`, `Succeeded`, `CompletedAt`.
4. Deliver via `INotificationDispatcher` (email + in-app) with a signed link from `GetDownloadUrlAsync(key, LinkTtl)`. Dispatch goes through the Notifications **outbox** → eventual.
5. Any exception → `Failed` + error + failure notification. No retry.

**Recurring** — `ScheduleAsync` persists an `ExportSchedule` and registers a **Quartz cron trigger** carrying `{ scheduleId }`. On fire: resolve schedule → resolve relative params against fire-time → run the same Submit→`ExportJob` path (fresh run). One execution path; only the trigger differs.

**Cleanup** — a recurring `CleanupJob` (`CleanupCron`) finds expired runs **across all tenants** under a `BypassTenantFilter()` scope (`ExpiresAt < now`, `Status = Succeeded`), then processes them **grouped by tenant**: for each tenant it establishes tenant context (same mechanism as `ExportJob`), calls the tenant-scoped `ITenantStorage.DeleteAsync`, and marks the run `Expired`. (A single global pass can't delete tenant-scoped blobs — Storage is tenant-bound.)

## Background execution context (tenant scope)

Every job runs on a Quartz **background thread with no HTTP request**, so the data layer's tenant — resolved from an `AsyncLocal` that request middleware normally sets (`TenantContextAccessor.CurrentTenantId`, applied in `ThemiaDbContext.cs:69-77`) — is **unset**. Without establishing it, the definition's `RowsAsync`, the `export_runs`/`export_schedules` reads/writes, and cleanup all run with a null tenant (`EffectiveFilterTenantId is null` is a distinct, unsafe branch — `ThemiaDbContext.cs:285`). This is the single highest correctness risk and MUST be explicit:

- `ExportJob`/`CleanupJob` **open a fresh DI scope per run** and set tenant context from the persisted `TenantId` — either by setting `TenantContextAccessor.CurrentTenantId` directly or by registering a background `ITenantContext` carrying that id — then **restore on exit** (the framework has no `RunAsTenant` helper today; the job owns this).
- This wraps **everything tenant-bound**: definition execution, the `ITenantStorage` write, and the run-status update — they must all observe the same tenant.
- The recurring path resolves `{scheduleId}` → schedule's `TenantId` → same scope. The cleanup path establishes tenant **per tenant group** (see _Cleanup_ above).
- Consider extracting a small reusable `using var _ = tenantScope.Begin(tenantId);` helper in the framework if Scheduling/Notifications drains need the same thing — but that is additive, not required by this spec.

## Soft-delete bypass (framework addition, in this spec)

The existing `IDataFilterScope.BypassTenantFilter()` is the **tenant** axis and deliberately keeps soft-delete on. There is no sanctioned way to include soft-deleted rows; raw `IgnoreQueryFilters()` also drops the tenant filter (analyzer-forbidden). This spec adds the missing, equally-deliberate primitive:

```csharp
public interface IDataFilterScope
{
    IDisposable BypassTenantFilter();
    bool IsTenantFilterBypassed { get; }
    IDisposable BypassSoftDeleteFilter();      // NEW — relaxes ONLY the soft-delete predicate
    bool IsSoftDeleteFilterBypassed { get; }   // NEW
}
```

- **`DataFilterScope`:** add a **second** `AsyncLocal<bool>` for soft-delete bypass alongside the existing tenant one (today it is a single `AsyncLocal<bool>`), plus `BypassSoftDeleteFilter()` / `IsSoftDeleteFilterBypassed`.
- **EF adapter — make the soft-delete clause AsyncLocal-conditional, do NOT re-derive tenant.** Today the combined filter ANDs the tenant predicate with a hard `IsDeleted == false` constant (`ThemiaDbContext.cs:583-585`). Change that clause to `softDeleteBypassed || !IsDeleted`, reading the new flag the same way the tenant predicate already reads the tenant at query time. **Do not** implement this as "`IgnoreQueryFilters()` + re-apply tenant" (the apparent mirror of `WithSoftDeleteOnly`): the tenant predicate is non-trivial (PerTenantModel constant-baking vs RuntimeTenantAccess AsyncLocal) and re-deriving it risks the exact divergence the codebase forbids (`ThemiaDbContext.cs:87`). Re-applying soft-delete on tenant bypass is trivial; re-deriving tenant on soft-delete bypass is not — they are not symmetric. The standalone soft-delete filter (`ApplySoftDeleteQueryFilters`, non-tenant entities) gets the same conditional clause.
- **Dapper factory** (`TenantQueryFactory` / `TenantPredicate`): omit the `IsDeleted = false` clause while keeping the tenant predicate, gated on the same flag.
- **Composition:** the existing tenant-bypass path uses `IgnoreQueryFilters()` and would drop the new conditional clause — acceptable, since export needs only soft-delete bypass (never combined with tenant bypass). State this explicitly so no one assumes the two compose.
- **Export integration:** `ExportJob` opens the scope only when `IncludeSoftDeleted` is set _and_ the definition allows it. The bypass stays a one-line, reviewable scope — never scattered `IgnoreQueryFilters` calls. Tenant + audit remain fully enforced under it.

## Persistence (FluentMigrator, one migration, `IfDatabase` per engine)

- **`export_runs`**: `Id (PK)`, `TenantId`, `UserId`, `DefinitionKey`, `ParametersJson`, `Format`, `Status`, `StorageKey`, `FileName`, `SizeBytes`, `ExpiresAt`, `Error`, `IncludeSoftDeleted`, audit columns, `IsDeleted` / `DeletedAt`, `CreatedAt`, `CompletedAt`. Indexes: `(TenantId, Status)` (listing), `(ExpiresAt)` (cleanup).
- **`export_schedules`**: `Id (PK)`, `TenantId`, `UserId`, `DefinitionKey`, `ParametersJson`, `Format`, `Cron`, `Enabled`, `IncludeSoftDeleted`, audit columns, `IsDeleted` / `DeletedAt`, `CreatedAt`.
- Both entities derive from the framework's auditable/soft-deletable base → global query filter hides soft-deleted infra records automatically. Soft-deleting a run also **hard-purges its bytes** from Storage immediately (Storage has no soft tier — no orphan blob).

## Options

`ExportModuleOptions` (typed, `ValidateOnStart`):

| Option | Default | Purpose |
|---|---|---|
| `Retention` | 7 days | `ExpiresAt = CompletedAt + Retention` |
| `LinkTtl` | 1 hour | Signed download-URL TTL |
| `CleanupCron` | daily | `CleanupJob` cadence |

## Error handling

- Boundary validation: unknown `DefinitionKey`, invalid `TParams`, or `IncludeSoftDeleted` without `AllowsIncludeSoftDeleted` → reject at `SubmitAsync`/`ScheduleAsync` (no run created, or a run never enqueued).
- In-job exceptions (definition throws, non-numeric value in an aggregated column from the neutral writer, Storage/dispatch error) → `Failed` + error message + failure notification. No retry, no double-log (log once).
- `OperationCanceledException` treated as cancellation, not failure.

## Testing

- **Framework — `BypassSoftDeleteFilter` (the critical safety surface):** EF and Dapper both include soft-deleted rows under the scope while **tenant isolation + audit still hold**; scope is AsyncLocal-scoped and disposes correctly; nested with `BypassTenantFilter` behaves predictably.
- **Module unit:** `ExportDefinition<TRow,TParams>` base — params deserialize + validate, format dispatch (Csv/Xlsx), headers; `IncludeSoftDeleted` gating (allowed vs rejected); relative-param resolution against a fixed fire-time.
- **`ExportJob` integration** (fakes for `ITenantStorage` / `INotificationDispatcher` / definition registry): `Pending→Running→Succeeded`; storage key + `ExpiresAt` set; delivery dispatches a signed-link notification; failure → `Failed` + notify; soft-delete scope opened only when allowed + requested.
- **Background tenant scope:** a job for tenant A must read/write only tenant A's rows and storage even with no ambient request context; assert a job with tenant unset never silently operates null-tenant (the critical isolation test for background execution).
- **Scheduler precondition:** `InitializeAsync` fails fast when no `ISchedulerFactory` is registered.
- **Recurring:** cron fire → relative params resolved at fire-time → fresh run via the same path.
- **Cleanup:** expired `Succeeded` runs → bytes deleted + `Expired`; soft-deleted run → bytes purged immediately.
- **DB integration via Testcontainers** (SQL Server at minimum) for the store + FluentMigrator migration across engines; tenant isolation + soft-delete on the infra tables.
- A timing benchmark, if wanted, lives as a local/manual `BenchmarkDotNet` harness — never a CI assertion.

## Out of scope (YAGNI — additive later, no contract break)

- Persisted export _templates_.
- Retry policies / dead-letter for failed exports (explicitly dropped — notify-on-every-failure).
- Additional backends / formats (separate **Spec B**).
- **Email-attachment delivery** — the Notifications stack carries no attachment channel today (`NotificationRequest` / `NotificationMessage`); adding one touches the dispatcher, outbox persistence, neutral message, and every `IEmailSender` provider. Deferred; v1 is link-only.
- An ASP.NET package (`Themia.Modules.Export.AspNetCore`) — hosts call `IExportRequestService` directly.

## Catalog / roadmap

Adds `Themia.Modules.Export` (a tenant-aware module) above the two neutral export cores, and extends the data layer with `BypassSoftDeleteFilter`. Update the `Themia.Modules.Export` catalog row in `docs/themia-architecture-overview.md` to note the realized two-tier shape (neutral cores + async module). **Spec B** (additional backends/formats) follows as a separate brainstorm.
