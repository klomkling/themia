using Dapper;

namespace Themia.SecondFactor.PostgreSql;

internal sealed class PostgresMfaRecoveryCodeStore(SecondFactorDataSource source) : IMfaRecoveryCodeStore
{
    private const string UserScope = "user_id = @UserId AND tenant_id IS NOT DISTINCT FROM @TenantId";

    public async Task ReplaceAsync(
        string? tenantId, string userId, IReadOnlyCollection<string> codeHashes, string hashScheme, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(codeHashes);
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Two concurrent regenerations would each delete then insert under READ COMMITTED and leave two batches;
        // the per-user advisory lock serialises them for the life of this transaction.
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@Key, 0))",
            new { Key = $"second-factor-recovery:{tenantId}\n{userId}" },
            transaction,
            cancellationToken: cancellationToken));

        // Used codes stay as history; only the unused backups are swapped for the new batch.
        await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM second_factor_recovery_codes WHERE used_at IS NULL AND {UserScope}",
            new { UserId = userId, TenantId = tenantId },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var hash in codeHashes)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO second_factor_recovery_codes (id, tenant_id, user_id, code_hash, hash_scheme, created_at)
                VALUES (@Id, @TenantId, @UserId, @Hash, @HashScheme, @Now)
                """,
                new { Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, Hash = hash, HashScheme = hashScheme, Now = now.ToUniversalTime() },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> TryConsumeAsync(string? tenantId, string userId, string codeHash, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE second_factor_recovery_codes SET used_at = @Now WHERE code_hash = @CodeHash AND used_at IS NULL AND {UserScope}",
            new { UserId = userId, TenantId = tenantId, CodeHash = codeHash, Now = now.ToUniversalTime() },
            cancellationToken: cancellationToken));
        return affected == 1;
    }

    public async Task<int> CountUnusedAsync(string? tenantId, string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT count(*) FROM second_factor_recovery_codes WHERE used_at IS NULL AND {UserScope}",
            new { UserId = userId, TenantId = tenantId },
            cancellationToken: cancellationToken));
    }
}
