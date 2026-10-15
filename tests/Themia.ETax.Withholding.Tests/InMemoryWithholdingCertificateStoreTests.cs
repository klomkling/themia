using Themia.ETax.Withholding.Testing;

namespace Themia.ETax.Withholding.Tests;

/// <summary>The test double must itself satisfy the conformance class every real store is held to.</summary>
public sealed class InMemoryWithholdingCertificateStoreTests : WithholdingCertificateStoreConformanceTests
{
    protected override Task<IWithholdingCertificateStoreHarness> CreateHarnessAsync() =>
        Task.FromResult<IWithholdingCertificateStoreHarness>(new InMemoryWithholdingCertificateStore.Harness());
}
