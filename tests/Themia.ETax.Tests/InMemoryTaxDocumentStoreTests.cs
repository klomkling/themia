using Themia.ETax.Testing;

namespace Themia.ETax.Tests;

/// <summary>The test double must itself satisfy the conformance class every real store is held to.</summary>
public sealed class InMemoryTaxDocumentStoreTests : TaxDocumentStoreConformanceTests
{
    protected override Task<ITaxDocumentStoreHarness> CreateHarnessAsync() =>
        Task.FromResult<ITaxDocumentStoreHarness>(new InMemoryTaxDocumentStore.Harness());
}
