using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using Themia.Exceptional;

namespace Themia.Exceptional.SqlServer;

/// <summary>SQL Server implementation of <see cref="IExceptionalSqlDialect"/> (Microsoft.Data.SqlClient).</summary>
public sealed class SqlServerExceptionalDialect : IExceptionalSqlDialect
{
    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    public SqlServerExceptionalDialect(string connectionString) => this.connectionString = connectionString;

    /// <inheritdoc />
    public DbConnection CreateConnection() => new SqlConnection(connectionString);

    /// <inheritdoc />
    public void AddTemporalFilters(DynamicParameters args, DateTime? from, DateTime? to)
    {
        args.Add("From", from, DbType.DateTime2);
        args.Add("To", to, DbType.DateTime2);
    }

    /// <inheritdoc />
    public string RollupSql => """
        UPDATE [Exceptions]
        SET [DuplicateCount] = [DuplicateCount] + 1, [LastLogDate] = @LastLogDate
        WHERE [Id] = (
            SELECT TOP 1 [Id] FROM [Exceptions]
            WHERE [ErrorHash] = @ErrorHash AND [ApplicationName] = @ApplicationName
              AND [DeletionDate] IS NULL AND [CreationDate] >= @RollupSince
            ORDER BY [CreationDate] DESC);
        """;

    /// <inheritdoc />
    public string InsertSql => """
        INSERT INTO [Exceptions]
        ([Guid],[ApplicationName],[MachineName],[Type],[Source],[Message],[Detail],[Host],[Url],[HttpMethod],[IpAddress],[StatusCode],[ErrorHash],[DuplicateCount],[TenantId],[CreationDate],[LastLogDate],[DeletionDate],[IsProtected],[RequestBody],[RequestContext])
        VALUES (@Guid,@ApplicationName,@MachineName,@Type,@Source,@Message,@Detail,@Host,@Url,@HttpMethod,@IpAddress,@StatusCode,@ErrorHash,@DuplicateCount,@TenantId,@CreationDate,@LastLogDate,@DeletionDate,@IsProtected,@RequestBody,@RequestContext);
        """;

    /// <inheritdoc />
    public string GetByGuidSql => "SELECT * FROM [Exceptions] WHERE [Guid] = @Guid;";

    /// <inheritdoc />
    public string ListSql => """
        SELECT * FROM [Exceptions]
        WHERE (@ApplicationName IS NULL OR [ApplicationName] = @ApplicationName)
          AND (@TenantId IS NULL OR [TenantId] = @TenantId)
          AND (@From IS NULL OR [CreationDate] >= @From)
          AND (@To IS NULL OR [CreationDate] <= @To)
          AND (@Search IS NULL OR [Type] LIKE @Search OR [Message] LIKE @Search)
          AND (@IncludeDeleted = 1 OR [DeletionDate] IS NULL)
        ORDER BY [LastLogDate] DESC OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
        """;

    /// <inheritdoc />
    public string CountSql => """
        SELECT COUNT(*) FROM [Exceptions]
        WHERE (@ApplicationName IS NULL OR [ApplicationName] = @ApplicationName)
          AND (@TenantId IS NULL OR [TenantId] = @TenantId)
          AND (@From IS NULL OR [CreationDate] >= @From)
          AND (@To IS NULL OR [CreationDate] <= @To)
          AND (@Search IS NULL OR [Type] LIKE @Search OR [Message] LIKE @Search)
          AND (@IncludeDeleted = 1 OR [DeletionDate] IS NULL);
        """;

    /// <inheritdoc />
    public string ProtectSql => "UPDATE [Exceptions] SET [IsProtected] = 1 WHERE [Guid] = @Guid;";

    /// <inheritdoc />
    public string SoftDeleteSql => "UPDATE [Exceptions] SET [DeletionDate] = @DeletionDate WHERE [Guid] = @Guid AND [DeletionDate] IS NULL;";

    /// <inheritdoc />
    public string HardDeleteSql => "DELETE FROM [Exceptions] WHERE [Guid] = @Guid;";

    /// <inheritdoc />
    public string PurgeSql => "DELETE FROM [Exceptions] WHERE [IsProtected] = 0 AND [CreationDate] < @OlderThan;";

    /// <inheritdoc />
    public DynamicParameters BuildInsertParameters(ExceptionEntry entry)
        => ExceptionStoreParameters.Insert(entry, DbType.DateTime2);

    /// <inheritdoc />
    public DynamicParameters BuildRollupParameters(ExceptionEntry entry, TimeSpan rollupPeriod)
        => ExceptionStoreParameters.Rollup(entry, rollupPeriod, DbType.DateTime2);

    /// <inheritdoc />
    public DynamicParameters BuildSoftDeleteParameters(Guid guid, DateTime deletionDateUtc)
        => ExceptionStoreParameters.SoftDelete(guid, deletionDateUtc, DbType.DateTime2);

    /// <inheritdoc />
    public DynamicParameters BuildPurgeParameters(DateTime olderThanUtc)
        => ExceptionStoreParameters.Purge(olderThanUtc, DbType.DateTime2);
}
