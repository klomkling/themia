using Themia.Data.Migrations;

using Xunit;

namespace Themia.Audit.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(PostgresAuditStoreCollection.Name)]
public sealed class PostgresAuditSchemaMigrationTests(PostgresAuditStoreFixture fixture)
    : AuditSchemaMigrationTestsBase(MigrationEngine.Postgres, fixture.Dialect, fixture.ConnectionString);
