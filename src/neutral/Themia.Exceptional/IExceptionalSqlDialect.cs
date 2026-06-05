using System.Data;
using System.Data.Common;

namespace Themia.Exceptional;

/// <summary>
/// Per-database strategy: supplies a connection and every DB-specific SQL statement the engine runs.
/// Implemented once per provider package (PostgreSql/MySql/SqlServer).
/// </summary>
public interface IExceptionalSqlDialect
{
    /// <summary>Creates a new, unopened connection to the exception store.</summary>
    DbConnection CreateConnection();

    /// <summary>
    /// DbType to apply to nullable temporal filter parameters (From/To) so the provider can resolve the
    /// type when the value is null. Return null to let the data provider infer it (e.g. SQLite).
    /// The engine binds From/To as UTC <see cref="DateTime"/> values under this <see cref="DbType"/>,
    /// so a provider must return a <see cref="DbType"/> it accepts for a UTC <see cref="DateTime"/>
    /// (PostgreSQL uses <see cref="DbType.DateTimeOffset"/>).
    /// </summary>
    DbType? TemporalFilterDbType { get; }

    /// <summary>
    /// Atomically rolls up a duplicate: increments DuplicateCount and refreshes LastLogDate for the most
    /// recent active row with the same ErrorHash+ApplicationName whose CreationDate >= @RollupSince.
    /// Params: @ErrorHash, @ApplicationName, @RollupSince, @LastLogDate. Returns rows affected (0 or 1).
    /// </summary>
    string RollupSql { get; }

    /// <summary>Inserts a new <see cref="ExceptionEntry"/>. Params mirror the entry's columns.</summary>
    string InsertSql { get; }

    /// <summary>Selects one row by @Guid. Returns the full column set.</summary>
    string GetByGuidSql { get; }

    /// <summary>Selects a filtered, paged page. Params: filter fields + @Offset, @PageSize.</summary>
    string ListSql { get; }

    /// <summary>Counts rows matching the filter. Params: filter fields.</summary>
    string CountSql { get; }

    /// <summary>Sets IsProtected = true by @Guid.</summary>
    string ProtectSql { get; }

    /// <summary>Sets DeletionDate = @DeletionDate by @Guid where not already deleted.</summary>
    string SoftDeleteSql { get; }

    /// <summary>Deletes the row by @Guid.</summary>
    string HardDeleteSql { get; }

    /// <summary>Deletes unprotected rows with CreationDate &lt; @OlderThan. Returns rows affected.</summary>
    string PurgeSql { get; }
}
