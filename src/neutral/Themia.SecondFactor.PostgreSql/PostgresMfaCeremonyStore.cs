using Dapper;

namespace Themia.SecondFactor.PostgreSql;

internal sealed class PostgresMfaCeremonyStore(SecondFactorDataSource source) : IMfaCeremonyStore
{
    public async Task StoreAsync(
        string challengeId, string optionsJson, string? tenantId, string? userId, MfaCeremonyPurpose purpose, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        // A register or enrol ceremony with no user would be spendable by any caller.
        if (userId is null && purpose != MfaCeremonyPurpose.Authenticate)
        {
            throw new ArgumentException($"A {purpose} ceremony must be bound to a user.", nameof(userId));
        }

        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO second_factor_webauthn_ceremonies (challenge_id, options_json, tenant_id, user_id, purpose, expires_at)
            VALUES (@ChallengeId, @OptionsJson, @TenantId, @UserId, @Purpose, now() + @Ttl)
            """,
            new { ChallengeId = challengeId, OptionsJson = optionsJson, TenantId = tenantId, UserId = userId, Purpose = purpose.ToString(), Ttl = ttl },
            cancellationToken: cancellationToken));
    }

    // One statement: expiry is judged by the database clock inside the DELETE, and user and purpose are part of the
    // predicate, so a register ceremony cannot be spent as an assertion and A's ceremony cannot complete for B.
    // A discoverable sign-in has no user at begin, so a null stored user matches any caller user.
    public async Task<MfaCeremony?> TryConsumeAsync(string challengeId, string? tenantId, string? userId, MfaCeremonyPurpose purpose, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<CeremonyRow>(new CommandDefinition(
            """
            DELETE FROM second_factor_webauthn_ceremonies
            WHERE challenge_id = @ChallengeId AND purpose = @Purpose AND expires_at > now()
              AND tenant_id IS NOT DISTINCT FROM @TenantId AND (user_id IS NULL OR user_id = @UserId)
            RETURNING options_json AS OptionsJson, user_id AS UserId
            """,
            new { ChallengeId = challengeId, TenantId = tenantId, UserId = userId, Purpose = purpose.ToString() },
            cancellationToken: cancellationToken));
        return row is null ? null : new MfaCeremony(row.OptionsJson, row.UserId);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM second_factor_webauthn_ceremonies WHERE expires_at <= now()",
            cancellationToken: cancellationToken));
    }

    private sealed record CeremonyRow(string OptionsJson, string? UserId);
}
