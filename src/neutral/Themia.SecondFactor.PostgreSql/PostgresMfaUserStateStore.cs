using Dapper;

using Npgsql;

namespace Themia.SecondFactor.PostgreSql;

internal sealed class PostgresMfaUserStateStore(NpgsqlDataSource dataSource) : IMfaUserStateStore
{
    public async Task<MfaLockState> GetLockStateAsync(string? tenantId, string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<LockRow>(new CommandDefinition(
            """
            SELECT failed_attempts AS FailedAttempts, locked_until AS LockedUntil
            FROM second_factor_user_state
            WHERE user_id = @UserId AND tenant_id IS NOT DISTINCT FROM @TenantId
            """,
            new { UserId = userId, TenantId = tenantId },
            cancellationToken: cancellationToken));
        return row is null ? new MfaLockState(0, null) : new MfaLockState(row.FailedAttempts, ToUtc(row.LockedUntil));
    }

    // One atomic upsert: the counter is per (tenant, user) across TOTP and recovery codes, so alternating kinds
    // cannot double the attempts. The lock is set in the same statement that reaches the limit.
    public async Task<MfaLockState> RecordFailureAsync(
        string? tenantId, string userId, int maxFailures, TimeSpan lockFor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<LockRow>(new CommandDefinition(
            """
            INSERT INTO second_factor_user_state (tenant_id, user_id, failed_attempts, locked_until, updated_at)
            VALUES (@TenantId, @UserId, 1, CASE WHEN 1 >= @MaxFailures THEN @LockedUntil END, @Now)
            ON CONFLICT ON CONSTRAINT ux_second_factor_user_state DO UPDATE
            SET failed_attempts = CASE WHEN second_factor_user_state.locked_until IS NOT NULL
                                            AND second_factor_user_state.locked_until <= @Now
                                       THEN 1 ELSE second_factor_user_state.failed_attempts + 1 END,
                locked_until = CASE
                    WHEN second_factor_user_state.locked_until IS NOT NULL AND second_factor_user_state.locked_until > @Now
                        THEN second_factor_user_state.locked_until
                    WHEN (CASE WHEN second_factor_user_state.locked_until IS NOT NULL
                                    AND second_factor_user_state.locked_until <= @Now
                               THEN 1 ELSE second_factor_user_state.failed_attempts + 1 END) >= @MaxFailures
                        THEN @LockedUntil
                    ELSE NULL END,
                updated_at = @Now
            RETURNING failed_attempts AS FailedAttempts, locked_until AS LockedUntil
            """,
            new { TenantId = tenantId, UserId = userId, MaxFailures = maxFailures, LockedUntil = (now + lockFor).ToUniversalTime(), Now = now.ToUniversalTime() },
            cancellationToken: cancellationToken));
        return new MfaLockState(row.FailedAttempts, ToUtc(row.LockedUntil));
    }

    public async Task ResetFailuresAsync(string? tenantId, string userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE second_factor_user_state SET failed_attempts = 0, locked_until = NULL, updated_at = @Now
            WHERE user_id = @UserId AND tenant_id IS NOT DISTINCT FROM @TenantId
            """,
            new { UserId = userId, TenantId = tenantId, Now = now.ToUniversalTime() },
            cancellationToken: cancellationToken));
    }

    public async Task<DateTimeOffset?> GetSpareNagStartedAtAsync(string? tenantId, string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var started = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            """
            SELECT spare_nag_started_at FROM second_factor_user_state
            WHERE user_id = @UserId AND tenant_id IS NOT DISTINCT FROM @TenantId
            """,
            new { UserId = userId, TenantId = tenantId },
            cancellationToken: cancellationToken));
        return ToUtc(started);
    }

    public async Task StartSpareNagAsync(string? tenantId, string userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO second_factor_user_state (tenant_id, user_id, spare_nag_started_at, updated_at)
            VALUES (@TenantId, @UserId, @Now, @Now)
            ON CONFLICT ON CONSTRAINT ux_second_factor_user_state DO UPDATE
            SET spare_nag_started_at = COALESCE(second_factor_user_state.spare_nag_started_at, @Now), updated_at = @Now
            """,
            new { TenantId = tenantId, UserId = userId, Now = now.ToUniversalTime() },
            cancellationToken: cancellationToken));
    }

    // timestamptz comes back as a UTC DateTime; give it its offset back.
    private static DateTimeOffset? ToUtc(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private sealed record LockRow(int FailedAttempts, DateTime? LockedUntil);
}
