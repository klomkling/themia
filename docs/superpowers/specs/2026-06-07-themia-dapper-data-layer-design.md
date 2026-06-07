# Themia Dapper Data Layer — Design

**Status:** approved (brainstorm 2026-06-07)
**Target:** 0.4.1 (PostgreSQL) → 0.4.2 (MySQL) → 0.4.3 (SQL Server)
**Supersedes** (for the Dapper-first use case) the CLAUDE.md note "Raw Dapper is allowed only as a
controlled read-only escape-hatch." That note was about mixing Dapper *into* the EF layer; this design
is a **first-class Dapper data layer as a sibling to the EF layer**.

---

## Goal

Provide a **Dapper (+ SqlKata) data-access layer** that is a peer of `Themia.Framework.Data.EFCore`,
so an application that prefers Dapper/SqlKata (e.g. EzyAssets) gets the framework's data guarantees —
**multi-tenant isolation, audit stamping, soft-delete, unit-of-work / transactions** — **without taking
an EF Core dependency**. Both layers sit behind a shared abstraction so application code (entities,
specifications, repository/UoW usage) is written once and can run on either layer.

## Non-goals / explicitly deferred

- **Not** an `IQueryable<T>` LINQ provider over Dapper (would mean reimplementing EF Core).
- **Not** cross-engine in 0.4.1 — PostgreSQL only; MySQL (0.4.2) and SQL Server (0.4.3) are fast-follows.
- **Not** a change-tracker with dirty-property diffing — the Dapper UoW updates full rows (see §5.2).
- **No** nested-navigation / join / projection support in the portable spec layer — those are tier-2
  (native SqlKata), by design.

---

## 1. Decisions (locked)

1. **Standalone Dapper layer**, sibling to the EF layer (not an escape-hatch inside EF).
2. **Shared abstraction** `Themia.Framework.Data.Abstractions`; **both** EF and Dapper implement it; the
   EF layer is **retrofitted** onto it (additive — existing direct-`DbContext` code is untouched).
3. **Query model = `ISpecification<T>`** (filter/sort/page as expression trees). EF translates to LINQ;
   Dapper translates to SqlKata.
4. **Three authoring tiers:**
   - **Tier 1 — Specification** (portable across both layers). The common 80%.
   - **Tier 2 — provider-native, tenant-safe-by-default.** Dapper exposes a tenant-seeded SqlKata `Query`;
     EF exposes a tenant-filtered `IQueryable<T>`. The EzyAssets path. Provider-specific by type.
   - **Tier 3 — raw SQL escape.** Shared connection/tx + current `TenantId` + a predicate helper.
     Adopter-responsible. For the rare hand-tuned query.
   Cross-tenant / admin access requires a **deliberate, explicit opt-out** (`IDataFilterScope.BypassTenantFilter()`).
5. **TFM = `net10.0`** (EzyAssets is/goes net10). Normal `Framework.*` layer.
6. **SqlKata behind an internal `ISqlCompiler` seam** — its quiet upstream stays swappable/vendorable;
   public contracts are SqlKata-free except the deliberate tier-2 power path.
7. **UoW semantics match across providers** — Dapper uses a per-UoW pending-ops queue flushed inside the
   transaction on `SaveChangesAsync`, so `Add → SaveChanges` (and key population) behaves like EF.
8. **Version line:** 0.4.1 / 0.4.2 / 0.4.3 (additive; kept in the 0.4.x line by request).

---

## 2. Package topology (all `net10.0`, under `src/framework/`)

| Package | Role | Key deps |
|---|---|---|
| `Themia.Framework.Data.Abstractions` (new) | Provider-agnostic contracts only | `Themia.Framework.Core` |
| `Themia.Framework.Data.Dapper` (new) | Engine-agnostic core: `ISqlCompiler` seam, spec→SqlKata translator, tenant-seeded query factory, repository/UoW, audit/soft-delete, connection/tx context, mapping, DI | Abstractions, Core, `Dapper`, `SqlKata` |
| `Themia.Framework.Data.Dapper.PostgreSql` (new, 0.4.1) | `PostgresCompiler` + Npgsql connection factory + `IDapperDatabaseProvider` impl | Dapper core, `Npgsql` |
| `Themia.Framework.Data.Dapper.MySql` (0.4.2) | MySql compiler + `MySqlConnector` | Dapper core |
| `Themia.Framework.Data.Dapper.SqlServer` (0.4.3) | SqlServer compiler + `Microsoft.Data.SqlClient` | Dapper core |
| `Themia.Framework.Data.EFCore` (existing) | Implement the shared contracts (adapters) | + Abstractions |

Build constraints inherited repo-wide: `Nullable=enable`, `TreatWarningsAsErrors=true`,
`GenerateDocumentationFile=true`, central package management. Each cross-cutting package tracks PublicAPI
(`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`). `Dapper 2.1.66` is already pinned; `SqlKata`,
`Npgsql` (and later `MySqlConnector`, `Microsoft.Data.SqlClient`) get pinned in `Directory.Packages.props`.

---

## 3. Shared abstraction (`Themia.Framework.Data.Abstractions`)

Reuses existing Core markers — `ITenantEntity { TenantId? TenantId }`, `IAuditableEntity`
(`CreatedAt/By`, `LastModifiedAt/By`), `ISoftDeletable` (`IsDeleted`, `DeletedAt/By`), `Entity<TKey>`.
Entities stay POCOs with these markers — no provider attributes.

### 3.1 Specification

```csharp
public interface ISpecification<T>
{
    Expression<Func<T, bool>>? Criteria { get; }
    IReadOnlyList<OrderExpression<T>> OrderBy { get; }   // member selector + descending flag
    int? Skip { get; }
    int? Take { get; }
    bool IgnoreTenantFilter { get; }                     // explicit cross-tenant opt-out at spec level
}

public abstract class Specification<T> : ISpecification<T> { /* fluent: Where/And/Or, OrderBy(Desc), Page */ }
// Combinators: And/Or/Not over Criteria (ExpressionVisitor parameter-rebind).
```

### 3.2 Repositories + Unit of Work

```csharp
public interface IReadRepository<T, TKey> where T : class
{
    Task<T?> GetByIdAsync(TKey id, CancellationToken ct = default);
    Task<IReadOnlyList<T>> ListAsync(ISpecification<T> spec, CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(ISpecification<T> spec, CancellationToken ct = default);
    Task<int> CountAsync(ISpecification<T> spec, CancellationToken ct = default);
    Task<bool> AnyAsync(ISpecification<T> spec, CancellationToken ct = default);
    Task<PagedResult<T>> PageAsync(ISpecification<T> spec, CancellationToken ct = default);
}

public interface IRepository<T, TKey> : IReadRepository<T, TKey> where T : class
{
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Remove(T entity);   // soft-delete when T : ISoftDeletable, else hard delete
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task<ITransactionScope> BeginTransactionAsync(CancellationToken ct = default);
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default);
}
```

`PagedResult<T>` carries `Items`, `Total`, `Skip`, `Take`. `ITransactionScope : IAsyncDisposable` with
`CommitAsync`/`RollbackAsync`.

### 3.3 Cross-tenant opt-out

```csharp
public interface IDataFilterScope
{
    IDisposable BypassTenantFilter();   // sets an AsyncLocal both layers read; restores on dispose
}
```

Backed by an `AsyncLocal<bool>` ("tenant filter enabled"). The EF adapter applies `IgnoreQueryFilters()`
when bypass is active; the Dapper tenant-seeded factory skips the tenant predicate. Audit + soft-delete
are unaffected by a tenant bypass.

---

## 4. Dapper layer internals (`Themia.Framework.Data.Dapper`)

### 4.1 SqlKata seam

```csharp
internal interface ISqlCompiler { SqlResult Compile(Query query); }   // SqlResult = sql + named params
```

The PostgreSql package registers a `PostgresSqlCompiler` wrapping SqlKata `PostgresCompiler`. SqlKata
types never appear in the abstraction; inside the Dapper core they are used freely. (Vendoring SqlKata
later, if its upstream stays dormant, is a swap of this seam + the compiler impls.)

### 4.2 Connection / transaction context (scoped, UoW-bound)

```csharp
internal interface IDapperConnectionContext : IAsyncDisposable
{
    Task<DbConnection> GetOpenConnectionAsync(CancellationToken ct);
    DbTransaction? CurrentTransaction { get; }
}
```

Resolves the connection string **DB-per-tenant** via `ITenantAccessor.Current?.ConnectionString` first,
then a configured default — identical to `PostgresDatabaseProvider`. Lazily opens one connection per
scope; all reads/writes and the UoW transaction share it.

### 4.3 Entity mapping

Convention by default to match the EF layer (`UseSnakeCaseNamingConvention`): table = snake_case
(pluralized; overridable), columns = snake_case of property names. Dapper column→property mapping via
`DefaultTypeMap.MatchNamesWithUnderscores = true` (set once at init) so `tenant_id` → `TenantId`.
SqlKata column references are produced from the same mapping. Optional per-entity override registration
(`EntityMap<T>` with table name + column name overrides + key column).

### 4.4 Tenant-seeded query factory (tier-2)

```csharp
public interface ITenantQueryFactory
{
    Query For<T>();   // SqlKata Query seeded with: table, tenant predicate, is_deleted = false
}
```

Seeding rules (skipped/relaxed under a bypass scope):
- `tenant_id = @tenant` and, when global records are allowed, `OR tenant_id IS NULL`.
- No current tenant (and not bypassed) → only `tenant_id IS NULL` (global records) — matches EF's
  `BuildTenantPredicate`.
- `is_deleted = false` for `ISoftDeletable`.

Adopters compose joins/sub-queries/filters on the returned `Query` and execute via Dapper using
`ISqlCompiler` + the shared connection/tx. Tenant + soft-delete cannot be accidentally dropped.

### 4.5 Spec → SqlKata translator (the bounded hard piece)

Walks `ISpecification<T>.Criteria` and emits SqlKata `Where` clauses. **Supported subset (documented):**

| Expression | SqlKata |
|---|---|
| `==, !=, >, >=, <, <=` (member vs constant / captured var) | `Where`, `WhereNot`, comparison ops |
| `&&, \|\|, !` | nested `Where`/`OrWhere`/`WhereNot` |
| `x.Prop == null` / `!= null` | `WhereNull` / `WhereNotNull` |
| `string.Contains/StartsWith/EndsWith` | `WhereLike` (LIKE with `%`, escaped) |
| `coll.Contains(x.Id)` | `WhereIn` |
| member access on entity root | column ref |
| `OrderBy` member selectors | `OrderBy` / `OrderByDesc` |
| `Skip` / `Take` | `Offset` / `Limit` |

Single-table predicates only. Anything outside the subset (nested navigation, projections, method calls
not listed, joins) throws **`UnsupportedSpecificationException`** at translate time with a message
pointing the adopter to tier-2 native SqlKata. Captured variables are evaluated to **parameters** (never
inlined) — SqlKata parameterizes all values.

### 4.6 Repository + UnitOfWork implementation

- **Reads:** `spec → translate → seed (tenant + soft-delete) → ISqlCompiler.Compile → Dapper QueryAsync
  (with shared connection + CurrentTransaction)`. `GetByIdAsync` seeds the key predicate (still
  tenant-scoped). `PageAsync` issues a `COUNT(*)` of the same predicate + the paged select.
- **Writes (deferred):** `AddAsync`/`Update`/`Remove` enqueue a typed pending op on the UoW. On
  `SaveChangesAsync`:
  1. open a transaction on the shared connection if none is active;
  2. for each op in order — **Add:** stamp `TenantId` (from tenant context) + `CreatedAt/By`; full-row
     `INSERT … RETURNING <key>`; write the generated key back onto the entity. **Update:** stamp
     `LastModifiedAt/By`; full-row `UPDATE … WHERE key = @id AND <tenant predicate>`. **Remove +
     `ISoftDeletable`:** `UPDATE is_deleted = true, deleted_at, deleted_by …` (tenant-scoped); else
     `DELETE … WHERE key AND tenant`.
  3. commit; **rollback on any failure**; clear the queue.
- Timestamps from `TimeProvider`; audit "who" from the **same source the EF layer's
  `UpdateAuditableEntities` uses** (to confirm in the plan; align, don't invent a second identity source).

### 4.7 DI

`AddThemiaDapperPostgres(this IServiceCollection, Action<DapperDataOptions>)` (in the PostgreSql package)
registers: connection context (scoped), `PostgresSqlCompiler`, `ITenantQueryFactory`, open-generic
`IRepository<,>` / `IReadRepository<,>`, `IUnitOfWork`, `IDataFilterScope`, entity-map registry. A core
`AddThemiaDapperCore` holds the engine-agnostic registrations the engine packages call.

---

## 5. EF retrofit (`Themia.Framework.Data.EFCore`) — additive

Thin adapters over the existing `ThemiaDbContext` (which already enforces tenant filter, audit,
soft-delete):

- `EfRepository<T, TKey> : IRepository<T, TKey>` — `ListAsync(spec)` =
  `Set<T>().Where(spec.Criteria).OrderBy(…).Skip().Take()`, applying `IgnoreQueryFilters()` when
  `spec.IgnoreTenantFilter` or the bypass scope is active; `GetByIdAsync` → the tenant-guarded
  `FindAsync`; Add/Update/Remove → `DbSet`.
- `EfUnitOfWork : IUnitOfWork` — wraps `SaveChangesAsync` + `Database.BeginTransactionAsync` /
  execution-strategy-aware `ExecuteInTransactionAsync`.
- `IDataFilterScope` — the AsyncLocal that `EfRepository` reads to decide `IgnoreQueryFilters()`.
- DI: an `AddThemiaDataRepositories<TContext>()` extension registering the adapters. Existing
  direct-`DbContext` usage is unchanged.

---

## 6. Data flow, error handling, security

- **Read (spec):** repo → translate → seed tenant + soft-delete → compile → execute → map.
- **Write:** enqueue → `SaveChanges` → stamp + execute in tx → populate keys → commit / rollback.
- **Cross-tenant:** `using (filter.BypassTenantFilter()) { … }` — both layers skip only the tenant
  predicate; audit + soft-delete still apply. No tenant + not bypassed → only global records (EF parity).
- **Errors:** `UnsupportedSpecificationException` (translator); transaction rollback on flush failure;
  `OperationCanceledException` propagates as cancellation. All SQL is **parameterized** (SqlKata; no
  string concatenation) — satisfies the repo's "parameterized queries" security rule. Table/column names
  derive from compile-time mapping/conventions, never from request input.
- **Logging:** `ILogger<T>` only (repo rule); no `Console.*`.

---

## 7. Testing strategy

- **Translator unit tests** — each supported expression shape → expected SQL + parameter set (and each
  unsupported shape → `UnsupportedSpecificationException`).
- **Spec / repository unit tests** — combinators, paging math, soft-delete/remove routing, audit-stamp
  ordering.
- **Testcontainers PostgreSQL conformance suite** (real Npgsql) — CRUD round-trip; **tenant A cannot see
  tenant B**; global-records visibility; soft-delete hide/show; audit stamping (`CreatedAt/By`,
  `LastModifiedAt/By`); paging + total; tier-2 seeded-query safety; cross-tenant bypass; UoW
  commit + rollback. Pin the Postgres image (no `:latest`).
- **Conformance base class** so 0.4.2 (MySQL) / 0.4.3 (SQL Server) retarget the same suite — and, where
  practical, run the same conformance checks against the **EF adapter** to prove both layers honor the
  shared contract identically.
- Follow the `Themia.Exceptional` durable lessons: pin Testcontainers images; never assert
  `DateTimeOffset` tick-exact across a DB round-trip (Postgres `timestamptz` = microsecond); per-engine
  type quirks (GUID format, bool rendering, identifier quoting) live in the engine package, proven by the
  conformance suite.

---

## 8. 0.4.1 deliverables (PostgreSQL)

1. `Themia.Framework.Data.Abstractions` — specs, repo/UoW, `IDataFilterScope`, `PagedResult`, exceptions.
2. `Themia.Framework.Data.Dapper` — seam, translator, tenant-seeded factory, mapping, repo/UoW, DI core.
3. `Themia.Framework.Data.Dapper.PostgreSql` — compiler + Npgsql provider + `AddThemiaDapperPostgres`.
4. `Themia.Framework.Data.EFCore` — adapters implementing the shared contracts.
5. Unit + Testcontainers-PG conformance suites (shared base).
6. CHANGELOG + PublicAPI files; pinned `SqlKata` / `Npgsql`.

0.4.2 = `…Dapper.MySql` + run the conformance base on MySQL. 0.4.3 = `…Dapper.SqlServer` likewise.

## 9. Open items to resolve during planning

- Confirm the EF layer's **audit-identity ("who") source** and reuse it verbatim in the Dapper stamper.
- Confirm `Entity<TKey>` / key-column conventions in Core (single-column keys assumed for 0.4.1).
- Decide whether `IRepository` exposes `AddRangeAsync` in 0.4.1 or defers it (lean: include, it's cheap).
- SqlKata + Npgsql exact pinned versions.
