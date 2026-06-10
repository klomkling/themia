# Themia.Framework.Data.Dapper.SqlServer engine — design

**Status:** confirmed (brainstorm, 2026-06-10). Target: **0.4.4**.

## Goal

Add a SQL Server engine package for the Dapper data layer — the third and final sibling to the existing
`Themia.Framework.Data.Dapper.PostgreSql` and `.MySql` — so a Dapper-first app on SQL Server gets the same
framework guarantees (tenant isolation, audit, soft-delete, UoW/transactions) the other two engines already
deliver. The engine-agnostic core (`Themia.Framework.Data.Dapper`) is **unchanged**: only the two engine seams
(`IDapperConnectionFactory`, `ISqlCompiler`) plus a process-global Dapper configuration are implemented for SQL
Server, all reusing the shared `DapperConnectionString.Resolve(...)` helper added in 0.4.3. Last of the three
staged engines (PostgreSQL 0.4.1 → MySQL 0.4.3 → **SQL Server 0.4.4**).

> **No core change.** Verified against SqlKata 2.4.0: the `SqlServerCompiler` natively emits
> `;SELECT scope_identity() as Id` for `returnId`, which the UoW's existing `ExecuteScalarAsync` consumes, and
> `ConvertKey`'s `Convert.ChangeType` already converts SCOPE_IDENTITY's `decimal` (`numeric(38,0)`) to the
> entity's `int` key. The shared `DapperConnectionString.Resolve` resolver (extracted in 0.4.3) means there is
> no third copy of the tenant-connection-string logic. All work is purely additive in the new SQL Server package.

## Context

- The core data layer (specs → SqlKata translator, tenant-seeded queries, repositories, deferred-write UoW,
  audit/soft-delete, mapping, connection/tx context) is engine-agnostic. The MySQL package is four small files:
  `MySqlConnectionFactory` (`IDapperConnectionFactory`), `MySqlSqlCompiler` (`ISqlCompiler`),
  `MySqlDapperConfiguration` (process-global Dapper type handler), and `AddThemiaDapperMySql` (DI), plus the
  csproj + tracked PublicAPI. The SQL Server package mirrors this four-file shape.
- The shared behavioural contract lives in `DataLayerConformanceTests` (provider-agnostic), run against a
  concrete provider by a subclass with a Testcontainers fixture.
- The EF Core data layer is **PostgreSQL-only** (`PostgresDatabaseProvider`); there is no EF-SQL-Server provider,
  so SQL Server conformance is Dapper-only (same as MySQL).
- `Microsoft.Data.SqlClient 6.1.5` and `Testcontainers.MsSql 4.12.0` are **already pinned** in
  `Directory.Packages.props` (from the 0.3.0 Exceptional SQL Server work) — no new package versions are added.

## Decisions (resolved in brainstorm)

1. **Mirror the MySQL engine package** — same four-file shape (factory / compiler / Dapper config / DI), same
   shared `DapperConnectionString.Resolve` tenant-CS resolution.
2. **Connection factory is the simplest of the three.** `Microsoft.Data.SqlClient` maps `uniqueidentifier` ↔
   `Guid` natively, so there is **no Guid-format tweak** (contrast MySQL's `OldGuids=false`/`GuidFormat=Char36`).
   The factory is just `new SqlConnection(DapperConnectionString.Resolve(configuration, serviceProvider))`.
3. **Store-generated keys: `INT IDENTITY(1,1)` via native `scope_identity()`.** SqlKata's `SqlServerCompiler`
   emits `;SELECT scope_identity() as Id` for `returnId` (verified in the 2.4.0 assembly) — so **no compiler
   override is needed** (contrast PostgreSQL's `lastval()`→`RETURNING` rewrite; same as MySQL's native
   `LAST_INSERT_ID()`). SCOPE_IDENTITY returns `numeric(38,0)` → surfaced as `decimal`; `ConvertKey` already
   widens it to `int`. Store-generated **Guid** is **not** supported (no `RETURNING`); use client-assigned Guids
   (the common Themia pattern), documented as PostgreSQL-only.
4. **Explicit modern pagination.** The compiler wraps `new SqlServerCompiler { UseLegacyPagination = false }` so
   list paging emits `OFFSET … ROWS FETCH NEXT … ROWS ONLY` (SQL Server 2012+) rather than the legacy
   `ROW_NUMBER()` form. Set explicitly rather than relying on the SqlKata default, so the choice is
   self-documenting and version-independent.
5. **Timestamps: `datetime2(7)` + a SQL-Server `DateTimeOffset` Dapper handler** (`DbType.DateTime2`; write
   `value.UtcDateTime`, read `new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))`). This mirrors
   MySQL's "store UTC, read back UTC" semantic for cross-engine consistency, but uses `DbType.DateTime2` (µs+
   precision, full date range) rather than MySQL's `DbType.DateTime`. Registered once via
   `SqlServerDapperConfiguration.EnsureConfigured()`; the process-global handler keeps the documented
   one-Dapper-engine-per-process assumption (same as MySQL).
6. **Conformance is Dapper-only for SQL Server** — run the full shared `DataLayerConformanceTests` plus the
   audit / no-tenant / µs-precision-UTC facts the MySQL package added; there is no EF-SQL-Server counterpart.

## Architecture / components (all new)

### `src/framework/Themia.Framework.Data.Dapper.SqlServer/`

- **`SqlServerConnectionFactory.cs`** (`internal sealed`, `IDapperConnectionFactory`) —
  `Create() => new SqlConnection(DapperConnectionString.Resolve(configuration, serviceProvider))`. No
  connection-string-builder tweaks (native Guid mapping).
- **`SqlServerSqlCompiler.cs`** (`internal sealed`, `ISqlCompiler`) — wraps
  `new SqlServerCompiler { UseLegacyPagination = false }`; returns `CompiledSql(result.Sql, result.NamedBindings)`.
  No `scope_identity` rewrite (SqlKata native).
- **`SqlServerDapperConfiguration.cs`** (`internal static`) — `EnsureConfigured()` (double-checked lock +
  volatile guard, mirroring `MySqlDapperConfiguration`) registering a nested
  `DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>`: `Parse` passes a `DateTimeOffset` through /
  maps a `DateTime` to `new DateTimeOffset(SpecifyKind(dt, Utc))` / else throws; `SetValue` sets
  `parameter.DbType = DbType.DateTime2` and `parameter.Value = value.UtcDateTime`. A comment documents the
  one-engine-per-process assumption (a dual-engine process would mis-target the other engine's timestamp type).
- **`DependencyInjection/SqlServerDapperServiceCollectionExtensions.cs`** — `public static`
  `AddThemiaDapperSqlServer(this IServiceCollection, IConfiguration, Action<DapperDataOptions>? = null)`: calls
  `SqlServerDapperConfiguration.EnsureConfigured()`, `AddThemiaDapperCore(configure)`, registers
  `SqlServerConnectionFactory` (scoped) and `SqlServerSqlCompiler` (singleton `ISqlCompiler`) — the exact
  analogue of `AddThemiaDapperMySql`.
- **`.csproj`** — `net8.0;net10.0`; `PackageId Themia.Framework.Data.Dapper.SqlServer`; refs the Dapper core
  project + `Microsoft.Data.SqlClient`, `SqlKata`, `Microsoft.Extensions.Configuration.Abstractions`,
  `Microsoft.Extensions.DependencyInjection.Abstractions`, `Dapper`, and the PublicApiAnalyzer;
  `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` (only `AddThemiaDapperSqlServer` is public surface).
- Add the project to `Themia.sln`.

### Known SQL Server adaptations

- **Guid** → `UNIQUEIDENTIFIER` columns; native `Guid` mapping in `Microsoft.Data.SqlClient` (no format tweak).
- **Timestamps** → `datetime2(7)` (100 ns precision) + the `DbType.DateTime2` handler above. Store UTC; read-back
  normalizes `Kind` to UTC. Conformance assertions are round-trip-tolerant (never tick-exact).
- **Bool** → `BIT`.
- **Quoting / paging** → `[bracket]` quoting and `OFFSET/FETCH` — handled by `SqlServerCompiler`
  (`UseLegacyPagination = false`).

### Tests — `tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/`

- **`SqlServerContainerFixture`** — Testcontainers `MsSqlBuilder` (`mcr.microsoft.com/mssql/server:2022-latest`,
  image tag pinned); creates the `widgets` table (`id UNIQUEIDENTIFIER` PK, `tenant_id NVARCHAR(100) NULL`,
  `name NVARCHAR(200)`, `quantity INT`, `is_deleted BIT`, `created_at/last_modified_at/deleted_at/restored_at
  datetime2(7)`, `created_by/last_modified_by/deleted_by/restored_by NVARCHAR(100)`); `ResetAsync` truncates.
- **`DapperSqlServerConformanceTests : DataLayerConformanceTests`** — `NewScopeAsync` wires
  `AddThemiaDapperSqlServer` + `ITenantContext`, builds a DI scope, resolves the three contracts; `ResetAsync`
  delegates to the fixture. Runs the **entire** shared suite against Dapper-SQL-Server (no EF subclass), plus the
  audit-stamping, no-tenant-soft-delete, and µs-precision UTC round-trip facts (mirroring the MySQL package, using
  a `FixedTimeProvider`).
- **`SqlServerStoreGeneratedKeyTests`** — an `INT IDENTITY(1,1)`-keyed entity (`Gadget : AuditableEntity<int>`,
  table `gadgets (id INT IDENTITY(1,1) PRIMARY KEY, …)`); insert with an unassigned key, save, assert the key was
  populated from `scope_identity()` and the row round-trips. (No uuid store-gen test — PG-only.)

## Testing strategy

The shared conformance suite is the proof the SQL Server engine honours the contract (tenant A≠B, cross-tenant
write throws / under-bypass succeeds, no-tenant→tenant throws, soft-delete hide/restore, audit stamping,
paging+total, IN-list, transaction rollback). The store-gen-int test locks the one SQL-Server-specific write path
(`scope_identity()` → `decimal` → `int`). Carry the established lessons: pin the image, never assert
`DateTimeOffset` tick-exact across a round-trip, normalize read-back `Kind` to UTC.

## Non-goals

- Store-generated **Guid** on SQL Server (no `RETURNING`) — documented PostgreSQL-only.
- An EF-Core SQL Server provider — out of scope; SQL Server conformance is Dapper-only.
- Legacy SQL Server (< 2012) pagination — `OFFSET/FETCH` only; `mcr.microsoft.com/mssql/server:2022-latest` is
  the tested target.
- The unrelated Dapper backlog (`RETURNING <keyColumn>` threading; EF write-verify batching;
  `DataFilterScope` static-AsyncLocal refactor; one-engine-per-process startup guard; FluentMigrator 6→8).

## Open items

None — all forks resolved (mirror MySQL; native Guid, no format tweak; `INT IDENTITY` store-gen via native
`scope_identity()`, Guid PG-only; explicit `UseLegacyPagination = false`; `datetime2(7)` + `DbType.DateTime2`
handler; Dapper-only conformance). The store-gen path is verified at the assembly level (SqlKata emits
`scope_identity()`; `ConvertKey` widens `decimal`→`int`) and locked by the store-gen-int test. Versioned 0.4.4 at
release time.
