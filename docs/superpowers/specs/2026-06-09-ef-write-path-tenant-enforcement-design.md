# EF write-path tenant enforcement — design

**Status:** confirmed (brainstorm, 2026-06-09). Target: a small **0.4.2** patch.

## Goal

Close the write-side tenant-isolation gap flagged in the PR #68 review: `Themia.Framework.Data.EFCore`
issues `UPDATE`/`DELETE` by primary key with **no tenant predicate**, relying entirely on read-gating. A
caller who hands a *detached* entity with another tenant's key to `EfRepository.Update`/`Remove` (then
`SaveChanges`) mutates a row outside its tenant scope. The Dapper layer already scopes/rejects such writes
(`TenantScoped` + the 0-row `ConcurrencyException`), so the two providers diverge on the framework's #1
guarantee. This brings EF up to the same contract and makes both layers bypass-aware and symmetric.

## Context

- Read side already enforces per-tenant access: `ThemiaDbContext.ValidateTenantAccess` (Find/FindAsync) and
  the runtime query filters. Bypass for reads is handled at the **repository** layer
  (`EfReadRepository`/`DapperReadRepository` consult `IDataFilterScope` and `IgnoreQueryFilters()` /
  drop the predicate) — **not** in the DbContext.
- Dapper writes enforce at the **UoW** layer (`DapperUnitOfWork.TenantScoped`) but currently **ignore**
  `IDataFilterScope` entirely (no write bypass).
- `EfUnitOfWork` wraps a `ThemiaDbContext` and already centralises `SaveChangesAsync` (it wraps EF's
  `DbUpdateConcurrencyException` into the shared `ConcurrencyException`).

## Decisions (resolved in brainstorm)

1. **Enforce on `UPDATE`/`DELETE` only.** Inserts are out of scope — `EfRepository.AddAsync` already
   auto-stamps the ambient tenant when `TenantId` is null; rejecting an explicit foreign-tenant insert is a
   rarer, more deliberate path and is deferred.
2. **Honor `IDataFilterScope.BypassTenantFilter()` on writes — on *both* layers.** Under an active bypass
   scope, cross-tenant writes are permitted (admin/migration escape hatch), symmetric with read bypass.
   Honoring bypass on EF necessitates honoring it on Dapper too, or the conformance parity claim breaks.
3. **Reuse `ConcurrencyException`** on a non-bypassed out-of-scope write. Dapper already throws it for the
   equivalent 0-row case, and the type's docstring already reads "…or is outside the current tenant scope."
   Keeps both providers throwing one type (one conformance assertion); no new public API. Trade-off: the
   caller cannot distinguish a cross-tenant attempt from a genuinely-missing row — but neither can Dapper.

## Architecture

**Enforce at the Unit-of-Work layer (not `ThemiaDbContext.SaveChanges`).**

- **Symmetric with Dapper** — both providers enforce + honor bypass at the UoW layer, exactly as reads do
  at the repository layer.
- **No `ThemiaDbContext` ctor change** — avoids a breaking migration for every derived context and avoids
  deepening the static-`AsyncLocal` coupling the type-design review flagged.
- Covers the flagged hole precisely (the repository/UoW write path). Direct `DbSet.Update` + `SaveChanges`
  outside the abstraction remains the app's responsibility — the same boundary reads already have.

### Shared tenant rule (DRY)

Extract the tenant comparison out of `ThemiaDbContext.ValidateTenantAccess` into one internal helper:

```csharp
// ThemiaDbContext — the single tenant-scope rule, shared by read and write paths.
internal bool IsWithinCurrentTenantScope(ITenantEntity entity)
{
    if (!EnableTenantFilters) return true;
    var current = EffectiveFilterTenantId;            // strategy-correct ambient tenant
    var owner = entity.TenantId;
    if (current is null) return owner is null;        // no tenant context => global rows only
    if (owner == current) return true;                // belongs to the current tenant
    return IncludeGlobalRecordsForTenants && owner is null;  // global rows allowed when configured
}
```

Both `ValidateTenantAccess` overloads compose this with the existing soft-delete check (read path returns
null when false). The write path uses the tenant check **only** — updating/un-deleting a soft-deleted row
within your own tenant must stay allowed. This also de-duplicates the two existing `ValidateTenantAccess`
overloads.

### EF write flow (`EfUnitOfWork`)

Inject `IDataFilterScope`. Before `context.SaveChangesAsync`:

```csharp
private void ValidateTenantWrites()
{
    if (filterScope.IsTenantFilterBypassed) return;   // admin/migration escape hatch
    foreach (var entry in context.ChangeTracker.Entries<ITenantEntity>())
    {
        if (entry.State is not (EntityState.Modified or EntityState.Deleted)) continue;
        if (!context.IsWithinCurrentTenantScope(entry.Entity))
            throw new ConcurrencyException(
                "A tracked update or delete targets a row outside the current tenant scope: " +
                "the row belongs to another tenant (or no tenant is ambient and the row is not global).");
    }
}
```

Called at the start of both `SaveAsync` (the wrapped path) and inside the `ExecuteInTransactionAsync`
strategy lambda, so every UoW write path is covered. Validation runs **before** EF's soft-delete
state-conversion (a `Remove`d soft-deletable is still `Deleted` here), so checking `Modified`/`Deleted`
covers update, hard-delete, and soft-delete.

### Dapper write flow (`DapperUnitOfWork`)

Inject `IDataFilterScope`. `TenantScoped` becomes bypass-aware:

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
rows → `ConcurrencyException`, already in place).

## Components / files

- `src/framework/Themia.Framework.Data.EFCore/ThemiaDbContext.cs` — extract
  `internal bool IsWithinCurrentTenantScope(ITenantEntity)`; route both `ValidateTenantAccess` overloads
  through it. No ctor change.
- `src/framework/Themia.Framework.Data.EFCore/UnitOfWork/EfUnitOfWork.cs` — inject `IDataFilterScope`; add
  `ValidateTenantWrites()` and call it before `SaveChangesAsync` in both the wrapped path and
  `ExecuteInTransactionAsync` (skip under bypass).
- `src/framework/Themia.Framework.Data.Dapper/UnitOfWork/DapperUnitOfWork.cs` — inject `IDataFilterScope`;
  `TenantScoped` skips the predicate under bypass.
- DI: `IDataFilterScope` is already registered for both layers (the read repositories depend on it) — no new
  registration. The EF adapters are registered by `AddThemiaDataRepositories<TContext>()`.

## Behavior matrix (both providers, after)

| Ambient tenant | Target row | Bypass off | Bypass on |
|---|---|---|---|
| tenant T | tenant T (or global\*) | succeeds | succeeds |
| tenant T | tenant U | **`ConcurrencyException`** | succeeds |
| none (system) | global (`tenant_id` NULL) | succeeds | succeeds |
| none (system) | tenant U | **`ConcurrencyException`** | succeeds |

\* a tenant may write a global row only when `IncludeGlobalRecordsForTenants` is enabled.

## Testing

Add to the shared `DataLayerConformanceTests` (run on both Dapper-PG and EF-PG):

1. **`CrossTenantWrite_WithoutBypass_Throws`** — tenant A inserts a row; tenant B constructs the entity with
   A's key, `Update`/`Remove`, `SaveChanges` → `ConcurrencyException`; the row is unchanged.
2. **`CrossTenantWrite_UnderBypass_Succeeds`** — same setup, but inside `Filter.BypassTenantFilter()` the
   cross-tenant `Update` succeeds and is visible to tenant A.

Keep the existing `NoTenantScope_CannotSoftDelete_TenantOwnedRow` (Dapper) and
`Update_MissingRow_Throws_NotSilentlyLost` facts green. Add a Dapper unit assertion that `TenantScoped`
drops the predicate under bypass (compiled SQL has no `tenant_id` clause).

## Non-goals / out of scope

- Insert-side enforcement (rejecting an explicit foreign-tenant `AddAsync`) — deferred.
- A dedicated `TenantIsolationViolationException` — rejected for parity; reuse `ConcurrencyException`.
- Enforcing on direct `DbSet` + `SaveChanges` usage outside the repository/UoW abstraction.
- The unrelated backlog items (`RETURNING <keyColumn>` threading; FluentMigrator 6→8; MySQL/SQL Server
  engines).

## Open items

None — all forks resolved in the brainstorm. Versioned as a 0.4.2 patch when cut.
