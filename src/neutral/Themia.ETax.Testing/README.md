# Themia.ETax.Testing

Conformance tests for your `ITaxDocumentStore`. The store is yours (your tables, your engine); this package makes sure it behaves
like everyone else's: conditional transitions, leases, backoff, the cancel-vs-worker rule, rollback with the issue transaction.

```csharp
public sealed class MyStoreTests : TaxDocumentStoreConformanceTests
{
    protected override async Task<ITaxDocumentStoreHarness> CreateHarnessAsync() =>
        await MyHarness.CreateOnEmptyDatabaseAsync();   // your store + a way to open a READ COMMITTED transaction
}
```

Test-framework dependency: `xunit.extensibility.core` and `xunit.assert` only (not the `xunit` metapackage); your own test project
brings the runner. xUnit v2.
