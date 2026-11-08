using Dapper;

namespace Themia.SecondFactor.PostgreSql;

internal sealed class PostgresMfaAuditLog(SecondFactorDataSource source) : IMfaAuditLog
{
    public async Task AppendAsync(MfaAuditEntry entry, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO second_factor_audit (at, tenant_id, target_user_id, actor, action, method_kind, reason)
            VALUES (@Now, @TenantId, @TargetUserId, @Actor, @Action, @MethodKind, @Reason)
            """,
            new
            {
                Now = now.ToUniversalTime(),
                entry.TenantId,
                entry.TargetUserId,
                entry.Actor,
                entry.Action,
                MethodKind = entry.MethodKind?.ToString(),
                entry.Reason,
            },
            cancellationToken: cancellationToken));
    }
}
