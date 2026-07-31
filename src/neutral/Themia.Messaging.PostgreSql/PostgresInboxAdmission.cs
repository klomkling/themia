using System.Data.Common;

using Dapper;

using Themia.Messaging.Inbox;

namespace Themia.Messaging.PostgreSql;

/// <summary>PostgreSQL inbox admission. <c>ON CONFLICT DO NOTHING</c> makes the check-and-insert a single
/// atomic statement, and <c>received_at</c> is left to the database clock so a skewed app-server clock
/// cannot distort the retention window (the same reasoning as coord #0026's DB-generated sentAt).</summary>
internal sealed class PostgresInboxAdmission : IInboxAdmissionDialect
{
    private const string AdmitSql = """
        INSERT INTO messaging_inbox_messages (origin, message_id, tenant_id, type, received_at)
        VALUES (@origin, @messageId, @tenantId, @type, now())
        ON CONFLICT (origin, message_id) DO NOTHING
        """;

    /// <inheritdoc />
    public async Task<bool> TryAdmitAsync(
        DbConnection connection, DbTransaction? transaction, string origin, Guid messageId,
        string? tenantId, string type, CancellationToken ct)
    {
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            AdmitSql,
            new { origin, messageId, tenantId, type },
            transaction,
            cancellationToken: ct));

        return inserted == 1;
    }
}
