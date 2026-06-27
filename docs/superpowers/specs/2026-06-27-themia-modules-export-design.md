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
4. **Delivery:** always persist to Storage; `SizeBytes ≤ AttachmentThresholdBytes` → email **attachment** (+ in-app), else a **signed download link** (+ in-app).
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
2. `ExportJob` fires → load run → `Running` → resolve `IExportDefinition` by key → build `ExportContext` → (if `IncludeSoftDeleted`) open `BypassSoftDeleteFilter()` scope → `definition.ExportAsync` → bytes.
3. `ITenantStorage.PutAsync` at `exports/{tenantId}/{runId}.{ext}` → set `StorageKey`, `SizeBytes`, `ExpiresAt = now + Retention`, `Succeeded`, `CompletedAt`.
4. Deliver via `INotificationDispatcher`: `SizeBytes ≤ AttachmentThresholdBytes` → attachment (+ in-app); else signed link via `GetDownloadUrlAsync(key, LinkTtl)` (+ in-app).
5. Any exception → `Failed` + error + failure notification. No retry.

**Recurring** — `ScheduleAsync` persists an `ExportSchedule` and registers a **Quartz cron trigger** carrying `{ scheduleId }`. On fire: resolve schedule → resolve relative params against fire-time → run the same Submit→`ExportJob` path (fresh run). One execution path; only the trigger differs.

**Cleanup** — a recurring `CleanupJob` (`CleanupCron`) queries `export_runs` where `ExpiresAt < now` and `Status = Succeeded` → `ITenantStorage.DeleteAsync` → mark `Expired`.

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

- **EF adapter** (`ThemiaDbContext` / `EfReadRepository`): when bypassed, drop the global filters and re-apply the **tenant** predicate (the mirror of today's `WithSoftDeleteOnly`, which re-applies soft-delete on tenant bypass). Tenant + audit stay enforced; only soft-deleted rows become visible.
- **Dapper factory** (`TenantQueryFactory` / `TenantPredicate`): omit the `IsDeleted = false` predicate while keeping the tenant predicate.
- **Honored identically by both layers** (the `IDataFilterScope` contract). Async-flow scoped (AsyncLocal), disposes cleanly.
- **Export integration:** `ExportJob` opens the scope only when `IncludeSoftDeleted` is set _and_ the definition allows it. The bypass stays a one-line, reviewable scope — never scattered `IgnoreQueryFilters` calls.

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
| `AttachmentThresholdBytes` | 5 MB | Attach vs link cutoff |
| `CleanupCron` | daily | `CleanupJob` cadence |

## Error handling

- Boundary validation: unknown `DefinitionKey`, invalid `TParams`, or `IncludeSoftDeleted` without `AllowsIncludeSoftDeleted` → reject at `SubmitAsync`/`ScheduleAsync` (no run created, or a run never enqueued).
- In-job exceptions (definition throws, non-numeric value in an aggregated column from the neutral writer, Storage/dispatch error) → `Failed` + error message + failure notification. No retry, no double-log (log once).
- `OperationCanceledException` treated as cancellation, not failure.

## Testing

- **Framework — `BypassSoftDeleteFilter` (the critical safety surface):** EF and Dapper both include soft-deleted rows under the scope while **tenant isolation + audit still hold**; scope is AsyncLocal-scoped and disposes correctly; nested with `BypassTenantFilter` behaves predictably.
- **Module unit:** `ExportDefinition<TRow,TParams>` base — params deserialize + validate, format dispatch (Csv/Xlsx), headers; `IncludeSoftDeleted` gating (allowed vs rejected); relative-param resolution against a fixed fire-time.
- **`ExportJob` integration** (fakes for `ITenantStorage` / `INotificationDispatcher` / definition registry): `Pending→Running→Succeeded`; storage key + `ExpiresAt` set; delivery attach-vs-link by size; failure → `Failed` + notify; soft-delete scope opened only when allowed + requested.
- **Recurring:** cron fire → relative params resolved at fire-time → fresh run via the same path.
- **Cleanup:** expired `Succeeded` runs → bytes deleted + `Expired`; soft-deleted run → bytes purged immediately.
- **DB integration via Testcontainers** (SQL Server at minimum) for the store + FluentMigrator migration across engines; tenant isolation + soft-delete on the infra tables.
- A timing benchmark, if wanted, lives as a local/manual `BenchmarkDotNet` harness — never a CI assertion.

## Out of scope (YAGNI — additive later, no contract break)

- Persisted export _templates_.
- Retry policies / dead-letter for failed exports (explicitly dropped — notify-on-every-failure).
- Additional backends / formats (separate **Spec B**).
- An ASP.NET package (`Themia.Modules.Export.AspNetCore`) — hosts call `IExportRequestService` directly.

## Catalog / roadmap

Adds `Themia.Modules.Export` (a tenant-aware module) above the two neutral export cores, and extends the data layer with `BypassSoftDeleteFilter`. Update the `Themia.Modules.Export` catalog row in `docs/themia-architecture-overview.md` to note the realized two-tier shape (neutral cores + async module). **Spec B** (additional backends/formats) follows as a separate brainstorm.
