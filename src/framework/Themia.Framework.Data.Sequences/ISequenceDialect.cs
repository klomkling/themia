using System.Data.Common;

namespace Themia.Framework.Data.Sequences;

/// <summary>Per-engine SQL and connection factory for the sequence allocator.</summary>
/// <remarks>
/// Public so an adopter on an engine Themia does not ship can supply one without forking the package —
/// the same seam as <c>IExceptionalSqlDialect</c> and <c>INotificationsSqlDialect</c>. Every statement
/// takes <c>@tenant</c>, <c>@key</c> and (where it writes) <c>@val</c>.
/// </remarks>
public interface ISequenceDialect
{
    /// <summary>Opens a NEW connection. Enlistment in an ambient transaction must be suppressed.</summary>
    /// <param name="connectionString">The configured connection string.</param>
    /// <returns>An unopened connection.</returns>
    DbConnection CreateConnection(string connectionString);

    /// <summary>Reads <c>next_value</c> for <c>(@tenant, @key)</c>, holding a row lock until commit.</summary>
    string SelectForUpdateSql { get; }

    /// <summary>Sets <c>next_value = @val</c> for <c>(@tenant, @key)</c>.</summary>
    string UpdateNextValueSql { get; }

    /// <summary>Inserts <c>(@tenant, @key, @val)</c> atomically, doing nothing when the row exists.</summary>
    string InsertIfMissingSql { get; }
}
