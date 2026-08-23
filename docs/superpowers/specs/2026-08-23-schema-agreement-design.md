# Schema agreement between migrations and stores (coord #0088)

Date: 2026-08-23
Status: design, rev 2 — approved to implement
Coord: #0088 (`Themia.Data.Migrations`, open since 2026-08-17)

Rev 1 specified a migrate-time guard on `current_schema()` plus a store probe, and was reworked
after review. Both halves were wrong in the same way: they asserted where a table *ought* to
live. See "Options considered and rejected" — that section is the main output of this design and
most of what the coord reply is built from.

## Problem

On PostgreSQL, FluentMigrator ignores `search_path`. It resolves an unqualified migration to
`public` for both the existence probe (`Schema.Table(x).Exists()`) and the DDL. Themia's stores
issue unqualified SQL through Npgsql, which *does* follow `search_path`.

Two failure modes, both silent:

1. **Stray copy.** The table already exists outside `public`. FluentMigrator's guard does not see
   it, the CREATE lands in `public`, and a second empty copy appears. Nothing breaks today — the
   store keeps using the real one — but a later Themia migration would alter the copy nothing reads.
2. **Wrong schema on a fresh database.** `public` is not on the path. The migration creates
   `public.<table>`, boot succeeds, health checks pass, and the first runtime write fails with
   `42P01`. Green deploy, broken at runtime — the late-failure signature of #0078 and #0085.

Mode 2 is the one that costs an outage. Mode 1 breaks nothing today.

## Inventory (measured 2026-08-23, not inferred)

#0088 verified only `Themia.AspNetCore.DataProtection` and asked for an inventory of the rest.
Five assemblies create tables without `InSchema(...)` and read them through unqualified SQL:

| Assembly | Table(s) | Runtime access |
| --- | --- | --- |
| `Themia.AspNetCore.DataProtection` | `data_protection_keys` | Dapper, unqualified |
| `Themia.Exceptional` | `"Exceptions"` + 4 indexes + one `Alter` | `PostgresExceptionalDialect`, unqualified, **quoted/case-sensitive** |
| `Themia.Modules.Pdf` | `pdf_templates` | EF Core `ToTable("pdf_templates")`, no `HasDefaultSchema` |
| `Themia.Modules.Messaging` | `messaging_outbox_messages`, `messaging_inbox_messages` | Dapper, unqualified |
| `Themia.Challenges` | `challenges`, `challenge_rate_windows` | Dapper, unqualified |

Unaffected — these qualify DDL with `Create.Schema(SchemaName)` + `InSchema(...)`, so both halves
name the same schema: `Themia.Modules.Export`, `Themia.Modules.Identity`,
`Themia.Modules.Notifications`, `Themia.Modules.Storage`, `Themia.Scheduling`.

## The constraint that shapes everything

**Themia cannot know which schema a table *should* be in.** There are two populations and
"correct" means different things in each:

- Consumers who let `ThemiaMigrations.Run` migrate: FluentMigrator writes `public`, so the store
  must resolve `public`.
- Consumers on `runMigration: false` — the remedy #0085 recommended and pointed propertiezy at —
  own the migration themselves and may legitimately create the table in their own schema. Themia
  has no basis to call that wrong.

Every rejected option below fails by asserting an answer Themia does not have.

## Design

One component. No changes to `ThemiaMigrations`, no new option, no behaviour change for any
currently-working deployment.

### New package: `Themia.Data.Probes`

Neutral, `net8.0;net10.0`. Dependencies: BCL (`System.Data`) plus
`Microsoft.Extensions.Logging.Abstractions` for the mode-1 warning, and the repo-standard
`Microsoft.CodeAnalysis.PublicApiAnalyzers`.

A separate package rather than a home in `Themia.Data.Migrations`, because that one pulls
FluentMigrator, all three runners and all three database drivers. Four of the five affected
packages already pay that; `Themia.Messaging.PostgreSql` carries only `Npgsql` + `Dapper` today,
and a guard this small does not justify that dependency.

```csharp
namespace Themia.Data.Probes;

/// <summary>
/// Confirms that a table a Themia store addresses without a schema actually resolves through the
/// connection's <c>search_path</c>. PostgreSQL only.
/// </summary>
public static class PostgresSchemaProbe
{
    /// <param name="tableName">
    /// The identifier exactly as the store's own SQL writes it — unqualified, quoting included:
    /// <c>data_protection_keys</c>, but <c>"Exceptions"</c>.
    /// </param>
    /// <exception cref="SchemaVisibilityException">The identifier resolves to nothing.</exception>
    public static void EnsureResolvable(IDbConnection connection, string tableName, ILogger? logger = null);
}

public sealed class SchemaVisibilityException : Exception;
```

### What it asserts, and what it deliberately does not

**Assert: the identifier resolves.** Not that it resolves to `public`. This is the only claim
true in both populations, and it is exactly the claim that catches mode 2.

One round trip carries both signals:

```sql
SELECT
  (SELECT n.nspname
     FROM pg_class c
     JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.oid = to_regclass(@name))            AS resolved_schema,
  (to_regclass('public.' || @name) IS NOT NULL)  AS public_copy_exists
```

- `resolved_schema` NULL → **throw**. The store is about to address a table that does not exist
  on its path. This is mode 2, caught before the first write instead of at it.
- `resolved_schema` is not `public` **and** `public_copy_exists` → **log a warning**, proceed.
  That conjunction is a literal description of mode 1: the store is using one table while a
  Themia migration manages another. It cannot fire on a deployment that merely lives outside
  `public`, because such a deployment has no `public` copy.
- otherwise → proceed silently.

`to_regclass` returns NULL rather than throwing for an unresolvable name, and resolves names
exactly the way the store's own unqualified SQL does — which is the property that makes it the
right probe rather than a lookup in `information_schema`.

String-concatenating `'public.' || @name` is safe with the quoting rule above: `'public.' ||
'"Exceptions"'` gives `public."Exceptions"`, which `to_regclass` parses correctly. `@name` is a
compile-time constant in every call site; no caller-supplied value reaches it.

**No caching in the package.** It stays a pure function; each store owns its own once-flag. A
static cache needs a key, and neither the connection string (a secret) nor the database name
(not unique per search_path) is both safe and correct.

### Call sites — PostgreSQL legs only, once on first store use

| Package | Identifier(s) |
| --- | --- |
| `Themia.AspNetCore.DataProtection.PostgreSql` | `data_protection_keys` |
| `Themia.Exceptional.PostgreSql` | `"Exceptions"` |
| `Themia.Challenges.PostgreSql` | `challenges`, `challenge_rate_windows` |
| `Themia.Messaging.PostgreSql` | `messaging_outbox_messages`, `messaging_inbox_messages` |
| `Themia.Modules.Pdf` | `pdf_templates`, via `DbContext.Database.GetDbConnection()` |

MySQL binds schema to the connection's database and SQL Server defaults to `dbo`, which
FluentMigrator agrees with. Neither engine has the split; neither gets a probe.

## Error handling

`SchemaVisibilityException` must name the remedy, not just the fault, and must never include the
connection string. It carries the identifier as probed, the schemas that *do* hold a table of
that name (from `pg_class`, so the message can say "exists in `public`, not on your search_path"),
and the fact that Themia's migrations create it in `public`. "Table not found" is not
diagnosable; "created in `public`, your path resolves `app`" is.

The mode-1 warning names both schemas and states the consequence — a later Themia migration will
alter the copy the store does not read.

## Testing

Testcontainers against `postgres:16-alpine`. The EF InMemory provider reproduces none of this.

- table only in `app`, `search_path=app` → **no throw** (the `runMigration:false` population,
  working correctly — this is the false positive rev 1 would have produced)
- table only in `public`, `search_path=app` with `public` off the path → **throws** (mode 2)
- table in both `app` and `public`, `search_path=app` → no throw, **warning logged** (mode 1)
- default `search_path`, table in `public` → no throw, no warning
- `search_path` = `"$user", public` with a role-named schema present that holds no Themia table →
  **no throw** (rev 1 threw here; the deployment is correct)
- `"Exceptions"` resolves through the quoted path; probing a quoted table unquoted folds to lower
  case and must not report a false negative
- MySQL and SQL Server stores never call the probe

**Regression pin.** #0088's own reproduction — seed `app.data_protection_keys`, run
`ThemiaMigrations.Run` with `Search Path=app`, observe the table in **both** `app` and `public` —
lands as a test asserting the store now logs the mode-1 warning. #0088 declined to commit it
because it pinned undecided behaviour; the behaviour is decided now.

## Options considered and rejected

Each fails the same way: it asserts a schema Themia is not entitled to choose.

**(a) Read `search_path` and pass it to FluentMigrator.** Silently relocates tables for anyone
living with a split. A data move dressed as a bug fix.

**(b) Fail fast in `ThemiaMigrations.Run` on `current_schema() != 'public'`.** Rev 1's design.
`current_schema()` is where an unqualified *CREATE* would land; read resolution walks the whole
path in order. With the PostgreSQL default `"$user", public` and a role-named schema that exists
but holds no Themia table, `current_schema()` returns that schema while every store read still
falls through to `public` and works. The guard refuses a correct deployment. It is only
salvageable by having each assembly declare its table names to the runner — plumbing out of
proportion to a defect #0088 itself records as *"No consumer has reported it"*.

**(c) A schema option consumed by both halves.** Blocked in the source:
`Themia.Challenges/Migrations/ChallengeSchemaMigration.cs:21-22` — *"Unprefixed literal table
names on every engine, never `InSchema(...)`. FluentMigrator drops `InSchema(...)` on MySQL —
there, 'schema' and 'database' are the same concept"*. `Themia.Modules.Messaging` carries the
same note. Applying (c) to those two either breaks MySQL parity or makes the option
PostgreSQL-only.

**Qualify the PostgreSQL stores' SQL to `public`.** Not one of #0088's options; raised in review
as the way to make both halves agree by construction, and rejected on a counter-example. It
removes a capability that works today: a consumer running their own migrations can place Themia
tables in their own schema, which is how schema-per-app and schema-per-tenant deployments in a
shared database work. Hardcoding `public` ends that permanently for every store, and restoring
it later is breaking. It would also fail `42P01` at runtime — not at boot — for any (d) consumer
on a non-default `search_path`, and (d) is not a hypothetical population: #0085's recommended
remedy created it.

**Probe asserting `to_regclass(t) = 'public.t'::regclass`.** The first correction proposed in
review, rejected for the same reason: it false-positives on exactly the (d) consumers the probe
exists to serve.

## Open question for the coord thread

Whether anyone runs Themia tables outside `public` today. The answer does not change this design
— resolvability is the right assertion either way — but it decides whether mode 1 deserves more
than a warning later, and it is cheaper to ask than to infer. ezy-assets measured themselves at
the default `search_path` on #0088; propertiezy has not been asked.
