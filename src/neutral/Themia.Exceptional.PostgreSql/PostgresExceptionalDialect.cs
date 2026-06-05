using System.Data;
using System.Data.Common;
using Npgsql;
using Themia.Exceptional;

namespace Themia.Exceptional.PostgreSql;

/// <summary>PostgreSQL implementation of <see cref="IExceptionalSqlDialect"/> (Npgsql).</summary>
public sealed class PostgresExceptionalDialect : IExceptionalSqlDialect
{
    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    public PostgresExceptionalDialect(string connectionString) => this.connectionString = connectionString;

    /// <inheritdoc />
    public DbConnection CreateConnection() => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public DbType? TemporalFilterDbType => DbType.DateTimeOffset;

    /// <inheritdoc />
    public string RollupSql => """
        UPDATE "Exceptions"
        SET "DuplicateCount" = "DuplicateCount" + 1, "LastLogDate" = @LastLogDate
        WHERE "Id" = (
            SELECT "Id" FROM "Exceptions"
            WHERE "ErrorHash" = @ErrorHash AND "ApplicationName" = @ApplicationName
              AND "DeletionDate" IS NULL AND "CreationDate" >= @RollupSince
            ORDER BY "CreationDate" DESC LIMIT 1);
        """;

    /// <inheritdoc />
    public string InsertSql => """
        INSERT INTO "Exceptions"
        ("Guid","ApplicationName","MachineName","Type","Source","Message","Detail","Host","Url","HttpMethod","IpAddress","StatusCode","ErrorHash","DuplicateCount","TenantId","CreationDate","LastLogDate","DeletionDate","IsProtected","RequestBody")
        VALUES (@Guid,@ApplicationName,@MachineName,@Type,@Source,@Message,@Detail,@Host,@Url,@HttpMethod,@IpAddress,@StatusCode,@ErrorHash,@DuplicateCount,@TenantId,@CreationDate,@LastLogDate,@DeletionDate,@IsProtected,@RequestBody);
        """;

    /// <inheritdoc />
    public string GetByGuidSql => """SELECT * FROM "Exceptions" WHERE "Guid" = @Guid;""";

    /// <inheritdoc />
    public string ListSql => """
        SELECT * FROM "Exceptions"
        WHERE (@ApplicationName IS NULL OR "ApplicationName" = @ApplicationName)
          AND (@TenantId IS NULL OR "TenantId" = @TenantId)
          AND (@From IS NULL OR "CreationDate" >= @From)
          AND (@To IS NULL OR "CreationDate" <= @To)
          AND (@Search IS NULL OR "Type" ILIKE @Search OR "Message" ILIKE @Search)
          AND (@IncludeDeleted OR "DeletionDate" IS NULL)
        ORDER BY "LastLogDate" DESC LIMIT @PageSize OFFSET @Offset;
        """;

    /// <inheritdoc />
    public string CountSql => """
        SELECT COUNT(*) FROM "Exceptions"
        WHERE (@ApplicationName IS NULL OR "ApplicationName" = @ApplicationName)
          AND (@TenantId IS NULL OR "TenantId" = @TenantId)
          AND (@From IS NULL OR "CreationDate" >= @From)
          AND (@To IS NULL OR "CreationDate" <= @To)
          AND (@Search IS NULL OR "Type" ILIKE @Search OR "Message" ILIKE @Search)
          AND (@IncludeDeleted OR "DeletionDate" IS NULL);
        """;

    /// <inheritdoc />
    public string ProtectSql => """UPDATE "Exceptions" SET "IsProtected" = TRUE WHERE "Guid" = @Guid;""";

    /// <inheritdoc />
    public string SoftDeleteSql => """UPDATE "Exceptions" SET "DeletionDate" = @DeletionDate WHERE "Guid" = @Guid AND "DeletionDate" IS NULL;""";

    /// <inheritdoc />
    public string HardDeleteSql => """DELETE FROM "Exceptions" WHERE "Guid" = @Guid;""";

    /// <inheritdoc />
    public string PurgeSql => """DELETE FROM "Exceptions" WHERE "IsProtected" = FALSE AND "CreationDate" < @OlderThan;""";
}
