# Themia.Modules.Audit

The framework half of Themia's audit log: tenant resolution, transaction enlistment, the
`IAuditLogService` adapter, and an observer that audits every Identity authentication event without
the application writing any code.

`net10.0`. Builds on `Themia.Audit`, which owns the record model, redaction, the store and the schema.

## Install

```csharp
services.AddThemiaAudit(o =>                     // Themia.Audit — store, redaction, schema
{
    o.ConnectionString = cfg.GetConnectionString("Default")!;
    o.Engine           = AuditEngine.Postgres;
});
services.AddThemiaAuditPostgreSql();

services.AddThemiaAuditModule();                 // this package
services.AddThemiaAuditIdentityObserver();       // opt-in: audit Identity
```

Call order between `AddThemiaAudit` and `AddThemiaAuditModule` does not matter — this package
supersedes the neutral recorder by identity rather than by which call ran last, and that is asserted
by tests rather than assumed.

`AddThemiaAuditIdentityObserver` is a **separate call** on purpose. A host without Identity is never
made to reference it, and a host that has Identity but does not want authentication auditing should
not have to opt out of something that turned itself on.

## Two write surfaces

`IAuditRecorder` is the real API. `IAuditLogService` (from `Themia.Services`) is a compatibility
adapter over it, and its `AuditEvent` record cannot express an outcome, a reason, an IP address, or a
null actor — so it cannot describe a failed login. Prefer `IAuditRecorder`.

## Transaction policy

An audit row recording a change that was rolled back is worse during an investigation than a missing
row: it asserts something that did not happen.

| category | policy | why |
| --- | --- | --- |
| `Activity` | `RequireTransaction` (default, configurable) | the guarantee is real, or its absence is loud |
| `Authentication`, `UserLifecycle` | `Never`, not configurable | a failed login has no transaction to join, and must be recorded even when the surrounding request fails |

Under `RequireTransaction` with no open transaction, recording **throws** and the message names
`IUnitOfWork.ExecuteInTransactionAsync`. That is not a Themia limitation being surfaced — a business
write and its audit row cannot be atomic without a transaction on any database. Making the caller say
so is the honest form.

```csharp
await unitOfWork.ExecuteInTransactionAsync(async ct =>
{
    await proposals.AcceptAsync(id, ct);
    await recorder.RecordAsync(new AuditEntry { /* … */ }, payload, ct);
});
```

`AuditTransactionPolicy.JoinIfPresent` is available for adopters who accept the weaker guarantee. It
degrades to durable-only when no transaction is open — stated here because that degradation is
otherwise invisible.

Works identically on EF Core and Dapper; both legs are covered by the same tests.

## Reading

`ITenantAuditReader` pre-seeds the ambient tenant and **overrides** an `AuditQuery` that names a
different one — it cannot be asked for another tenant's rows.

The raw `IAuditStore.QueryAsync` crosses tenants by design, because the neutral package cannot see
`ITenantContext`. Adopter code should use `ITenantAuditReader`.

## What auditing Identity gives you

`AddThemiaAuditIdentityObserver` records all twelve events Identity raises, with no changes at your
call sites:

| event | notable |
| --- | --- |
| `LOGIN_SUCCEEDED` / `LOGIN_FAILED` / `LOGIN_DENIED` | `Reason` carries the real `LoginFailureReason` the uniform 401 hides from the client |
| `EXTERNAL_LOGIN_SUCCEEDED` / `_FAILED` / `_DENIED` | the payload carries `wasCreated` and `wasLinked` — account auto-creation and provider linking are both takeover vectors |
| `REFRESH_SUCCEEDED` / `REFRESH_DENIED` / `REFRESH_FAILED` | `REFRESH_DENIED` carries `rotationCommitted`, distinguishing "nothing happened" from "the token was rotated and its holder does not know" |
| `LOCKED_OUT` | fires where lockout is applied, not inferred from the next attempt |
| `LOGOUT`, `USER_MUTATED` | |

`REFRESH_FAILED` with `RefreshOutcome.ReuseDetected` is the one to alert on: a rotated refresh token
presented twice is the textbook signature of token theft, and the row is attributed to the owning
account so an operator can revoke its sessions.

An observer that throws never changes the flow it observes — it is logged at Error and swallowed. A
failed audit write must not turn a successful login into a 500, and must not turn a failed login into
a different status code, which would leak the reason the uniform 401 exists to hide.

## Schema

`AddThemiaAudit` runs the migration. `AuditModule` asserts the table exists and does not migrate.

## Related

- `Themia.Audit` — record model, redaction, store, dialects, schema.
- `Themia.Audit.AspNetCore` — mountable read-only dashboard. Its `Authorize` predicate is fail-closed
  and tenant scoping there is the adopter's decision, since a host admin viewing every tenant and a
  tenant admin viewing their own are both legitimate.
