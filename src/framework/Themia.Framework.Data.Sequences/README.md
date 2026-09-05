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
