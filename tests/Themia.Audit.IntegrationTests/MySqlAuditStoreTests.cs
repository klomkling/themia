using Xunit;

namespace Themia.Audit.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(MySqlAuditStoreCollection.Name)]
public sealed class MySqlAuditStoreTests(MySqlAuditStoreFixture fixture)
    : AuditStoreTestsBase(fixture.Store, fixture.Dialect, fixture.ConnectionString);
