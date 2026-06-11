# RESOLVED: cross-tenant leak via `DbSet.Find` — stale tenant baked into EF's compiled finder query

**Found by:** `Themia.Framework.Data.EFCore.SqlServer.IntegrationTests.Tenancy.TenantIsolationTests.RuntimeTenantAccess_BlocksFindAcrossTenants`
(18/19 green; this one failed in the full-suite run only, passed in isolation).
**Fixed in:** `ThemiaDbContext.GetCurrentTenantExpression` (branch `feat/efcore-sqlserver-provider`, 0.4.5).
**Verdict:** pre-existing core bug in the RuntimeTenantAccess filter, newly exposed — not a SQL Server provider regression.

## Symptom

With the ambient tenant correctly `tenant-a`, a by-primary-key lookup of a `tenant-b` row behaved
inconsistently across the two Find APIs (diagnostic captured from the failing full-suite run):

```
acc=tenant-a,  viaDbSet.Tenant=tenant-b (LEAK),  viaContext.Tenant=null (blocked)
```

- `DbSet<T>.FindAsync(id)` returned the cross-tenant row.
- `DbContext.FindAsync<T>(id)` returned null — `ThemiaDbContext`'s override post-checks via
  `ValidateTenantAccess` (and `EfReadRepository.GetByIdAsync` deliberately routes through this
  guarded path, so the production repository was never exposed).
- Order-dependent: passed in isolation, failed after other tests had run — even fully serialized.

## Root cause

The RuntimeTenantAccess query filter resolved the tenant with a **static** property expression:

```csharp
Expression.Property(null, typeof(TenantContextAccessor), nameof(TenantContextAccessor.CurrentTenantId))
```

How EF treats that value differs by code path:

- **Ad-hoc LINQ queries** re-run parameter extraction on every execution, so the static value is
  re-read each time → those queries always filtered correctly (which is why every `ToListAsync`-based
  test passed and the bug stayed hidden).
- **`DbSet.Find`/`FindAsync`** go through EF's internal entity finder, which uses a **pre-compiled
  per-entity-type query**. Compiled queries bake non-context-rooted values as **constants at first
  compilation**. The first by-PK Find against the entity type froze whatever tenant the ambient
  accessor held at that moment into the cached plan — every later Find filtered by that *stale*
  tenant, regardless of the current one.

This also resolves the apparent contradiction that misled earlier debugging: the sibling test
`RuntimeTenantAccess_FindMirrorsFilter…` "passed" because *it* compiled the finder query under
accessor=`tenant-b`, and both of its assertions are consistent with a baked `tenant-b` — it
couldn't distinguish baked-constant from live-parameter. `BlocksFindAcrossTenants` could, and failed
whenever another test compiled the finder first. The PostgreSQL suite's equivalent test masked the
bug by calling `FindAsync(2)` for a row id that typically no longer exists after reseeding (null for
the wrong reason).

## Fix

Root the filter's tenant access at the **context instance** instead of the static:

```csharp
private TenantId? AmbientFilterTenantId => TenantContextAccessor.CurrentTenantId;  // same source

// RuntimeTenantAccess branch of GetCurrentTenantExpression():
Expression.Property(
    Expression.Constant(this, typeof(ThemiaDbContext)),
    typeof(ThemiaDbContext).GetProperty(nameof(AmbientFilterTenantId),
        BindingFlags.Instance | BindingFlags.NonPublic)!);
```

(The final code hardens this further: the `PropertyInfo` is cached in a `static readonly` field with a
fail-fast `?? throw`, so a broken lookup surfaces at type initialization rather than via the `!`.)

EF rewrites `DbContext`-typed constants inside query filters to the **current** context at query
time, so a context-rooted member is re-evaluated per execution in *every* path — including the
pre-compiled entity-finder query. (This is the same mechanism behind EF's documented multi-tenant
`HasQueryFilter(e => e.TenantId == _tenantId)` pattern, and the reason filters must reference the
context instance for dynamic values.) The underlying tenant *source* is unchanged — the instance
property reads the same static accessor — so the LOCKSTEP invariant between the filter and Find's
post-check (`EffectiveFilterTenantId`) is preserved.

## Verification

- SQL Server integration suite: **19/19** (previously 18/19; the regression test now passes in the
  full serialized run, repeatedly).
- PostgreSQL integration suite: **43/43** (no regression).
- EFCore unit tests: **45/45**; full solution build clean (TreatWarningsAsErrors, no RS0016/RS0017).

## Residual notes

- `DbSet.Find` on an **already-tracked** entity still returns the tracked instance without the
  post-check (EF identity-map semantics; reaching that state requires having already loaded the row,
  e.g. under a bypass scope, in the same context). The guarded `DbContext.FindAsync<T>` /
  `EfReadRepository.GetByIdAsync` path re-checks even tracked entities. Documented limitation.
- The SqlServer integration tests are serialized (`AssemblyInfo.cs`,
  `CollectionBehavior(DisableTestParallelization = true)`) because the ambient accessor is
  process-global; this also keeps Testcontainers resource use to one container at a time.
