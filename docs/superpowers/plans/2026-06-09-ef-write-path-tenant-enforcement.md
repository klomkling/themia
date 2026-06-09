# EF Write-Path Tenant Enforcement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make both data layers reject a tenant-scoped `UPDATE`/`DELETE` that targets a row outside the current tenant (throwing `ConcurrencyException`), while honoring `IDataFilterScope.BypassTenantFilter()` as an admin escape hatch — closing the EF write-path isolation gap from the PR #68 review.

**Architecture:** EF enforces at the `EfUnitOfWork` layer by calling a new `ThemiaDbContext.ValidateTenantWritesAsync` that reads each pending tenant row's **stored** tenant by PK (`EntityEntry.GetDatabaseValuesAsync`, bypassing query filters) and throws on a strict `dbTenant != ambient` mismatch — full parity with Dapper, including the forged-PK case. Dapper's `TenantScoped` (which already scopes writes in SQL) becomes bypass-aware. Both UoWs gain an injected `IDataFilterScope`; under bypass, both skip the tenant constraint.

**Tech Stack:** .NET 10, EF Core 10, Dapper + SqlKata, xUnit, Testcontainers (PostgreSQL). Branch: `feat/ef-write-tenant-enforcement`. Spec: `docs/superpowers/specs/2026-06-09-ef-write-path-tenant-enforcement-design.md`.

---

## File Structure

- `src/framework/Themia.Framework.Data.Dapper/UnitOfWork/DapperUnitOfWork.cs` — add `IDataFilterScope` ctor param; `TenantScoped` skips the tenant predicate under bypass.
- `src/framework/Themia.Framework.Data.EFCore/ThemiaDbContext.cs` — add `internal async Task ValidateTenantWritesAsync(CancellationToken)` (DB-verify, strict equality). Read-side `ValidateTenantAccess` is unchanged.
- `src/framework/Themia.Framework.Data.EFCore/UnitOfWork/EfUnitOfWork.cs` — add `IDataFilterScope` ctor param; call `ValidateTenantWritesAsync` in `SaveAsync` unless bypassed.
- `tests/Themia.Framework.Data.Dapper.Conformance/DataLayerConformanceTests.cs` — two new facts (run on both providers).

No DI changes: `IDataFilterScope` is already registered (scoped) by `AddThemiaDapperCore` and `AddThemiaDataRepositories<TContext>`; both UoWs are DI-constructed so the new parameter resolves automatically.

**Conformance facts run on BOTH providers** (subclasses `DapperPostgresConformanceTests` + `EfPostgresConformanceTests`). A fact added before both providers are correct will fail on one provider — that is the intended TDD red. Each task below ends with the full suite green.

---

### Task 1: Dapper `TenantScoped` honors bypass

**Files:**
- Modify: `src/framework/Themia.Framework.Data.Dapper/UnitOfWork/DapperUnitOfWork.cs`
- Test: `tests/Themia.Framework.Data.Dapper.Conformance/DataLayerConformanceTests.cs`

- [ ] **Step 1: Write the failing conformance fact**

Add this method inside `public abstract class DataLayerConformanceTests` in `tests/Themia.Framework.Data.Dapper.Conformance/DataLayerConformanceTests.cs` (e.g. directly after the existing `Update_MissingRow_Throws_NotSilentlyLost` fact). `using Themia.Framework.Data.Abstractions.Exceptions;` is already present in this file.

```csharp
    [Fact]
    public async Task CrossTenantWrite_UnderBypass_Succeeds()
    {
        await ResetAsync();

        Guid id;
        await using (var a = await NewScopeAsync(new TenantId("a")))
        {
            var w = NewWidget("shared", 1);
            id = w.Id;
            await a.Repo.AddAsync(w);
            await a.Uow.SaveChangesAsync();
        }

        await using (var b = await NewScopeAsync(new TenantId("b")))
        using (b.Filter.BypassTenantFilter())
        {
            var loaded = await b.Repo.GetByIdAsync(id);   // bypass reveals tenant A's row
            Assert.NotNull(loaded);
            loaded!.Quantity = 42;
            b.Repo.Update(loaded);
            await b.Uow.SaveChangesAsync();               // bypass => cross-tenant write permitted
        }

        await using var check = await NewScopeAsync(new TenantId("a"));
        var after = await check.Repo.GetByIdAsync(id);
        Assert.NotNull(after);
        Assert.Equal(42, after!.Quantity);
    }
```

- [ ] **Step 2: Run the fact and confirm it fails on the Dapper provider**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests.csproj --filter CrossTenantWrite_UnderBypass_Succeeds`
Expected: 2 tests run; the **Dapper** variant FAILS with `ConcurrencyException` (its `TenantScoped` still emits `WHERE tenant_id = 'b'`, so the cross-tenant update matches 0 rows). The EF variant passes (EF has no write enforcement yet). Requires Docker for Testcontainers.

- [ ] **Step 3: Make `TenantScoped` bypass-aware**

In `DapperUnitOfWork.cs`, add the filtering using if missing and inject `IDataFilterScope`:

Change the using block to include:
```csharp
using Themia.Framework.Data.Abstractions.Filtering;
```

Change the primary constructor to add the parameter (keep the others exactly as-is):
```csharp
internal sealed class DapperUnitOfWork(
    IDapperConnectionContext connection,
    EntityMappingRegistry registry,
    ISqlCompiler compiler,
    ITenantContext tenantContext,
    ICurrentUserAccessor currentUser,
    IDataFilterScope filterScope,
    TimeProvider timeProvider) : IUnitOfWork, IPendingOperationSink
```

Replace the `TenantScoped` method with:
```csharp
    // Scope the write to the ambient tenant's rows; with no ambient tenant, restrict to global
    // (tenant_id IS NULL) rows so a system context cannot mutate a tenant-owned row by primary key.
    // Under an active bypass scope the predicate is dropped (admin/migration cross-tenant write).
    private Query TenantScoped(Query q, object entity, EntityMapping map)
    {
        if (entity is ITenantEntity && !filterScope.IsTenantFilterBypassed)
        {
            var column = map.Column(nameof(ITenantEntity.TenantId));
            if (tenantContext.CurrentTenantId is { } t)
                q.Where(column, t.Value);
            else
                q.WhereNull(column);
        }
        return q;
    }
```

- [ ] **Step 4: Run the fact and confirm both providers pass**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests.csproj --filter CrossTenantWrite_UnderBypass_Succeeds`
Expected: 2 tests, both PASS.

- [ ] **Step 5: Run the Dapper unit tests and confirm no regression**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.Tests/Themia.Framework.Data.Dapper.Tests.csproj`
Expected: PASS (30 tests).

- [ ] **Step 6: Commit**

```bash
git add src/framework/Themia.Framework.Data.Dapper/UnitOfWork/DapperUnitOfWork.cs \
        tests/Themia.Framework.Data.Dapper.Conformance/DataLayerConformanceTests.cs
git commit -m "feat(data-dapper): honor BypassTenantFilter on the write path

TenantScoped drops the tenant predicate under an active IDataFilterScope bypass,
permitting admin/migration cross-tenant writes. Adds a conformance fact (both
providers) proving a cross-tenant update succeeds under bypass."
```

---

### Task 2: EF write enforcement via DB-verify

**Files:**
- Modify: `src/framework/Themia.Framework.Data.EFCore/ThemiaDbContext.cs`
- Modify: `src/framework/Themia.Framework.Data.EFCore/UnitOfWork/EfUnitOfWork.cs`
- Test: `tests/Themia.Framework.Data.Dapper.Conformance/DataLayerConformanceTests.cs`

- [ ] **Step 1: Write the failing conformance fact**

Add this method inside `DataLayerConformanceTests` (directly after `CrossTenantWrite_UnderBypass_Succeeds`):

```csharp
    [Fact]
    public async Task CrossTenantWrite_WithoutBypass_Throws()
    {
        await ResetAsync();

        Guid id;
        await using (var a = await NewScopeAsync(new TenantId("a")))
        {
            var w = NewWidget("owned", 1);
            id = w.Id;
            await a.Repo.AddAsync(w);
            await a.Uow.SaveChangesAsync();
        }

        await using (var b = await NewScopeAsync(new TenantId("b")))
        {
            var detached = NewWidget("hijack", 99);
            detached.SetId(id);              // tenant B targets tenant A's row by primary key
            b.Repo.Update(detached);
            await Assert.ThrowsAsync<ConcurrencyException>(() => b.Uow.SaveChangesAsync());
        }

        await using var check = await NewScopeAsync(new TenantId("a"));
        var loaded = await check.Repo.GetByIdAsync(id);
        Assert.NotNull(loaded);
        Assert.Equal("owned", loaded!.Name);   // tenant A's row is untouched
        Assert.Equal(1, loaded.Quantity);
    }
```

- [ ] **Step 2: Run the fact and confirm it fails on the EF provider**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests.csproj --filter CrossTenantWrite_WithoutBypass_Throws`
Expected: 2 tests run; the **EF** variant FAILS — EF has no write enforcement yet, so `SaveChanges` updates tenant A's row by PK and no exception is thrown (the final assertions also fail because the row changed). The Dapper variant passes (its `WHERE tenant_id = 'b'` already matches 0 rows → `ConcurrencyException`).

- [ ] **Step 3: Add the DB-verify method to `ThemiaDbContext`**

In `ThemiaDbContext.cs`, add to the using block:
```csharp
using Themia.Framework.Data.Abstractions.Exceptions;
```

Add this method to the class (place it immediately after the `ValidateTenantAccess(object?, Type)` method):
```csharp
    /// <summary>
    /// Verifies that every pending tenant-scoped update/delete targets a row owned by the current tenant,
    /// by reading the stored row's tenant by primary key (bypassing query filters so soft-deleted rows are
    /// still visible and the row's real tenant is read). Throws <see cref="ConcurrencyException"/> on a
    /// missing row or a tenant mismatch. The rule is strict: a tenant writes only its own rows; a no-tenant
    /// context writes only global (null-tenant) rows — matching the Dapper layer's <c>WHERE tenant_id = …</c>.
    /// </summary>
    internal async Task ValidateTenantWritesAsync(CancellationToken cancellationToken)
    {
        if (!EnableTenantFilters)
        {
            return;
        }

        var ambient = EffectiveFilterTenantId;

        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var stored = await entry.GetDatabaseValuesAsync(cancellationToken);
            if (stored is null)
            {
                throw new ConcurrencyException(
                    "A tracked update or delete affected no rows: the row does not exist or was concurrently deleted.");
            }

            var owner = stored.GetValue<TenantId?>(nameof(ITenantEntity.TenantId));
            if (owner != ambient)
            {
                throw new ConcurrencyException(
                    "A tracked update or delete targets a row outside the current tenant scope.");
            }
        }
    }
```

- [ ] **Step 4: Wire `EfUnitOfWork` to call it unless bypassed**

In `EfUnitOfWork.cs`, add to the using block:
```csharp
using Themia.Framework.Data.Abstractions.Filtering;
```

Change the primary constructor to inject the filter scope:
```csharp
public sealed class EfUnitOfWork(ThemiaDbContext context, IDataFilterScope filterScope) : IUnitOfWork
```

Replace the private `SaveAsync` method with (the `catch` body stays exactly as it is today):
```csharp
    // Translate EF's optimistic-concurrency failure (a tracked update/delete that affected no rows) into the
    // framework's provider-agnostic ConcurrencyException so both data layers surface a lost write the same way.
    private async Task<int> SaveAsync(CancellationToken cancellationToken)
    {
        if (!filterScope.IsTenantFilterBypassed)
        {
            await context.ValidateTenantWritesAsync(cancellationToken);
        }

        try
        {
            return await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyException(
                "A tracked update or delete affected no rows: the row does not exist, was concurrently deleted, " +
                "or is outside the current tenant scope.", ex);
        }
    }
```

- [ ] **Step 5: Run the fact and confirm both providers pass**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests.csproj --filter CrossTenantWrite_WithoutBypass_Throws`
Expected: 2 tests, both PASS.

- [ ] **Step 6: Run the full integration suite and confirm no regression**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests.csproj`
Expected: PASS — 31 tests (29 prior + the 2 new facts × … note: the 2 new facts add 4 test executions across the 2 provider subclasses; confirm 0 failures).

- [ ] **Step 7: Commit**

```bash
git add src/framework/Themia.Framework.Data.EFCore/ThemiaDbContext.cs \
        src/framework/Themia.Framework.Data.EFCore/UnitOfWork/EfUnitOfWork.cs \
        tests/Themia.Framework.Data.Dapper.Conformance/DataLayerConformanceTests.cs
git commit -m "feat(data-efcore): enforce tenant scope on UPDATE/DELETE via DB-verify

EfUnitOfWork now calls ThemiaDbContext.ValidateTenantWritesAsync before
SaveChanges (unless an IDataFilterScope bypass is active): each Modified/Deleted
ITenantEntity has its stored row read by PK (GetDatabaseValuesAsync, query
filters ignored) and its real tenant compared strictly to the ambient tenant,
throwing ConcurrencyException on a mismatch or missing row. Full parity with the
Dapper layer, including the forged-PK case. Adds a conformance fact (both
providers) proving a cross-tenant update throws without bypass."
```

---

### Task 3: Full verification and CHANGELOG

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Full solution build (0 warnings under TreatWarningsAsErrors)**

Run: `dotnet build Themia.sln`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 2: Run the data-layer unit suites**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.Tests/Themia.Framework.Data.Dapper.Tests.csproj && dotnet test tests/Themia.Framework.Data.Abstractions.Tests/Themia.Framework.Data.Abstractions.Tests.csproj`
Expected: both PASS (Dapper 30, Abstractions 5).

- [ ] **Step 3: Add the CHANGELOG entry**

In `CHANGELOG.md`, under `## [Unreleased]`, add a `### Changed` section (create it if absent) with:
```markdown
- **Write-path tenant isolation is now enforced on both data layers.** A tenant-scoped `UPDATE`/`DELETE`
  that targets a row outside the current tenant throws `ConcurrencyException` (EF verifies the stored row's
  tenant by primary key; Dapper scopes the SQL predicate). `IDataFilterScope.BypassTenantFilter()` now also
  applies to writes as an admin/migration escape hatch on both layers.
```

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): note write-path tenant enforcement"
```

---

## Self-Review

**1. Spec coverage:**
- Decision 1 (enforce UPDATE/DELETE only) → Task 2 checks `Modified`/`Deleted` entries; inserts untouched. ✓
- Decision 2 (honor bypass both layers) → Task 1 (Dapper `TenantScoped`) + Task 2 (`EfUnitOfWork` skip under bypass). ✓
- Decision 3 (reuse `ConcurrencyException`) → Task 2 throws it; Dapper already does. ✓
- Decision 4 (DB-verify, strict rule) → Task 2 `ValidateTenantWritesAsync` via `GetDatabaseValuesAsync`, strict `owner != ambient`. ✓
- Testing (two conformance facts, both providers) → Task 1 + Task 2 facts. ✓

**2. Placeholder scan:** No TBD/TODO; every code step shows full code and exact commands. ✓

**3. Type consistency:** `IDataFilterScope filterScope` ctor param + `filterScope.IsTenantFilterBypassed` used consistently in both UoWs. `ValidateTenantWritesAsync(CancellationToken)` defined in Task 2 Step 3 and called in Step 4. `GetValue<TenantId?>(nameof(ITenantEntity.TenantId))`, `EffectiveFilterTenantId`, and `EnableTenantFilters` all exist on `ThemiaDbContext`. `NewWidget`, `SetId`, `ConformanceScope.Filter`, `Repo`, `Uow` all exist in the conformance harness. ✓
