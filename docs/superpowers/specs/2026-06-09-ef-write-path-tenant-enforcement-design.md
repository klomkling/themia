# EF write-path tenant enforcement — design

**Status:** confirmed (brainstorm, 2026-06-09). Target: a small **0.4.2** patch.

## Goal

Close the write-side tenant-isolation gap flagged in the PR #68 review: `Themia.Framework.Data.EFCore`
issues `UPDATE`/`DELETE` by primary key with **no tenant predicate**, relying entirely on read-gating. A
caller who hands a *detached* entity with another tenant's key to `EfRepository.Update`/`Remove` (then
`SaveChanges`) mutates a row outside its tenant scope. The Dapper layer already scopes/rejects such writes
(`TenantScoped` adds `WHERE tenant_id = …`, so a cross-tenant target matches 0 rows → `ConcurrencyException`).
This brings EF to the same contract — verified against the **actual DB row** for full parity — and makes both
layers' write paths bypass-aware and symmetric.

## Context

- Read side enforces per-tenant access: `ThemiaDbContext.ValidateTenantAccess` (Find/FindAsync) + runtime
  query filters. Read bypass is handled at the **repository** layer (`EfReadRepository`/`DapperReadRepository`
  consult `IDataFilterScope`) — not in the DbContext.
- Dapper writes enforce at the **UoW** layer (`DapperUnitOfWork.TenantScoped`) but currently **ignore**
  `IDataFilterScope` (no write bypass).
- `EfUnitOfWork` wraps a `ThemiaDbContext` and already centralises `SaveChangesAsync` (it wraps EF's
  `DbUpdateConcurrencyException` into the shared `ConcurrencyException`).
- `IDataFilterScope` is already DI-registered (scoped) for both layers — the read repositories depend on it.

## Decisions (resolved in brainstorm)

1. **Enforce on `UPDATE`/`DELETE` only.** Inserts are out of scope — `EfRepository.AddAsync` already
   auto-stamps the ambient tenant when `TenantId` is null; rejecting an explicit foreign-tenant insert is a
   rarer, deliberate path, deferred.
2. **Honor `IDataFilterScope.BypassTenantFilter()` on writes — on *both* layers.** Under an active bypass
   scope, cross-tenant writes are permitted (admin/migration escape hatch), symmetric with read bypass.
   Honoring bypass on EF necessitates honoring it on Dapper too, or conformance parity breaks.
3. **Reuse `ConcurrencyException`** on a non-bypassed out-of-scope (or missing-row) write. Dapper already
   throws it for the equivalent 0-row case; the type's docstring already says "…or is outside the current
   tenant scope." Both providers throw one type (one conformance assertion); no new public API.
4. **EF enforces via a DB-verify, with a *strict* tenant rule — full parity with Dapper.** Before
   `SaveChanges`, for each `Modified`/`Deleted` `ITenantEntity` EF reads the **stored** row by PK
   (`EntityEntry.GetDatabaseValuesAsync`, which bypasses query filters) and compares the **DB row's** tenant
   to the ambient tenant. This catches the forged case (caller sets their own `TenantId` on a foreign PK)
   that an in-memory check cannot, since EF writes by PK from the ChangeTracker. **The write rule is strict
   equality** (`dbTenant == ambient`, both-null = system writing global) — it does **not** reuse the read
   rule's `IncludeGlobalRecordsForTenants` leniency, exactly matching Dapper's `WHERE tenant_id = T` (a tenant
   cannot write a global row; a no-tenant context writes only global rows). Cost: one extra `SELECT`-by-PK per
   modified/deleted tenant entity (acceptable; batching is a possible later optimization).

## Architecture

**Enforce at the Unit-of-Work layer (not in EF's `SaveChanges` override).**

- **Symmetric with Dapper** — both providers enforce + honor bypass at the UoW layer, as reads do at the
  repository layer.
- **No `ThemiaDbContext` ctor change** — `EfUnitOfWork` already holds the context and decides bypass; it calls
  an internal verify method on the context. Avoids a breaking migration for every derived context and avoids
  deepening the static-`AsyncLocal` coupling the type-design review flagged.
- Covers the flagged hole precisely (the repository/UoW write path). Direct `DbSet.Update` + `SaveChanges`
  outside the abstraction remains the app's responsibility — the same boundary reads already have.

### EF: DB-verify in `ThemiaDbContext`, gated by `EfUnitOfWork`

`ThemiaDbContext` gains an internal async verifier (it owns `ChangeTracker`, `EffectiveFilterTenantId`,
`EnableTenantFilters`). `EfUnitOfWork` owns the bypass decision and calls it before `SaveChanges`.

```csharp
// ThemiaDbContext.cs — verify each pending tenant write against the stored DB row.
internal async Task ValidateTenantWritesAsync(CancellationToken cancellationToken)
{
    if (!EnableTenantFilters) return;
    var ambient = EffectiveFilterTenantId;
    foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
    {
        if (entry.State is not (EntityState.Modified or EntityState.Deleted)) continue;

        // Stored values by PK, ignoring query filters — so soft-deleted rows are still visible
        // (you may update/un-delete your own soft-deleted row) and the row's REAL tenant is read.
        var stored = await entry.GetDatabaseValuesAsync(cancellationToken);
        if (stored is null)
            throw new ConcurrencyException(
                "A tracked update or delete affected no rows: the row does not exist or was concurrently deleted.");

        var owner = stored.GetValue<TenantId?>(nameof(ITenantEntity.TenantId));
        if (owner != ambient)   // strict: a tenant writes only its own rows; a no-tenant context only global
            throw new ConcurrencyException(
                "A tracked update or delete targets a row outside the current tenant scope.");
    }
}
```

```csharp
// EfUnitOfWork.cs — inject IDataFilterScope; verify unless bypassed, before every UoW write.
private async Task<int> SaveAsync(CancellationToken cancellationToken)
{
    if (!filterScope.IsTenantFilterBypassed)
        await context.ValidateTenantWritesAsync(cancellationToken);
    try { return await context.SaveChangesAsync(cancellationToken); }
    catch (DbUpdateConcurrencyException ex) { throw new ConcurrencyException("…", ex); }
}
```

`SaveAsync` is the existing wrapped path; `ExecuteInTransactionAsync` already calls it, so both write paths are
covered. Validation runs **before** EF's soft-delete state-conversion (a `Remove`d soft-deletable is still
`Deleted` here), so checking `Modified`/`Deleted` covers update, hard-delete, and soft-delete. The read rule
(`ValidateTenantAccess`) is left unchanged.

### Dapper: bypass-aware `TenantScoped`

`DapperUnitOfWork` gains `IDataFilterScope`; the predicate is dropped under bypass:

```csharp
private Query TenantScoped(Query q, object entity, EntityMapping map)
{
    if (entity is ITenantEntity && !filterScope.IsTenantFilterBypassed)
    {
        var column = map.Column(nameof(ITenantEntity.TenantId));
        if (tenantContext.CurrentTenantId is { } t) q.Where(column, t.Value);
        else q.WhereNull(column);
    }
    return q;   // under bypass: no predicate -> write by PK across tenants
}
```

Without bypass: unchanged (scope to ambient tenant, or global when none → a cross-tenant target matches 0
rows → `ConcurrencyException`, already in place from the prior 0.4.1 hardening).

## Components / files

- `src/framework/Themia.Framework.Data.EFCore/ThemiaDbContext.cs` — add
  `internal async Task ValidateTenantWritesAsync(CancellationToken)` (DB-verify, strict equality); add
  `using Themia.Framework.Data.Abstractions.Exceptions;`. `ValidateTenantAccess` (read) is unchanged.
- `src/framework/Themia.Framework.Data.EFCore/UnitOfWork/EfUnitOfWork.cs` — inject `IDataFilterScope filterScope`;
  in `SaveAsync`, call `ValidateTenantWritesAsync` unless bypassed.
- `src/framework/Themia.Framework.Data.Dapper/UnitOfWork/DapperUnitOfWork.cs` — inject
  `IDataFilterScope filterScope`; `TenantScoped` skips the predicate under bypass.
- DI: no new registration (`IDataFilterScope` already registered for both layers); both UoWs are DI-constructed,
  so the new ctor parameter resolves automatically.

## Behavior matrix (both providers, after)

| Ambient tenant | Stored row's tenant | Bypass off | Bypass on |
|---|---|---|---|
| tenant T | tenant T | succeeds | succeeds |
| tenant T | tenant U **or global (null)** | **`ConcurrencyException`** | succeeds |
| tenant T | row missing | **`ConcurrencyException`** | (EF) `ConcurrencyException` / (Dapper) 0-rows `ConcurrencyException` |
| none (system) | global (null) | succeeds | succeeds |
| none (system) | tenant U | **`ConcurrencyException`** | succeeds |

Strict rule: a tenant writes only its own rows (not global, matching Dapper's `WHERE tenant_id = T`); a
no-tenant/system context writes only global rows.

## Testing

Add to the shared `DataLayerConformanceTests` (run on both Dapper-PG and EF-PG):

1. **`CrossTenantWrite_WithoutBypass_Throws`** — tenant A inserts a row (capture id). In a tenant-B scope,
   construct a detached `Widget` with A's id, `Update` it, `SaveChanges` → `ConcurrencyException` on both
   providers (EF: stored row's tenant = A ≠ B; Dapper: `WHERE tenant_id = B AND id` → 0 rows). Re-open a
   tenant-A scope and assert the row is unchanged.
2. **`CrossTenantWrite_UnderBypass_Succeeds`** — tenant A inserts (capture id). In a tenant-B scope inside
   `Filter.BypassTenantFilter()`, `GetByIdAsync(id)` (bypass reveals it), change `Quantity`, `Update`,
   `SaveChanges` → succeeds on both. Re-open a tenant-A scope and assert `Quantity` changed. (Load-then-modify
   keeps the row's `tenant_id` intact — EF writes only changed columns, Dapper excludes the key/tenant
   columns from the SET.)

Keep the existing `NoTenantScope_CannotSoftDelete_TenantOwnedRow` (Dapper) and
`Update_MissingRow_Throws_NotSilentlyLost` facts green.

## Non-goals / out of scope

- Insert-side enforcement (rejecting an explicit foreign-tenant `AddAsync`) — deferred.
- A dedicated `TenantIsolationViolationException` — rejected for parity; reuse `ConcurrencyException`.
- Enforcing on direct `DbSet` + `SaveChanges` usage outside the repository/UoW abstraction.
- Batching the per-entity verify `SELECT`s — a possible later optimization; v1 verifies per entity.
- The unrelated backlog items (`RETURNING <keyColumn>` threading; FluentMigrator 6→8; MySQL/SQL Server
  engines).

## Open items

None — all forks resolved (enforce on UPDATE/DELETE; honor bypass on both layers; reuse `ConcurrencyException`;
DB-verify with a strict rule). Versioned as a 0.4.2 patch when cut.
