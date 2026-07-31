using System.Data.Common;

using Dapper;

using Microsoft.Data.SqlClient;

using Themia.Messaging.Inbox;

namespace Themia.Messaging.SqlServer;

/// <summary>SQL Server inbox admission. SQL Server has no insert-or-ignore, and <c>MERGE</c> is
/// deliberately avoided here — it is not race-free without <c>HOLDLOCK</c> and has a long history of
/// concurrency defects. Instead this uses a guarded insert (<c>INSERT ... SELECT ... WHERE NOT EXISTS</c>
/// with <c>WITH (UPDLOCK, HOLDLOCK)</c> on the existence check) and treats the residual unique-violation
/// race as a duplicate rather than an error: losing the insert race means another delivery of the same
/// message admitted first. <c>received_at</c> is left to the database clock via
/// <c>SYSDATETIMEOFFSET()</c> so a skewed app-server clock cannot distort the retention window.</summary>
internal sealed class SqlServerInboxAdmission : IInboxAdmissionDialect
{
    private const string AdmitSql = """
        INSERT INTO [messaging].[inbox_messages] (origin, message_id, tenant_id, type, received_at)
        SELECT @origin, @messageId, @tenantId, @type, SYSDATETIMEOFFSET()
        WHERE NOT EXISTS (
            SELECT 1 FROM [messaging].[inbox_messages] WITH (UPDLOCK, HOLDLOCK)
            WHERE origin = @origin AND message_id = @messageId
        )
        """;

    /// <inheritdoc />
    public async Task<bool> TryAdmitAsync(
        DbConnection connection, DbTransaction? transaction, string origin, Guid messageId,
        string? tenantId, string type, CancellationToken ct)
    {
        try
        {
            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                AdmitSql, new { origin, messageId, tenantId, type }, transaction, cancellationToken: ct));
            return inserted == 1;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Lost the insert race — another delivery of this message admitted first. That is a duplicate,
            // not an error.
            return false;
        }
    }
}
