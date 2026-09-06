using Xunit;

namespace Themia.Audit.Integration.Tests;

[Trait("Category", "Integration")]
[Collection(PostgresAuditStoreCollection.Name)]
public sealed class PostgresAuditStoreTests(PostgresAuditStoreFixture fixture)
    : AuditStoreTestsBase(fixture.Store, fixture.Dialect, fixture.ConnectionString);
