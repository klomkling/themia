using Xunit;

namespace Themia.Audit.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(SqlServerAuditStoreCollection.Name)]
public sealed class SqlServerAuditStoreTests(SqlServerAuditStoreFixture fixture)
    : AuditStoreTestsBase(fixture.Store, fixture.Dialect, fixture.ConnectionString);
