# Themia.Audit — design

**Status:** approved, not yet implemented
**Target version:** `0.23.0` (release 1). Release 2 is scoped here but specced separately.
**Supersedes:** the `Themia.Modules.Audit` row in `docs/themia-architecture-overview.md` §B
("ezy-assets `AuditLogRepository` + Zenity audit"), which describes a different artifact than
the one this spec builds — see *Why the listed sources do not fit* below.

---

## 1. The problem

Three seams ship today with **zero implementations and zero callers**. Each was built expecting an
audit consumer that was never written.

| seam | package | shipped | consumers |
| --- | --- | --- | --- |
| `IAuditLogService` + `AuditEvent` | `Themia.Framework.Services` | `0.2.0` | none |
| `IAuthenticationHooks` (6 methods, doc'd *"for audit"*) | `Themia.Modules.Identity.Abstractions` | `0.5.1` | none |
| `IUserLifecycleHooks.OnUserMutatedAsync` | `Themia.Modules.Identity.Abstractions` | `0.20.0` | none |

`LoginFailureReason`'s own doc comment states the intent outright:

> The real internal reason a login failed, supplied to `IAuthenticationHooks` **for audit**. Never
> surfaced to the client (which sees a uniform 401).

A dead seam is worse than a missing one: an adopter injects `IAuditLogService`, gets a DI resolution
failure at runtime, and learns the framework promised something it does not deliver. This is the same
failure class as coord #0057 (`NotificationMessage.Metadata` — a property no sender ever read).

### What exists instead

**Audit stamping** — `AuditableEntity` (`CreatedAt`/`CreatedBy`/`LastModifiedAt`/`LastModifiedBy`) and
`SoftDeletableEntity` (`DeletedAt`/`DeletedBy`/`RestoredAt`/`RestoredBy`). Both EF (`ThemiaDbContext`)
and Dapper (`DapperUnitOfWork`) stamp these automatically. **Overwritten on every write** — it answers
"who touched this last", never "what changed, when, by whom".

**Login failure counting** — `User.AccessFailedCount` + `LockoutEnd`, reset to `0` both on a successful
login *and* at the moment lockout triggers (`UserService.cs:371-388`). It is a counter that forgets;
it cannot answer "how many failures did this account see last month, from which addresses".

**`ILogger`** — `AuthenticationFlow` logs login failures at Warning. Not queryable, not tenant-scoped,
and subject to whatever retention the host's log sink happens to have.

### Why the listed sources do not fit

`docs/themia-architecture-overview.md` lists ezy-assets' `AuditLogRepository` as the source to port.
Read directly, it is **not** an entity change log:

- The `AuditLogs` table has `OldValue`/`NewValue` `jsonb` columns, so it reads like a change log.
- All **46** call sites are hand-written. `Action` is a business verb (`PROPOSAL_INTEREST_SUBMITTED`,
  `DEAL_COMMISSION_SPLIT_CAPTURED`), never a CRUD verb.
- `OldValue` — set at roughly half the sites — holds a hand-picked field subset
  (`new { status = oldStatus }`), never a computed row diff.

It is an **activity log with two columns named old/new**. `AuditEvent` in `Themia.Framework.Services`
has no old/new fields at all, so it is unambiguously an activity log too.

The **entity change log** — the thing most people mean by "audit" — exists in neither source. It is
built here, in release 2, from scratch.

---

## 2. Scope

Three legs, split across two releases.

| leg | what | who writes | release |
| --- | --- | --- | --- |
| **3. Activity log** | business events the app chooses to record | the app calls in | `0.23.0` |
| **4. Auth/security events** | login success/failure/logout/refresh/lockout, user credential mutations | the module, via Identity observers | `0.23.0` |
| **2. Entity change log** | per-row `CREATE`/`UPDATE`/`DELETE`/`SOFT_DELETE` with field-level old→new | the data layer, automatically | `0.24.0` |

### Why the split falls here

Legs 3 and 4 share **one record shape** — "who did what, when, with what outcome". They differ only in
who calls: the app, or the module observing Identity. One table, one write path.

Leg 2 needs a **second shape**: one event owning N field-change rows. It is also the only leg that hits
the EF/Dapper parity fork (§13), because EF's `ChangeTracker` knows the original values and Dapper has
no change tracker at all. Isolating it means release 1 settles nothing it would have to revisit.

Release 1 makes all three dead seams live and gives an adopter login auditing for one `AddThemiaAudit()`
call, with no code changes on their side.

### Non-goals

- **No dashboard/UI.** `Themia.Exceptional` earned its dashboard from an existing SilkierQuartz port;
  audit has no such source and no consumer asking. Query the table.
- **No retention job in `0.23.0`.** The default is keep-forever, so a scheduled job would do nothing by
  default while adding a Quartz dependency and a tenant-scoping surface. The core ships
  `PurgeAsync(olderThan, …)`; a Quartz wrapper lands when an adopter actually configures retention.
- **No Serenity adapter.** Per `CLAUDE.md`, built only if PowerACC actually migrates.
- **No log-shipping / SIEM export.** Adopters read the table.

---

## 3. Package shape

Follows `Themia.Exceptional`, audit's nearest sibling — an append-only store with one schema across
three engines behind a dialect strategy.

```
Themia.Audit                net8.0;net10.0   record model, IAuditStore, Dapper store engine,
                                             IAuditDialect, redaction, schema migration
Themia.Audit.PostgreSql     net8.0;net10.0   dialect
Themia.Audit.SqlServer      net8.0;net10.0   dialect
Themia.Audit.MySql          net8.0;net10.0   dialect
Themia.Modules.Audit        net10.0          IThemiaModule, tenant resolution, IAuditLogService impl,
                                             Identity observer, ambient-transaction enlistment
```

The neutral core carries the heaviest part — three-engine SQL — and can be tested against all three
containers without EF, the framework, or Identity in the graph. The net8 leg is mandatory per the
target-framework policy in `CLAUDE.md`.

**`Themia.Audit` must not reference any `Themia.Framework.*` package.** Enforced by a build assertion
in the test suite, not by convention alone.

---

## 4. Record model

`Themia.Audit.AuditEntry` — one immutable record, both legs.

| field | type | notes |
| --- | --- | --- |
| `Id` | `Guid` | client-generated, so the caller holds it before commit |
| `TenantId` | `string?` | `null` = host-level (§8) |
| `Category` | `AuditCategory` | `Unspecified = 0`, `Activity`, `Authentication`, `UserLifecycle`. Release 2 adds `EntityChange`. |
| `EventType` | `string` | `PROPOSAL_ACCEPTED`, `LOGIN_FAILED`, `PASSWORD_CHANGED` |
| `Outcome` | `AuditOutcome` | `Unspecified = 0`, `Success`, `Failure`, `Denied` |
| `ActorId` | `string?` | null when unauthenticated (a failed login for an unknown identifier) |
| `ActorName` | `string?` | |
| `EntityType` | `string?` | |
| `EntityId` | `string?` | |
| `OccurredAt` | `DateTimeOffset` | capture time, not persist time |
| `IpAddress` | `string?` | |
| `UserAgent` | `string?` | truncated to 512 |
| `CorrelationId` | `string?` | |
| `Reason` | `string?` | the internal reason — `LoginFailureReason`, `DenialReason` |
| `Data` | `string?` | JSON, **already redacted** (§7) |

**Both enums reserve `0` for `Unspecified` and both are validated with `Enum.IsDefined` at the recorder.**
This is the direct lesson from `SequenceEngine.Postgres = 0` in `0.22.0`, where `default` was a valid
value, so an unset engine passed validation and a doc comment claiming otherwise was false.

### `AuditEngine`

`Unspecified = 0`, `Postgres`, `SqlServer`, `MySql`. Mirrors `SequenceEngine` after its `0.22.0`
correction: reserving `0` is what makes an unset value fail `Enum.IsDefined` instead of silently
selecting the first dialect.

`ActorId`, `EntityId`, and `EntityType` are **nullable here and non-nullable on
`Themia.Framework.Services.AuditEvent`**. That older record cannot express a failed login for an
identifier that matches no user. `AuditEvent` is not changed; see §9.

---

## 5. Storage

One table, `audit.audit_events`. Schema-qualified, matching the other modules (`export`, `identity`);
framework-level tables like `themia_sequences` stay unqualified, and this is a module.

```
id               guid           PK
tenant_id        varchar(100)   NULL
category         smallint       NOT NULL
event_type       varchar(100)   NOT NULL
outcome          smallint       NOT NULL
actor_id         varchar(256)   NULL
actor_name       varchar(256)   NULL
entity_type      varchar(256)   NULL
entity_id        varchar(256)   NULL
occurred_at      <per-engine>   NOT NULL
ip_address       varchar(64)    NULL
user_agent       varchar(512)   NULL
correlation_id   varchar(128)   NULL
reason           varchar(256)   NULL
data             text/jsonb     NULL
```

`occurred_at`: `timestamptz` (Postgres) / `datetime2` (SQL Server) / `DATETIME(6)` (MySQL), matching
`ExceptionLogMigration`.

Indexes, each tied to a query the module actually issues:

- `ix_audit_events_tenant_occurred (tenant_id, occurred_at DESC)` — the tenant timeline
- `ix_audit_events_actor_occurred (actor_id, occurred_at DESC)` — "everything this user did"
- `ix_audit_events_entity (entity_type, entity_id, occurred_at DESC)` — "history of this record"
- `ix_audit_events_occurred (occurred_at)` — the purge scan

**Append-only.** The store exposes insert, query, and `PurgeAsync`. No update, no delete-by-id.

### Migration

`AuditSchemaMigration`, following `ExportSchemaMigration` and `SequencesSchemaMigration` exactly:

1. The **unsupported-provider guard runs first**, before the adopt-if-exists check. Reversed, a run
   against an unsupported engine that already had the table would return silently and the ledger would
   record the migration as applied.
2. **Adopt-if-exists per table, not merely per schema** (coord #0078/#0085/#0096) — the per-assembly
   version ledger starts empty on every pre-existing database, so `Up()` runs once against objects that
   may already be there. Guarding only the schema leaves `CREATE TABLE` to fail and crash the host at
   boot. This is the guard-must-cover-every-branch hazard: a check on the container reads as covering
   its contents.
3. The engine whitelist in `IfDatabase(...)` and the guard's prefix list are **two parallel whitelists
   that must agree**. Both are edited together or neither.

---

## 6. Write path and atomicity

**Decision (approved): leg 3 writes inside the caller's transaction.**

An audit row recording a change that was rolled back is worse during an investigation than a missing
row: it asserts something happened that did not. Both failure modes exist; this one misleads.

ezy-assets does the opposite (`AuditLogRepository` opens its own connection via `NpgsqlDataSource`), so
a future port of those 46 call sites changes their durability semantics. Recorded here so the change is
deliberate rather than discovered.

`IAuditStore` **never opens a connection**. The caller always supplies one:

```csharp
Task WriteAsync(AuditEntry entry, DbConnection connection, DbTransaction? transaction, CancellationToken ct);
```

One method, no mode flag, no branch inside the store that could take the wrong path.

| leg | connection |
| --- | --- |
| 3 (activity) | the ambient unit-of-work connection and its transaction |
| 4 (auth events) | a fresh connection from the dialect — a login failure has no transaction to join |

### The ambient-connection seam

Reaching the ambient connection differs by data layer, and `Themia.Modules.Audit` must not touch raw
connections directly — `Themia.Analyzers` flags raw-connection use outside the data layer (DECISION #6),
and adding an analyzer exception for the audit module would weaken the rule for everyone.

So: **`IAmbientConnectionAccessor` is defined in `Themia.Framework.Data.Abstractions` and implemented
in each data-layer package.**

```csharp
public interface IAmbientConnectionAccessor
{
    // Null when no unit of work is open on this scope.
    Task<(DbConnection Connection, DbTransaction? Transaction)?> TryGetAsync(CancellationToken ct);
}
```

- `Themia.Framework.Data.EFCore` → `DbContext.Database.GetDbConnection()` / `GetDbTransaction()`
- `Themia.Framework.Data.Dapper` → `IDapperConnectionContext.GetOpenConnectionAsync` / `CurrentTransaction`

The audit module consumes only the abstraction. Both peers get identical semantics, and release 2 reuses
the same seam.

**When no unit of work is open**, `TryGetAsync` returns null and the recorder opens its own connection —
the leg-4 path. An adopter calling `IAuditLogService.WriteAsync` outside a transaction gets a durable
row rather than an exception, which is the useful behaviour; the atomicity guarantee is stated as
"joins the ambient transaction when one exists", not "requires one".

---

## 7. Redaction

**Redaction is unconditional and lives on the single write path.** It is not an option, not a helper the
caller may forget, and not a policy object that defaults to permissive.

`AuditRecorder.RecordAsync` redacts `Data` before handing the entry to `IAuditStore`. Every leg goes
through the recorder.

Default deny-list, matched case-insensitively against JSON property names at any depth:

```
password, passwordhash, passwordsalt, secret, token, refreshtoken, accesstoken,
apikey, api_key, authorization, otp, pin, cvv, creditcard, card_number, privatekey, ssn
```

Matched values become the literal `"[redacted]"`. The property name is kept — knowing that a password
field was present is itself audit-relevant.

`AuditRedactionOptions` lets an adopter **add** patterns. It does not let them remove the defaults;
an adopter who needs a default gone can supply their own `IAuditRedactor`, which is a conspicuous act
rather than a config line.

**Stated limitation:** `IAuditStore` is public so an adopter can supply a store for an engine Themia does
not ship. A caller writing to `IAuditStore` directly bypasses redaction. The realistic accidental path —
`RecordAsync(new AuditEntry { Data = json })` — goes through the recorder and is covered. `IAuditStore`
is documented as the raw sink.

---

## 8. Tenant semantics

`TenantId` is nullable. `null` means host-level, not "unknown".

A login failure with `LoginFailureReason.NotFound` has no user, therefore no tenant, and is exactly the
event a security review most wants to see. Forcing a tenant would either drop these rows or file them
under a fabricated tenant.

Unlike `themia_sequences` — where `''` stands in for host-level because no engine permits a NULL column
in a primary key — `audit_events` has a surrogate `Guid` primary key and no uniqueness constraint over
`tenant_id`, so a real `NULL` carries no engine-divergent NULL semantics here.

The module resolves the tenant from `ITenantContext` when one is present and writes `null` when it is
not. **The recorder never invents a tenant.**

---

## 9. Identity changes (leg 4)

Leg 4 requires additive changes to Identity. All are non-breaking; each is listed with the defect it
fixes.

### 9a. `IAuthenticationHooks` cannot carry an audit consumer

Registration is `services.TryAddScoped<IAuthenticationHooks, AuthenticationHooksBase>()`
(`IdentityAspNetCoreServiceCollectionExtensions.cs:59`) and `AuthenticationFlow` takes exactly one.
`IUserLifecycleHooks` is the same (`IdentityServiceCollectionExtensions.cs:110`).

Either failure is silent:

- Audit registers with `TryAdd` → Identity's registration is already there → **audit never fires**.
- Audit registers with `Add` → **the adopter's own hooks are replaced**.

Neither produces an error. The seam is a policy seam — `Deny()` needs exactly one owner — and it cannot
also be a fan-out observation seam.

**Fix: `IIdentityEventObserver` in `Themia.Modules.Identity.Abstractions`**, resolved as
`IEnumerable<IIdentityEventObserver>`, invoked by `AuthenticationFlow` and `UserService` alongside the
existing hooks. Observation only — no return value, nothing to deny, which matches what
`LoginFailedContext` and `LogoutContext` already document about themselves.

Every method has a default no-op implementation, so an adopter implements only what they care about.
Additive: existing consumers are untouched, and an adopter registering zero observers gets today's
behaviour exactly.

**Every event gets a method, not a chosen few** — the rule `IUserLifecycleHooks` already states for
itself: *"A seam covering three of seven paths reads as covering all seven."*

```
OnLoginSucceededAsync(userId, userName)
OnLoginFailedAsync(userName, LoginFailureReason)
OnLoginDeniedAsync(userName, denialReason)
OnLogoutAsync(userId, allSessions)
OnRefreshSucceededAsync(userId)
OnRefreshDeniedAsync(userId?, denialReason)
OnLockedOutAsync(userId, lockoutEnd)
OnUserMutatedAsync(userId, UserMutation)
```

An observer that throws must not break the flow it observes: `AuthenticationFlow` and `UserService`
catch, log at Error, and continue. A failed audit write must never turn a successful login into a 500 —
and must never turn a *failed* login into a different status code, which would leak the failure reason
the uniform 401 exists to hide.

### 9b. `LogoutContext` has no user identity

```csharp
public sealed class LogoutContext(bool allSessions)
```

The audit row can say a logout happened but not whose. `IIdentityEventObserver.OnLogoutAsync` carries
the user id, resolved from the refresh token before revocation. `LogoutContext` itself gains a nullable
`UserId` property for symmetry — additive, and only Identity constructs it.

### 9c. Lockout raises no event

`UserService.cs:371-375` sets `LockoutEnd` and resets `AccessFailedCount` with no notification. Today an
observer can only infer it from the *next* login attempt returning `LoginFailureReason.LockedOut`, which
is a different event at a different time. `OnLockedOutAsync` fires where the lockout is applied.

### 9d. No IP or user-agent anywhere

Not an Identity defect. `Themia.Modules.Audit` reads them from `IHttpContextAccessor`, which it already
depends on as an ASP.NET-hosted module. No Identity change.

---

## 10. Configuration and DI

```csharp
services.AddThemiaAudit(o =>
{
    o.ConnectionString = cfg.GetConnectionString("Default")!;
    o.Engine           = AuditEngine.Postgres;      // Unspecified = 0 is rejected
    o.Redaction.AddPattern("internal_ref");
});

services.AddThemiaAuditIdentityObserver();          // opt-in: leg 4
```

- `AddThemiaAudit` registers the store, dialect, recorder, redactor, and `IAuditLogService`.
- The Identity observer is a **separate call**. A host without Identity must not be made to reference it,
  and a host that has Identity but does not want auth auditing should not have to opt out of something
  that turned itself on.
- `AuditEngine` follows `SequenceEngine`: `Unspecified = 0`, validated with `Enum.IsDefined`, so an unset
  value fails at startup rather than picking a dialect by accident.
- Options are validated on startup (`ValidateOnStart`).

`AuditModule : ThemiaModuleBase` runs the migration in `InitializeAsync`, matching `ExportModule`.

---

## 11. `IAuditLogService` — the adapter

`Themia.Framework.Services.AuditEvent` stays exactly as shipped. `Themia.Modules.Audit` implements
`IAuditLogService` by mapping onto `AuditEntry`:

```
AuditEvent.EventType   → AuditEntry.EventType
AuditEvent.ActorId     → AuditEntry.ActorId
AuditEvent.ActorName   → AuditEntry.ActorName
AuditEvent.EntityId    → AuditEntry.EntityId
AuditEvent.EntityType  → AuditEntry.EntityType
AuditEvent.OccurredAtUtc → AuditEntry.OccurredAt
AuditEvent.Metadata    → AuditEntry.Data   (JSON, redacted)
                       → Category = Activity, Outcome = Success
```

**Why not widen `AuditEvent` instead:** it lives in `Themia.Framework.Services` (net10, framework) and
`AuditEntry` lives in `Themia.Audit` (neutral, net8+net10). A neutral core cannot reference a framework
package. The adapter is what the layering requires, not a workaround — and it means the dead seam becomes
live with no change to a shipped public API.

Adopters wanting outcome, reason, IP, or a null actor use `IAuditRecorder` directly. `IAuditLogService`
is the compatibility surface; `IAuditRecorder` is the real one. Documented as such in the README, so the
overlap is a stated hierarchy rather than two rival APIs.

---

## 12. Testing

**Neutral core, all three engines, Testcontainers** — following `SequencesSchemaMigrationTests`, which
was split onto its own container after the `0.22.0` review: migration tests get one container, behaviour
tests share another. The `0.22.0` suite went from 43 containers to 4 by doing this; do not regress it.

Required coverage:

- Round-trip insert/read on each engine, including every nullable column left null.
- `tenant_id NULL` round-trips as `null`, not `""`.
- **Column-transposition guard.** Every dialect maps its row positionally; five consecutive nullable
  strings (`actor_id`, `actor_name`, `entity_type`, `entity_id`, `reason`) make a swap invisible. Assert
  each field carries a **distinct** value and lands in its own column. This is the defect that shipped in
  `0.21.4`'s outbox dialects and was caught only because SQL Server's `OUTPUT` clause failed loudly.
- Migration replay: run `Up()` twice; the second is a no-op. Run against a pre-existing table.
- Unsupported provider throws at migrate time.
- **Redaction: proven able to fail.** A test that removes redaction and observes the secret in the row,
  then restores it. A redaction test that passes against a payload with no secret in it proves nothing.
- Enum validation rejects `Unspecified` and out-of-range values.

**Module:**

- Leg 3 joins the ambient transaction: write an audit row inside a rolled-back unit of work, assert the
  row is absent. Assert present after commit. **Both EF and Dapper.**
- Leg 4 writes without an ambient transaction and survives a surrounding rollback.
- An observer that throws does not change the login result — assert the same status code and the same
  response body as an unobserved flow.
- `IAuditLogService` resolves from the real DI graph produced by `AddThemiaAudit`, not from a hand-built
  container. `0.19.0`'s notification-provider defect passed unit tests and failed on the real default
  graph.
- Registering the Identity observer alongside an adopter's own `IAuthenticationHooks` leaves **both**
  running. This is the defect in §9a; it needs a test, not just a design.

**PublicAPI:** every new public member enters `PublicAPI.Unshipped.txt`. A clean `--no-incremental`
build must report no `RS0016`.

---

## 13. Release 2 — entity change log (`0.24.0`, specced separately)

Recorded here so release 1 is not designed in a way that blocks it.

**Shape:** one `audit_events` row (`Category = EntityChange`) owning N `audit_event_changes` rows
(`field_name`, `old_value`, `new_value`), keyed by `audit_event_id`. Release 1's table needs no change.

**Opt-in, never opt-out.** Auditing everything means auditing `refresh_tokens`, the notification outbox,
and `themia_sequences` — a volume problem and a secret-disclosure problem at once. An entity opts in by
marker interface or attribute.

**The unresolved fork — EF/Dapper parity.** EF's `ChangeTracker` holds original values, so capture is
free. `DapperUnitOfWork` sees only the incoming entity (`op.Entity`) and has no prior state. Three
options, none yet chosen:

1. Read-before-write on the Dapper path, for audited entities only. Preserves parity; costs one SELECT
   per audited update.
2. Dapper records new values only. Asymmetric, and an adopter reading the table cannot tell whether a
   null `old_value` means "unchanged" or "this layer cannot know".
3. Auto change log is EF-only, documented and enforced at startup.

DECISION #6 makes EF and Dapper first-class peers, so option 3 needs an explicit ruling against that
decision rather than a silent gap. **The choice belongs to release 2's spec and is not made here.**

---

## 14. Decisions — do not relitigate

1. Three legs; legs 3+4 in `0.23.0`, leg 2 in `0.24.0`.
2. Leg 3 joins the caller's transaction. Leg 4 uses its own connection.
3. `IAuditStore` never opens connections; the caller supplies one. One method, no mode flag.
4. `IAmbientConnectionAccessor` lives in `Themia.Framework.Data.Abstractions`, implemented per data
   layer — so the audit module never touches a raw connection and needs no analyzer exception.
5. Redaction is unconditional, on the recorder, deny-list additive-only.
6. `TenantId` is nullable; `null` is host-level; the recorder never invents a tenant.
7. `AuditEvent` is not modified. `IAuditLogService` is implemented as an adapter over `IAuditRecorder`.
8. `IIdentityEventObserver` is a new fan-out seam; `IAuthenticationHooks` stays single-registration and
   keeps `Deny()`.
9. All enums reserve `0` for `Unspecified` and are validated with `Enum.IsDefined`.
10. No dashboard, no retention job, no Serenity adapter in `0.23.0`.
11. `Themia.Audit` references no `Themia.Framework.*` package; asserted by a test.
