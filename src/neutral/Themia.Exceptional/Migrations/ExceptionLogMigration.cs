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
        IfDatabase("postgres").Delegate(() => CreateTable(c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() => CreateTable(c => c.AsCustom("DATETIME(6)")));
        IfDatabase("sqlserver").Delegate(() => CreateTable(c => c.AsDateTime2()));

        Create.Index("IX_Exceptions_App_Hash_Created")
            .OnTable("Exceptions")
            .OnColumn("ApplicationName").Ascending()
            .OnColumn("ErrorHash").Ascending()
            .OnColumn("CreationDate").Ascending();
        Create.Index("IX_Exceptions_DeletionDate")
            .OnTable("Exceptions").OnColumn("DeletionDate").Ascending();
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
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table("Exceptions");
}
