using System.Data.Common;

namespace Themia.Audit;

/// <summary>
/// Per-database strategy: supplies a connection and every DB-specific SQL statement
/// <see cref="AuditStoreEngine"/> runs against <c>themia_audit_events</c>. Implemented once per
/// provider package (<c>Themia.Audit.PostgreSql</c>/<c>.MySql</c>/<c>.SqlServer</c>). Public so an
/// adopter on an engine Themia does not ship can supply one without forking — the same seam as
/// <c>IExceptionalSqlDialect</c> and <c>ISequenceDialect</c>.
/// </summary>
public interface IAuditDialect
{
    /// <summary>
    /// Opens a new, unopened connection. <see cref="AuditStoreEngine"/> never calls this (design §7,
    /// the store never opens a connection) — it exists for every caller that has no ambient unit of
    /// work to join instead: a dashboard, a scheduled <see cref="IAuditStore.PurgeAsync"/> job, and tests.
    /// </summary>
    DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Inserts one row and yields the generated <c>id</c>. Each engine retrieves its identity value its
    /// own way (<c>RETURNING id</c> on PostgreSQL, <c>OUTPUT INSERTED.id</c> on SQL Server, a trailing
    /// <c>SELECT LAST_INSERT_ID()</c> on MySQL), so this is never one portable statement across engines.
    /// </summary>
    string InsertSql { get; }

    /// <summary>
    /// Selects a filtered, paged page of <c>themia_audit_events</c>, ordered by
    /// <c>occurred_at DESC, id DESC</c> with engine-appropriate paging.
    /// </summary>
    string SelectPageSql { get; }

    /// <summary>Counts rows matching the same predicate as <see cref="SelectPageSql"/>, ignoring paging.</summary>
    string CountSql { get; }

    /// <summary>Deletes every row with <c>occurred_at &lt; @OlderThan</c>.</summary>
    string PurgeSql { get; }
}
