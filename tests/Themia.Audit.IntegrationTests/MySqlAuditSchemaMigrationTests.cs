using Themia.Data.Migrations;

using Xunit;

namespace Themia.Audit.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(MySqlAuditStoreCollection.Name)]
public sealed class MySqlAuditSchemaMigrationTests(MySqlAuditStoreFixture fixture)
    : AuditSchemaMigrationTestsBase(MigrationEngine.MySql, fixture.Dialect, fixture.ConnectionString);
