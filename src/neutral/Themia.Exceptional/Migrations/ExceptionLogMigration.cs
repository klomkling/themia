using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Exceptional.Migrations;

/// <summary>Creates the <c>Exceptions</c> table. Timestamp column types are rendered per provider.</summary>
[Migration(202606060001, "Themia.Exceptional: create Exceptions table")]
public sealed class ExceptionLogMigration : Migration
{
    /// <inheritdoc />
    public override void Up()
    {
        // LOCKSTEP: this per-provider list and the unsupported-provider guard below are two parallel
        // whitelists that MUST agree. Adding a provider here (a CreateTable branch) without adding its
        // prefix to the guard leaves it throwing NotSupportedException; adding it to the guard without a
        // branch here lets it through to a column-type failure. Edit BOTH when adding a provider.
        IfDatabase("postgres").Delegate(() => CreateTable(c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() => CreateTable(c => c.AsCustom("DATETIME(6)")));
        IfDatabase("sqlserver").Delegate(() => CreateTable(c => c.AsDateTime2()));

        // Fail fast if run against an unsupported provider so the caller gets a clear error at
        // migration time rather than a confusing "table does not exist" failure at runtime.
        // The predicate receives the processor's primary DatabaseType (e.g. "Postgres", "MySql8",
        // "SqlServer2016") — not the friendly aliases used by the string overload — so we match by
        // prefix to cover all versioned variants of each supported family. MariaDB runs on the MySql
        // processor (DatabaseType "MySql8" → matches the "MySql" prefix and the mysql CreateTable
        // branch); a literal "MariaDB" DatabaseType has no branch, so it correctly throws here.
        IfDatabase(p =>
                !p.StartsWith("Postgres", System.StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", System.StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", System.StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new System.NotSupportedException(
                "Themia.Exceptional supports only PostgreSQL, MySQL/MariaDB, and SQL Server. " +
                "The active database provider is not supported; add a migration branch for it."));
    }

    private void CreateTable(System.Func<ICreateTableColumnAsTypeSyntax, ICreateTableColumnOptionOrWithColumnSyntax> ts)
    {
        var table = Create.Table("Exceptions")
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("Guid").AsGuid().NotNullable()
            .WithColumn("ApplicationName").AsString(256).NotNullable()
            .WithColumn("MachineName").AsString(256).NotNullable()
            .WithColumn("Type").AsString(1000).NotNullable()
            .WithColumn("Source").AsString(500).Nullable()
            .WithColumn("Message").AsString(1000).NotNullable()
            .WithColumn("Detail").AsString(int.MaxValue).NotNullable()
            .WithColumn("RequestBody").AsString(int.MaxValue).Nullable()
            .WithColumn("Host").AsString(512).Nullable()
            .WithColumn("Url").AsString(2000).Nullable()
            .WithColumn("HttpMethod").AsString(16).Nullable()
            .WithColumn("IpAddress").AsString(64).Nullable()
            .WithColumn("StatusCode").AsInt32().Nullable()
            .WithColumn("ErrorHash").AsString(64).NotNullable()
            .WithColumn("DuplicateCount").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("TenantId").AsString(256).Nullable();
        ts(table.WithColumn("CreationDate")).NotNullable();
        ts(table.WithColumn("LastLogDate")).NotNullable();
        ts(table.WithColumn("DeletionDate")).Nullable();
        table.WithColumn("IsProtected").AsBoolean().NotNullable().WithDefaultValue(false);

        // Guid is the lookup key for Get/Protect/SoftDelete/HardDelete; unique (one per stored error) + indexed.
        Create.Index("IX_Exceptions_Guid")
            .OnTable("Exceptions").OnColumn("Guid").Ascending()
            .WithOptions().Unique();
        Create.Index("IX_Exceptions_App_Hash_Created")
            .OnTable("Exceptions")
            .OnColumn("ApplicationName").Ascending()
            .OnColumn("ErrorHash").Ascending()
            .OnColumn("CreationDate").Ascending();
        Create.Index("IX_Exceptions_DeletionDate")
            .OnTable("Exceptions").OnColumn("DeletionDate").Ascending();
        // Purge query: WHERE IsProtected = FALSE AND CreationDate < @OlderThan
        Create.Index("IX_Exceptions_Protected_Created")
            .OnTable("Exceptions")
            .OnColumn("IsProtected").Ascending()
            .OnColumn("CreationDate").Ascending();
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table("Exceptions");
}
