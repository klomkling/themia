using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Themia.Exceptional;

namespace Themia.Exceptional.Tests;

/// <summary>
/// Real-SQLite dialect used to exercise ExceptionStoreEngine orchestration without Docker.
/// Uses a single shared in-memory connection (kept open by the test) so the schema persists.
/// </summary>
internal sealed class SqliteExceptionalDialect : IExceptionalSqlDialect
{
    private readonly string connectionString;

    static SqliteExceptionalDialect()
    {
        // SQLite stores GUIDs as TEXT; Dapper cannot convert string → Guid without a type handler.
        SqlMapper.AddTypeHandler(new GuidTypeHandler());
    }

    public SqliteExceptionalDialect(string connectionString) => this.connectionString = connectionString;

    private sealed class GuidTypeHandler : SqlMapper.TypeHandler<Guid>
    {
        public override void SetValue(IDbDataParameter parameter, Guid value) =>
            parameter.Value = value.ToString();

        public override Guid Parse(object value) =>
            Guid.Parse(value.ToString()!);
    }

    public DbConnection CreateConnection() => new SqliteConnection(connectionString);

    public static string CreateTableSql => """
        CREATE TABLE IF NOT EXISTS "Exceptions" (
            "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
            "Guid" TEXT NOT NULL,
            "ApplicationName" TEXT NOT NULL,
            "MachineName" TEXT NOT NULL,
            "Type" TEXT NOT NULL,
            "Source" TEXT NULL,
            "Message" TEXT NOT NULL,
            "Detail" TEXT NOT NULL,
            "Host" TEXT NULL,
            "Url" TEXT NULL,
            "HttpMethod" TEXT NULL,
            "IpAddress" TEXT NULL,
            "StatusCode" INTEGER NULL,
            "ErrorHash" TEXT NOT NULL,
            "DuplicateCount" INTEGER NOT NULL,
            "TenantId" TEXT NULL,
            "CreationDate" TEXT NOT NULL,
            "LastLogDate" TEXT NOT NULL,
            "DeletionDate" TEXT NULL,
            "IsProtected" INTEGER NOT NULL
        );
        """;

    public string RollupSql => """
        UPDATE "Exceptions"
        SET "DuplicateCount" = "DuplicateCount" + 1, "LastLogDate" = @LastLogDate
        WHERE "Id" = (
            SELECT "Id" FROM "Exceptions"
            WHERE "ErrorHash" = @ErrorHash AND "ApplicationName" = @ApplicationName
              AND "DeletionDate" IS NULL AND "CreationDate" >= @RollupSince
            ORDER BY "CreationDate" DESC LIMIT 1);
        """;

    public string InsertSql => """
        INSERT INTO "Exceptions"
        ("Guid","ApplicationName","MachineName","Type","Source","Message","Detail","Host","Url","HttpMethod","IpAddress","StatusCode","ErrorHash","DuplicateCount","TenantId","CreationDate","LastLogDate","DeletionDate","IsProtected")
        VALUES (@Guid,@ApplicationName,@MachineName,@Type,@Source,@Message,@Detail,@Host,@Url,@HttpMethod,@IpAddress,@StatusCode,@ErrorHash,@DuplicateCount,@TenantId,@CreationDate,@LastLogDate,@DeletionDate,@IsProtected);
        """;

    public string GetByGuidSql => """SELECT * FROM "Exceptions" WHERE "Guid" = @Guid;""";

    public string ListSql => """
        SELECT * FROM "Exceptions"
        WHERE (@ApplicationName IS NULL OR "ApplicationName" = @ApplicationName)
          AND (@TenantId IS NULL OR "TenantId" = @TenantId)
          AND (@From IS NULL OR "CreationDate" >= @From)
          AND (@To IS NULL OR "CreationDate" <= @To)
          AND (@Search IS NULL OR "Type" LIKE @Search OR "Message" LIKE @Search)
          AND (@IncludeDeleted = 1 OR "DeletionDate" IS NULL)
        ORDER BY "LastLogDate" DESC LIMIT @PageSize OFFSET @Offset;
        """;

    public string CountSql => """
        SELECT COUNT(*) FROM "Exceptions"
        WHERE (@ApplicationName IS NULL OR "ApplicationName" = @ApplicationName)
          AND (@TenantId IS NULL OR "TenantId" = @TenantId)
          AND (@From IS NULL OR "CreationDate" >= @From)
          AND (@To IS NULL OR "CreationDate" <= @To)
          AND (@Search IS NULL OR "Type" LIKE @Search OR "Message" LIKE @Search)
          AND (@IncludeDeleted = 1 OR "DeletionDate" IS NULL);
        """;

    public string ProtectSql => """UPDATE "Exceptions" SET "IsProtected" = 1 WHERE "Guid" = @Guid;""";

    public string SoftDeleteSql => """UPDATE "Exceptions" SET "DeletionDate" = @DeletionDate WHERE "Guid" = @Guid AND "DeletionDate" IS NULL;""";

    public string HardDeleteSql => """DELETE FROM "Exceptions" WHERE "Guid" = @Guid;""";

    public string PurgeSql => """DELETE FROM "Exceptions" WHERE "IsProtected" = 0 AND "CreationDate" < @OlderThan;""";
}
