# Themia.Data.Migrations engine split

**Status:** design, revision 4 (revisions 1-3 were reworked — §11 records what was wrong and why)
**Raised by:** coord #0116 (ezy-assets) and #0117 (propertiezy), filed seven minutes apart, independently.
**Precedent:** #0058 (Identity split), #0071 (Scheduling split). Third instance of the same shape.

**Governing constraint, and the reason for revision 3:** an adopter who followed the *published*
documentation must not break on upgrade. #0085 is what that failure looks like here — a Themia migration
change crash-looped propertiezy's production API at boot, and Coolify had already removed the last good
container. Any design whose upgrade path is "and now edit your `Program.cs`" repeats it.

---

## 1. The report, and what is true in it

Both consumers measured `Themia.Audit` 0.23.1 at **fifteen** nuspec dependencies and declined to adopt.
Propertiezy benchmarked against Themia's own cores at the same version:

| package | deps |
| --- | --- |
| `Themia.Storage` | 0 |
| `Themia.Imaging` | 4 |
| `Themia.Audit` | 15 |

Verified against source, not taken on report:

- `Themia.Audit.csproj` declares four `PackageReference`s and one `ProjectReference` to
  `Themia.Data.Migrations`, which declares **three FluentMigrator runners and three ADO drivers**.
- Propertiezy's hypothesis — "a ProjectReference to the framework flattening into the nuspec" — is
  correct, and their control group is exact: `Themia.Storage` and `Themia.Imaging` are the two cores
  that do **not** reference `Themia.Data.Migrations`.

### The JWT stack is not ours, and only SQL Server keeps it

Nothing in `Themia.Audit` reads a token. `Microsoft.Data.SqlClient`'s nuspec declares
`Microsoft.IdentityModel.JsonWebTokens`, `Microsoft.IdentityModel.Protocols.OpenIdConnect`,
`System.IdentityModel.Tokens.Jwt`, `Azure.Core` and `Azure.Identity`. Propertiezy's proposed
`IAuditActorAccessor` seam is unnecessary — there is no actor resolution to extract.

Measured from the runner nuspecs: **`FluentMigrator.Runner.SqlServer` declares
`Microsoft.Data.SqlClient` itself**, while `Runner.Postgres` and `Runner.MySql` declare no driver (the
core takes `Npgsql` and `MySqlConnector` directly, for `MigrationLock`). After the split
`Themia.Data.Migrations.SqlServer` **still carries the JWT stack, unavoidably** — correct, since only
SQL Server adopters pay for it, but the changelog must not claim the stack is gone everywhere.

### It is not an Audit defect

Twenty packages `ProjectReference` `Themia.Data.Migrations` on `main` and carry the same fifteen:

```
neutral/   Themia.Audit  Themia.Exceptional  Themia.Scheduling
           Themia.AspNetCore.DataProtection(.MySql/.PostgreSql/.SqlServer)
           Themia.Challenges.{MySql,PostgreSql,SqlServer}
           Themia.Exceptional.{MySql,PostgreSql,SqlServer}
modules/   Export  Identity.Dapper  Identity.EFCore  Messaging  Notifications  Pdf  Storage
```

`Themia.Exceptional` has shipped this since 0.8.x and **both consumers already deploy it**, as they do
`Themia.AspNetCore.DataProtection`. Propertiezy's stated position — no SQL Server driver in a
Postgres-only marketplace — is already violated in their production image by two packages they took for
other reasons. That widens the report rather than weakening it, and both should be told.

---

## 2. Why the existing dialect packages do not fix it

Both checked `Themia.Audit.PostgreSql` expecting an escape hatch and found it depends on `Themia.Audit`,
carrying everything transitively. Correct. **"The dialect split is cosmetic while the core holds all
three runners"** (#0116) is the whole finding.

---

## 3. What is actually coupled

Three compile-time sites in the core, not a set of `PackageReference` lines:

- `ThemiaMigrations.cs:256-258` — a switch calling `rb.AddPostgres()`, `rb.AddMySql8()`,
  `rb.AddSqlServer()`, each from a different runner package.
- `MigrationLock.cs:311-322` — constructs `NpgsqlConnection`, `MySqlConnection`, `SqlConnection`
  directly, each with pooling disabled through that driver's own connection-string builder.
- `MigrationLock.cs:174-230, 265-272` — engine-specific advisory-lock SQL: `pg_advisory_lock` /
  `pg_advisory_unlock`, `GET_LOCK` / `RELEASE_LOCK`, `sp_getapplock` / `sp_releaseapplock`.

The lock scope is a **`string`** (`MigrationLock.cs:129,148,172`), not a dedicated type.

---

## 4. The seam

```csharp
namespace Themia.Data.Migrations;

/// One engine's migration behaviour. Implemented by Themia.Data.Migrations.{PostgreSql,MySql,SqlServer}.
public interface IMigrationEngineAdapter
{
    MigrationEngine Engine { get; }
    string DisplayName { get; }                                   // "PostgreSQL"
    void ConfigureRunner(IMigrationRunnerBuilder builder);        // rb.AddPostgres()
    DbConnection CreateUnpooledConnection(string connectionString);
    bool TryAcquireLock(DbConnection connection, string scope, TimeSpan timeout);
    void ReleaseLock(DbConnection connection, string scope);
}
```

That is the complete engine-specific surface, taken from the three sites in §3 and nothing else.

`ThemiaMigrations.Run` gains an overload taking `IMigrationEngineAdapter`. The existing
`Run(MigrationEngine, …)` overload stays and resolves through the registry (§6). Every one of its
thirteen call sites is inside a Themia package — no adopter calls it — so this is an internal seam that
keeps its public modifier for compatibility, not for adopters.

---

## 5. Adapter delivery: the engine-specific entry point the adopter already calls

**Four families already require the adopter to call an engine-specific method** that knows its engine at
compile time. For those, supplying the adapter there is a package-reference change and nothing else. The
rest go through the registry (§6). Revision 3 claimed five families; `Themia.Messaging` was counted
wrongly — see §5.4.

### 5.1 Why the dialect call is already mandatory (verified)

`IAuditDialect` and `IAuditStore` are registered **only** in `Themia.Audit.{PostgreSql,MySql,SqlServer}`
(`ServiceCollectionExtensions.cs:18-19` in each), and `AddThemiaAudit` builds an `IAuditRecorder` that
resolves both from DI. An adopter whose audit works today therefore already calls
`AddThemiaAudit{Engine}()`. The same holds for the other four families.

### 5.2 Move the eager migration to the engine package

`AddThemiaAudit` runs the migration eagerly at `AuditServiceCollectionExtensions.cs:95`, and the shipped
README (`Themia.Audit/README.md:27-32`) documents this order:

```csharp
services.AddThemiaAudit(o => { o.Engine = AuditEngine.Postgres; … });   // migration runs here today
services.AddThemiaAuditPostgreSql();
```

Registering the adapter in the second call while the migration runs in the first would throw on the
**published example**, with both packages referenced and every call present.

**Naively moving the migration into the engine method is worse than the throw.** The `runMigration` flag
lives on `AddThemiaAudit`, so this adopter — who deliberately disabled it — would get DDL anyway, with no
error to notice:

```csharp
services.AddThemiaAudit(o => { … }, runMigration: false);   // "do not migrate"
services.AddThemiaAuditPostgreSql();                        // …migrates
```

That flag is a documented contract (`Themia.Audit/README.md:36`, "Pass `runMigration: false` to defer
it") with real users — `Themia.Modules.Audit.IntegrationTests:124` among them, because the module layer
owns migration in that configuration. Silent unrequested DDL on a production database is the #0085 shape
again, so the design has to be stated here rather than settled during implementation.

**The design: an order-free handshake through the service collection.** Neither call migrates on its own.
Each records its half and then checks whether both halves are present; whichever runs second performs the
migration:

- `AddThemiaAudit(configure, runMigration)` records the *intent* — `runMigration`, the probed
  `ConnectionString`, and the migration options — and no longer calls `ThemiaMigrations.Run` itself.
- `AddThemiaAudit{Engine}()` records its `IMigrationEngineAdapter`.
- Whichever call completes the pair runs the migration, once, iff `runMigration` was `true`.

This keeps every existing signature, honours `runMigration: false` exactly, works in either call order
(so the README example and its reverse both migrate), and needs no registry for this family. Precedent
for the flag living beside the engine already exists: `PersistKeysToThemiaPostgres(…, runMigration: true,
migrationOptions)` takes it on the engine-specific method today.

The handshake state is per-`IServiceCollection` and consumed at registration time, which is
single-threaded by convention. **A test must cover both orders and both flag values** — four cases, and
the reverse-order one is the case no existing test exercises.

**Two rules the handshake needs beyond those four cases**, both because splitting one eager call into two
recorded halves creates states the eager version could not reach:

- **A recorded `runMigration: true` is never downgraded.** Recording the intent last-wins would let a
  later `AddThemiaAudit(cfg, runMigration: false)` cancel a migration an earlier call asked for — which
  the pre-handshake code could not do, since the first call had already migrated. `runMigration`
  accumulates with OR, and a completed pair is marked so a redundant later call cannot start a second
  attempt.
- **An intent that never meets an adapter must not be silent.** An adopter using the neutral core with
  their own `IAuditDialect`/`IAuditStore` calls `AddThemiaAudit` and no `AddThemiaAudit{Engine}`; they
  used to get the table and would now get nothing, no throw and no log — the #0085 shape again, surfacing
  as `relation "audit_log" does not exist` at the first write. `AddThemiaAudit` therefore registers an
  `AuditOptions` validation that fails at startup when an intent with `runMigration: true` was never
  paired, naming the call to add. §5.3's "none" for `Themia.Audit` holds for adopters who use an engine
  package; this adopter costs one new line like the modules do.

### 5.3 Per family

| family | engine-specific entry point | adopter change |
| --- | --- | --- |
| `Themia.Audit` | `AddThemiaAudit{PostgreSql,MySql,SqlServer}` | none |
| `Themia.Exceptional` | `AddThemiaExceptional{Postgres,MySql,SqlServer}` | none — **but see below** |
| `Themia.Challenges` | `AddThemiaChallenges{Postgres,MySql,SqlServer}` | none |
| `Themia.AspNetCore.DataProtection` | `PersistKeysToThemia{Postgres,MySql,SqlServer}` | none |
| `Themia.Scheduling`, `Themia.Modules.Messaging` + five other modules | *none exists* | **one new line** (§6) |

`Themia.Exceptional` is the easiest *through its engine package*: `AddThemiaExceptionalPostgres`
delegates to `AddThemiaExceptionalProvider` with a compile-time literal
(`Themia.Exceptional.PostgreSql/ServiceCollectionExtensions.cs:39-41`).

**But `AddThemiaExceptionalProvider` is public and takes `IExceptionalSqlDialect` as a parameter**, so an
adopter with their own dialect implementation calls it with `MigrationEngine.Postgres` and never
references `Themia.Exceptional.PostgreSql` at all. That adopter has no engine-specific call to carry an
adapter and falls into the registry group (§6). The table's "none" holds only for adopters who go through
the engine package; the custom-dialect path costs one new line like the modules do.

### 5.4 `Themia.Messaging` is not in this group

Revision 3 listed it as a family needing no change. Verified false:
`Themia.Messaging.{PostgreSql,MySql,SqlServer}` **do not reference `Themia.Data.Migrations` at all** and
contain no `ThemiaMigrations.Run`. Messaging's migration lives in
`Themia.Modules.Messaging/MessagingModule.cs:48` with a runtime enum, so it belongs with the modules in
§6, and §8 must not add a `Themia.Data.Migrations.{Engine}` reference to a package that never had one.

**Names are inconsistent in the shipped API** — `AddThemiaExceptionalPostgres` and
`AddThemiaChallengesPostgres` have no `Sql`, while `AddThemiaAuditPostgreSql` and
`AddThemiaMessagingPostgreSql` do. Do not "fix" that here; it would be a second breaking change riding
on a packaging fix. The new API in §6 follows the `PostgreSql` majority.

---

## 6. The registry, now scoped to one case

Three callers take a `MigrationEngine` value with **no engine package** to hang an adapter on — fifteen
public signatures (`SchedulingSchema.Migrate` plus seven module constructors and their overloads):

- `Themia.Scheduling`
- the seven `Themia.Modules.*`, **including `Themia.Modules.Messaging`** (§5.4)
- any adopter using `AddThemiaExceptionalProvider` with their own dialect (§5.3)

Those keep the enum and resolve through a registry:

```csharp
// Themia.Data.Migrations.PostgreSql
public static class PostgresMigrationEngine
{
    public static IMigrationEngineAdapter Adapter { get; } = new PostgresMigrationEngineAdapter();
    public static IServiceCollection AddThemiaDataMigrationsPostgreSql(this IServiceCollection services)
    { MigrationEngineRegistry.Add(Adapter); return services; }
}
```

**Registration is an explicit call, never a `[ModuleInitializer]`.** .NET loads an assembly when one of
its types is used; a merely-referenced assembly may never load, so an initializer may never run and the
adapter would be missing on a correct configuration. This is the same rule that made the Geo and AI
layering tests unfalsifiable with a bare `ProjectReference`.

**Ordering is not a hazard for the modules, and that is verified rather than assumed:** they migrate in
`InitializeAsync` (`StorageModule.cs:40-52`), which runs after the container is built, so
`AddThemiaDataMigrations{Engine}()` may appear anywhere in the adopter's startup. The custom-dialect
Exceptional path is the exception — `AddThemiaExceptionalProvider` migrates during registration, so there
`AddThemiaDataMigrations{Engine}()` must come first, and `MIGRATION.md` has to say so.

The registry is idempotent, thread-safe, and resettable for tests.

---

## 7. Build-time enforcement

The runtime error is a backstop, not the primary guard — #0085 is the precedent for discovering a
migration problem by production boot loop.

`Themia.Data.Migrations` ships `buildTransitive/Themia.Data.Migrations.targets`:

```xml
<Target Name="ThemiaCheckMigrationEngine" BeforeTargets="Build"
        Condition="'$(OutputType)' == 'Exe' AND '$(ThemiaMigrationEngineSelected)' != 'true'">
  <Error Code="THEMIA2001"
         Text="Themia.Data.Migrations requires an engine package. Add a PackageReference to one of
               Themia.Data.Migrations.PostgreSql, .MySql or .SqlServer." />
</Target>
```

Each engine package ships `buildTransitive/<PackageId>.props` setting
`<ThemiaMigrationEngineSelected>true</ThemiaMigrationEngineSelected>`.

**Diagnostic id.** `CLAUDE.md` reserves `THEMIA1xx` for Roslyn analyzer diagnostics; this is an MSBuild
error, not an analyzer. `THEMIA2xxx` is reserved here for build/MSBuild errors so the two ranges cannot
collide. No `THEMIA` id is in use in `src/tooling` yet, so this is a decision to record, not a conflict
to resolve.

Two details decide whether this works at all:

- **`buildTransitive/`, not `build/`.** An adopter references `Themia.Audit`, not
  `Themia.Data.Migrations`. NuGet imports build assets only from direct `PackageReference`s;
  `buildTransitive` exists for exactly this case. In `build/` the check would silently never run.
- **`OutputType == 'Exe'`.** Twenty Themia libraries reference the core and must stay engine-agnostic.
  Verified with `dotnet msbuild -getProperty:OutputType`: both `Themia.Audit` and `Themia.Audit.Tests`
  report `Library`, so neither the libraries nor the test suite trips the check — only the application
  that actually runs migrations.

**What it cannot catch:** configuration selects Postgres while only the SQL Server package is
referenced. Configuration is unknown at build time, so that stays with the runtime error, which names
the missing package. Both guards are needed.

---

## 8. Package layout

```
Themia.Data.Migrations              FluentMigrator, FluentMigrator.Runner,
                                    Microsoft.Extensions.{DependencyInjection,Logging.Abstractions}
                                    — no runner, no ADO driver, no JWT
Themia.Data.Migrations.PostgreSql   + FluentMigrator.Runner.Postgres, Npgsql
Themia.Data.Migrations.MySql        + FluentMigrator.Runner.MySql, MySqlConnector
Themia.Data.Migrations.SqlServer    + FluentMigrator.Runner.SqlServer (brings Microsoft.Data.SqlClient
                                      and the JWT stack with it)
```

The twelve engine packages that reference `Themia.Data.Migrations` today — `Themia.Audit.*`,
`Themia.Exceptional.*`, `Themia.Challenges.*`, `Themia.AspNetCore.DataProtection.*` — each gain a
`ProjectReference` to the matching `Themia.Data.Migrations.{Engine}`. **`Themia.Messaging.*` is not among
them** and is left alone (§5.4).

**A Postgres adopter then carries:** `FluentMigrator`, `FluentMigrator.Runner`,
`FluentMigrator.Runner.Postgres`, `Npgsql`, `Dapper`, two `Microsoft.Extensions.*`. No
`Microsoft.Data.SqlClient`, no `MySqlConnector`, no JWT — the outcome #0117 asked for.

---

## 9. Upgrade impact, stated plainly

- **Four families — Audit, Exceptional (via its engine package), Challenges, DataProtection: package
  references only.** The new transitive dependency arrives automatically; `Program.cs` is untouched, the
  documented call order still works, and `runMigration: false` still means no migration (§5.2).
- **`Themia.Scheduling`, all seven modules including Messaging, and the custom-dialect Exceptional path:
  one new line,** `AddThemiaDataMigrations{Engine}()`. Position-independent everywhere except the
  custom-dialect Exceptional path, which migrates during registration. This is the only source change and
  it belongs in `MIGRATION.md`, not just the changelog.
- An adopter who upgrades and adds nothing gets `THEMIA2001` at build if their app is an `Exe`, and
  otherwise a runtime error naming the package to add.

---

## 10. Testing

- **Keep the `mariadb:11` advisory-lock test** in `Themia.Data.Migrations.IntegrationTests` — `CLAUDE.md`
  names it as the one legitimate MariaDB coverage.
- The advisory-lock behaviour moves into three adapters. Each engine's existing lock test runs against
  its own adapter, unchanged in intent, so a regression in one engine cannot hide behind another.
- **The `runMigration` handshake, four cases** (§5.2): core-first and engine-first x `runMigration`
  true and false. The reverse-order case has no existing test, and the `false` cases are the ones where a
  regression runs DDL nobody asked for.
- **Per family: the shipped README's call order still migrates.** `Themia.Audit/README.md:27-32` is the
  regression this revision exists to prevent; it belongs in a test, not in a reviewer's memory.
- **Per engine package: a test that the engine-specific entry point supplies the adapter.** Fifteen call
  sites rely on it; without these tests one deleted line reintroduces a boot failure silently.
- **Per engine: `Run(MigrationEngine, …)` with no adapter registered throws naming that package.**
- **A pack-and-restore test for the build asset.** The `buildTransitive` check exists only in a packed
  `.nupkg` consumed by another project and cannot be verified by a unit test. Pack the core and one
  engine package to a local feed; restore two throwaway `Exe` projects, one without an engine (expect
  THEMIA2001) and one with (expect success). Without it, `build/` vs `buildTransitive/` can be wrong and
  nothing would say so.
- **A packaging assertion that `Themia.Data.Migrations`'s resolved dependency set contains no `Npgsql`,
  `MySqlConnector` or `Microsoft.Data.SqlClient`** — otherwise the defect returns the next time someone
  adds a convenient `ProjectReference`, which is how it arrived.

---

## 11. What changed across revisions

Revision 1 → 2:

1. **`[ModuleInitializer]` registration would not reliably run.** Referenced-but-unused assemblies are
   not loaded. Replaced with an explicit call.
2. **The stated reason for the registry was false** — no adopter calls `ThemiaMigrations.Run`; the real
   reason is a runtime, config-bound engine.
3. "Adopters supply the engine package" was not enough — it must be registered.
4. `Themia.Modules.Messaging` does have an engine trio.
5. The SQL Server package keeps the JWT stack unavoidably.
6. The lock scope is a `string`, not a `MigrationLockScope` type.
7. Build-time enforcement added.

Revision 3 → 4:

12. **Moving the migration into the engine method would have run DDL an adopter disabled.** `runMigration`
    lives on `AddThemiaAudit`; the naive move honours neither the flag nor the reverse call order.
    Replaced with an order-free handshake (§5.2). Revision 3 called this "one API detail to design
    carefully" — it was silent unrequested DDL on a production database.
13. **`Themia.Messaging` was in the wrong group.** Its engine packages do not reference
    `Themia.Data.Migrations` and run no migration; messaging migrates from `Themia.Modules.Messaging`
    with a runtime enum. Moved to the registry group, and §8 no longer adds a reference it never had.
14. **`AddThemiaExceptionalProvider` is public and takes a dialect**, so a custom-dialect adopter never
    references an engine package. "No adopter change" for Exceptional holds only through its engine
    package; the custom-dialect path joins the registry group.
15. Diagnostic id moved out of the analyzer range: `THEMIA2001`, with `THEMIA2xxx` reserved for MSBuild.

Revision 2 → 3:

8. **The published README's call order would have thrown** (`Themia.Audit/README.md:27-32`): the
   migration runs eagerly in `AddThemiaAudit`, before the dialect call that would have registered the
   adapter. Found by reading the README, which revision 2 had not done.
9. **The registry was applied to everything; it is needed for one case.** Five of six families already
   require an engine-specific call that knows its engine at compile time, so they take the adapter
   directly — verified by tracing who registers `IAuditDialect` (only the three engine packages).
10. **Ordering is a non-issue for modules** — they migrate in `InitializeAsync`, after the container is
    built (`StorageModule.cs:40-52`). Revision 2 neither knew nor claimed this.
11. Three API names were wrong: `AddThemiaExceptionalPostgres` and `AddThemiaChallengesPostgres` carry no
    `Sql`, and DataProtection uses `PersistKeysToThemia{Postgres,MySql,SqlServer}` on
    `IDataProtectionBuilder`, not `AddThemia*` on `IServiceCollection`.

---

## 12. Answering the two coord requests

- **#0116 ("why does the core need JWT?")** — it does not; `Microsoft.Data.SqlClient` brings it, and only
  the SQL Server package keeps it after the split.
- **#0117 blocker 2 (tenant-scoped read surface)** — ezy-assets already answered correctly on the
  thread: `IAuditStore.QueryAsync` lives in `Themia.Audit`, not the module, and `AuditQuery.TenantId =
  null` means *no filter*, with `HostLevelOnly` for the single-org case. No code change; the README is
  the fix, since two consumers read the same description and reached the same wrong conclusion.
- **Both should be told the reach is wider than Audit** — they already ship `Themia.Exceptional` and
  `Themia.AspNetCore.DataProtection`, which carry the identical fifteen today.
