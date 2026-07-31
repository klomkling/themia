# Themia.Messaging — outbox/inbox persistence (coord #0050, step 2b)

**Date:** 2026-07-31 (rev 2 — watermark dropped, admission scoped to the Dapper peer, purge moved into
the drain loop)
**Status:** approved, ready for an implementation plan
**Target version:** unreleased — no version pinned yet
**Request:** coord #0050, from `propertiezy`

## Why

#0050 asked for a generic inter-service messaging framework. The premise that the outbox was greenfield
was wrong — `Themia.Modules.Notifications` already shipped a working transactional outbox on three
engines — so **step 1** extracted that machinery into the neutral `Themia.Messaging`
(`IOutboxDialect<TRow>`, `OutboxDrainer<TRow>`, `IOutboxDispatcher<TRow>`, `BackoffPolicy`,
`DrainSignal`) and refactored Notifications onto it. **Step 2** added the generic contracts
(`MessageEnvelope`, `ClaimedMessageRow`, `IMessageOutboxStore`, `IInboxStore`, `InboxAdmission`).

This spec covers **step 2b: the persistence those contracts need.** HMAC transport (step 3) and the loop
guard + DI surface (step 4) are out of scope.

## What rev 2 changed, and why

Rev 1 proposed a framework-owned `inbox_watermark` table implementing a version fence. Review killed it:

- **The fence duplicates state the application already holds.** The same guarantee is one clause in the
  upsert the app is already writing — `... WHERE version < @v` against its own entity row. That is
  *more* durable than a watermark, because an entity's version survives as long as the entity does,
  whereas the watermark was to be protected from a purge window that never applied to it anyway.
- **It was the highest-risk surface in the spec** — three per-engine conditional upserts, each with a
  different stale-detection mechanism (`RETURNING`, `ROW_COUNT()`, `@@ROWCOUNT`) — built to duplicate a
  `WHERE` clause.
- It also forced a `NOT NULL DEFAULT ''` sentinel to dodge the fact that no engine allows NULL in a
  primary key, and that the surrogate-key alternative silently enforces nothing on PostgreSQL and MySQL.

**The fence is now the application's job.** The framework carries `EntityKey`/`Version` on the envelope
so the value reaches the receiver, and stops there.

The residual gap is **hard-deleted** entities: delete the row and its version dies with it, so a replayed
create could resurrect it. Soft deletes (`deleted_at`, per the repo's database standards) close this,
because the row keeps its version. If a genuine hard-delete case appears, a watermark can be added then
as an **opt-in** table — not as the default path for every adopter.

## Scope

**In:** the `messaging` schema and its two tables; admission semantics; purge, retrofitted onto the
Notifications outbox.

**Out:** version fencing (now application-side), HMAC signing/verification, the loop guard,
`AddThemiaMessaging`, the Quartz dispatcher wiring.

## Schema

Schema name `messaging`, mirroring `notifications`: snake_case identifiers, one FluentMigrator migration
with `IfDatabase(...)` per engine for datetime types, `Down()` dropping in reverse order. No module uses
`dotnet ef migrations add` — FluentMigrator owns DDL for both data layers.

### `messaging.outbox_messages`

Envelope fields plus the lifecycle columns the notifications outbox already proves:

| column | type | null | note |
|---|---|---|---|
| `id` | guid | no | PK, row identity |
| `message_id` | guid | no | stable across retries; what the receiver dedups on |
| `tenant_id` | varchar(100) | yes | plain data, not part of any key |
| `type` | varchar(200) | no | logical message type |
| `payload` | text | no | opaque; never inspected by the framework |
| `destination` | varchar(100) | no | logical peer name |
| `origin` | varchar(100) | no | originating system, not last hop |
| `entity_key` | varchar(200) | yes | carried for the receiver's own fence |
| `version` | bigint | yes | carried for the receiver's own fence |
| `headers` | text | yes | JSON; never credentials |
| `status` | int | no | pending / sending / sent / failed / dead |
| `attempts` | int | no | |
| `next_attempt_at` | datetime | no | |
| `scheduled_for` | datetime | yes | future-dated sends |
| `lease_owner` | varchar(100) | yes | |
| `lease_expires_at` | datetime | yes | |
| `created_at` | datetime | no | |
| `sent_at` | datetime | yes | |
| `last_error` | text | yes | |

Indexes: `(status, next_attempt_at)` for the claim (mirrors notifications), `(status, sent_at)` for the
purge, `(tenant_id)`, and **unique `(message_id, destination)`**.

The unique constraint is deliberate: the same logical message fanned out to two peers legitimately shares
a `message_id` — each receiver dedups on `(origin, message_id)` independently — but the same message
enqueued twice for the *same* destination is a double-publish bug, caught at the database rather than at
the far end.

### `messaging.inbox_messages`

| column | type | null | note |
|---|---|---|---|
| `origin` | varchar(100) | no | PK |
| `message_id` | guid | no | PK |
| `tenant_id` | varchar(100) | yes | data only |
| `type` | varchar(200) | no | |
| `received_at` | datetime | no | **database-generated**; drives the purge |

`PK (origin, message_id)` **is** the deduplication guarantee — admission is an insert-if-not-exists
against it. Keyed on origin as well as id so two peers can never collide on an identifier either of them
generated independently. Index on `received_at` for the purge.

`received_at` defaults to the database clock (`now()` / `GETUTCDATE()`), not an app-server clock. A
skewed-fast app clock would leave rows that never purge; skewed-slow would delete dedup records early and
let duplicates through. This follows the precedent of #0026, where `sentAt` was made DB-generated for
exactly this reason. The `IInboxStore` parameter is retained for tests only.

## Admission semantics

`IInboxStore.TryAdmitAsync` records the message and reports whether the caller should process it:
`Accepted` (first sight) or `Duplicate`. There is no `Stale` — staleness is now the application's fence.

**Two obligations on the caller, both load-bearing:**

1. **Admit before applying.** An application that applies the payload first and admits afterwards gets no
   protection at all. This is stated on the interface, not merely implied.
2. **Admission commits with the state change.** `TryAdmitAsync` participates in the caller's transaction.

The second is what prevents permanent message loss. With admission on its own connection:

1. admission records `message_id` → committed
2. the application applies the payload → **crash**
3. the peer redelivers → `Duplicate` → dropped

The message is gone forever and it looks like correct deduplication. Sharing the transaction means the
admission record and the state change commit together or not at all — the mirror image of what
`IOutboxStore` already does on the send side.

### Admission is Dapper-peer-only in v1

This is a real constraint, stated rather than discovered during implementation.

Admission needs an insert-if-not-exists that reports whether it inserted. `IRepository<T,TKey>.AddAsync`
cannot express that, so raw SQL on the caller's ambient transaction is unavoidable. The repo offers
exactly one way to reach an ambient transaction — `IDapperConnectionContext.CurrentTransaction` — and:

- `Themia.Framework.Data.EFCore` exposes **no** connection or transaction access at all
- `IUnitOfWork` exposes only `SaveChangesAsync` / `BeginTransactionAsync` / `ExecuteInTransactionAsync`

So there is no peer-agnostic mechanism today. Rather than invent framework surface speculatively, v1
ships inbox admission on the **Dapper peer only**, using `IDapperConnectionContext` inside the data-layer
boundary where `RawConnectionBypassAnalyzer` permits it — the same pattern
`Themia.Modules.Pdf.Store.DapperPdfTemplateStore` already uses. Both adopters on #0050's roadmap are on
Dapper/PostgreSQL per #0039, so nothing is blocked.

Registering the inbox on an EF peer must **fail fast at startup** with a message naming the limitation —
never silently degrade to a non-transactional admission, which would reintroduce the loss window.

Adding EF support later means a narrow ambient-transaction accessor on `Framework.Data.Abstractions`
implemented by both peers. That is additive and can wait for a consumer that needs it.

**Outbox enqueue is unaffected** and stays peer-agnostic: it is an ordinary repository insert staged into
the caller's unit of work, exactly as the notifications outbox does today.

## Purge

Two interfaces, so each implementor is only asked for what it has:

- **`IOutboxPurgeDialect`** — `PurgeSentAsync`, `PurgeDeadAsync`. Implemented by messaging *and* by
  Notifications.
- **`IInboxPurgeDialect`** — `PurgeAdmittedAsync`. Messaging only; Notifications has no inbox and must
  not be forced to stub one.

### Driven by the drain loop, not a scheduler

`OutboxDrainer.ExecuteAsync` already runs a poll loop on `DrainIntervalSeconds` and already holds an open
connection and a dialect. The purge runs from there, when `now - lastPurge` exceeds the configured
interval (default daily).

The alternative — a Quartz job — would add a **new package dependency to a shipped module**:
`Themia.Modules.Notifications` currently references only `Themia.Notifications`, `Framework.Core`,
`Data.Abstractions`, `Data.EFCore`, `Data.Dapper` and `Data.Migrations`. Every adopter would inherit
Quartz and a scheduler they may not run, to delete rows on a timer. Out-of-band scheduling stays possible
for adopters who want it, by disabling the in-loop purge and calling the same dialect methods.

### Windows

| rows | default | rationale |
|---|---|---|
| `sent` | 7d | high volume, audit trail only |
| `dead` | 90d | rare; each is an unresolved delivery failure someone may still need |
| inbox ids | 30d | must exceed any redelivery age the outbox can produce |

All configurable. The inbox window has a stated gap: a redelivery older than it is reprocessed as new.
With default settings the outbox abandons a message within roughly an hour (`MaxAttempts` 5, backoff
capped at 15 minutes), so only a replay or a database restore reaches that far — but both are
configurable, and an adopter who raises `MaxAttempts` substantially must raise the inbox window with it.

**Deletes are batched.** An unbounded `DELETE` on a large outbox holds long locks and bloats the table;
each method deletes at most N rows and the loop repeats until it returns 0. `DELETE ... LIMIT n` on
MySQL, `DELETE TOP (n)` on SQL Server, `WHERE ctid IN (SELECT ctid ... LIMIT n)` on PostgreSQL.

### Notifications retrofit

`Themia.Modules.Notifications` has the same unbounded growth: its migration comments the outbox row as
*"purged, not tombstoned"* (`NotificationsSchemaMigration.cs:52`) but no purge was ever written. Since the
drainer is now shared, the purge is retrofitted here rather than left as a known defect:

- three `IOutboxPurgeDialect` implementations against `notifications.outbox_messages` (not
  `IInboxPurgeDialect` — Notifications has no inbox)
- a **new** migration adding the `(status, sent_at)` index — forward-only; the deployed
  `NotificationsSchemaMigration` is not edited
- the misleading comment corrected

**Purge defaults differ between the two, deliberately.** Messaging defaults **on** — greenfield, no
history to lose. Notifications defaults **off**, opt-in via options, because ezy-assets runs it in
production and a purge enabled by default would silently delete every historical `sent` row on the first
run after a version bump. A data-destroying change must be something an adopter turns on, not something a
patch release does to them. The changelog must say so explicitly.

## Contract delta from step 2

The contracts committed in `7b1d00b` need three edits, all pre-release:

1. `InboxAdmission` loses `Stale` — the framework no longer fences, so it can never return it.
2. `IInboxStore.TryAdmitAsync` loses its `entityKey` and `version` parameters.
3. `IInboxStore` gains the admit-before-apply and shared-transaction obligations in its documentation.

`MessageEnvelope.EntityKey`/`Version` are **unchanged** — they still travel so the receiver's own fence
can use them, and `Validate()` still rejects a `Version` with no `EntityKey`.

## Layering

| package | TFM | holds |
|---|---|---|
| `Themia.Messaging` | net10.0 | envelope, contracts, dialect interfaces, drainer, backoff, purge contracts |
| `Themia.Messaging.{PostgreSql,MySql,SqlServer}` | net10.0 | claim/complete/fail, admission, purge SQL |
| `Themia.Modules.Messaging` | net10.0 | `IThemiaModule`, repository-backed outbox store, migration, DI |

net10-only for now. propertiezy answered `net8.0;net10.0`; that was reversed with reasons on the record in
#0050 — the net8 leg would force a second ADO enqueue path for a consumer that does not exist, since both
roadmap adopters are already net10. Adding the leg later is purely additive.

## Testing

- **Unit** — envelope validation; purge window arithmetic; admission decision table over a faked store.
- **Integration (Testcontainers, per engine)** — the semantics only a real engine proves:
  - concurrent `TryAdmitAsync` of the same `(origin, message_id)` admits exactly once
  - admission rolls back with the caller's transaction: roll back after admitting, redeliver, and the
    message must be `Accepted` again rather than swallowed as a duplicate
  - registering the inbox on an EF peer fails at startup rather than degrading
  - batched purge deletes only rows past the window and terminates
  - `received_at` is set by the database when the caller passes nothing
  - existing outbox claim-concurrency and dead-letter tests stay green

The rollback test is the load-bearing one — that failure is silent in production and costs a message.

## Open questions

**propertiezy's fence answer is now largely moot.** #0050 asked whether one monotonic version per
`(origin, entity-key)` suffices. With fencing moved application-side, the framework takes no position;
what remains is to tell them the fence is theirs to implement, and that `EntityKey`/`Version` are carried
for exactly that purpose. Worth confirming they are content with that split before implementation.

## Decisions taken

1. No framework version fence — it duplicates the application's own `WHERE version < @v` and is less
   durable. Revisit only for a real hard-delete case, as opt-in.
2. Admission joins the caller's transaction; **Dapper peer only** in v1, failing fast on EF.
3. Admit-before-apply is a stated contract obligation, not an assumption.
4. `received_at` is database-generated, per #0026.
5. Purge runs in the drain loop — no new package dependency on a shipped module.
6. Batched deletes.
7. Purge on by default for messaging, **off** by default for Notifications.
8. Shared code, separate tables — no shipped, populated table is rewritten across three engines.
