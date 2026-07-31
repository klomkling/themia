using System.Data.Common;

using Dapper;

using Themia.Messaging.Inbox;

namespace Themia.Messaging.MySql;

/// <summary>MySQL/MariaDB inbox admission. <c>INSERT IGNORE</c> makes the check-and-insert a single
/// atomic statement (a duplicate key simply inserts zero rows instead of raising an error), and
/// <c>received_at</c> is left to the database clock via <c>UTC_TIMESTAMP(6)</c> so a skewed app-server
/// clock cannot distort the retention window (the same reasoning as coord #0026's DB-generated sentAt).</summary>
internal sealed class MySqlInboxAdmission : IInboxAdmissionDialect
{
    private const string AdmitSql = """
        INSERT IGNORE INTO inbox_messages (origin, message_id, tenant_id, type, received_at)
        VALUES (@origin, @messageId, @tenantId, @type, UTC_TIMESTAMP(6))
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
