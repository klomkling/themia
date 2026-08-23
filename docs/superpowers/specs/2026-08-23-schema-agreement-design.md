# Schema agreement between migrations and stores (coord #0088)

Date: 2026-08-23
Status: design, approved to implement
Coord: #0088 (`Themia.Data.Migrations`, open since 2026-08-17)

## Problem

On PostgreSQL, FluentMigrator resolves an unqualified migration to `public` for both the
existence probe (`Schema.Table(x).Exists()`) and the DDL. It ignores `search_path`. Themia's
Dapper stores issue unqualified SQL through Npgsql, which *does* follow `search_path`.

So on a non-default `search_path` the two halves manage different tables and neither mentions
the other. Two orders, both silent:

1. **Table already outside `public`.** The guard does not see it, the CREATE lands in `public`,
   and a stray empty copy appears. Nothing breaks today, but every future Themia migration
   edits the copy nothing reads.
2. **Fresh database, `public` not on the path.** The migration creates `public.<table>`, boot
   succeeds, health checks pass, and the first runtime write fails with `42P01`. Green deploy,
   broken at runtime — the same late-failure signature as coord #0078 and #0085.

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

Unaffected — these qualify DDL with `Create.Schema(SchemaName)` + `InSchema(...)`, so both
halves name the same schema: `Themia.Modules.Export`, `Themia.Modules.Identity`,
`Themia.Modules.Notifications`, `Themia.Modules.Storage`, `Themia.Scheduling`.

### The constraint that rules out option (c)

`Themia.Modules.Messaging` and `Themia.Challenges` are unqualified **deliberately**, and the
reason is recorded in the source:

> `Themia.Challenges/Migrations/ChallengeSchemaMigration.cs:21-22` — *"Unprefixed literal table
> names on every engine, never `InSchema(...)`. FluentMigrator drops `InSchema(...)` on MySQL —
> there, 'schema' and 'database' are the same concept"*

So option (c) from #0088 — a schema option both halves consume — cannot be applied uniformly.
Doing it for these two would either break MySQL parity or make the option PostgreSQL-only.
(c) is therefore **not built** here; the constraint is reported back on the coord thread so the
consumers arguing for it can see what it costs.

## Scope

Two independent guards that deliberately cover different populations:

- **(b) fail-fast in `ThemiaMigrations`** covers consumers who let Themia run its migrations.
- **Store-side probe** covers the `runMigration: false` population — consumers who create the
  table with their own migration, as recommended on #0085. `ThemiaMigrations.Run` is never
  called for them, so (b) can never fire; ezy-assets raised this gap on #0088 and it is correct.

Neither guard subsumes the other. The probe also covers a case (b) structurally cannot see: an
app that connects with a different role than the migration runner, where the two roles have
different `search_path` settings.

## Component 1 — schema agreement check in `ThemiaMigrations`

**Placement.** `MigrationLock.RunExclusive` (`MigrationLock.cs:66-73`) already opens a dedicated
`DbConnection` and holds it for the whole migration. The check runs immediately after
`connection.Open()` and before `Acquire`. No extra connection.

**The invariant.** Not "is `search_path` the default" — that is a proxy. The real invariant:

> the schema where unqualified runtime SQL lands (`current_schema()`) must equal the schema
> FluentMigrator writes DDL to (always `public` on PostgreSQL).

Read the *effective* value from the open connection, never from the connection string:
`ALTER ROLE ... SET search_path` is invisible in the DSN, so a DSN-based check is the weaker
claim. (Raised by ezy-assets on #0088; correct.)

**Behaviour.** PostgreSQL only. MySQL binds schema to the connection's database and SQL Server
defaults to `dbo`, which FluentMigrator agrees with; neither engine has the split.

```
SELECT current_schema()
```

- result is `public` → proceed
- result is anything else, or NULL (every entry on the path is missing) → throw

**Option.** `ThemiaMigrationOptions.SchemaAgreement`, defaulting to `Enforce`:

```csharp
public enum SchemaAgreementCheck
{
    /// <summary>Refuse to migrate when migrations and runtime would resolve to different schemas.</summary>
    Enforce,

    /// <summary>Migrate anyway. The caller owns the split.</summary>
    Disabled,
}
```

An enum rather than a `bool`: a `Warn` state was considered and rejected for this pass, so a
third state is plausible. Adding it to an enum breaks the exhaustive `switch` at every consumer;
adding a second bool compiles silently.

**Known behaviour change, accepted.** The PostgreSQL default `search_path` is `"$user", public`.
If a schema matching the role name exists, `current_schema()` returns *that*, not `public` — a
deployment can be split today without anyone having configured anything. Such a deployment boots
today and will throw on upgrade. That is the intended outcome (the split is real and silent), and
`SchemaAgreement = Disabled` is the escape hatch. This must be called out in the release notes:
it is an upgrade that can fail at boot, the same failure shape as #0085.

## Component 2 — `Themia.Data.Probes`

**New neutral package.** `net8.0;net10.0`, no dependencies beyond the BCL (`System.Data`) and
the repo-standard `Microsoft.CodeAnalysis.PublicApiAnalyzers`.

A new package rather than a home in `Themia.Data.Migrations`, because that package pulls
FluentMigrator, all three FluentMigrator runners and all three database drivers. Four of the five
affected packages already pay that cost, but `Themia.Messaging.PostgreSql` today carries only
`Npgsql` + `Dapper`, and a ~15-line guard is not worth that dependency.

**Surface.**

```csharp
namespace Themia.Data.Probes;

/// <summary>
/// Verifies that a table Themia's migrations create in <c>public</c> is the same table the
/// connection's <c>search_path</c> resolves. PostgreSQL only.
/// </summary>
public static class PostgresSchemaProbe
{
    /// <param name="tableName">
    /// The identifier exactly as the store's own SQL writes it, quoting included —
    /// <c>data_protection_keys</c> but <c>"Exceptions"</c>.
    /// </param>
    public static void EnsureVisible(IDbConnection connection, string tableName);
}

public sealed class SchemaVisibilityException : Exception;
```

**Implementation.** `SELECT to_regclass(@name)` — NULL means the identifier does not resolve
through the current `search_path`. `to_regclass` resolves exactly the way the store's own
unqualified SQL does, which is the property that makes it the right probe.

**No caching inside the package.** It stays a pure function; each store owns its own once-flag.
A static cache would need a key (connection string or database name), and neither is both safe
and correct.

**Call sites** — PostgreSQL legs only, probed once on first store use:

| Package | Identifier(s) |
| --- | --- |
| `Themia.AspNetCore.DataProtection.PostgreSql` | `data_protection_keys` |
| `Themia.Exceptional.PostgreSql` | `"Exceptions"` |
| `Themia.Challenges.PostgreSql` | `challenges`, `challenge_rate_windows` |
| `Themia.Messaging.PostgreSql` | `messaging_outbox_messages`, `messaging_inbox_messages` |
| `Themia.Modules.Pdf` | `pdf_templates`, via `DbContext.Database.GetDbConnection()` |

## Error handling

Both messages must name the remedy, not just the fault. Neither may include the connection
string.

`MigrationSchemaException` — carries the observed `current_schema()`, the schema FluentMigrator
will write to, and the two ways out: put `public` first on the migration role's `search_path`,
or set `SchemaAgreement = Disabled` and own the split.

`SchemaVisibilityException` — carries the identifier as probed, the observed `current_schema()`,
and the fact that Themia's migrations create the table in `public`. When the table exists in some
other schema, name that schema: "created in `public`, runtime resolves `app`" is diagnosable;
"table not found" is not.

## Testing

Testcontainers against `postgres:16-alpine`. The EF InMemory provider cannot reproduce any of
this.

**Component 1**
- `search_path=app` with `app` existing → `Run` throws `MigrationSchemaException`
- default `search_path` → no throw
- `search_path=app`, `SchemaAgreement = Disabled` → no throw, migration proceeds
- `search_path` naming only a missing schema (`current_schema()` NULL) → throws
- MySQL and SQL Server legs → never throw, whatever the connection says

**Component 2**
- table created in `app`, connection on `public` → `EnsureVisible` throws
- table visible through `search_path` → no throw
- quoted case-sensitive identifier (`"Exceptions"`) resolves correctly — an unquoted probe of a
  quoted table folds to lower case and would report a false negative

**Regression pin.** The #0088 reproduction — seed `app.data_protection_keys`, run
`ThemiaMigrations.Run` with `Search Path=app`, observe the table in **both** `app` and `public` —
becomes a test asserting the run now fails fast instead of silently creating the second copy.
#0088 declined to commit this test because it pinned a behaviour nobody had decided to keep;
that decision is now made, so it lands.

## Out of scope

- Option (c), a schema option consumed by both halves — blocked by the MySQL `InSchema`
  constraint above. Reported on coord, not built.
- Option (a), reading `search_path` and passing it to FluentMigrator — silently relocates tables
  for anyone currently living with a split. A data move dressed as a bug fix.
- MySQL and SQL Server: no split exists on either.
