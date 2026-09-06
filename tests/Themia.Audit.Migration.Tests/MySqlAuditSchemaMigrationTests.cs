using Themia.Data.Migrations;

using Xunit;

namespace Themia.Audit.Migration.Tests;

[Trait("Category", "Integration")]
[Collection(MySqlAuditMigrationCollection.Name)]
public sealed class MySqlAuditSchemaMigrationTests(MySqlAuditMigrationFixture fixture)
    : AuditSchemaMigrationTestsBase(MigrationEngine.MySql, fixture.Dialect, fixture.ConnectionString);
