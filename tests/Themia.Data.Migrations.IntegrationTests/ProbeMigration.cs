using FluentMigrator;

namespace Themia.Data.Migrations.IntegrationTests;

/// <summary>A trivial engine-agnostic migration used only to prove the runner applies migrations.</summary>
[Migration(202606120001, "Themia.Data.Migrations probe table")]
public sealed class ProbeMigration : Migration
{
    public override void Up() =>
        Create.Table("migrations_probe").WithColumn("Id").AsInt32().PrimaryKey();

    public override void Down() => Delete.Table("migrations_probe");
}
