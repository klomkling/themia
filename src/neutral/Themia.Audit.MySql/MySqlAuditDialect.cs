using System.Data.Common;
using MySqlConnector;

namespace Themia.Audit.MySql;

/// <summary>MySQL implementation of <see cref="IAuditDialect"/> (MySqlConnector).</summary>
public sealed class MySqlAuditDialect : IAuditDialect
{
    /// <summary>
    /// Creates a connection with <c>GuidFormat</c> pinned to <see cref="MySqlGuidFormat.Char36"/>
    /// regardless of what <paramref name="connectionString"/> requests: the <c>event_uid</c> column is
    /// always <c>CHAR(36)</c> (FluentMigrator <c>AsGuid()</c> on MySQL), and a caller-supplied
    /// <c>GuidFormat</c>/<c>OldGuids</c> that disagreed with the column would silently corrupt every
    /// lookup by <see cref="AuditEntry.EventUid"/> (every by-guid query would match zero rows). Mirrors
    /// <c>Themia.Exceptional.MySql.MySqlExceptionalDialect</c>.
    /// </summary>
    public DbConnection CreateConnection(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            OldGuids = false, // cleared first: OldGuids and GuidFormat are mutually exclusive.
            GuidFormat = MySqlGuidFormat.Char36,
        };
        return new MySqlConnection(builder.ConnectionString);
    }

    /// <inheritdoc />
    public string InsertSql => """
        INSERT INTO themia_audit_events
        (event_uid, tenant_id, category, event_type, outcome, actor_id, actor_name, entity_type, entity_id, occurred_at, ip_address, user_agent, correlation_id, reason, data)
        VALUES (@EventUid, @TenantId, @Category, @EventType, @Outcome, @ActorId, @ActorName, @EntityType, @EntityId, @OccurredAt, @IpAddress, @UserAgent, @CorrelationId, @Reason, @Data);
        SELECT LAST_INSERT_ID();
        """;

    /// <inheritdoc />
    public string SelectPageSql => """
        SELECT id AS Id, event_uid AS EventUid, tenant_id AS TenantId, category AS Category,
               event_type AS EventType, outcome AS Outcome, actor_id AS ActorId, actor_name AS ActorName,
               entity_type AS EntityType, entity_id AS EntityId, occurred_at AS OccurredAt,
               ip_address AS IpAddress, user_agent AS UserAgent, correlation_id AS CorrelationId,
               reason AS Reason, data AS Data
        FROM themia_audit_events
        WHERE (@TenantId IS NULL OR tenant_id = @TenantId)
          AND (@HostLevelOnly = 0 OR tenant_id IS NULL)
          AND (@ActorId IS NULL OR actor_id = @ActorId)
          AND (@EntityType IS NULL OR entity_type = @EntityType)
          AND (@EntityId IS NULL OR entity_id = @EntityId)
          AND (@Category IS NULL OR category = @Category)
          AND (@Outcome IS NULL OR outcome = @Outcome)
          AND (@From IS NULL OR occurred_at >= @From)
          AND (@To IS NULL OR occurred_at <= @To)
        ORDER BY occurred_at DESC, id DESC
        LIMIT @Take OFFSET @Skip;
        """;

    /// <inheritdoc />
    public string CountSql => """
        SELECT COUNT(*) FROM themia_audit_events
        WHERE (@TenantId IS NULL OR tenant_id = @TenantId)
          AND (@HostLevelOnly = 0 OR tenant_id IS NULL)
          AND (@ActorId IS NULL OR actor_id = @ActorId)
          AND (@EntityType IS NULL OR entity_type = @EntityType)
          AND (@EntityId IS NULL OR entity_id = @EntityId)
          AND (@Category IS NULL OR category = @Category)
          AND (@Outcome IS NULL OR outcome = @Outcome)
          AND (@From IS NULL OR occurred_at >= @From)
          AND (@To IS NULL OR occurred_at <= @To);
        """;

    /// <inheritdoc />
    /// <remarks>Formatted as the 36-character "D" form the <c>CHAR(36)</c> column stores, so a write that
    /// joins a caller's unit of work is unaffected by that connection's <c>GuidFormat</c>/<c>OldGuids</c>
    /// settings — <c>CreateConnection</c>'s pin does not reach those writes.</remarks>
    public object BindEventUid(Guid value) => value.ToString("D");

    /// <inheritdoc />
    public string PurgeSql => "DELETE FROM themia_audit_events WHERE occurred_at < @OlderThan;";
}
