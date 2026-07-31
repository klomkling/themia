# Themia.Messaging — outbox/inbox persistence (coord #0050, step 2b)

**Date:** 2026-07-31
**Status:** approved, ready for an implementation plan
**Target version:** unreleased — no version pinned yet
**Request:** coord #0050, from `propertiezy`

## Why

#0050 asked for a generic inter-service messaging framework: outbox, inbox deduplication with version
fencing, HMAC transport, and a loop guard. The premise that the outbox was greenfield was wrong —
`Themia.Modules.Notifications` already shipped a working transactional outbox on three engines, so
**step 1 extracted that machinery into the neutral `Themia.Messaging`** (`IOutboxDialect<TRow>`,
`OutboxDrainer<TRow>`, `IOutboxDispatcher<TRow>`, `BackoffPolicy`, `DrainSignal`) and refactored
Notifications onto it. **Step 2 added the generic contracts** — `MessageEnvelope`, `ClaimedMessageRow`,
`IMessageOutboxStore`, `IInboxStore`, `InboxAdmission`.

This spec covers **step 2b: the persistence those contracts need.** HMAC transport (step 3) and the
loop guard + DI surface (step 4) are out of scope here.

## Scope

**In:** the `messaging` schema and its three tables; admission semantics; the per-engine fence upsert;
a batched purge with a scheduled job; retrofitting that purge onto the Notifications outbox.

**Out:** HMAC signing/verification, the loop guard, `AddThemiaMessaging`, the Quartz dispatcher wiring.

## Schema

Schema name `messaging`, mirroring `notifications`: snake_case identifiers, one FluentMigrator
migration with `IfDatabase(...)` per engine for datetime types, `Down()` dropping in reverse order.
No module uses `dotnet ef migrations add` — FluentMigrator owns DDL for both data layers.

### `messaging.outbox_messages`

Envelope fields plus the same lifecycle columns the notifications outbox already proves:

| column | type | null | note |
|---|---|---|---|
| `id` | guid | no | PK, row identity |
| `message_id` | guid | no | stable across retries; what the receiver dedups on |
| `tenant_id` | varchar(100) | yes | plain data, not part of any key |
| `type` | varchar(200) | no | logical message type |
| `payload` | text | no | opaque; never inspected by the framework |
| `destination` | varchar(100) | no | logical peer name |
| `origin` | varchar(100) | no | originating system, not last hop |
| `entity_key` | varchar(200) | yes | what the fence scopes within |
| `version` | bigint | yes | monotonic within its stream |
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

The unique constraint is deliberate: the same logical message fanned out to two peers legitimately
shares a `message_id` — each receiver dedups on `(origin, message_id)` independently — but the same
message enqueued twice for the *same* destination is a double-publish bug, and this catches it at the
database rather than at the far end.

### `messaging.inbox_messages`

| column | type | null |
|---|---|---|
| `origin` | varchar(100) | no (PK) |
| `message_id` | guid | no (PK) |
| `tenant_id` | varchar(100) | yes |
| `type` | varchar(200) | no |
| `received_at` | datetime | no |

`PK (origin, message_id)` **is** the deduplication guarantee — admission is an insert-if-not-exists
against it. Keyed on origin as well as id so two peers can never collide on an identifier either of
them generated independently. Index on `received_at` for the purge.

### `messaging.inbox_watermark`

| column | type | null |
|---|---|---|
| `tenant_id` | varchar(100) | **no**, default `''` (PK) |
| `origin` | varchar(100) | no (PK) |
| `type` | varchar(200) | no (PK) |
| `entity_key` | varchar(200) | no (PK) |
| `version` | bigint | no |
| `updated_at` | datetime | no |

One row per tracked entity-stream, holding the newest version applied. This table is **never purged**:
it grows with the number of entities, not the number of messages, so 1M messages about 5k listings is
5k rows.

**`tenant_id` is in the key** because omitting it is cross-tenant data loss, not a preference: tenant A's
`listing-42` and tenant B's `listing-42` would share one mark, and B's version 3 would silently suppress
A's version 2 — rejected as ordinary staleness, invisible in logs. ezy-assets is multi-tenant, so this
is live rather than theoretical.

**Why `NOT NULL DEFAULT ''` rather than nullable.** No engine permits NULL in a primary key. The obvious
alternative — surrogate `id` PK plus a unique index over the four columns — is worse, because unique-index
NULL semantics diverge across exactly the three engines in scope:

| engine | multiple NULLs in a unique index |
|---|---|
| PostgreSQL | allowed → constraint silently enforces nothing |
| MySQL | allowed → constraint silently enforces nothing |
| SQL Server | rejected → constraint works |

On two of three engines the fence would appear enforced and quietly not be. A sentinel is ugly and
portable and fails loudly; the store maps `null ↔ ''` at the boundary so callers never see it. This
applies to the watermark table only — outbox and inbox keep nullable `tenant_id` as plain data.

## Admission semantics

`IInboxStore.TryAdmitAsync` **stages into the caller's unit of work.** It does not own a connection.

This is not a stylistic choice. With admission on its own connection, the sequence is:

1. admission records `message_id` → committed
2. the application applies the payload → **crash**
3. the peer redelivers → `Duplicate` → dropped

The message is permanently lost and it looks like correct deduplication. Joining the caller's
transaction means the admission record and the state change commit together or not at all — the mirror
image of what `IOutboxStore` already does on the send side.

Consequence: the implementation lives in `Themia.Modules.Messaging` over `Framework.Data`, not in a
standalone dialect with its own connection. The `IInboxStore` contract committed in `7b1d00b` is
unchanged in shape; only its transactional obligation is now explicit.

Admission is two steps inside that transaction:

1. **Dedup** — insert-if-not-exists on `(origin, message_id)`. Already present → return `Duplicate` and
   leave the watermark untouched.
2. **Fence** — conditional watermark upsert. Version not greater than the mark → return `Stale`.

A stale message still gets its id recorded and returns `Stale`, so the application skips it and any
later redelivery answers `Duplicate`. A message with no `EntityKey`/`Version` skips step 2 entirely and
relies on deduplication alone.

All three outcomes are answered **2xx** to the sender: retrying cannot change the verdict, so a non-2xx
would only produce pointless redelivery.

### Fence upsert, per engine

The upsert must report whether it moved the mark. That reporting is what diverges:

| engine | mechanism | stale signal |
|---|---|---|
| PostgreSQL | `ON CONFLICT (...) DO UPDATE ... WHERE watermark.version < EXCLUDED.version RETURNING 1` | no row returned |
| MySQL | `ON DUPLICATE KEY UPDATE version = IF(VALUES(version) > version, VALUES(version), version)` | `ROW_COUNT() = 0` |
| SQL Server | `UPDATE ... WHERE ... AND version < @v`, then conditional `INSERT` | `@@ROWCOUNT = 0` on both |

**SQL Server deliberately does not use `MERGE`.** It is the obvious fit and a known footgun: not race-free
without `HOLDLOCK`, with a long history of deadlocks and correctness advisories under concurrency.
Update-then-insert with a unique-violation catch on the insert race is duller and correct.

## Purge

Two interfaces, not one, so each implementor is only asked for what it has:

- **`IOutboxPurgeDialect`** — `PurgeSentAsync`, `PurgeDeadAsync`. Implemented by messaging *and* by
  Notifications.
- **`IInboxPurgeDialect`** — `PurgeAdmittedAsync`. Implemented by messaging only; Notifications has no
  inbox and must not be forced to stub one.

Both are driven by a scheduled job over `Themia.Quartz`, defaulting to daily. The watermark is never
purged.

Two windows, because the rows are not equally valuable:

| rows | default window | rationale |
|---|---|---|
| `sent` | 7d | high volume, audit trail only |
| `dead` | 90d | rare; each is an unresolved delivery failure someone may still need |
| inbox ids | 30d | must exceed any redelivery age the outbox can produce |

Both outbox windows and the inbox window are configurable.

The inbox window has a stated gap: a redelivery older than 30 days is reprocessed as new. The outbox
itself abandons a message within roughly an hour (`MaxAttempts` 5, backoff capped at 15 minutes), so only
a replay or a database restore could reach that far — and for **versioned** messages the watermark still
rejects it, because the watermark never forgot. Non-versioned events (leads, #0040) have the window alone.

**Deletes are batched.** An unbounded `DELETE` on a large outbox holds long locks and bloats the table;
each method deletes at most N rows and the job loops until it returns 0. `DELETE ... LIMIT n` on MySQL,
`DELETE TOP (n)` on SQL Server, `WHERE ctid IN (SELECT ctid ... LIMIT n)` on PostgreSQL.

### Notifications retrofit

`Themia.Modules.Notifications` has the same unbounded growth: its migration comments the outbox row as
*"purged, not tombstoned"* (`NotificationsSchemaMigration.cs:52`) but no purge was ever written. Since the
drainer is now shared, the purge is retrofitted here rather than left as a known defect:

- three `IOutboxPurgeDialect` implementations against `notifications.outbox_messages` (not
  `IInboxPurgeDialect` — Notifications has no inbox)
- a **new** migration adding the `(status, sent_at)` index — forward-only; the deployed
  `NotificationsSchemaMigration` is not edited
- the misleading comment corrected

**The purge defaults differ between the two, deliberately.** Messaging defaults **on** — greenfield, no
history to lose. Notifications defaults **off**, opt-in via options, because ezy-assets runs it in
production and a purge enabled by default would silently delete every historical `sent` row on the first
run after a version bump. A data-destroying change must be something an adopter turns on, not something a
patch release does to them. The changelog must say so explicitly.

## Layering

| package | TFM | holds |
|---|---|---|
| `Themia.Messaging` | net10.0 | envelope, contracts, dialect interfaces, drainer, backoff, purge contract |
| `Themia.Messaging.{PostgreSql,MySql,SqlServer}` | net10.0 | claim/complete/fail, fence upsert, purge SQL |
| `Themia.Modules.Messaging` | net10.0 | `IThemiaModule`, repository-backed stores over `Framework.Data`, migration, DI, purge job |

net10-only for now. propertiezy answered `net8.0;net10.0`, and that was reversed with reasons on the
record in #0050: the net8 leg would force a second ADO enqueue path for a consumer that does not exist,
since both roadmap adopters are already net10. Adding the leg later is purely additive.

## Testing

- **Unit** — envelope validation; purge window arithmetic; admission decision table over a faked store.
- **Integration (Testcontainers, per engine)** — the semantics that only a real engine proves:
  - concurrent `TryAdmitAsync` of the same `(origin, message_id)` admits exactly once
  - out-of-order versions: v7 then v5 leaves the mark at 7 and returns `Stale`
  - fence is scoped per `(tenant, origin, type, entity_key)` — tenant B cannot suppress tenant A
  - admission rolls back with the caller's transaction (crash between admit and apply loses neither)
  - batched purge deletes only rows past the window and terminates
  - existing outbox claim-concurrency and dead-letter tests stay green

The tenant-isolation and rollback tests are the load-bearing ones: both failures are silent in
production.

## Open questions

**Fence semantics are designed ahead of propertiezy's answer.** #0050 asked whether one monotonic version
per `(origin, entity-key)` suffices, or whether streams need different semantics. This spec assumes one
monotonic `bigint` per `(tenant, origin, type, entity_key)`, with the application owning what the number
means. If they answer differently, the watermark key or comparison changes — the rest of the spec does
not.

**Who generates the version** is the application's business. #0026 established that `sentAt` must be
DB-generated because it is a load-bearing staleness fence; an application may reuse that value here, but
the framework only compares `bigint`s and takes no position.

## Decisions taken

1. Separate watermark per message **type**, so a price update cannot suppress a snapshot.
2. `tenant_id` in the watermark key — cross-tenant suppression is data loss, not staleness.
3. `NOT NULL DEFAULT ''` over a nullable unique index — portable and fails loudly.
4. Admission joins the caller's UoW — otherwise a crash between admit and apply loses the message forever.
5. No `MERGE` on SQL Server.
6. Batched deletes.
7. Purge on by default for messaging, **off** by default for Notifications.
8. Shared code, separate tables — no shipped, populated table is rewritten across three engines.
