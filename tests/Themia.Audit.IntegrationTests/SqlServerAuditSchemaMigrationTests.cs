using Themia.Data.Migrations;

using Xunit;

namespace Themia.Audit.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(SqlServerAuditStoreCollection.Name)]
public sealed class SqlServerAuditSchemaMigrationTests(SqlServerAuditStoreFixture fixture)
    : AuditSchemaMigrationTestsBase(MigrationEngine.SqlServer, fixture.Dialect, fixture.ConnectionString);
