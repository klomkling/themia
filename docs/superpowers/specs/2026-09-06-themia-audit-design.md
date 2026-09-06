# Themia.Audit — design

**Status:** approved. Revised after a `/scrutinize` pass that found two blockers in the first draft;
both fixes are recorded inline with the evidence that forced them.
**Target version:** `0.23.0` (release 1). Release 2 is scoped here but specced separately.
**Supersedes:** the `Themia.Modules.Audit` row in `docs/themia-architecture-overview.md` §B
("ezy-assets `AuditLogRepository` + Zenity audit"), which names a different artifact than the one this
spec builds — see *Why the listed sources do not fit*.

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
failure at runtime, and learns the framework promised something it does not deliver. Same failure class
as coord #0057 (`NotificationMessage.Metadata` — a property no sender ever read).

### What exists instead

**Audit stamping** — `AuditableEntity` (`CreatedAt`/`CreatedBy`/`LastModifiedAt`/`LastModifiedBy`) and
`SoftDeletableEntity` (`DeletedAt`/`DeletedBy`/`RestoredAt`/`RestoredBy`), stamped automatically by both
`ThemiaDbContext` and `DapperUnitOfWork`. **Overwritten on every write** — it answers "who touched this
last", never "what changed, when, by whom".

**Login failure counting** — `User.AccessFailedCount` + `LockoutEnd`, reset to `0` both on a successful
login *and* at the moment lockout triggers (`UserService.cs:371-388`). A counter that forgets. It cannot
answer "how many failures did this account see last month, from which addresses".

**`ILogger`** — `AuthenticationFlow` logs login failures at Warning. Not queryable, not tenant-scoped,
retained for however long the host's log sink happens to keep it.

### Why the listed sources do not fit

`docs/themia-architecture-overview.md` names ezy-assets' `AuditLogRepository` as the source to port.
Read directly, it is **not** an entity change log:

- The `AuditLogs` table has `OldValue`/`NewValue` `jsonb` columns, so it reads like one.
- All **46** call sites are hand-written. `Action` is a business verb (`PROPOSAL_INTEREST_SUBMITTED`,
  `DEAL_COMMISSION_SPLIT_CAPTURED`), never a CRUD verb.
- `OldValue` — set at roughly half the sites — holds a hand-picked field subset
  (`new { status = oldStatus }`), never a computed row diff.

It is an **activity log with two columns named old/new**. `AuditEvent` in `Themia.Framework.Services`
has no old/new fields at all, so it is unambiguously an activity log too.

The **entity change log** — what most people mean by "audit" — exists in neither source. It is built
here, in release 2, from scratch.

---

## 2. Scope

| leg | what | who writes | release |
| --- | --- | --- | --- |
| **3. Activity log** | business events the app chooses to record | the app calls in | `0.23.0` |
| **4. Auth/security events** | login success/failure/logout/refresh/lockout, credential mutations | the module, via Identity observers | `0.23.0` |
| **2. Entity change log** | per-row `CREATE`/`UPDATE`/`DELETE`/`SOFT_DELETE`, field-level old→new | the data layer, automatically | `0.24.0` |

### Why the split falls here

Legs 3 and 4 share **one record shape** — "who did what, when, with what outcome". They differ only in
who calls. One table, one write path.

Leg 2 needs a **second shape**: one event owning N field-change rows. It is also the only leg that hits
the EF/Dapper parity fork (§15), because EF's `ChangeTracker` holds original values and Dapper has no
change tracker at all. Isolating it means release 1 settles nothing it would have to revisit.

### Non-goals

- **No retention job in `0.23.0`.** The default is keep-forever, so a scheduled job would do nothing by
  default while adding a Quartz dependency and a tenant-scoping surface. The core ships
  `PurgeAsync(olderThan, …)`; a Quartz wrapper lands when an adopter configures retention.
- **No Serenity adapter.** Per `CLAUDE.md`, built only if PowerACC actually migrates.
- **No log-shipping / SIEM export.**
- **No write surface in the dashboard.** Read-only, always. An audit log an operator can edit is not an
  audit log.

---

## 3. Package shape

Follows the `Themia.Exceptional` family — an append-only store with one schema across three engines
behind a dialect strategy, plus a separate mountable dashboard package.

```
Themia.Audit                net8.0;net10.0   record model, IAuditRecorder + AuditRecorder, IAuditStore,
                                             Dapper store, IAuditDialect, redaction, HTTP enrichment,
                                             schema migration
Themia.Audit.PostgreSql     net8.0;net10.0   dialect
Themia.Audit.SqlServer      net8.0;net10.0   dialect
Themia.Audit.MySql          net8.0;net10.0   dialect
Themia.Audit.AspNetCore     net8.0;net10.0   mountable read-only dashboard (§13)
Themia.Modules.Audit        net10.0          IThemiaModule, TransactionalAuditRecorder, tenant
                                             resolution, IAuditLogService impl, Identity observer
```

**"Neutral" means no `Themia.Framework.*` dependency — not "no ASP.NET".** `Themia.Exceptional` itself
carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />` and ships
`Middleware/RequestBodyLoggingMiddleware.cs` and `Serilog/HttpContextEnricher.cs` in the neutral core.
`Themia.Audit` does the same for IP / user-agent capture, so a web host gets enrichment without the
module layer and a worker host simply reads nulls.

`Themia.Audit` references `Themia.Data.Migrations`, as `Themia.Exceptional` does, so it can run its own
schema without the module layer (§11).

**`Themia.Audit` must not reference any `Themia.Framework.*` package.** Asserted by a test, not left to
convention.

---

## 4. Record model

`Themia.Audit.AuditEntry` — one immutable record, both legs.

| field | type | notes |
| --- | --- | --- |
| `EventUid` | `Guid` | client-generated public identifier; the database key is a separate `bigint` (§5) |
| `TenantId` | `string?` | `null` = host-level (§9) |
| `Category` | `AuditCategory` | `Unspecified = 0`, `Activity`, `Authentication`, `UserLifecycle`. Release 2 adds `EntityChange`. |
| `EventType` | `string` | `PROPOSAL_ACCEPTED`, `LOGIN_FAILED`, `PASSWORD_CHANGED` |
| `Outcome` | `AuditOutcome` | `Unspecified = 0`, `Success`, `Failure`, `Denied` |
| `ActorId` | `string?` | null when unauthenticated — a failed login for an unknown identifier |
| `ActorName` | `string?` | |
| `EntityType` | `string?` | |
| `EntityId` | `string?` | |
| `OccurredAt` | `DateTimeOffset` | capture time, not persist time |
| `IpAddress` | `string?` | |
| `UserAgent` | `string?` | truncated to 512 |
| `CorrelationId` | `string?` | |
| `Reason` | `string?` | the internal reason — `LoginFailureReason`, `DenialReason` |
| `Data` | `string?` | JSON, **set by the recorder from a payload object, already redacted** (§8) — never assigned by a caller |

### `AuditEngine`

`Unspecified = 0`, `Postgres`, `SqlServer`, `MySql`.

**Every enum here reserves `0` for `Unspecified` and is validated with `Enum.IsDefined` at the recorder.**
Direct lesson from `SequenceEngine.Postgres = 0` in `0.22.0`: `default` was a valid value, so an unset
engine passed validation and a doc comment claiming otherwise was false.

`ActorId`, `EntityId`, and `EntityType` are **nullable here and non-nullable on
`Themia.Framework.Services.AuditEvent`**. That older record cannot express a failed login for an
identifier matching no user. `AuditEvent` is not changed; see §12.

---

## 5. Storage

One table: **`themia_audit_events`, unqualified, identical on every engine. Never `InSchema(...)`.**

> `ChallengeSchemaMigration.cs:21-28`: *"FluentMigrator drops `InSchema(...)` on MySQL — there, 'schema'
> and 'database' are the same concept... That divergence is exactly how `Themia.Modules.Messaging`'s
> `MessagingSchemaMigration` ended up with its `outbox_messages` once colliding with
> `Themia.Modules.Notifications`'s identically-named table on MySQL."*

The first draft of this spec specified `audit.audit_events`, reproducing that defect class. It is
corrected here, not patched per-engine. `NotificationsPurgeIndexMigration.cs:51` still carries the
patched form (`DROP INDEX ix_outbox_purge ON outbox_messages;`, schema prefix stripped for MySQL only) —
that is the shape to avoid, not to copy.

The `themia_` prefix is deliberate: this table shares a namespace with the adopter's own tables, and
`audit_events` is a name an application plausibly already owns.

```
id               bigint identity         PK (clustered)
event_uid        guid                    NOT NULL, UNIQUE
tenant_id        varchar(100)            NULL
category         smallint                NOT NULL
event_type       varchar(100)            NOT NULL
outcome          smallint                NOT NULL
actor_id         varchar(256)            NULL
actor_name       varchar(256)            NULL
entity_type      varchar(256)            NULL
entity_id        varchar(256)            NULL
occurred_at      <per-engine>            NOT NULL
ip_address       varchar(64)             NULL
user_agent       varchar(512)            NULL
correlation_id   varchar(128)            NULL
reason           varchar(256)            NULL
data             AsString(int.MaxValue)  NULL
```

`occurred_at`: `timestamptz` (Postgres) / `datetime2` (SQL Server) / `DATETIME(6)` (MySQL), matching
`ExceptionLogMigration`.

### `data` is text on every engine, not `jsonb`

An earlier draft used `jsonb` (Postgres) / `JSON` (MySQL) / `nvarchar(max)` (SQL Server). **Postgres and
MySQL validate JSON on insert; SQL Server does not.** `data` holds a payload the *adopter* supplies, so a
caller passing a non-JSON string succeeds on SQL Server and throws on the other two — and because leg 3
runs inside the caller's transaction (§7), that throw **rolls back the adopter's business write**. A
malformed audit payload must never be able to fail the operation it describes, and it must not do so on
two engines out of three.

`Themia.Exceptional` stores its equivalent adopter-supplied payload as `AsString(int.MaxValue)` on every
engine (`ExceptionLogMigration.cs:55`). Audit follows it. ezy-assets could use `jsonb` because all 46 of
its call sites serialize before writing; a framework accepting a string from anyone has no such
guarantee.

If JSON querying is wanted later, it arrives as a per-engine generated column — a separate decision with
its own migration, not a constraint smuggled into the storage type.

### Length limits are validated, not left to the engine

Every bounded column has a public constant on `AuditEntry`, bound to the migration column, exactly as
`SequenceProvider.MaxSequenceKeyLength = 100` is bound to `themia_sequences.sequence_key`:

```
MaxEventTypeLength     = 100     MaxActorIdLength      = 256
MaxActorNameLength     = 256     MaxEntityTypeLength   = 256
MaxEntityIdLength      = 256     MaxReasonLength       = 256
MaxCorrelationIdLength = 128     MaxIpAddressLength    = 64
MaxUserAgentLength     = 512
```

Without this the two outcomes are both wrong: MySQL in non-strict mode **truncates silently**, so an
over-length `event_type` becomes a *different* event that queries then miscount; every other engine
throws, which under §7's atomicity rolls back the adopter's business transaction.

**Adopter-named fields are rejected, never truncated** — `event_type`, `actor_id`, `entity_type`,
`entity_id`. Truncating them merges two distinct events into one and the audit trail then asserts
something false. `user_agent` and `ip_address` are truncated instead: they are captured by the framework,
not named by the adopter, and a clipped user-agent is still the same event.

### Index budget

Each index exists for a query in §6 or §13. An index with no query behind it is write cost for nothing.

| index | serves |
| --- | --- |
| `(tenant_id, occurred_at DESC)` | the tenant timeline — any query filtering `TenantId` |
| `(actor_id, occurred_at DESC)` | "everything this user did" |
| `(entity_type, entity_id, occurred_at DESC)` | "history of this record" |
| `(occurred_at)` | the purge scan, **and the dashboard's unfiltered default list** |

The unfiltered list is deliberately served by `(occurred_at)`, not by the tenant index: §6's
`TenantId = null` means *no filter*, so there is no leading-column predicate and the composite index
cannot be used. An index whose stated purpose is a query that never matches it is write cost for nothing.

**Key-size ceiling, stated because Challenges was bitten by the unstated one.** The widest index is
`(entity_type, entity_id, occurred_at)`: on SQL Server `nvarchar(256)` = 512 bytes each, plus
`datetime2` = **1032 bytes**. That fits the 1700-byte nonclustered limit (SQL Server 2016+) but exceeds
the 900-byte clustered/legacy limit. **These columns cannot be widened past 256 without re-checking that
budget**, and none of them may be moved into a clustered key.

### Why the key is a `bigint`, not the `Guid`

`ExceptionLogMigration.cs:48-49` — the sibling this design follows — uses
`.WithColumn("Id").AsInt64().PrimaryKey().Identity()` plus a separate `Guid` column under a unique index,
and audit does the same for the same reason.

FluentMigrator's `.PrimaryKey()` produces a **clustered** primary key on SQL Server. A random `Guid` key
scatters inserts across the whole B-tree, so an append-only table page-splits on essentially every write —
and this table has no size ceiling, since retention defaults to keep-forever (§2).

An earlier draft used a client-generated `Guid` PK justified as "the caller holds it before commit". That
is not load-bearing: neither leg reads the id back, and release 2's child rows are inserted inside the same
transaction, where the returned identity is available. `themia_audit_changes` therefore keys on the
`bigint`.

`event_uid` is what the dashboard's detail route uses — a sequential `bigint` in a URL invites walking the
table by hand.

### `IAuditStore`

Append-only: insert, read, purge. **No update, no delete-by-id.** Every method takes its connection, per
decision 4.

```csharp
public interface IAuditStore
{
    Task<long> WriteAsync(AuditEntry entry, DbConnection connection, DbTransaction? transaction, CancellationToken ct);

    Task<PagedResult<AuditEntry>> QueryAsync(AuditQuery query, DbConnection connection, CancellationToken ct);

    Task<AuditEntry?> GetAsync(Guid eventUid, DbConnection connection, CancellationToken ct);

    /// Deletes every row older than <paramref name="olderThan"/> in ONE statement.
    /// On a table that has never been purged this can be very large: unlike Themia.Exceptional —
    /// whose Exceptions table is bounded by DuplicateCount dedup — audit never collapses rows and
    /// defaults to keep-forever, so a first purge after a year of traffic holds a long lock.
    /// Purge in date slices on a big table rather than in one call.
    Task<int> PurgeAsync(DateTimeOffset olderThan, DbConnection connection, CancellationToken ct);
}
```

`WriteAsync` returns the generated `bigint`; nothing in release 1 uses it, and release 2's change rows do.

### `IAuditRecorder`

The write surface adopters actually use. Two implementations (§7); one interface.

```csharp
public interface IAuditRecorder
{
    /// The payload is serialized by the recorder — callers never supply a JSON string (§8).
    ValueTask<Guid> RecordAsync(AuditEntry entry, object? payload = null, CancellationToken ct = default);
}
```

Returns the `EventUid`, which the caller holds without a round-trip.

### `IAuditDialect`

Public, so an adopter on an engine Themia does not ship can supply one without forking — the same seam as
`IExceptionalSqlDialect` and `ISequenceDialect`.

```csharp
public interface IAuditDialect
{
    /// Opens a NEW, unopened connection. Used by every caller that has no ambient
    /// transaction to join: leg 4, the dashboard, and PurgeAsync.
    DbConnection CreateConnection(string connectionString);

    string InsertSql { get; }       // named parameters, one per column in §5
    string SelectPageSql { get; }   // §6 filters, ORDER BY occurred_at DESC, id DESC, engine paging
    string CountSql { get; }        // same predicate as SelectPageSql, no paging
    string PurgeSql { get; }        // DELETE WHERE occurred_at < @olderThan
}
```

`CreateConnection` is on the dialect and not on the store because §7 makes the store connectionless.
Removing it entirely — as an earlier draft did — left leg 4, the dashboard, and the purge with no stated
source of a connection at all.

### Migration

`AuditSchemaMigration`, following `ChallengeSchemaMigration` and `SequencesSchemaMigration`:

1. The **unsupported-provider guard runs first**, before the adopt-if-exists check. Reversed, a run
   against an unsupported engine that already had the table would return silently and the ledger would
   record the migration as applied.
2. **Adopt-if-exists per table** (coord #0078/#0085/#0096) — the per-assembly version ledger starts empty
   on every pre-existing database, so `Up()` runs once against objects that may already be there. An
   unguarded `CREATE TABLE` crash-loops the host at boot.
3. The engine whitelist in `IfDatabase(...)` and the guard's prefix list are **two parallel whitelists
   that must agree**. Both are edited together or neither.

---

## 6. Query API

Defined here because §5's indexes and §13's dashboard both depend on it; leaving it to the implementer
would let the two drift.

```csharp
public sealed record AuditQuery
{
    public string? TenantId { get; init; }        // null = no tenant filter, NOT host-level
    public bool HostLevelOnly { get; init; }      // tenant_id IS NULL
    public string? ActorId { get; init; }
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public AuditCategory? Category { get; init; }
    public AuditOutcome? Outcome { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
```

Returns `PagedResult<AuditEntry>` from `Themia.Framework.Data.Abstractions/Paging/PagedResult.cs` —
except that type lives in the framework and `Themia.Audit` is neutral, so the core returns
`Themia.Audit.PagedResult<AuditEntry>`, mirroring how `Themia.Exceptional` ships its own `PagedResult.cs`
for the same reason. **Do not "unify" these two; the layering forbids it.**

`TenantId = null` means *no filter* and `HostLevelOnly = true` means *`tenant_id IS NULL`*. Two distinct
intentions that a single nullable field cannot express — the same reason the entry's `TenantId` needed
its meaning pinned in §9.

Results order by `occurred_at DESC, id DESC`. The `id` tiebreak is not decoration: `occurred_at` is
capture time and two events in one request routinely share a millisecond, so ordering by timestamp alone
gives an unstable page boundary and rows that appear twice or never across pages.

---

## 7. Write path and atomicity

**Decision (approved): leg 3 writes inside the caller's transaction.** An audit row recording a change
that was rolled back is worse during an investigation than a missing row — it asserts something that did
not happen. Both failure modes exist; this one misleads.

ezy-assets does the opposite (`AuditLogRepository` opens its own `NpgsqlDataSource` connection), so a
future port of those 46 call sites changes their durability semantics. Recorded so the change is
deliberate rather than discovered.

`IAuditStore` **never opens a connection**. The caller always supplies one:

```csharp
Task WriteAsync(AuditEntry entry, DbConnection connection, DbTransaction? transaction, CancellationToken ct);
```

One method, no mode flag, no branch inside the store that could take the wrong path.

### The transaction policy — and why the first draft was wrong

The first draft said: join the ambient transaction when one exists, otherwise open our own connection.
Traced against the real code, that silently voids the guarantee on the common path.

`EfUnitOfWork.cs:28` → `SaveAsync` → `context.SaveChangesAsync(ct)`. **No explicit transaction.** EF opens
and commits its own inside `SaveChanges`, so `Database.CurrentTransaction` is null before and after. The
sequence was:

1. app calls `IAuditLogService.WriteAsync` mid-handler
2. no ambient transaction found
3. fallback opens its own connection — row is durable
4. app's `SaveChangesAsync` fails — business change rolls back, **audit row survives**

Exactly the outcome the decision above rejects. And measured, that is the normal case: `src/modules`
contains **43** `SaveChangesAsync` call sites against **4** `ExecuteInTransactionAsync`.

**`AuditTransactionPolicy`** — `Unspecified = 0`, `RequireTransaction`, `JoinIfPresent`, `Never`:

| category | policy | rationale |
| --- | --- | --- |
| `Activity` | `RequireTransaction` (default, configurable) | the guarantee is real or its absence is loud |
| `Authentication`, `UserLifecycle` | `Never`, not configurable | a failed login has no transaction to join, and must be recorded even when the surrounding request fails |

Under `RequireTransaction` with no ambient transaction, `TransactionalAuditRecorder` throws, and the
message names `IUnitOfWork.ExecuteInTransactionAsync` as the fix. This is not a Themia limitation being surfaced — a
business write and its audit row cannot be atomic without a transaction on any database. Making the app
say so is the honest form.

`JoinIfPresent` remains available for adopters who accept the weaker guarantee, and its XML doc states
plainly that it degrades to durable-only when no transaction is open. `Never` always uses its own
connection.

**Rejected alternative — writing the audit row as an EF-tracked entity** so the app's own `SaveChanges`
carries it. It gives a stronger guarantee on the default path with no new abstraction, but the entity
would have to be mapped in `ThemiaDbContext.OnModelCreating`, forcing `Themia.Audit` onto **every** EF
adopter whether or not they use auditing. Rejected on that cost, not on the mechanism.

### Two recorders, one interface — and why

`IAmbientConnectionAccessor` lives in `Themia.Framework.Data.Abstractions`, and `Themia.Audit` may not
reference any `Themia.Framework.*` package (§3, asserted by a test). A single recorder cannot be both
neutral and transaction-aware. An earlier draft claimed one — "every leg goes through the recorder" in §8
and "the recorder throws" here — which cannot be built as written.

Split, with the seam named rather than left to the implementer:

| type | package | does |
| --- | --- | --- |
| `AuditRecorder` | `Themia.Audit` (neutral) | validates lengths and enums, **redacts**, writes through `IAuditStore` on a connection handed to it. Knows nothing about transactions. |
| `TransactionalAuditRecorder` | `Themia.Modules.Audit` | implements the same `IAuditRecorder`, resolves the ambient connection, enforces `AuditTransactionPolicy`, delegates to `AuditRecorder`. |

DI registers `TransactionalAuditRecorder` as `IAuditRecorder`; `AuditRecorder` is its dependency, not a
second registration. Consumers see one interface.

**Redaction stays on the single write path** because it lives in the *inner* recorder: every route to
`IAuditStore` that this design creates — leg 3, leg 4, the module adapter — passes through
`AuditRecorder`. Putting redaction in the outer type instead would leave leg 4 (which bypasses the
transaction logic) unredacted, which is precisely the split-path failure §8 exists to prevent.

A neutral consumer using `Themia.Audit` alone gets `AuditRecorder` — validation and redaction intact,
transaction policy absent, because there is no unit of work to enlist in.

### The ambient-connection seam

Reaching the ambient connection differs per data layer, and `Themia.Modules.Audit` must not touch raw
connections: `RawConnectionBypassAnalyzer.cs:9` (THEMIA103) flags
`IDapperConnectionContext.GetOpenConnectionAsync`, and `DataLayerScope.cs:14` exempts only assemblies
whose name starts with `Themia.Framework.Data.`.

So **`IAmbientConnectionAccessor` is defined in `Themia.Framework.Data.Abstractions` and implemented in
each data-layer package** — inside the analyzer's exemption, no rule weakened for anyone:

```csharp
public interface IAmbientConnectionAccessor
{
    // Null when no transaction is open on this scope.
    Task<(DbConnection Connection, DbTransaction Transaction)?> TryGetAsync(CancellationToken ct);
}
```

- `Themia.Framework.Data.EFCore` → `Database.GetDbConnection()` / `Database.CurrentTransaction`
- `Themia.Framework.Data.Dapper` → `IDapperConnectionContext` (`GetOpenConnectionAsync` / `CurrentTransaction`)

The tuple is non-nullable in its transaction: a connection with no transaction is not a thing this seam
returns, because it would let a caller believe it had enlisted when it had not.

---

## 8. Redaction

**Unconditional, on the single write path.** Not an option, not a helper the caller may forget, not a
policy object defaulting to permissive.

`AuditRecorder.RecordAsync` — the **inner**, neutral recorder (§7) — redacts `Data` before handing the
entry to `IAuditStore`. Every leg reaches `IAuditStore` through it: leg 3 via
`TransactionalAuditRecorder`, leg 4 and the `IAuditLogService` adapter directly. Redaction deliberately
does not live in the outer transactional recorder, which leg 4 bypasses.

### `Data` is never a string the caller supplies

`IAuditRecorder.RecordAsync` takes an **object** and serializes it with `System.Text.Json` itself. There
is no code path by which an adopter hands over a raw `Data` string.

This is the fix for a hole the previous revision opened. Loosening `data` to text on every engine (§5) was
correct — it stops a malformed payload from failing the adopter's transaction on two engines out of three
— but redaction walks *JSON property names*, so a payload that does not parse could not be walked and
**nothing would be redacted**. The `jsonb`/`JSON` columns had at least guaranteed parseable input on two
engines; removing them removed that guarantee with them.

Serializing inside the recorder keeps all three properties at once: the column is text, so no engine
validates and none can roll back a business write; `data` is nonetheless well-formed JSON **by
construction**; and the redactor always has something it can walk. The invariant is structural, not
documented.

`AuditEvent.Metadata` is already `IReadOnlyDictionary<string, string>`, so the adapter (§13) passes it
straight through.

Default deny-list, matched case-insensitively against JSON property names at any depth:

```
password, passwordhash, passwordsalt, secret, token, refreshtoken, accesstoken,
apikey, api_key, authorization, otp, pin, cvv, creditcard, card_number, privatekey, ssn
```

Matched values become the literal `"[redacted]"`. The property name is kept — that a password field was
present is itself audit-relevant.

`AuditRedactionOptions` lets an adopter **add** patterns, never remove the defaults. An adopter who needs
a default gone supplies their own `IAuditRedactor` — a conspicuous act rather than a config line.

**Stated limitation:** `IAuditStore` is public so an adopter can supply a store for an engine Themia does
not ship, and a caller writing to it directly bypasses redaction. The realistic accidental path —
`RecordAsync(new AuditEntry { Data = json })` — goes through the recorder and is covered. `IAuditStore`
is documented as the raw sink.

---

## 9. Tenant semantics

`TenantId` is nullable. `null` means host-level, not "unknown".

A login failure with `LoginFailureReason.NotFound` has no user, therefore no tenant, and is exactly the
event a security review most wants. Forcing a tenant would either drop these rows or file them under a
fabricated one.

Unlike `themia_sequences` — where `''` stands in for host-level because no engine permits a NULL column
in a primary key — `themia_audit_events` has a surrogate `Guid` primary key and no uniqueness constraint
over `tenant_id`, so a real `NULL` carries no engine-divergent NULL semantics.

The module resolves the tenant from `ITenantContext` when present and writes `null` when not. **The
recorder never invents a tenant.**

### Reads are not tenant-filtered, and that is a decision

Writes resolve a tenant; `IAuditStore.QueryAsync` applies **no tenant predicate unless `AuditQuery` names
one**. That asymmetry is deliberate and has to be stated, because in this codebase the default runs the
other way: both data layers filter by tenant by construction, and THEMIA103 exists to stop code slipping
around it. A read surface that fails open is a real divergence, and an undiscussed one reads as covered
when it is not.

The neutral store cannot filter: it has no `ITenantContext` — that type lives in the framework, which
`Themia.Audit` may not reference (§3). So the split is:

| surface | tenant predicate |
| --- | --- |
| `IAuditStore.QueryAsync` (neutral) | only what `AuditQuery` carries. Unfiltered by default, **by design**. |
| `ITenantAuditReader` (module) | pre-seeds `TenantId` from `ITenantContext`; cannot be asked for another tenant's rows |
| the dashboard (§13) | neither — scoping is the adopter's, through `Authorize` |

`ITenantAuditReader` is the analogue of `ITenantQueryFactory.For<T>()` in DECISION #6: the safe path is
the one with the predicate already applied, and the raw one stays available and conspicuous.

Adopter code should use `ITenantAuditReader`. `IAuditStore.QueryAsync`'s XML doc says outright that it
returns rows across every tenant.

---

## 10. Identity changes (leg 4)

All additive and non-breaking. Each is listed with the defect it fixes.

### 10a. `IAuthenticationHooks` cannot carry an audit consumer

Registration is `services.TryAddScoped<IAuthenticationHooks, AuthenticationHooksBase>()`
(`IdentityAspNetCoreServiceCollectionExtensions.cs:59`) and `AuthenticationFlow.cs:33` takes exactly one.
`IUserLifecycleHooks` is the same (`IdentityServiceCollectionExtensions.cs:110`).

Either failure is silent:

- Audit registers with `TryAdd` → Identity's registration is already there → **audit never fires**.
- Audit registers with `Add` → **the adopter's own hooks are replaced**.

Neither errors. The seam is a policy seam — `Deny()` needs exactly one owner — and cannot also be a
fan-out observation seam.

**Fix: `IIdentityEventObserver` in `Themia.Modules.Identity.Abstractions`**, resolved as
`IEnumerable<IIdentityEventObserver>`, invoked by `AuthenticationFlow` and `UserService` alongside the
existing hooks. Observation only — no return value, nothing to deny — which matches what
`LoginFailedContext` and `LogoutContext` already document about themselves.

Every method has a default no-op, so an adopter implements only what they need, and one registering zero
observers gets today's behaviour exactly.

**Every event gets a method, not a chosen few** — the rule `IUserLifecycleHooks` already states for
itself: *"A seam covering three of seven paths reads as covering all seven."*

```
// password login
OnLoginSucceededAsync(userId, userName)
OnLoginFailedAsync(userName, LoginFailureReason)
OnLoginDeniedAsync(userName, denialReason)

// external login — Google / LINE (§10b)
OnExternalLoginSucceededAsync(userId, provider, wasCreated, wasLinked)
OnExternalLoginFailedAsync(provider, ExternalLoginOutcome)
OnExternalLoginDeniedAsync(provider, denialReason)

// refresh — all five outcomes (§10c)
OnRefreshSucceededAsync(userId)
OnRefreshDeniedAsync(userName?, denialReason, bool rotationCommitted)
OnRefreshFailedAsync(userId?, RefreshOutcome)

// session and account
OnLogoutAsync(userId, allSessions)
OnLockedOutAsync(userId, lockoutEnd)
OnUserMutatedAsync(userId, UserMutation)
```

An observer that throws must not break the flow it observes: `AuthenticationFlow` and `UserService`
catch, log at Error, and continue. A failed audit write must never turn a successful login into a 500 —
and must never turn a *failed* login into a different status code, which would leak the failure reason
the uniform 401 exists to hide.

### 10b. External login is a whole shipped flow with no audit

`Themia.Modules.Identity.ExternalAuth.AspNetCore` has shipped Google and LINE sign-in since `0.5.2`. It
has its own hook interface, invoked at three points:

```
ExternalAuthenticationFlow.cs:68   OnBeforeExternalLoginAsync
ExternalAuthenticationFlow.cs:92   OnExternalLoginSucceededAsync
ExternalAuthenticationFlow.cs:120  OnExternalLoginFailedAsync
```

It carries the same single-registration defect as §10a —
`ExternalAuthBuilder.cs:34`: `services.TryAddScoped<IExternalAuthenticationHooks, ExternalAuthenticationHooksBase>()`.

An earlier draft of this spec covered password login only. The result would have been an audit trail that
*looks* complete — every password event present — while a user signing in with Google left no trace at
all. That is the failure §10a quotes `IUserLifecycleHooks` against: *"A seam covering three of seven paths
reads as covering all seven."*

`ExternalLoginSucceededContext(User user, bool wasCreated, bool wasLinked)` carries the two events a
security review most needs: an account **auto-created** from a provider identity, and an existing account
**linked** to a new provider. Account takeover through provider linking is a real attack; it must be in
the log.

`ExternalLoginOutcome.AccountInactive`'s own doc comment closes the argument — *"the distinct value exists
only so audit sees the reason"* — a fourth seam built for a consumer that was never written.

`ExternalAuthenticationFlow` invokes the observers at the same three points, exactly as
`AuthenticationFlow` does.

### 10c. Refresh has five outcomes; the hooks see two

Traced through `AuthenticationFlow.RefreshAsync` (`AuthenticationFlow.cs:178-227`):

| outcome | line | hook today |
| --- | --- | --- |
| denied by hook, **before** rotation | 186 | `OnBeforeRefreshAsync` |
| `RefreshOutcome.ReuseDetected` | ~195 | **none** |
| `RefreshOutcome.Invalid` | ~196 | **none** |
| account inactive or locked out | 207 | **none** — logged only |
| denied by hook, **after** rotation committed | 221 | `OnRefreshSucceededAsync` + `IsDenied` |

**`ReuseDetected` is the most security-relevant event in the whole flow** — a rotated refresh token
presented twice is the textbook signature of token theft — and nothing observes it. `OnRefreshFailedAsync`
covers the three unobserved outcomes.

The two hook denials are **not** one event: the first happens before any state changes; the second happens
after the rotation has already persisted, so a valid successor token exists that the client never
received (`RefreshSucceededContext`'s own doc says the rotation is not rolled back). `OnRefreshDeniedAsync`
carries `rotationCommitted` so the log distinguishes "nothing happened" from "the token was rotated and
the holder does not know". An investigator reading a bare `REFRESH_DENIED` cannot tell which, and the two
call for opposite responses.

### 10d. `LogoutContext` has no user identity

```csharp
public sealed class LogoutContext(bool allSessions)
```

An audit row can say a logout happened but not whose. `OnLogoutAsync` carries the user id, resolved from
the refresh token before revocation. `LogoutContext` gains a nullable `UserId` for symmetry — additive,
and only Identity constructs it.

### 10e. Lockout raises no event

`UserService.cs:371-375` sets `LockoutEnd` and resets `AccessFailedCount` with no notification. Today an
observer can only infer it from the *next* login attempt returning `LoginFailureReason.LockedOut` — a
different event at a different time. `OnLockedOutAsync` fires where the lockout is applied.

### 10f. IP and user-agent

Not an Identity defect and no Identity change. `Themia.Audit` reads them from `IHttpContextAccessor`
directly, as `Themia.Exceptional`'s `HttpContextEnricher` does. A non-web host records nulls, which is
correct, not an error.

---

## 11. Configuration and DI

```csharp
services.AddThemiaAudit(
    o =>
    {
        o.ConnectionString = cfg.GetConnectionString("Default")!;
        o.Engine           = AuditEngine.Postgres;   // Unspecified = 0 is rejected
        o.Redaction.AddPattern("internal_ref");
    },
    runMigration: true);                             // default

services.AddThemiaAuditIdentityObserver();           // opt-in: leg 4
```

- `AddThemiaAudit` registers the store, dialect, both recorders (§7), the redactor, and
  `IAuditLogService`.
- **The Identity observer is a separate call.** A host without Identity must not be made to reference it,
  and a host that has Identity but does not want auth auditing should not have to opt out of something
  that turned itself on.
- `AuditEngine` is validated with `Enum.IsDefined`, so `Unspecified` fails at startup rather than
  selecting a dialect by accident. Options are validated with `ValidateOnStart`.

**`AddThemiaAudit` runs the schema migration — not `AuditModule`.** §7 explicitly supports a consumer
using `Themia.Audit` plus a dialect with no module at all; if only the module ran the migration, that
consumer would get a working recorder and no table. `Themia.Exceptional` puts the runner in the neutral
core for exactly this reason (`ServiceCollectionExtensions.cs:74`:
`ThemiaMigrations.Run(engine, connectionString, typeof(ExceptionLogMigration).Assembly)`), and references
`Themia.Data.Migrations` from its csproj. `Themia.Audit` does both.

`AuditModule : ThemiaModuleBase` asserts the schema is present and does **not** re-run it.

---

## 12. `IAuditLogService` — the adapter

`Themia.Framework.Services.AuditEvent` stays exactly as shipped. `Themia.Modules.Audit` implements
`IAuditLogService` by mapping onto `AuditEntry`:

```
AuditEvent.EventType     → AuditEntry.EventType
AuditEvent.ActorId       → AuditEntry.ActorId
AuditEvent.ActorName     → AuditEntry.ActorName
AuditEvent.EntityId      → AuditEntry.EntityId
AuditEvent.EntityType    → AuditEntry.EntityType
AuditEvent.OccurredAtUtc → AuditEntry.OccurredAt
AuditEvent.Metadata      → AuditEntry.Data   (JSON, redacted)
                         → Category = Activity, Outcome = Success
```

**Why not widen `AuditEvent` instead:** it lives in `Themia.Framework.Services` (framework) and
`AuditEntry` lives in `Themia.Audit` (neutral). A neutral core cannot reference a framework package. The
adapter is what the layering requires — and the dead seam becomes live with no change to a shipped
public API.

Adopters wanting outcome, reason, IP, or a null actor use `IAuditRecorder` directly. `IAuditLogService`
is the compatibility surface; `IAuditRecorder` is the real one. The README states that hierarchy, so the
overlap is documented rather than two rival APIs.

---

## 13. Dashboard — `Themia.Audit.AspNetCore`

Modelled on `Themia.Exceptional.AspNetCore`: a mountable, self-rendered, read-only UI over
`IAuditStore`, with no external asset dependencies.

`AuditDashboardOptions` mirrors `ExceptionalDashboardOptions` member for member where the meaning
carries, so an adopter who has mounted one dashboard already knows this one:

- **`Authorize` — fail-closed.** `null` denies every request. The dashboard cannot be served without an
  explicit predicate. This is the single most important line in the package: an audit log is a record of
  who did what, and an unauthenticated one is a reconnaissance tool.
- **`OnDenied`** — optional; owns the whole response (e.g. redirect an expired session to login). If it
  throws, the request still fails closed with the route-hiding 404.
- `DefaultPageSize` = 50, `MaxPageSize` = 200, clamping the `pageSize` query parameter.
- `Title`, `Heading`, `CustomStyleSheet`, `CustomFavicon`, `HeadHtml`, `BodyStartHtml` — same semantics
  and the same "not encoded, adopter-authored, use root-relative URLs" warnings.
- **`ShowData`** (default `false`) — whether the detail view renders the `data` column. This is the
  counterpart of Exceptional's `ShowRequestBody`, and it defaults the other way: `data` is
  adopter-supplied payload that redaction filters by *field name*, so a secret in a field the deny-list
  does not name survives to the page. Off by default, on by an explicit decision.

Routes: list (filters from §6's `AuditQuery`) and detail by `id`. No mutation endpoint of any kind.

The dashboard has no unit of work, so it opens its own connection through
`IAuditDialect.CreateConnection` — the same path as leg 4 and `PurgeAsync`. It never participates in an
adopter's transaction; a read must not be able to hold a lock on the audit table while a business
transaction is open.

**Tenant scoping is the adopter's job, through `Authorize`.** The dashboard does not resolve a tenant
from `ITenantContext` — it is mounted by the host, and a host admin viewing all tenants and a tenant
admin viewing their own are both legitimate. The `Authorize` XML doc states this explicitly, because the
failure mode of assuming otherwise is one tenant reading another's audit trail.

---

## 14. Testing

**Neutral core, all three engines, Testcontainers.** Following `SequencesSchemaMigrationTests`, split
onto its own container after the `0.22.0` review: migration tests get one container, behaviour tests
share another. That suite went from 43 containers to 4; do not regress it.

Required:

- Round-trip insert/read on each engine, including every nullable column left null.
- `tenant_id NULL` round-trips as `null`, not `""`.
- **Column-transposition guard.** Every dialect maps its row positionally, and five consecutive nullable
  strings (`actor_id`, `actor_name`, `entity_type`, `entity_id`, `reason`) make a swap invisible. Assert
  each field carries a **distinct** value and lands in its own column. This is the defect that shipped in
  `0.21.4`'s outbox dialects, caught only because SQL Server's `OUTPUT` clause happened to fail loudly.
- Postgres `data` accepts a JSON payload — proves the `jsonb` cast exists.
- Table name is unqualified and **identical on all three engines** — the §5 blocker, asserted rather than
  reviewed.
- Migration replay: `Up()` twice, second is a no-op; and against a pre-existing table.
- Unsupported provider throws at migrate time.
- **Redaction proven able to fail:** remove redaction, observe the secret in the row, restore. A
  redaction test over a payload containing no secret proves nothing.
- Enum validation rejects `Unspecified` and out-of-range values.
- **Length limits reject, and reject on every engine.** An over-length `event_type` throws on all three —
  in particular on MySQL, where the failure mode without validation is a silent truncation, not an error.
  `user_agent` over 512 is truncated and written, not rejected.
- A payload the caller passes as a plain object round-trips as valid JSON on all three engines. This is
  the assertion that stops someone "improving" the column back to `jsonb`.
- **A payload whose secret sits in a non-dictionary shape is still redacted** — nested objects, arrays of
  objects, and a deeply nested match. Redaction over a flat one-level object proves only the easy case.
- `WriteAsync` returns a monotonically increasing `id` across successive inserts on every engine, and
  `GetAsync(eventUid)` finds the row — the identity/uid split works both ways.
- Paging is stable across pages when many rows share one `occurred_at` — the §6 `id` tiebreak.

**Module:**

- `RequireTransaction` with no ambient transaction **throws**, and the message names
  `ExecuteInTransactionAsync`. Assert the message, not just the type — the message is the fix.
- `RequireTransaction` inside `ExecuteInTransactionAsync`: row absent after rollback, present after
  commit. **Both EF and Dapper.**
- `JoinIfPresent` with no transaction writes durably and does **not** throw.
- Leg 4 writes survive a surrounding rollback.
- An observer that throws does not change the login result — same status code and same response body as
  an unobserved flow.
- `IAuditLogService` resolves from the real DI graph produced by `AddThemiaAudit`, not a hand-built
  container. `0.19.0`'s notification-provider defect passed unit tests and failed on the real default
  graph.
- Registering the Identity observer alongside an adopter's own `IAuthenticationHooks` leaves **both**
  running — and the same test for `IExternalAuthenticationHooks`. This is §10a's defect; it needs a test,
  not just a design.
- **Every Identity path that can produce an event produces one.** One test per row of §10b's and §10c's
  tables, plus password login and lockout: external login succeeded / failed / denied, refresh
  `ReuseDetected` / `Invalid` / inactive-account / denied-before / denied-after. A partial sweep here
  recreates exactly the gap this revision exists to close.
- `OnRefreshDeniedAsync` reports `rotationCommitted = false` for the pre-rotation denial and `true` for the
  post-rotation one. Asserting only that the event fired would pass with the flag inverted.

**Dashboard:**

- `Authorize = null` returns 404 for every route, including detail with a valid id.
- An `Authorize` that throws returns 404, never 500 and never content.
- `ShowData = false` omits the `data` field from the rendered detail page — assert the payload string is
  absent from the response body, not that a flag was read.
- `pageSize` above `MaxPageSize` is clamped, not honoured.
- The detail route addresses rows by `event_uid`; a sequential `id` in the URL does not resolve.

**Tenant reads:**

- `ITenantAuditReader` returns only the ambient tenant's rows, and returns none when asked for a
  different tenant — assert on a database holding rows for two tenants plus host-level rows.
- `IAuditStore.QueryAsync` with an empty `AuditQuery` returns rows from **both** tenants. This asserts
  the documented fail-open behaviour rather than leaving it to be discovered.

**Migration ownership:**

- `AddThemiaAudit(runMigration: true)` with no module registered creates the table — the neutral-only
  consumer path.
- `AddThemiaAudit(runMigration: false)` does not, and `AuditModule` then fails with a message naming the
  missing table rather than a raw SQL error.

**PublicAPI:** every new public member enters `PublicAPI.Unshipped.txt`. A clean `--no-incremental`
build must report no `RS0016`.

---

## 15. Release 2 — entity change log (`0.24.0`, specced separately)

Recorded so release 1 is not designed in a way that blocks it.

**Shape:** one `themia_audit_events` row (`Category = EntityChange`) owning N `themia_audit_changes` rows
(`field_name`, `old_value`, `new_value`), keyed by `audit_event_id`. Release 1's table needs no change.

**Opt-in, never opt-out.** Auditing everything means auditing `refresh_tokens`, the notification outbox,
and `themia_sequences` — a volume problem and a secret-disclosure problem at once. An entity opts in by
marker interface or attribute.

**The unresolved fork — EF/Dapper parity.** EF's `ChangeTracker` holds original values, so capture is
free. `DapperUnitOfWork` sees only the incoming entity (`op.Entity`) and has no prior state. Three
options, none chosen:

1. Read-before-write on the Dapper path, for audited entities only. Preserves parity; costs one SELECT
   per audited update.
2. Dapper records new values only. Asymmetric, and a reader cannot tell whether a null `old_value` means
   "unchanged" or "this layer cannot know".
3. Auto change log is EF-only, documented and enforced at startup.

DECISION #6 makes EF and Dapper first-class peers, so option 3 needs an explicit ruling *against* that
decision rather than a silent gap. **The choice belongs to release 2's spec and is not made here.**

---

## 16. Decisions — do not relitigate

1. Three legs; legs 3+4 in `0.23.0`, leg 2 in `0.24.0`.
2. **`themia_audit_events` is unqualified on every engine. Never `InSchema(...)`.**
3. Leg 3 defaults to `RequireTransaction` and **throws** when none is open. No silent degradation.
   Leg 4 is `Never` and not configurable.
4. `IAuditStore` never opens connections; the caller supplies one. One method, no mode flag.
   `IAuditDialect.CreateConnection` is where every non-transactional caller gets one — leg 4, the
   dashboard, `PurgeAsync`.
4b. Two recorders: neutral `AuditRecorder` (validate + redact + store) wrapped by
   `TransactionalAuditRecorder` in the module. Redaction lives in the **inner** one, so leg 4 cannot
   bypass it.
4c. `id` is a `bigint identity` clustered PK; `event_uid` is the public `Guid` under a unique index —
   following `ExceptionLogMigration`. A random Guid clustered key page-splits an append-only table.
5. `IAmbientConnectionAccessor` lives in `Themia.Framework.Data.Abstractions`, implemented per data
   layer — inside THEMIA103's exemption, so no analyzer rule is weakened.
6. The EF-tracked-entity alternative is rejected: it would force `Themia.Audit` onto every EF adopter.
7. Redaction is unconditional, on the inner recorder, deny-list additive-only.
7b. `data` is `AsString(int.MaxValue)` on every engine — never `jsonb`/`JSON`. Adopter-supplied payload
   must not be able to fail the transaction it describes, on any subset of engines.
7d. `IAuditRecorder.RecordAsync` takes an **object** and serializes it. No caller supplies a `Data`
   string, so "well-formed JSON" holds by construction and the redactor always has parseable input.
7c. Bounded columns have constants on `AuditEntry` bound to the migration. Adopter-named fields are
   **rejected** when over-length; framework-captured ones are truncated.
8. `TenantId` is nullable; `null` is host-level; the recorder never invents a tenant.
8b. **Reads are unfiltered in the neutral store by design** (it cannot see `ITenantContext`).
   `ITenantAuditReader` in the module is the tenant-scoped path; the dashboard scopes through
   `Authorize`.
9. `AuditEvent` is not modified. `IAuditLogService` is an adapter over `IAuditRecorder`.
10. `IIdentityEventObserver` is a new fan-out seam; `IAuthenticationHooks` and
    `IExternalAuthenticationHooks` stay single-registration and keep `Deny()`.
10b. Leg 4 covers **every** authentication path Identity ships: password, external (Google/LINE), all
    five refresh outcomes, logout, lockout, and user mutation. Not a subset.
11. All enums reserve `0` for `Unspecified`, validated with `Enum.IsDefined`.
12. The dashboard is read-only and fail-closed. `ShowData` defaults to `false`.
13. `Themia.Audit` references no `Themia.Framework.*` package; asserted by a test. ASP.NET is permitted
    in the neutral core, as in `Themia.Exceptional`.
14. `AddThemiaAudit` runs the schema migration, not `AuditModule` — a neutral-only consumer must still
    get a table.
15. No retention job and no Serenity adapter in `0.23.0`.
