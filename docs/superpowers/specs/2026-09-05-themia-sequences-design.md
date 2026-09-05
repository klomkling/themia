# Themia Sequences — document numbering (design)

Date: 2026-09-05
Status: approved, unimplemented
Realises: DECISION #2 / §F of `docs/themia-architecture-overview.md`, with three corrections (below).

## Problem

Two Idevs products will both need allocated document numbers, for the same reason, and neither has a
numbering mechanism today.

Coord #0052 and #0055 settled that ezy-assets puts its `BillingDocument` **running number** in PromptPay
`BillRef1`, and propertiezy does the same under biller suffix `02`. Both must issue Thai tax invoices —
Sarawut confirmed on #0052 that VAT and the ใบกำกับภาษี are obligatory, not optional — and a tax invoice
is numbered sequentially by law.

**Not imminent, and worth stating plainly rather than overselling:** #0052 has been blocked for over a
month on a bank visit nobody has made, and propertiezy's last note says it has no payment code at all and
has deliberately not adopted `Themia.PromptPay` because there is no call site yet. The demand is real and
committed on both sides; the deadline is not. This is being built now because the design is already
settled (DECISION #2), because the `MAX()+1` analyzer and its code fix cannot be written until the
provider exists, and because the alternative — each app writing its own — is two divergent
implementations of a thing whose failure is silent.

The failure mode of the obvious workaround is what makes this infrastructure rather than app code.
`SELECT MAX(number) + 1` returns the same value to two concurrent callers, silently, until two tax
invoices carry the same number. Nothing errors; the duplicate is discovered by an accountant, or an
auditor, long after the fact.

Idevs' `ISequenceProvider` already solves this and is proven in production (`Idevs.Net.CoreLib`,
`src/Idevs.Net.CoreLib/Repositories/Sequences`, ~400 lines): atomic allocation in a **separate**
transaction, `SELECT … FOR UPDATE` / `UPDLOCK`, overflow-checked, with the multi-DB UPSERT for SQL
Server / MySQL / PostgreSQL already written. Only its storage is Serenity-coupled.

## Decision

Port the proven allocator into a new package, drop the Serenity storage, add tenant scoping.

### Three corrections to §F

§F predates DECISION #6 (2026-06-11) and is stale in three places. Recording them so the next reader
does not follow the older text:

1. **"ship an EF migration for the `Sequences` table"** — DECISION #6 made FluentMigrator the single DDL
   authority for both data peers; no module uses `dotnet ef migrations add`. The migration is
   FluentMigrator, `IfDatabase(...)` per engine.
2. **"add `EfSequenceProvider`"** — DECISION #6 made Dapper a first-class peer, so an EF-only provider
   would leave every Dapper adopter without sequences. The allocator needs no ORM at all (see
   *ORM-agnostic* below), so there is one provider, not one per peer.
3. **"port into `Themia.Framework.Data`"** — no package by that name exists. There is
   `Themia.Framework.Data.Abstractions`, `.Dapper*` and `.EFCore*`.

### Package

**`Themia.Framework.Data.Sequences`**, `net10.0`.

**Framework layer, not neutral,** because the provider reads `ITenantContext`. That couples it to
`Themia.Framework.Core.Abstractions.Tenancy`, which is framework, and settles the layer.

**Reading the ambient tenant is not the same as trusting it.** `TenantContext.CurrentTenantId` is
`TenantId?`, and background work only has a tenant if it opted in — `Themia.Modules.Export` wraps its
jobs in `BackgroundTenantScope.Begin(tenantId)` for exactly that reason. Invoice generation is the
canonical scheduler job, so "no ambient tenant" is a state this package will meet in production.

Mapping that null quietly onto the host-level `''` row would mean a job that forgot the scope draws every
tenant's invoice numbers from one shared counter, with no error anywhere. That is worse than a duplicate
within one tenant, and `NotificationOutboxDispatcher.cs` already carries a forward-note warning about the
identical shape — an ambient null tenant silently falling back to global config.

So **null is not a value here**: `NextAsync` throws when `ITenantContext.HasTenant` is false. The
host-level row is reachable only by asking for it (`NextHostAsync`). An unstated tenant is a bug, and it
fails loudly at the call that made it.

PowerACC is not a design driver (per `CLAUDE.md`); its Serenity `SqlSequenceProvider` stays where it is.

**ORM-agnostic, one package, no engine split.** The allocator's defining semantic is that it runs on its
*own* connection and transaction, independent of the ambient UoW — so it needs a `DbConnection` and raw
SQL, not EF or Dapper. Identity and Challenges were split into engine packages because they genuinely
bind to an ORM; this does not. It follows `Themia.Data.Migrations` instead: one package referencing the
three ADO providers, engine selected at runtime.

### Public API

```csharp
public interface ISequenceProvider
{
    // Tenant-scoped. Throws InvalidOperationException when there is no ambient tenant.
    Task<long> NextAsync(string sequenceKey, CancellationToken ct = default);
    Task<IReadOnlyList<long>> NextRangeAsync(string sequenceKey, int count, CancellationToken ct = default);
    Task EnsureSequenceAsync(string sequenceKey, long startValue = 1, CancellationToken ct = default);

    // Host-level (the '' row). Separate methods, never a null-tenant fallback, so a job that lost its
    // BackgroundTenantScope fails instead of quietly sharing one counter across every tenant.
    Task<long> NextHostAsync(string sequenceKey, CancellationToken ct = default);
    Task<IReadOnlyList<long>> NextHostRangeAsync(string sequenceKey, int count, CancellationToken ct = default);
    Task EnsureHostSequenceAsync(string sequenceKey, long startValue = 1, CancellationToken ct = default);
}
```

The tenant-scoped three are the port's original signatures, minus the Serenity reference in the doc
comment. The `Host` trio is new and is the correction described above. Values are `long`; the caller
formats them.

The exception when no tenant is ambient names the sequence key and says what to do — wrap the call in a
tenant scope, or use the `Host` overload deliberately. A message that only says "no tenant" sends the
reader to the wrong layer.

`ISequenceDialect` is also public — one implementation per engine, holding the locking SELECT and the
UPSERT. Public so an adopter on an unsupported engine can add one without forking the package, the same
shape as `IExceptionalSqlDialect` and `INotificationsSqlDialect`.

**Configuration.** The provider opens its own connection, so it needs its own connection string rather
than borrowing the peer's `DbContext`/`IDapperConnectionContext` — borrowing would put the allocation
back inside the ambient transaction and destroy the whole semantic. Registration takes a connection
string and an engine, mirroring `ThemiaMigrations.Run`:

```csharp
services.AddThemiaSequences(o =>
{
    o.ConnectionString = cfg.GetConnectionString("Default")!;
    o.Engine           = SequenceEngine.Postgres;
});
```

Normally the same connection string the app already gives the migration runner. It is a separate setting
rather than an inferred one so that pointing sequences at a different database stays possible and
visible.

**The provider must not enlist in an ambient `System.Transactions` transaction.** Themia does not use
`System.Transactions` today — the `TransactionScope` in `Themia.Framework.Data.Dapper` is Themia's own
per-connection type, not the BCL one — so nothing in the framework triggers this. A *consumer* wrapping a
call in `System.Transactions.TransactionScope` is another matter: ADO providers default to `Enlist=true`,
the freshly opened connection would join that ambient transaction, and the allocation would roll back
with it. The number is then reissued to the next caller, silently, which is the one outcome this package
exists to prevent. The provider opens its connection with enlistment suppressed, and a test pins it.

**Running the migration.** The package ships the FluentMigrator migration but does not run it. The
consumer passes this assembly to `ThemiaMigrations.Run`, the same as every other Themia module — stated
here because the provider throws on an unseeded key, and a missing table would otherwise surface as a
confusing first-allocation failure rather than a missing migration.

### Semantics kept verbatim from the port

These are the reasons the package exists. None is negotiable:

- **Allocation runs in a separate transaction** and survives the outer caller's rollback. Gaps in the
  allocated range are normal and expected; duplicates are catastrophic.
- **Row lock while allocating** — `SELECT … FOR UPDATE` (PostgreSQL/MySQL), `WITH (UPDLOCK, HOLDLOCK)`
  (SQL Server).
- **Overflow is a loud failure.** At `long.MaxValue` an unchecked `+1` wraps to `long.MinValue` and
  produces negative allocations that collide with future values once the counter wraps forward. The port
  uses `checked` and raises `InvalidOperationException` naming the exhausted key; keep that.
- **Allocating an unseeded key throws** rather than creating the row implicitly, so a typo in a sequence
  key is not silently a brand-new counter starting at 1.
- **`EnsureSequenceAsync` is idempotent** and preserves an existing `NextValue`, ignoring `startValue`.

Dropped: Serenity `Row`, `ISqlConnections`, `RowRepositoryBase`, `InNewTransactionAsync`. Replaced by an
`ISequenceDialect` per engine and a connection the provider opens itself.

`Themia.Analyzers`' `RawConnectionBypassAnalyzer` does not fire here — it is documented as silent inside
`Themia.Framework.Data.*` assemblies.

### Storage

FluentMigrator, one migration, `IfDatabase(...)` per engine:

```sql
tenant_id    VARCHAR(100) NOT NULL DEFAULT ''   -- '' = host-level
sequence_key VARCHAR(100) NOT NULL
next_value   BIGINT       NOT NULL
PRIMARY KEY (tenant_id, sequence_key)
```

**`tenant_id` is `NOT NULL` with `''` for host-level, and this is a correction to §F.** §F specifies a
`(TenantId, SequenceKey)` primary key, but `TenantId` is nullable throughout Themia (null = host-level)
and **no engine permits a NULL column in a primary key**. No Themia table uses `tenant_id` in a primary
key today, so there is no precedent to copy.

The alternative of a surrogate key plus `UNIQUE (tenant_id, sequence_key)` over a nullable column was
rejected: unique-index NULL semantics **diverge by engine** — PostgreSQL treats NULLs as distinct and
would admit many host-level rows for one key, while SQL Server treats NULL as a single value and admits
one. Two rows for one host-level sequence means two allocators and duplicate numbers, which is the exact
outcome the interface's own contract calls catastrophic. A sentinel is a magic value; an engine-divergent
uniqueness rule is a latent duplicate.

The `null → ''` conversion happens in **one place** in the provider.

The migration adopts an existing table rather than failing on one (coord #0085, #0096).

## Boundaries — what this package does NOT do

- **It does not format.** `INV-2026-00042` is a domain decision — prefix, year segment, padding, and when
  a counter resets are all app policy, and the two consumers will not agree on them. The provider returns
  a `long`.
- **It does not choose sequence keys.** Convention is a colon-namespaced string
  (`DocNo:Invoice:2026`); the caller owns it.
- **It does not guarantee gapless numbering.** It cannot: the value is allocated before the caller's own
  transaction commits, and that transaction may roll back. Any consumer whose regulator requires *gapless*
  numbering needs a different mechanism, and should read this line before adopting.

## Out of scope for v1

- **`IDocumentNumberFormatter`** — §F calls it an optional value-add. It is a few lines in the consuming
  app and its rules are per-app. Build it when a consumer asks, not before.
- **The `MAX()+1` analyzer and its code fix** — the architecture overview pairs them with this package
  (`Themia.Analyzers`, rule + codefix scaffolding `ISequenceProvider.NextAsync`). They are tooling work
  and cannot be written until the provider exists. A separate task, after this ships.
- **A Serenity adapter** — built only if PowerACC actually migrates (YAGNI, per `CLAUDE.md`).

## Testing

The whole package is one claim — *no two callers ever receive the same value* — so the tests that matter
run against real engines.

- **Concurrency, all three engines (Testcontainers).** N concurrent `NextAsync` calls on one key return N
  distinct values. This is the package.
- **The separate-transaction semantic — and a test that can actually fail.** The obvious version
  ("allocate inside an outer transaction, roll it back, assert the number was not reissued") **passes no
  matter what the implementation does**: the provider holds its own connection, Themia's `ITransactionScope`
  is a per-connection database transaction, and a rollback on a different connection cannot touch a
  committed row. It would stay green against an implementation that had lost the semantic entirely.

  So the suite pins the mechanism, not just the outcome: (a) a test that hands the provider the *ambient*
  connection and asserts the rollback **does** lose the number — proving the check can go red; (b) the real
  case, asserting the provider's connection is distinct from the unit of work's; (c) the same rollback
  case under `System.Transactions.TransactionScope`, which fails unless enlistment is suppressed.
- **Tenant isolation, and the missing-tenant case.** Two tenants, same sequence key, independent counters;
  a host-level sequence (`''`) and a tenant sequence with the same key do not collide. Plus the one that
  motivated the design change: with **no ambient tenant**, `NextAsync` throws and allocates nothing — it
  must not fall through to the host row.
- **Overflow.** A row seeded at `long.MaxValue` raises `InvalidOperationException` naming the key, rather
  than wrapping negative.
- **Unseeded key throws**, and `EnsureSequenceAsync` on an existing row preserves `NextValue`.
- **`NextRangeAsync`** returns contiguous ascending values and advances by exactly `count`.
- **Migration replay** against a database that already has the table (coord #0085/#0096).
- **Mutation testing with a canary**, per the harness convention established in 0.21.3/0.21.4: a harness
  that cannot report KILLED cannot report SURVIVED either.

## Version

`0.22.0` — a new package is a MINOR bump under the pre-1.0 policy in `CHANGELOG.md`.
