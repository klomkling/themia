# Themia.Framework.Data.Dapper.MySql engine — design

**Status:** confirmed (brainstorm, 2026-06-10). Target: **0.4.3**.

## Goal

Add a MySQL engine package for the Dapper data layer — the sibling to the existing
`Themia.Framework.Data.Dapper.PostgreSql` — so a Dapper-first app on MySQL gets the same framework
guarantees (tenant isolation, audit, soft-delete, UoW/transactions) that the PostgreSQL engine already
delivers. The engine-agnostic core (`Themia.Framework.Data.Dapper`) is essentially unchanged — only the two
engine seams (`IDapperConnectionFactory`, `ISqlCompiler`) are implemented for MySQL. (One small core addition
was made during review: a shared `DapperConnectionString.Resolve(...)` helper, extracted so both the
PostgreSQL and MySQL connection factories share one tenant-CS resolution rule — see "Implementation note"
below.) Second of the three staged engines (PostgreSQL 0.4.1 → **MySQL 0.4.3** → SQL Server 0.4.4).

> **Implementation note (added during review):** to avoid a third copy of the tenant connection-string
> resolution when the SQL Server engine lands, the duplicated logic was extracted into a public
> `Themia.Framework.Data.Dapper.Connection.DapperConnectionString.Resolve(IConfiguration, IServiceProvider)`
> in the core, and `NpgsqlConnectionFactory` was refactored to use it (the core gains a
> `Microsoft.Extensions.Configuration.Abstractions` reference + one PublicAPI entry). This is the only core
> change; the rest of the work is purely additive in the new MySQL package.

## Context

- The core data layer (specs → SqlKata translator, tenant-seeded queries, repositories, deferred-write UoW,
  audit/soft-delete, mapping, connection/tx context) is engine-agnostic. The PostgreSQL package is just three
  small files: `NpgsqlConnectionFactory` (`IDapperConnectionFactory`), `PostgresSqlCompiler` (`ISqlCompiler`),
  and `AddThemiaDapperPostgres` (DI), plus the csproj + tracked PublicAPI.
- The shared behavioural contract lives in `DataLayerConformanceTests` (provider-agnostic), run against a
  concrete provider by a subclass with a Testcontainers fixture.
- The EF Core data layer is **PostgreSQL-only** (`PostgresDatabaseProvider`); there is no EF-MySQL provider, so
  MySQL conformance is Dapper-only.
- `MySqlConnector 2.6.0` and `Testcontainers.MySql 4.12.0` are already pinned in `Directory.Packages.props`
  (from the 0.3.0 Exceptional MySQL work), which established the MySQL gotchas reused here.

## Decisions (resolved in brainstorm)

1. **Mirror the PostgreSQL engine package** — same three-file shape, same tenant-connection-string resolution.
2. **Enforce `GuidFormat=Char36`** in the connection factory. Themia entities use `Guid` keys; MySqlConnector's
   default Guid handling otherwise yields phantom-empty Guids (the 0.3.0 Exceptional lesson). This is a
   correctness requirement, applied idempotently via `MySqlConnectionStringBuilder`.
3. **Store-generated keys: AUTO_INCREMENT integer via `LAST_INSERT_ID()` only.** SqlKata's `MySqlCompiler`
   natively emits `;SELECT LAST_INSERT_ID()` for `returnId`, which the UoW's existing `ExecuteScalarAsync` +
   `ConvertKey` path consumes — so **no compiler override is expected** (contrast PostgreSQL, which needed the
   `lastval()`→`RETURNING` rewrite for uuids). Store-generated **UUID** is **not** supported on MySQL (no
   `RETURNING`); documented as PostgreSQL-only. Use client-assigned Guids (the common Themia pattern).
4. **Conformance is Dapper-only for MySQL** — run the full shared `DataLayerConformanceTests` against
   Dapper-MySQL; there is no EF-MySQL counterpart.

## Architecture / components (all new)

### `src/framework/Themia.Framework.Data.Dapper.MySql/`

- **`MySqlConnectionFactory.cs`** (`internal sealed`, `IDapperConnectionFactory`) — identical tenant-CS
  resolution to `NpgsqlConnectionFactory`: `ITenantAccessor.Current?.ConnectionString` → `"Default"` fallback,
  throw if neither resolves. Creates a `MySqlConnection`, forcing `GuidFormat=Char36` via
  `MySqlConnectionStringBuilder` (set it if not already `Char36`).
- **`MySqlSqlCompiler.cs`** (`internal sealed`, `ISqlCompiler`) — wraps SqlKata's `MySqlCompiler`; returns
  `CompiledSql(result.Sql, result.NamedBindings)`. No `LAST_INSERT_ID` rewrite expected (SqlKata native). If a
  multi-statement `INSERT … ; SELECT LAST_INSERT_ID()` does not round-trip cleanly through MySqlConnector's
  `ExecuteScalarAsync`, the override falls back to reading the auto-increment id — to be confirmed in the plan's
  store-gen task (the conformance suite uses client-assigned keys and does not depend on this path).
- **`DependencyInjection/MySqlDapperServiceCollectionExtensions.cs`** — `public static`
  `AddThemiaDapperMySql(this IServiceCollection, IConfiguration, Action<DapperDataOptions>? = null)`: calls
  `AddThemiaDapperCore(configure)`, registers `MySqlConnectionFactory` (scoped) and `MySqlSqlCompiler`
  (singleton `ISqlCompiler`) — the exact analogue of `AddThemiaDapperPostgres`.
- **`.csproj`** — net10; `PackageId Themia.Framework.Data.Dapper.MySql`; refs the Dapper core project +
  `MySqlConnector`, `SqlKata`, `Microsoft.Extensions.Configuration.Abstractions`,
  `Microsoft.Extensions.DependencyInjection.Abstractions`, and the PublicApiAnalyzer; `PublicAPI.Shipped.txt` /
  `PublicAPI.Unshipped.txt` (only `AddThemiaDapperMySql` is public surface).
- Add the project to `Themia.sln`.

### Known MySQL adaptations (reuse 0.3.0 Exceptional lessons)

- **Guid** → `CHAR(36)` columns + `GuidFormat=Char36` (above).
- **Timestamps** → `DATETIME(6)` (µs precision). MySQL is tz-naive; read-back normalizes to UTC. Conformance
  assertions are round-trip-tolerant (never tick-exact), so the audit facts (`CreatedAt > MinValue`,
  `LastModifiedAt` not null) hold. Store UTC.
- **Quoting / paging** — backticks, `LIMIT/OFFSET` — handled by SqlKata's `MySqlCompiler`.

### Tests — `tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/`

- **`MySqlContainerFixture`** — Testcontainers `mysql:8.4`; creates the `widgets` table (`id CHAR(36)` PK,
  `tenant_id VARCHAR(100) NULL`, `name VARCHAR(200)`, `quantity INT`, `is_deleted TINYINT(1)`,
  `created_at/last_modified_at/deleted_at DATETIME(6)`, `created_by/last_modified_by/deleted_by VARCHAR(100)`);
  `ResetAsync` truncates. Connection string sets `GuidFormat=Char36`. Pin the image tag (Exceptional lesson).
- **`DapperMySqlConformanceTests : DataLayerConformanceTests`** — `NewScopeAsync` wires
  `AddThemiaDapperMySql` + `ITenantContext`, builds a DI scope, resolves the three contracts; `ResetAsync`
  delegates to the fixture. Runs the **entire** shared suite against Dapper-MySQL (no EF subclass).
- **`MySqlStoreGeneratedKeyTests`** — an `AUTO_INCREMENT` integer-keyed entity (e.g. `Gadget : Entity<int>`,
  table `gadgets (id BIGINT AUTO_INCREMENT PRIMARY KEY, …)`); insert with an unassigned key, save, assert the
  key was populated from `LAST_INSERT_ID()` and the row round-trips. (No uuid store-gen test — PG-only.)
- The conformance/test class library marks `IsTestProject=false`/`IsPublishable=false` where it mirrors the
  existing pattern; the integration project is a normal test project.

## Testing strategy

The shared conformance suite is the proof the MySQL engine honours the contract (tenant A≠B, cross-tenant write
throws / under-bypass succeeds, no-tenant→tenant throws, soft-delete hide/restore, audit stamping, paging+total,
IN-list, transaction rollback). The store-gen-int test locks the one MySQL-specific write path
(`LAST_INSERT_ID`). Carry the Exceptional lessons: pin the image, never assert `DateTimeOffset` tick-exact
across a round-trip, normalize read-back `Kind` to UTC.

## Non-goals

- Store-generated **UUID** on MySQL (no `RETURNING`) — documented PostgreSQL-only.
- An EF-Core MySQL provider — out of scope; MySQL conformance is Dapper-only.
- SQL Server (that is 0.4.4).
- MariaDB-specific tuning — `mysql:8.4` is the tested target; MariaDB likely works but is untested this slice.
- The unrelated Dapper backlog (`RETURNING <keyColumn>` threading; EF write-verify batching; FluentMigrator).

## Open items

None — all forks resolved (mirror PG; enforce Char36; AUTO_INCREMENT int store-gen via LAST_INSERT_ID, uuid
PG-only; Dapper-only conformance). The one implementation unknown (whether SqlKata's native `LAST_INSERT_ID`
multi-statement round-trips through MySqlConnector, vs. needing a small compiler/UoW fallback) is isolated to the
store-gen task and does not affect the client-assigned-key path the conformance suite exercises. Versioned 0.4.3
at release time.
