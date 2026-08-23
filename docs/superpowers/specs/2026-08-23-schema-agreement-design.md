# Schema agreement between migrations and stores (coord #0088)

Date: 2026-08-23
Status: design, rev 3 — approved to implement
Coord: #0088 (`Themia.Data.Migrations`, open since 2026-08-17)

Revision history, because two of the three revisions were wrong in ways worth not repeating:

- **rev 1** — a migrate-time guard on `current_schema()` plus a store probe. Both asserted where
  a table *ought* to live, which Themia is not entitled to decide. The guard refused correct
  deployments.
- **rev 2** — probe only, asserting resolvability. Correct assertion, wrong placement: it ran on
  first store use, which is the same moment the missing table would have thrown anyway. It paid
  for a new package and delivered a better error message.
- **rev 3** — same assertion, moved to boot, where it changes *when* the failure happens.

## Problem

On PostgreSQL, FluentMigrator ignores `search_path`. It resolves an unqualified migration to
`public` for both the existence probe (`Schema.Table(x).Exists()`) and the DDL. Themia's stores
issue unqualified SQL through Npgsql, which *does* follow `search_path`.

Two failure modes, both silent:

1. **Stray copy.** The table already exists outside `public`. FluentMigrator's guard does not see
   it, the CREATE lands in `public`, and a second empty copy appears. Nothing breaks today — the
   store keeps using the real one — but a later Themia migration would alter the copy nothing reads.
2. **Wrong schema on a fresh database.** `public` is not on the path. The migration creates
   `public.<table>`, boot succeeds, health checks pass, and the first runtime use fails with
   `42P01`. Green deploy, broken later — the late-failure signature of #0078 and #0085.

Mode 2 is the one that costs an outage, and *when* it surfaces is the whole problem. For
DataProtection the key ring is not read at boot — it is read when the first protector is created,
i.e. on a real user's first request needing antiforgery or an auth cookie. So today mode 2
presents as "deploy went green, users cannot sign in", with the fault dating from the deploy.

Mode 1 breaks nothing today.

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

Every rejected option below fails by asserting an answer Themia does not have. The assertion this
design does make — *the table resolves at all* — is the only one true in both populations, and it
is exactly the one that catches mode 2.

## Design

A startup probe, registered by each affected package's own DI extension. No changes to
`ThemiaMigrations`, no new option, no behaviour change for a deployment whose tables resolve.

### Placement: boot, via the existing advisory pattern

Themia already does startup-time checks this way —
`Themia.Scheduling/UnclusteredPersistenceAdvisory.cs:31` is
`internal sealed class X(ILogger<X>) : IHostedService`, registered from the package's own
extension (`SchedulingServiceCollectionExtensions.cs:147`). `ExplicitInstanceIdAdvisory` and
`Themia.Messaging.AspNetCore`'s `LoopGuardStartupWarnings` are the same shape.

The probe follows it, with one difference: those advise, this refuses. An exception from
`IHostedService.StartAsync` aborts host startup, before the server accepts a request. That is the
point of the whole design — mode 2 becomes "the container does not start" instead of "the first
user cannot sign in".

Because it is a hosted service, it runs exactly once per process by construction. No per-store
flags, no first-use hook, no cache-key question, nothing for a store to remember.

### New package: `Themia.Data.Probes`

Neutral, `net8.0;net10.0`. Dependencies: BCL (`System.Data`),
`Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`,
`Microsoft.Extensions.DependencyInjection.Abstractions`, and the repo-standard
`Microsoft.CodeAnalysis.PublicApiAnalyzers`.

A separate package rather than a home in `Themia.Data.Migrations`, because that one pulls
FluentMigrator, all three runners and all three database drivers. Four of the five affected
packages already pay that; `Themia.Messaging.PostgreSql` carries only `Npgsql` + `Dapper` today.

**No database driver dependency.** The caller supplies the connection, so the package stays
driver-free and each store keeps using the driver it already has.

```csharp
namespace Themia.Data.Probes;

public static class PostgresSchemaProbeServiceCollectionExtensions
{
    /// <param name="componentName">Names the component in messages, e.g. "Themia.Exceptional".</param>
    /// <param name="connectionFactory">Opens a short-lived connection for the probe.</param>
    /// <param name="tables">
    /// Identifiers exactly as the store's own SQL writes them — unqualified, quoting included:
    /// <c>data_protection_keys</c>, but <c>"Exceptions"</c>.
    /// </param>
    public static IServiceCollection AddPostgresSchemaProbe(
        this IServiceCollection services,
        string componentName,
        Func<IServiceProvider, IDbConnection> connectionFactory,
        params string[] tables);
}

public sealed class SchemaVisibilityException : Exception;
```

Registering it more than once is expected: each package registers its own probe with its own
tables, and each becomes a separate hosted service.

### What it asserts, and what it deliberately does not

**Assert: the identifier resolves.** Not that it resolves to `public`.

One round trip per table carries both signals:

```sql
SELECT
  (SELECT n.nspname
     FROM pg_class c
     JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.oid = to_regclass(@name))            AS resolved_schema,
  (to_regclass('public.' || @name) IS NOT NULL)  AS public_copy_exists
```

- `resolved_schema` NULL → **throw `SchemaVisibilityException`**, host does not start. Mode 2.
- `resolved_schema` is not `public` **and** `public_copy_exists` → **log a warning**, continue.
  Mode 1.
- otherwise → continue silently.

`to_regclass` returns NULL rather than throwing for an unresolvable name, and resolves names
exactly the way the store's own unqualified SQL does — which is what makes it the right probe
rather than a lookup in `information_schema`.

String-concatenating `'public.' || @name` is safe under the quoting rule above: `'public.' ||
'"Exceptions"'` gives `public."Exceptions"`, which `to_regclass` parses. Every `@name` is a
compile-time constant at the call site; no caller-supplied value reaches it.

### Connection failure is not a schema failure

If the probe cannot connect or the query fails for any reason other than a definitive answer, it
**logs a warning and skips** — it does not throw.

This matters and is easy to get backwards. For the `runMigration: false` population the probe is
the *first* database access at boot, so throwing on a connection error would newly make host
startup depend on database availability. That is a liveness gate nobody asked for, and it would
turn a transient database blip into a failed deploy. The probe exists to catch a **configuration**
fault; only a successful query that says "this table does not resolve" is evidence of one.

### Call sites — PostgreSQL only

| Package | Registered from | Identifier(s) |
| --- | --- | --- |
| `Themia.AspNetCore.DataProtection.PostgreSql` | `PersistKeysToThemiaPostgres` (`DataProtectionBuilderExtensions.cs:32`) | `data_protection_keys` |
| `Themia.Exceptional.PostgreSql` | `AddThemiaExceptionalPostgres` (`ServiceCollectionExtensions.cs:31`) | `"Exceptions"` |
| `Themia.Challenges.PostgreSql` | `AddThemiaChallengesPostgres` (`ServiceCollectionExtensions.cs:19`) | `challenges`, `challenge_rate_windows` |
| `Themia.Messaging.PostgreSql` | `AddThemiaMessagingPostgreSql` (`ServiceCollectionExtensions.cs:27`) | `messaging_outbox_messages`, `messaging_inbox_messages` |
| `Themia.Modules.Pdf` | `AddCommon`, reached from both `AddThemiaPdfModuleEfCore` and `AddThemiaPdfModuleDapper`; gated at run time by `appliesTo` on `IDatabaseProvider.ProviderName == DatabaseProviderNames.Postgres` | `pdf_templates` |

The first four are per-engine packages, so "PostgreSQL only" is structural. `Themia.Modules.Pdf`
is a single package serving all engines, but it resolves `IDatabaseProvider` from the container
rather than knowing the engine at registration time (`PdfModuleServiceCollectionExtensions.cs:32,38`),
so the gate is the `appliesTo` predicate, evaluated once at startup. It must be guarded on **both**
entry points — the module has an EF Core and a Dapper one.

MySQL binds schema to the connection's database and SQL Server defaults to `dbo`, which
FluentMigrator agrees with. Neither engine has the split.

## Error handling

Messages name the remedy, not just the fault, and never include the connection string.

`SchemaVisibilityException` carries the component name, the identifier as probed, and — from
`pg_class` — the schemas that *do* hold a table of that name. "Table not found" is not
diagnosable; "`Themia.Exceptional` expects `\"Exceptions\"`; it exists in `public`, which is not
on this connection's search_path" is.

The mode-1 warning names both schemas and states the consequence: a later Themia migration will
alter the copy the store does not read.

## Testing

Testcontainers against `postgres:16-alpine`, driving a real host so `StartAsync` is exercised —
asserting on the probe class directly would skip the behaviour that matters (that the host
refuses to start).

- table only in `app`, `search_path=app` → **host starts** (the `runMigration:false` population,
  working correctly — the false positive rev 1 would have produced)
- table only in `public`, `search_path=app` with `public` off the path → **host fails to start**
  with `SchemaVisibilityException` (mode 2)
- table in both `app` and `public`, `search_path=app` → host starts, **warning logged** (mode 1)
- default `search_path`, table in `public` → host starts, no warning
- `search_path` = `"$user", public` with a role-named schema present that holds no Themia table →
  **host starts** (rev 1 threw here; the deployment is correct)
- database unreachable at boot → host starts, warning logged, **no throw** (the connection-failure
  rule above; a test that skips this leaves the liveness-gate regression unguarded)
- `"Exceptions"` resolves through the quoted path; probing a quoted table unquoted folds to lower
  case and must not report a false negative
- MySQL and SQL Server registrations do not add a probe — including `Themia.Modules.Pdf` on both
  of its entry points

**Regression pin.** #0088's own reproduction — seed `app.data_protection_keys`, run
`ThemiaMigrations.Run` with `Search Path=app`, observe the table in **both** `app` and `public` —
lands as a test asserting the mode-1 warning. #0088 declined to commit it because it pinned
undecided behaviour; the behaviour is decided now.

## Options considered and rejected

Each fails the same way: it asserts a schema Themia is not entitled to choose.

**(a) Read `search_path` and pass it to FluentMigrator.** Silently relocates tables for anyone
living with a split. A data move dressed as a bug fix.

**(b) Fail fast in `ThemiaMigrations.Run` on `current_schema() != 'public'`.** rev 1's design.
`current_schema()` is where an unqualified *CREATE* would land; read resolution walks the whole
path in order. With the PostgreSQL default `"$user", public` and a role-named schema that exists
but holds no Themia table, `current_schema()` returns that schema while every store read still
falls through to `public` and works. The guard refuses a correct deployment. It is only
salvageable by having each assembly declare its table names to the runner — which is what this
design does, at the boundary that actually has them.

**(c) A schema option consumed by both halves.** Blocked in the source:
`Themia.Challenges/Migrations/ChallengeSchemaMigration.cs:21-22` — *"Unprefixed literal table
names on every engine, never `InSchema(...)`. FluentMigrator drops `InSchema(...)` on MySQL —
there, 'schema' and 'database' are the same concept"*. `Themia.Modules.Messaging` carries the same
note. Applying (c) to those two either breaks MySQL parity or makes the option PostgreSQL-only.

**Qualify the PostgreSQL stores' SQL to `public`.** Not one of #0088's options; raised in review
as the way to make both halves agree by construction, and rejected on a counter-example. It
removes a capability that works today: a consumer running their own migrations can place Themia
tables in their own schema, which is how schema-per-app and schema-per-tenant deployments in a
shared database work. Hardcoding `public` ends that permanently for every store, and restoring it
later is breaking. It would also fail `42P01` at runtime — not at boot — for any consumer on
`runMigration: false` with a non-default `search_path`, and that is not a hypothetical population:
#0085's recommended remedy created it.

**Probe asserting `to_regclass(t) = 'public.t'::regclass`.** The first correction proposed in
review, rejected for the same reason: it false-positives on exactly the `runMigration: false`
consumers the probe exists to serve.

**Probe on first store use (rev 2).** Correct assertion, no value. It fires on the same call that
would have thrown `42P01` anyway, so it buys a better message for the price of a package. If the
message were the only goal, wrapping `42P01` in the existing stores would be the honest way to
buy it.

## Known limitation

The mode-1 warning matches on table *name*, not ownership. A consumer with their own unrelated
`public.challenges` while Themia's lives in `app` gets a spurious warning. It is a log line, not a
failure, and distinguishing them would mean Themia recording which tables it created — which is
what `themia_version_<assembly>` does, but reading it here would make the probe depend on the
migration runner and undo the package split. Accepted; documented in the warning text.

Four migrations — not two — mix DDL styles that resolve schema differently on PostgreSQL, a shape
the inventory above missed and Task 5 found, then confirmed in three more assemblies on review.
`Themia.Challenges/Migrations/ChallengeSchemaMigration.cs` creates its tables with fluent
`Create.Table(...)`, which FluentMigrator forces to `public`, but creates four partial unique
indexes at lines 267-270 with raw `Execute.Sql($"CREATE UNIQUE INDEX ... ON {RateWindowsTable}
...")`, which follows `search_path`. `Themia.Modules.Messaging/Migrations/MessagingSchemaMigration.cs`
has the same shape — fluent `Create.Table(OutboxTable)` alongside `Execute.Sql($"CREATE INDEX ...
ON {table} ...")` at lines 106-110. `Themia.Modules.Pdf/Migrations/PdfTemplateSchemaMigration.cs`
has it too — fluent `Create.Table("pdf_templates")` alongside raw `Execute.Sql(...)` filtered unique
indexes at lines 81-82 (and the MySQL equivalent at lines 100-101).
`Themia.AspNetCore.DataProtection/Migrations/DataProtectionKeysCreatedAtDefaultMigration.cs` has
the inverse pairing of the same shape: it has no `Create.Table` of its own, but its raw
`Execute.Sql("ALTER TABLE data_protection_keys ...")` at lines 44-45 targets a table that an
earlier, already-shipped migration created with fluent `Create.Table` (forced to `public`) — so this
migration's unqualified `ALTER TABLE` follows `search_path` while the table it is altering does not.
Only `Themia.Exceptional` is clean: its migrations use no `Execute.Sql` at all. On a non-default
`search_path` all four affected migrations are internally inconsistent and throw during migration,
before the probe can run: the table (or, for the Data Protection case, the column default) lands in
`public` while the raw SQL statement targets whatever schema `search_path` resolves to, which does
not yet exist there. So for these four assemblies the probe only helps on a LATER boot; a first boot
on a non-default `search_path` dies inside the migration with a less specific error. The split is
inside one migration, not merely between migration and store — a shape coord #0088 does not
describe. The migrations are deliberately not changed here: they are shipped, and this plan is
about probes.

## Open question for the coord thread

Whether anyone runs Themia tables outside `public` today. The answer does not change this design —
resolvability is the right assertion either way — but it decides whether mode 1 deserves more than
a warning later, and it is cheaper to ask than to infer. ezy-assets measured themselves at the
default `search_path` on #0088; propertiezy has not been asked.
