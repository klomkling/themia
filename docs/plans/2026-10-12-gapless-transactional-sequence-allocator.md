# Implementation Plan: Gapless Transactional Sequence Allocation (coord #0155)

## 1. Context & Problem Statement
Under Thai tax regulations (Revenue Department), document numbers for Tax Invoices / Receipts, Withholding Tax Certificates, and Credit Notes must form an unbroken run (strictly gapless) per document type and period.

The existing `ISequenceProvider` operates on its own dedicated transaction with connection-enlistment disabled so that allocations survive caller rollback (gaps normal, duplicates prevented). This fails regulatory requirements where caller rollback must restore the counter so no numbers are skipped.

Coord request #0155 requires a gapless sequence allocator that operates inside the **caller's open transaction** (`DbTransaction`). If the caller's transaction commits, the counter increment commits; if the caller rolls back, the counter rolls back with it. Callers on the same sequence key are serialized via row locks (`FOR UPDATE` / `UPDLOCK, HOLDLOCK`) until commit/rollback.

## 2. API Design & Requirements

### 2.1 Interface: `ITransactionalSequenceAllocator`
Namespace: `Themia.Framework.Data.Sequences`
```csharp
namespace Themia.Framework.Data.Sequences;

public interface ITransactionalSequenceAllocator
{
    Task<long> NextInTransactionAsync(DbTransaction tx, string sequenceKey, CancellationToken ct = default);
    Task<long> NextHostInTransactionAsync(DbTransaction tx, string sequenceKey, CancellationToken ct = default);
}
```

### 2.2 Behavior & Invariants
1. **Transaction Attachment**:
   - `tx` cannot be null (`ArgumentNullException.ThrowIfNull(tx)`).
   - `tx.Connection` must not be null (`InvalidOperationException`).
2. **Key Validation**:
   - `sequenceKey` cannot be null, empty, or whitespace (`ArgumentException.ThrowIfNullOrEmpty`).
   - Length <= 100 characters (`SequenceProvider.MaxSequenceKeyLength`).
3. **Tenant Scoping**:
   - `NextInTransactionAsync`: uses ambient `ITenantContext.CurrentTenantId?.Value`. Throws `InvalidOperationException` if no ambient tenant.
   - `NextHostInTransactionAsync`: uses `HostTenant` (`""`), completely independent of tenant rows.
4. **Dialect Resolution**:
   - Resolved from `SequenceOptions.Dialect ?? SequenceDialectFactory.For(SequenceOptions.Engine)`.
   - Dialect provides `InsertIfMissingSql`, `SelectForUpdateSql`, `UpdateNextValueSql`.
5. **Atomic Seeding / Lazy Seeding**:
   - First call for a key seeds the counter lazily inside the transaction via `InsertIfMissingSql` with startValue = 1 (`@val = 1`).
   - If row already exists, `InsertIfMissingSql` is a no-op (Postgres `ON CONFLICT DO NOTHING`, MySQL `ON DUPLICATE KEY UPDATE next_value = next_value`, SqlServer `WHERE NOT EXISTS`).
   - Then `SelectForUpdateSql` locks the row and retrieves `next_value`.
   - Value `first = current.Value` is returned to the caller.
   - Next value is calculated: `checked(first + 1)`. If overflow, throws `InvalidOperationException` (exhausted).
   - `UpdateNextValueSql` updates `next_value = advanced`.
   - Transaction is NEVER committed or rolled back by the allocator; caller owns the transaction lifetime.
6. **Concurrency & Serialization**:
   - Concurrent calls on the same key inside separate transactions serialize on the row lock (`FOR UPDATE`).
   - When Tx A commits, Tx B reads the updated `next_value` and increments it.
   - When Tx A rolls back, the lock is released, Tx B reads the pre-Tx A `next_value`.

### 2.3 Dependency Injection
In `SequenceServiceCollectionExtensions.AddThemiaSequences`:
```csharp
services.TryAddScoped<ITransactionalSequenceAllocator, TransactionalSequenceAllocator>();
```

### 2.4 Public API Surface
Update `PublicAPI.Unshipped.txt` in `Themia.Framework.Data.Sequences`:
- `Themia.Framework.Data.Sequences.ITransactionalSequenceAllocator`
- `Themia.Framework.Data.Sequences.ITransactionalSequenceAllocator.NextInTransactionAsync(System.Data.Common.DbTransaction! tx, string! sequenceKey, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<long>!`
- `Themia.Framework.Data.Sequences.ITransactionalSequenceAllocator.NextHostInTransactionAsync(System.Data.Common.DbTransaction! tx, string! sequenceKey, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<long>!`

## 3. Test Strategy (TDD)
Never modify, weaken, skip or delete existing tests!
Add new comprehensive unit and integration tests:
1. **Unit Tests** (`Themia.Framework.Data.Sequences.Tests`):
   - `AddThemiaSequences_RegistersTransactionalSequenceAllocator`: verifies DI registration.
   - Key validation tests (null, empty, whitespace, >100 characters).
   - Null transaction or null connection handling.
   - No ambient tenant validation on `NextInTransactionAsync`.
2. **Integration Tests** (`Themia.Framework.Data.Sequences.IntegrationTests`):
   - Rollback test: Caller rolls back -> counter is not advanced.
   - Commit test: Caller commits -> counter advances.
   - Concurrency test: Multiple concurrent callers on one key in separate transactions get distinct 1..N numbers with no gaps.
   - Host vs Tenant test: Host counter and tenant counter for same key are completely isolated.
   - First use / lazy seeding test: First use without prior `EnsureSequenceAsync` initializes at 1 and succeeds race-free.

## 4. Documentation & Changelog
1. Update `README.md` in `Themia.Framework.Data.Sequences` explaining when to use `ISequenceProvider` vs `ITransactionalSequenceAllocator`.
2. Update `CHANGELOG.md` under `[Unreleased]`.
