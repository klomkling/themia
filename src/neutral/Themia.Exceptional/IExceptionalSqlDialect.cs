using Dapper;
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
    /// Binds the date-range filter parameters (<c>@From</c>/<c>@To</c>) onto <paramref name="args"/> using the
    /// DbType appropriate for this provider's timestamp column type. Values are already UTC-coerced by the engine.
    /// </summary>
    void AddTemporalFilters(DynamicParameters args, DateTime? from, DateTime? to);

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
