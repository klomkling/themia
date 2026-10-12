# Themia.Framework.Data.Sequences

Atomic, tenant-scoped document numbering. Allocation runs in its **own** transaction, so the number
survives the caller's rollback: **gaps are normal, duplicates are not.**

```csharp
services.AddThemiaSequences(o =>
{
    o.ConnectionString = builder.Configuration.GetConnectionString("Default")!;
    o.Engine           = SequenceEngine.Postgres;
});

await sequences.EnsureSequenceAsync("DocNo:Invoice:2026", startValue: 1);
var number = await sequences.NextAsync("DocNo:Invoice:2026");   // 1, then 2, …
```

Pass this assembly to `ThemiaMigrations.Run` so the `themia_sequences` table is created.

## Two things to know before adopting

**It does not guarantee gapless numbering, and cannot.** The value is allocated before your transaction
commits; if you roll back, the number is spent. If a regulator requires an unbroken run, this is not the
mechanism.

**There is no null-tenant fallback.** `NextAsync` throws when there is no ambient tenant. Background jobs
must establish one (`BackgroundTenantScope.Begin`), or call `NextHostAsync` when a host-level counter is
genuinely what you want. This is deliberate: a job that lost its tenant scope would otherwise draw every
tenant's numbers from one shared counter with nothing reporting it.

Formatting (`INV-2026-00042`) is yours. The provider returns a `long`.

## Gapless numbering inside caller's transaction (`ITransactionalSequenceAllocator`)

When a regulator (such as the Thai Revenue Department for Tax Invoices, Receipts, Withholding Tax Certificates, and Credit Notes) requires an **unbroken, strictly gapless run of numbers**, use `ITransactionalSequenceAllocator`:

```csharp
// Inside your own transaction:
await using var tx = await conn.BeginTransactionAsync(ct);

// Allocates in the caller's transaction; rows on the same key lock until commit/rollback.
var n = await allocator.NextHostInTransactionAsync(tx, $"DocNo:TaxInvoice:{period}", ct);

// Do your document inserts here...
// If anything fails or rolls back, the counter increment rolls back too -> no gap!
await tx.CommitAsync(ct);
```

- **`NextInTransactionAsync(tx, key, ct)`**: Tenant-scoped inside the caller's transaction (requires ambient tenant).
- **`NextHostInTransactionAsync(tx, key, ct)`**: Host-level inside the caller's transaction.
- **Lazy seeding**: Seeds starting at 1 on first use race-free (unlike `ISequenceProvider`, which throws when an unseeded key is accessed). No prior `EnsureSequenceAsync` call required.
- **Pre-seeding existing runs**: Calling `EnsureSequenceAsync(key, startValue)` (or `EnsureHostSequenceAsync`) beforehand is fully supported if you need an initial offset other than 1.
- **Shared table caution**: Both mechanisms share `themia_sequences`. Never call `ISequenceProvider.Next*` on a key meant for gapless transactional numbering, as its separate transaction will cause skipped numbers.
- **Database & connection**: The allocator ignores `SequenceOptions.ConnectionString` and runs directly on `tx.Connection`. Ensure `themia_sequences` exists in the caller's database (via `SequencesSchemaMigration`) and `SequenceOptions.Engine` matches the caller's database engine.
- **Transaction isolation**: Caller transactions must use `ReadCommitted`. Under `RepeatableRead` or `Serializable` on PostgreSQL, queued callers fail with 40001 upon the previous holder's commit. On MySQL, default `REPEATABLE READ` risks gap-lock deadlocks during first-use inserts.


