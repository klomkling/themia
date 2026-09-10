# Themia.Data.Migrations engine split — implementation plan

Spec: `docs/superpowers/specs/2026-09-09-data-migrations-engine-split.md` (revision 4 — read §5.2, §5.3,
§5.4 and §6 before starting; three earlier revisions were reworked and §11 records why).

Branch: `fix/data-migrations-engine-split`, off `origin/main` (`ec0ae5e`).

---

## Global constraints

- **No `git commit` and no `git push` Mon-Fri 09:00-18:00.** `klomkling/themia` is public. Outside the
  window, one commit per task. No co-author or "generated with" trailers.
- `net8.0;net10.0` for every package here. `TreatWarningsAsErrors=true`,
  `GenerateDocumentationFile=true` — XML docs on every public member.
- PublicAPI analyzers: every public member into `PublicAPI.Unshipped.txt` with the EXACT modifiers from
  source. RS0016 and RS0017 fire together on one line when they disagree; `dotnet format analyzers` does
  not fix it.
- `System.Text.Json` only. `ILogger<T>` / `ILoggerFactory` only.
- Central package management — no `Version=` on a `PackageReference`; new packages go in
  `Directory.Packages.props`, and `.github/dependabot-nuget/` coverage is checked by `verify-coverage.sh`.
- `dotnet build Themia.sln --no-incremental` must report 0 Warning(s) 0 Error(s) at the end of every task.
- **Keep the `mariadb:11` advisory-lock test** — `CLAUDE.md` names it as the one legitimate MariaDB
  coverage.
- **Falsification is required per task.** Neutralise the production line a load-bearing test targets, run,
  confirm precisely that test reddens, restore, confirm green. `if (false)` trips CS0162 under
  `TreatWarningsAsErrors` — change a value instead.
- **Report false premises rather than bending production to fit a wrong test.** This plan's spec was
  reworked three times because premises were wrong; catching one is a success.

---

## Task 1: The seam, the registry, and three engine packages

**Files:** `src/neutral/Themia.Data.Migrations/{IMigrationEngineAdapter,MigrationEngineRegistry}.cs`,
refactor `ThemiaMigrations.cs` + `MigrationLock.cs`; new
`src/neutral/Themia.Data.Migrations.{PostgreSql,MySql,SqlServer}/`.

This task is one unit on purpose: the core stops compiling the moment the runners leave, so the adapters
must land with it.

- [ ] **Step 1** — add `IMigrationEngineAdapter` exactly as spec §4 (note: the lock scope is a `string`,
      not a dedicated type — `MigrationLock.cs:129,148,172`).
- [ ] **Step 2** — extract each engine's behaviour into its package, from the three coupling sites in
      spec §3: the runner call (`ThemiaMigrations.cs:256-258`), the unpooled connection
      (`MigrationLock.cs:311-322`), and the advisory-lock SQL (`MigrationLock.cs:174-230, 265-272`).
      **Move the SQL verbatim.** A rewritten `sp_getapplock` or `pg_advisory_lock` call is a new defect in
      the one component whose failure mode is two hosts migrating at once.
- [ ] **Step 3** — `MigrationEngineRegistry`: idempotent, thread-safe, resettable for tests.
      `ThemiaMigrations.Run(MigrationEngine, …)` resolves through it and, when the engine is absent,
      throws naming the package: `"MigrationEngine.Postgres was requested but no adapter is registered.
      Add a reference to Themia.Data.Migrations.PostgreSql and call AddThemiaDataMigrationsPostgreSql()."`
      Add a `Run(IMigrationEngineAdapter, …)` overload.
- [ ] **Step 4** — core csproj drops `FluentMigrator.Runner.{Postgres,MySql,SqlServer}`, `Npgsql`,
      `MySqlConnector`, `Microsoft.Data.SqlClient`; keeps `FluentMigrator` + `FluentMigrator.Runner`.
- [ ] **Step 5** — the existing integration tests register the adapter they need in their fixtures. Run
      them per engine; **the advisory-lock tests must pass unchanged in intent**, including MariaDB.
- [ ] **Falsify** — swap two engines' lock SQL in the adapters and confirm the matching engine's lock test
      reddens. A lock test that passes with the wrong engine's SQL is not testing the lock.

## Task 2: The `runMigration` handshake (Audit)

Spec §5.2. This is the task that exists because a naive move would run DDL an adopter disabled.

- [ ] **Step 1: write the four failing tests first** — {core-first, engine-first} x {`runMigration` true,
      false}. `runMigration: false` must produce **no DDL in either order**; `true` must migrate exactly
      once in either order.
- [ ] **Step 2** — `AddThemiaAudit` stops calling `ThemiaMigrations.Run` and records intent
      (`runMigration`, connection string, options) on the `IServiceCollection`;
      `AddThemiaAudit{Engine}()` records its adapter; whichever completes the pair migrates, once.
- [ ] **Step 3** — the shipped README order (`Themia.Audit/README.md:27-32`) still migrates. Assert it as
      a test, not as a comment.
- [ ] **Falsify** — make the handshake fire on the intent alone (ignoring the adapter half) and confirm
      the engine-first test reddens; make it ignore `runMigration` and confirm both `false` tests redden.

## Task 3: The three other no-change families

`Themia.Exceptional`, `Themia.Challenges`, `Themia.AspNetCore.DataProtection` — each has an
engine-specific entry point that knows its engine at compile time and already owns its `runMigration`
flag (`PersistKeysToThemiaPostgres(…, runMigration, migrationOptions)`), so they pass the adapter
directly with no registry and no handshake.

- [ ] Per family, per engine: pass the adapter, and add a test that the engine-specific call migrates.
- [ ] `Themia.Challenges.{Engine}` already calls `Run(MigrationEngine.Postgres, …)` with a literal — the
      change is mechanical.
- [ ] **Do not rename anything.** `AddThemiaExceptionalPostgres` and `AddThemiaChallengesPostgres` carry
      no `Sql` while `AddThemiaAuditPostgreSql` does. Fixing that here would be a second breaking change
      riding on a packaging fix.

## Task 4: The registry group

Spec §6. `Themia.Scheduling`, all seven `Themia.Modules.*` **including Messaging** (§5.4), and the
custom-dialect `AddThemiaExceptionalProvider` path (§5.3).

- [ ] `AddThemiaDataMigrations{PostgreSql,MySql,SqlServer}()` in each engine package.
- [ ] **`Themia.Messaging.*` gets no `Themia.Data.Migrations.{Engine}` reference** — it never had one.
- [ ] Test per engine: `Run` with nothing registered throws naming that package.
- [ ] Test that module migration is order-free (they migrate in `InitializeAsync`,
      `StorageModule.cs:40-52`) and that the custom-dialect Exceptional path is **not** — it migrates
      during registration, so registration must come first.

## Task 5: Build-time enforcement

Spec §7. `buildTransitive/`, **not** `build/` — an adopter references `Themia.Audit`, not the core, and
NuGet imports build assets from direct references only. `OutputType == 'Exe'` (verified: both
`Themia.Audit` and `Themia.Audit.Tests` report `Library`, so libraries and the test suite are unaffected).

- [ ] Core ships `buildTransitive/Themia.Data.Migrations.targets` erroring `THEMIA2001`; each engine
      package ships `buildTransitive/<PackageId>.props` setting `ThemiaMigrationEngineSelected`.
- [ ] **Pack-and-restore test.** Pack to a local feed; restore two throwaway `Exe` projects — one without
      an engine (expect THEMIA2001), one with (expect success). A unit test cannot see this; without the
      test, `build/` vs `buildTransitive/` can be wrong and nothing says so.

## Task 6: Guard, docs, and the coord replies

- [ ] **Packaging assertion**: `Themia.Data.Migrations`'s resolved dependency set contains no `Npgsql`,
      `MySqlConnector` or `Microsoft.Data.SqlClient`. This is what stops the defect returning the next
      time someone adds a convenient `ProjectReference` — which is how it arrived.
- [ ] `MIGRATION.md`: the one new line for the registry group, and that four families need no source
      change. `CHANGELOG.md`: breaking, and **do not claim the JWT stack is gone everywhere** — the
      SQL Server package keeps it because `FluentMigrator.Runner.SqlServer` declares
      `Microsoft.Data.SqlClient` itself.
- [ ] `Themia.Audit/README.md`: state that `IAuditStore.QueryAsync` lives in the core and
      `AuditQuery.TenantId = null` means *no filter*, with `HostLevelOnly` for single-org. Two consumers
      read the current text and both concluded the read surface was tenant-locked (#0117).
- [ ] Answer #0116 and #0117: the JWT stack comes from `Microsoft.Data.SqlClient`, not from us; the
      reach is wider than Audit — they already ship `Themia.Exceptional` and
      `Themia.AspNetCore.DataProtection` carrying the identical fifteen.
- [ ] Version: this is a breaking packaging change and `0.24.0` is already claimed by the Geo/AI release
      on another branch. **Do not set a version in this branch** — pick it at merge time so the two
      branches cannot both claim one.
