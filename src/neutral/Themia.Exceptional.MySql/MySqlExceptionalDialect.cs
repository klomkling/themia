using System.Data;
using System.Data.Common;
using Dapper;
using MySqlConnector;
using Themia.Exceptional;

namespace Themia.Exceptional.MySql;

/// <summary>MySQL/MariaDB implementation of <see cref="IExceptionalSqlDialect"/> (MySqlConnector).</summary>
public sealed class MySqlExceptionalDialect : IExceptionalSqlDialect
{
    private readonly string connectionString;

    /// <summary>
    /// Creates the dialect over <paramref name="connectionString"/>. The <c>Exceptions.Guid</c> column is
    /// always <c>CHAR(36)</c> (FluentMigrator <c>AsGuid()</c> on MySQL), so the dialect pins
    /// <c>GuidFormat=Char36</c> on its own connections regardless of the caller's setting — guaranteeing
    /// <see cref="System.Guid"/> round-trips. A caller-supplied <c>GuidFormat</c>/<c>OldGuids</c> that
    /// disagreed with the column would otherwise silently corrupt Guid lookups (every by-Guid query matching
    /// zero rows). The connection serves only this package's <c>Exceptions</c> table.
    /// </summary>
    public MySqlExceptionalDialect(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            OldGuids = false, // clear the legacy flag first (OldGuids + GuidFormat are mutually exclusive)
            GuidFormat = MySqlGuidFormat.Char36,
        };
        this.connectionString = builder.ConnectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public void AddTemporalFilters(DynamicParameters args, DateTime? from, DateTime? to)
    {
        args.Add("From", from, DbType.DateTime);
        args.Add("To", to, DbType.DateTime);
    }

    /// <inheritdoc />
    public string RollupSql => """
        UPDATE `Exceptions`
        SET `DuplicateCount` = `DuplicateCount` + 1, `LastLogDate` = @LastLogDate
        WHERE `Id` = (
            SELECT `Id` FROM (
                SELECT `Id` FROM `Exceptions`
                WHERE `ErrorHash` = @ErrorHash AND `ApplicationName` = @ApplicationName
                  AND `DeletionDate` IS NULL AND `CreationDate` >= @RollupSince
                ORDER BY `CreationDate` DESC LIMIT 1
            ) AS `t`);
        """;

    /// <inheritdoc />
    public string InsertSql => """
        INSERT INTO `Exceptions`
        (`Guid`,`ApplicationName`,`MachineName`,`Type`,`Source`,`Message`,`Detail`,`Host`,`Url`,`HttpMethod`,`IpAddress`,`StatusCode`,`ErrorHash`,`DuplicateCount`,`TenantId`,`CreationDate`,`LastLogDate`,`DeletionDate`,`IsProtected`,`RequestBody`)
        VALUES (@Guid,@ApplicationName,@MachineName,@Type,@Source,@Message,@Detail,@Host,@Url,@HttpMethod,@IpAddress,@StatusCode,@ErrorHash,@DuplicateCount,@TenantId,@CreationDate,@LastLogDate,@DeletionDate,@IsProtected,@RequestBody);
        """;

    /// <inheritdoc />
    public string GetByGuidSql => "SELECT * FROM `Exceptions` WHERE `Guid` = @Guid;";

    /// <inheritdoc />
    public string ListSql => """
        SELECT * FROM `Exceptions`
        WHERE (@ApplicationName IS NULL OR `ApplicationName` = @ApplicationName)
          AND (@TenantId IS NULL OR `TenantId` = @TenantId)
          AND (@From IS NULL OR `CreationDate` >= @From)
          AND (@To IS NULL OR `CreationDate` <= @To)
          AND (@Search IS NULL OR `Type` LIKE @Search OR `Message` LIKE @Search)
          AND (@IncludeDeleted OR `DeletionDate` IS NULL)
        ORDER BY `LastLogDate` DESC LIMIT @PageSize OFFSET @Offset;
        """;

    /// <inheritdoc />
    public string CountSql => """
        SELECT COUNT(*) FROM `Exceptions`
        WHERE (@ApplicationName IS NULL OR `ApplicationName` = @ApplicationName)
          AND (@TenantId IS NULL OR `TenantId` = @TenantId)
          AND (@From IS NULL OR `CreationDate` >= @From)
          AND (@To IS NULL OR `CreationDate` <= @To)
          AND (@Search IS NULL OR `Type` LIKE @Search OR `Message` LIKE @Search)
          AND (@IncludeDeleted OR `DeletionDate` IS NULL);
        """;

    /// <inheritdoc />
    public string ProtectSql => "UPDATE `Exceptions` SET `IsProtected` = TRUE WHERE `Guid` = @Guid;";

    /// <inheritdoc />
    public string SoftDeleteSql => "UPDATE `Exceptions` SET `DeletionDate` = @DeletionDate WHERE `Guid` = @Guid AND `DeletionDate` IS NULL;";

    /// <inheritdoc />
    public string HardDeleteSql => "DELETE FROM `Exceptions` WHERE `Guid` = @Guid;";

    /// <inheritdoc />
    public string PurgeSql => "DELETE FROM `Exceptions` WHERE `IsProtected` = FALSE AND `CreationDate` < @OlderThan;";

    /// <inheritdoc />
    public DynamicParameters BuildInsertParameters(ExceptionEntry entry)
        => ExceptionStoreParameters.Insert(entry, temporalDbType: null);

    /// <inheritdoc />
    public DynamicParameters BuildRollupParameters(ExceptionEntry entry, TimeSpan rollupPeriod)
        => ExceptionStoreParameters.Rollup(entry, rollupPeriod, temporalDbType: null);

    /// <inheritdoc />
    public DynamicParameters BuildSoftDeleteParameters(Guid guid, DateTime deletionDateUtc)
        => ExceptionStoreParameters.SoftDelete(guid, deletionDateUtc, temporalDbType: null);

    /// <inheritdoc />
    public DynamicParameters BuildPurgeParameters(DateTime olderThanUtc)
        => ExceptionStoreParameters.Purge(olderThanUtc, temporalDbType: null);
}
