using System.Data.Common;

using Dapper;

using MySqlConnector;

using Themia.Messaging.Inbox;

namespace Themia.Messaging.MySql;

/// <summary>MySQL inbox admission. A plain <c>INSERT</c> raises every error class — including a
/// duplicate-key conflict — and this dialect narrowly catches only the duplicate-key case and treats it as
/// a (non-exceptional) duplicate, letting every other error propagate. <c>received_at</c> is left to the
/// database clock via <c>UTC_TIMESTAMP(6)</c> so a skewed app-server clock cannot distort the retention
/// window (the same reasoning as coord #0026's DB-generated sentAt).</summary>
/// <remarks>
/// <c>INSERT IGNORE</c> was rejected: it downgrades EVERY error class to a warning, not just a duplicate
/// key — an over-length <c>origin</c> is silently truncated to fit the column instead of being rejected,
/// which can collapse two distinct peers into one dedup key.
///
/// <c>INSERT ... ON DUPLICATE KEY UPDATE origin = origin</c> plus reading back the affected-row count (1 =
/// inserted, 0 = no-op update) was tried first and REJECTED after verifying it against MySqlConnector 2.6.0
/// + MySQL 8.4: the affected-row count for <c>ON DUPLICATE KEY UPDATE</c> depends on the connection's
/// <c>UseAffectedRows</c> flag (CLIENT_FOUND_ROWS), which MySqlConnector defaults to <c>false</c> — the
/// OPPOSITE of what the native/expected semantics need — so by default every conflict reports 1 ("found"),
/// not 0, indistinguishably from a fresh insert. Worse, <c>SELECT ROW_COUNT()</c> read back in the same
/// session mirrors the SAME flag rather than giving a flag-independent answer, so it does not sidestep the
/// problem either. Forcing <c>UseAffectedRows=true</c> is not a safe fix here: unlike
/// <see cref="MySqlMessagingDialect"/> (which creates and owns its connections), this dialect runs on the
/// CALLER's ambient connection — pinning a connection-string flag it does not own could silently change the
/// affected-row semantics of unrelated statements the caller runs over the same connection/transaction.
///
/// A plain <c>INSERT</c> with the duplicate-key error caught by <see cref="MySqlErrorCode.DuplicateKeyEntry"/>
/// (1062) has none of these problems — verified against the same driver/server combination — and mirrors
/// the pattern <c>SqlServerInboxAdmission</c> already uses (catch a specific error number, let everything
/// else propagate).
/// </remarks>
internal sealed class MySqlInboxAdmission : IInboxAdmissionDialect
{
    private const string AdmitSql = """
        INSERT INTO messaging_inbox_messages (origin, message_id, tenant_id, type, received_at)
        VALUES (@origin, @messageId, @tenantId, @type, UTC_TIMESTAMP(6))
        """;

    /// <inheritdoc />
    public async Task<bool> TryAdmitAsync(
        DbConnection connection, DbTransaction? transaction, string origin, Guid messageId,
        string? tenantId, string type, CancellationToken ct)
    {
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                AdmitSql,
                new { origin, messageId, tenantId, type },
                transaction,
                cancellationToken: ct));

            return true;
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            // Lost the insert race — another delivery of this message admitted first. That is a duplicate,
            // not an error.
            return false;
        }
    }
}
