using FluentMigrator;

namespace Themia.Data.Migrations.Tests;

// Two migrations deliberately sharing a version number. FluentMigrator's loader rejects this with a
// DuplicateMigrationException during discovery; the fixtures exist to prove ThemiaMigrations.Run wraps
// that failure in an InvalidOperationException rather than letting it escape raw.
[Migration(202606120099)]
public sealed class DuplicateVersionMigrationA : Migration
{
    public override void Up() { }
    public override void Down() { }
}

[Migration(202606120099)]
public sealed class DuplicateVersionMigrationB : Migration
{
    public override void Up() { }
    public override void Down() { }
}
