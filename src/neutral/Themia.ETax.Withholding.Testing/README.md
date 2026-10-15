# Themia.ETax.Withholding.Testing

Conformance tests for your `IWithholdingCertificateStore`. The store is yours (your tables, your engine); this package makes sure it
behaves like everyone else's: one certificate per key with racing issuers serialised, payer-scoped reads that fail closed, cancel for
replacement that is final and write-once, and rollback together with the issue transaction.

```csharp
public sealed class MyStoreTests : WithholdingCertificateStoreConformanceTests
{
    protected override async Task<IWithholdingCertificateStoreHarness> CreateHarnessAsync() =>
        await MyHarness.CreateOnEmptyDatabaseAsync();   // your store + a way to open a READ COMMITTED transaction
}
```

Test-framework dependency: `xunit.extensibility.core` and `xunit.assert` only (not the `xunit` metapackage); your own test project
brings the runner. xUnit v2.
