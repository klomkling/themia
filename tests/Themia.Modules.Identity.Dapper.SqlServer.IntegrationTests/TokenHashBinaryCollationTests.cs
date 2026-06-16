using Microsoft.Data.SqlClient;
using Themia.Modules.Identity.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Identity.Dapper.SqlServer.IntegrationTests;

/// <summary>SQL-Server-specific regression for #87: token hashes are mixed-case Base64, and on a SQL
/// Server instance with a case-insensitive default collation the <c>token_hash</c> equality and unique
/// index would fold case — risking a wrong-row match or a spurious unique-violation between two hashes
/// that differ only by letter case. After the binary-collation migration the comparison is byte-exact.
/// PostgreSQL needs no equivalent test: its <c>text</c> comparison is already case-sensitive.</summary>
[Trait("Category", "Integration")]
public sealed class TokenHashBinaryCollationTests(SqlServerIdentityFixture fixture)
    : IClassFixture<SqlServerIdentityFixture>
{
    // Differ ONLY by letter case; under a case-insensitive collation these collide, under BIN2 they don't.
    private const string HashLower = "aB1cD2";
    private const string HashUpper = "Ab1Cd2";

    [Fact]
    public async Task Case_variant_hashes_are_distinct_rows_and_lookup_is_case_exact()
    {
        await ResetTokensAndUser();
        var userId = Guid.NewGuid();
        await InsertUser(userId);

        // Both inserts must succeed: under the binary collation the two case-variant hashes are
        // distinct keys, so the unique index does NOT raise a spurious collision.
        await InsertRefreshToken(Guid.NewGuid(), userId, HashLower);
        await InsertRefreshToken(Guid.NewGuid(), userId, HashUpper);

        Assert.Equal(2, await CountTokensFor(userId));

        // A byte-exact lookup returns ONLY the matching-case row — no wrong-row (case-folded) match.
        Assert.Equal(1, await CountTokensWithHash(HashLower));
        Assert.Equal(1, await CountTokensWithHash(HashUpper));
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task ResetTokensAndUser()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM [identity].refresh_tokens; DELETE FROM [identity].users;";
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertUser(Guid userId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO [identity].users " +
            "(id, user_name, normalized_user_name, email_confirmed, phone_number_confirmed, " +
            " security_stamp, is_active, access_failed_count, lockout_enabled, two_factor_enabled, " +
            " created_at, is_deleted) " +
            "VALUES (@id, @name, @norm, 0, 0, @stamp, 1, 0, 0, 0, SYSDATETIMEOFFSET(), 0);";
        command.Parameters.AddWithValue("@id", userId);
        command.Parameters.AddWithValue("@name", "collation-user");
        command.Parameters.AddWithValue("@norm", "COLLATION-USER");
        command.Parameters.AddWithValue("@stamp", Guid.NewGuid().ToString("N"));
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertRefreshToken(Guid id, Guid userId, string tokenHash)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO [identity].refresh_tokens (id, user_id, token_hash, family_id, expires_at, created_at) " +
            "VALUES (@id, @userId, @hash, @family, DATEADD(day, 14, SYSDATETIMEOFFSET()), SYSDATETIMEOFFSET());";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@hash", tokenHash);
        command.Parameters.AddWithValue("@family", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountTokensFor(Guid userId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [identity].refresh_tokens WHERE user_id = @userId;";
        command.Parameters.AddWithValue("@userId", userId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> CountTokensWithHash(string tokenHash)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [identity].refresh_tokens WHERE token_hash = @hash;";
        command.Parameters.AddWithValue("@hash", tokenHash);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
