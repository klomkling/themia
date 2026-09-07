using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Themia.Audit.SqlServer;

/// <summary>SQL Server implementation of <see cref="IAuditDialect"/> (Microsoft.Data.SqlClient).</summary>
public sealed class SqlServerAuditDialect : IAuditDialect
{
    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    /// <inheritdoc />
    public string InsertSql => """
        INSERT INTO themia_audit_events
        (event_uid, tenant_id, category, event_type, outcome, actor_id, actor_name, entity_type, entity_id, occurred_at, ip_address, user_agent, correlation_id, reason, data)
        OUTPUT INSERTED.id
        VALUES (@EventUid, @TenantId, @Category, @EventType, @Outcome, @ActorId, @ActorName, @EntityType, @EntityId, @OccurredAt, @IpAddress, @UserAgent, @CorrelationId, @Reason, @Data);
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
        OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
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
    public string PurgeSql => "DELETE FROM themia_audit_events WHERE occurred_at < @OlderThan;";
}
