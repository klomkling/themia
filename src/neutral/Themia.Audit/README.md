# Themia.Audit

Framework-neutral, append-only audit event log: a record model with validation and unconditional
redaction, a Dapper store behind a per-engine dialect strategy, and the FluentMigrator schema.

`net8.0;net10.0`. No `Themia.Framework.*` dependency — asserted by a test, not left to convention.

## What this package is for

Two kinds of event share one table and one write path:

- **Activity** — business events an application chooses to record.
- **Authentication / user lifecycle** — login, logout, refresh, lockout and credential changes,
  recorded for the application by `Themia.Modules.Audit` without it writing any code.

An **entity change log** — per-row `CREATE`/`UPDATE`/`DELETE` with field-level old→new values — is a
different shape and is **not** in this package. It is planned for `0.24.0`.

## Install

```bash
dotnet add package Themia.Audit
dotnet add package Themia.Audit.PostgreSql   # or .SqlServer / .MySql
```

```csharp
services.AddThemiaAudit(o =>
{
    o.ConnectionString = cfg.GetConnectionString("Default")!;
    o.Engine           = AuditEngine.Postgres;
});
services.AddThemiaAuditPostgreSql();
```

`AddThemiaAudit` **runs the schema migration** by default, so this package works without the module
layer. Pass `runMigration: false` to defer it. Calling it with `runMigration: true` and an unset
`Engine` or empty `ConnectionString` throws immediately and names the missing setting — a requested
migration that silently does not run would surface much later as a missing-table error at the first
write.

## Recording an event

```csharp
await recorder.RecordAsync(
    new AuditEntry
    {
        EventType = "PROPOSAL_ACCEPTED",
        Category  = AuditCategory.Activity,
        Outcome   = AuditOutcome.Success,
        ActorId   = currentUser.Id,
        EntityType = nameof(Proposal),
        EntityId  = proposal.Id.ToString(),
    },
    payload: new { proposal.Status, proposal.Amount });
```

**You pass an object, never a JSON string.** The recorder serializes it and redacts it, so `data` is
well-formed JSON by construction and the redactor always has something it can walk. `AuditEntry.Data`
is `internal init` precisely so this cannot be bypassed.

## Redaction

Unconditional, on the single write path — not an option and not a helper you can forget. Property
names matching the deny-list have their values replaced with `[redacted]` at any depth, including
inside nested objects and arrays. The property name is kept: that a password field was present is
itself audit-relevant.

```
password, passwordhash, passwordsalt, secret, token, refreshtoken, accesstoken,
apikey, api_key, authorization, otp, pin, cvv, creditcard, card_number, privatekey, ssn
```

`AuditRedactionOptions.AddPattern` adds to that list and cannot remove from it. Use
`IsSensitive(name)` to test membership; there is deliberately no public collection to enumerate,
because no shape of one was safe to expose.

An adopter who genuinely needs a default gone supplies their own `IAuditRedactor` — a conspicuous act
rather than a config line.

## Storage

One table, `themia_audit_events`, **unqualified and identical on every engine**. Never
`InSchema(...)`: FluentMigrator drops it on MySQL, where "schema" and "database" are the same concept,
so a qualified name means something different per engine — which is how two Themia modules once ended
up with colliding table names there.

`data` is `nvarchar(max)`/text on every engine, never `jsonb` or `JSON`. Those types validate on
insert, and an adopter-supplied payload must never be able to fail the transaction it describes on
some engines but not others.

`id` is a `bigint identity` clustered key; `event_uid` is the public `Guid`. A random Guid clustered
key page-splits an append-only table on every insert.

Bounded columns have constants on `AuditEntry` (`MaxEventTypeLength` and siblings) bound to the
migration. Fields an adopter names — `EventType`, `ActorId`, `EntityType`, `EntityId` — are
**rejected** when over-length rather than truncated, because truncating them merges two distinct
events into one. Fields the framework captures — `UserAgent`, `IpAddress` — are truncated, because a
clipped user-agent is still the same event.

## Reads: the query surface lives here, not in the module

**`IAuditStore.QueryAsync` is defined and implemented in `Themia.Audit` (this package) — not in
`Themia.Modules.Audit`.** You can query without taking the module at all: construct an `AuditQuery`
and call `QueryAsync` against any `IAuditStore` your engine package registers.

`QueryAsync` applies **no tenant predicate unless `AuditQuery` asks for one**, because this package is
framework-neutral and cannot see `ITenantContext`. `AuditQuery` has two independent knobs for this,
not one:

- **`AuditQuery.TenantId = null`** (the default) means **no filter** — rows for every tenant *and*
  every host-level row (see below) come back. This is not "tenant-locked"; it is the widest read the
  store can do.
- **`AuditQuery.TenantId = "<id>"`** narrows to that one tenant's rows only.
- **`AuditQuery.HostLevelOnly = true`** narrows to rows where the stored `AuditEntry.TenantId IS NULL`
  — the single-org case, where every row belongs to "the org" and there is no per-tenant filter to
  apply in the first place.

If you are multi-tenant and want the ambient tenant applied for you, `ITenantAuditReader` from
`Themia.Modules.Audit` sets `AuditQuery.TenantId` to the caller's tenant and refuses to be asked for
another tenant's rows. That module wrapper is a convenience over `QueryAsync`, not a gate in front of
it — nothing about `Themia.Audit` on its own restricts which tenant's rows a caller can read.

## Tenant semantics

On the *written* row, `AuditEntry.TenantId` is nullable and `null` means **host-level**, not
"unknown". A failed login for an identifier matching no user genuinely has no tenant, and is exactly
the row a security review wants. Nothing in this package invents a tenant.

This is the same nullable `TenantId` shape as the query filter above, but a different property on a
different type: `AuditEntry.TenantId` is what got recorded; `AuditQuery.TenantId` (and
`HostLevelOnly`) is how you filter it back out.

## Retention

`IAuditStore.PurgeAsync(olderThan, …)` deletes in one statement. There is no scheduled job: the
default is keep-forever, and silently deleting an audit trail is worse than a growing table. On a
table that has never been purged the first call can be very large — purge in date slices rather than
in one go.

## Related

- `Themia.Audit.AspNetCore` — mountable read-only dashboard.
- `Themia.Modules.Audit` — tenant resolution, transaction enlistment, and automatic auditing of
  Identity.
