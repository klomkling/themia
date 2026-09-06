using Themia.Data.Migrations;

using Xunit;

namespace Themia.Audit.Migration.Tests;

[Trait("Category", "Integration")]
[Collection(SqlServerAuditMigrationCollection.Name)]
public sealed class SqlServerAuditSchemaMigrationTests(SqlServerAuditMigrationFixture fixture)
    : AuditSchemaMigrationTestsBase(MigrationEngine.SqlServer, fixture.Dialect, fixture.ConnectionString);
