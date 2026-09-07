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

    /// <summary>
    /// Converts <paramref name="value"/> into the parameter value this engine's <c>event_uid</c> column
    /// stores, independently of the connection it is bound on.
    /// </summary>
    /// <param name="value">The identifier to bind.</param>
    /// <returns>The value to pass as the <c>@EventUid</c> parameter.</returns>
    /// <remarks>
    /// <c>CreateConnection</c> pins MySQL's <c>GuidFormat</c>, but the writes that join a caller's unit of
    /// work never go through it — they run on the application's own connection
    /// (<c>AuditTransactionPolicy.RequireTransaction</c> is the default for activity events). An adopter
    /// whose MySQL connection string sets <c>GuidFormat=Binary16</c> or <c>OldGuids=true</c> would
    /// otherwise send 16 raw bytes into a <c>CHAR(36)</c> column, and every later lookup by
    /// <see cref="AuditEntry.EventUid"/> would match nothing. Formatting here makes the stored
    /// representation a property of the dialect rather than of whoever opened the connection.
    /// <para>
    /// Defaulted rather than abstract: this interface is a published extension seam — an adopter on an
    /// engine Themia does not ship supplies their own — so adding a required member would break every
    /// existing implementation. The default suits any engine with a native UUID type; override it where
    /// the column is textual, as MySQL's <c>CHAR(36)</c> is.
    /// </para>
    /// </remarks>
    object BindEventUid(Guid value) => value;
}
