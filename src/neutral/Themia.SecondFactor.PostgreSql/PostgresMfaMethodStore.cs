using Dapper;

using Npgsql;

namespace Themia.SecondFactor.PostgreSql;

internal sealed class PostgresMfaMethodStore(SecondFactorDataSource source) : IMfaMethodStore
{
    private const string UniqueViolation = "23505";

    // Every statement scopes by (tenant, user). IS NOT DISTINCT FROM so a platform user (null tenant) matches.
    private const string UserScope = "user_id = @UserId AND tenant_id IS NOT DISTINCT FROM @TenantId";

    public async Task<IReadOnlyList<MfaMethod>> ListAsync(string? tenantId, string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MethodRow>(new CommandDefinition(
            $"SELECT id, kind, label, enrolled_at AS EnrolledAt, disabled_at AS DisabledAt FROM second_factor_methods WHERE {UserScope} ORDER BY enrolled_at, id",
            new { UserId = userId, TenantId = tenantId },
            cancellationToken: cancellationToken));
        return [.. rows.Select(row => new MfaMethod(row.Id, Enum.Parse<MfaMethodKind>(row.Kind), row.Label, Utc(row.EnrolledAt), row.DisabledAt is { } disabled ? Utc(disabled) : null))];
    }

    public async Task<MfaEnrolOutcome> AddAsync(NewMfaMethod method, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO second_factor_methods
                    (id, tenant_id, user_id, kind, label, credential_id, public_key, user_handle, sign_count,
                     secret_protected, protection_scheme, last_used_step, enrolled_at, updated_at)
                VALUES
                    (@Id, @TenantId, @UserId, @Kind, @Label, @CredentialId, @PublicKey, @UserHandle, @SignCount,
                     @SecretProtected, @ProtectionScheme, @LastUsedStep, @EnrolledAt, @EnrolledAt)
                """,
                new
                {
                    Id = Guid.NewGuid(),
                    method.TenantId,
                    method.UserId,
                    Kind = method.Kind.ToString(),
                    method.Label,
                    method.CredentialId,
                    method.PublicKey,
                    method.UserHandle,
                    method.SignCount,
                    method.SecretProtected,
                    method.ProtectionScheme,
                    method.LastUsedStep,
                    EnrolledAt = method.EnrolledAt.ToUniversalTime(),
                },
                cancellationToken: cancellationToken));
            return MfaEnrolOutcome.Added;
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            return MfaEnrolOutcome.DuplicateCredential;
        }
    }

    public async Task<bool> RemoveAsync(string? tenantId, string userId, Guid methodId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM second_factor_methods WHERE id = @MethodId AND {UserScope}",
            new { MethodId = methodId, UserId = userId, TenantId = tenantId },
            cancellationToken: cancellationToken));
        return affected == 1;
    }

    public async Task<StoredPasskey?> FindPasskeyAsync(byte[] credentialId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<PasskeyRow>(new CommandDefinition(
            """
            SELECT id, tenant_id AS TenantId, user_id AS UserId, credential_id AS CredentialId,
                   public_key AS PublicKey, user_handle AS UserHandle, sign_count AS SignCount
            FROM second_factor_methods
            WHERE kind = 'Passkey' AND credential_id = @CredentialId AND disabled_at IS NULL
            """,
            new { CredentialId = credentialId },
            cancellationToken: cancellationToken));
        return row is null ? null : new StoredPasskey(row.Id, row.TenantId, row.UserId, row.CredentialId, row.PublicKey, row.UserHandle, row.SignCount);
    }

    public async Task<IReadOnlyList<byte[]>> ListPasskeyCredentialIdsAsync(string? tenantId, string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        var ids = await connection.QueryAsync<byte[]>(new CommandDefinition(
            $"SELECT credential_id FROM second_factor_methods WHERE kind = 'Passkey' AND disabled_at IS NULL AND {UserScope}",
            new { UserId = userId, TenantId = tenantId },
            cancellationToken: cancellationToken));
        return [.. ids];
    }

    public async Task<StoredTotp?> FindTotpAsync(string? tenantId, string userId, Guid methodId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TotpRow>(new CommandDefinition(
            $"""
            SELECT id, secret_protected AS SecretProtected, protection_scheme AS ProtectionScheme, last_used_step AS LastUsedStep
            FROM second_factor_methods
            WHERE id = @MethodId AND kind = 'Totp' AND disabled_at IS NULL AND {UserScope}
            """,
            new { MethodId = methodId, UserId = userId, TenantId = tenantId },
            cancellationToken: cancellationToken));
        return row is null ? null : new StoredTotp(row.Id, row.SecretProtected, row.ProtectionScheme, row.LastUsedStep);
    }

    public async Task<bool> TryAdvanceTotpStepAsync(string? tenantId, string userId, Guid methodId, long step, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE second_factor_methods
            SET last_used_step = @Step, last_used_at = @Now, updated_at = @Now
            WHERE id = @MethodId AND kind = 'Totp' AND disabled_at IS NULL AND last_used_step < @Step AND {UserScope}
            """,
            new { MethodId = methodId, UserId = userId, TenantId = tenantId, Step = step, Now = now.ToUniversalTime() },
            cancellationToken: cancellationToken));
        return affected == 1;
    }

    public async Task<bool> TryAdvanceTotpStepByMethodAsync(Guid methodId, long step, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE second_factor_methods
            SET last_used_step = @Step, last_used_at = @Now, updated_at = @Now
            WHERE id = @MethodId AND kind = 'Totp' AND disabled_at IS NULL AND last_used_step < @Step
            """,
            new { MethodId = methodId, Step = step, Now = now.ToUniversalTime() },
            cancellationToken: cancellationToken));
        return affected == 1;
    }

    public async Task<bool> TryUpdateSignCountAsync(string? tenantId, string userId, Guid methodId, long newSignCount, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE second_factor_methods
            SET sign_count = @NewSignCount, last_used_at = @Now, updated_at = @Now
            WHERE id = @MethodId AND kind = 'Passkey' AND disabled_at IS NULL
              AND (sign_count < @NewSignCount OR (sign_count = 0 AND @NewSignCount = 0))
              AND {UserScope}
            """,
            new { MethodId = methodId, UserId = userId, TenantId = tenantId, NewSignCount = newSignCount, Now = now.ToUniversalTime() },
            cancellationToken: cancellationToken));
        return affected == 1;
    }

    // timestamptz comes back as a UTC DateTime; give it its offset back.
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record MethodRow(Guid Id, string Kind, string Label, DateTime EnrolledAt, DateTime? DisabledAt);

    private sealed record PasskeyRow(Guid Id, string? TenantId, string UserId, byte[] CredentialId, byte[] PublicKey, byte[] UserHandle, long SignCount);

    private sealed record TotpRow(Guid Id, string SecretProtected, string ProtectionScheme, long LastUsedStep);
}
