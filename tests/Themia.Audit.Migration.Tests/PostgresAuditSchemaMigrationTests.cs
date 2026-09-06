using Themia.Data.Migrations;

using Xunit;

namespace Themia.Audit.Migration.Tests;

[Trait("Category", "Integration")]
[Collection(PostgresAuditMigrationCollection.Name)]
public sealed class PostgresAuditSchemaMigrationTests(PostgresAuditMigrationFixture fixture)
    : AuditSchemaMigrationTestsBase(MigrationEngine.Postgres, fixture.Dialect, fixture.ConnectionString);
