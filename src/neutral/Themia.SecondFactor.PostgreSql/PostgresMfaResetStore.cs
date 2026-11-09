using Dapper;

namespace Themia.SecondFactor.PostgreSql;

/// <summary>Calls <c>second_factor_reset_user</c>, the one implementation shared with the operator's break-glass function.</summary>
internal sealed class PostgresMfaResetStore(SecondFactorDataSource source) : IMfaResetStore
{
    public async Task<int> ResetAsync(
        string? tenantId, string userId, string actor, string action, string reason, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT second_factor_reset_user(@TenantId, @UserId, @Actor, @Action, @Reason, @Now)",
            new { TenantId = tenantId, UserId = userId, Actor = actor, Action = action, Reason = reason, Now = now.ToUniversalTime() },
            cancellationToken: cancellationToken));
    }
}
