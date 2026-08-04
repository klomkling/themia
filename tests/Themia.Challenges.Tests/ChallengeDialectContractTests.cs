using System.Data.Common;
using Themia.Challenges;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// Asserts the one part of <see cref="IChallengeDialect"/> that is not just "implement this string" —
/// that <see cref="IChallengeDialect.ConsumeSql"/> is a single conditional statement, not a read
/// followed by a write. A dialect whose <c>ConsumeSql</c> does not carry its own guard makes atomicity
/// the caller's problem, which is exactly how a read-then-write regression gets in later.
/// </summary>
/// <remarks>
/// <see cref="AllDialects"/> carries only <see cref="ReferenceChallengeDialect"/> today: Task 3
/// defines the seam (<see cref="IChallengeDialect"/> and the FluentMigrator schema) but no real
/// engine implements it yet — that stub exists solely so this theory has a case to run and exercises
/// the contract it is meant to enforce. Tasks 4-6 add the PostgreSQL, MySQL, and SQL Server dialects
/// — each one MUST be appended to <see cref="AllDialects"/> as it lands, alongside (not instead of)
/// the reference stub, or this theory silently stops covering the real implementations.
/// </remarks>
public class ChallengeDialectContractTests
{
    public static IEnumerable<object[]> AllDialects()
    {
        yield return new object[] { new ReferenceChallengeDialect() };
        // Tasks 4-6 append one `yield return new object[] { new <Provider>ChallengeDialect(...) };`
        // per engine as PostgreSQL, MySQL, and SQL Server land.
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void ConsumeSql_ShouldBeConditional(IChallengeDialect dialect)
    {
        var sql = dialect.ConsumeSql;

        Assert.Contains("UPDATE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("consumed_at IS NULL", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A minimal, non-functional <see cref="IChallengeDialect"/> that exists only to give
    /// <see cref="AllDialects"/> a case to run before a real engine package lands. Its SQL properties
    /// are illustrative text satisfying each member's documented contract, not statements ever
    /// executed against a database — <see cref="CreateConnection"/> throws if called.
    /// </summary>
    private sealed class ReferenceChallengeDialect : IChallengeDialect
    {
        public DbConnection CreateConnection() =>
            throw new NotSupportedException($"{nameof(ReferenceChallengeDialect)} is a contract-test stub, not a real dialect.");

        public string InsertSql =>
            "INSERT INTO challenges (id, tenant_id, key, purpose, secret_hash, secret_salt, token_hash, attempts, expires_at, created_at) " +
            "VALUES (@Id, @TenantId, @Key, @Purpose, @SecretHash, @SecretSalt, @TokenHash, @Attempts, @ExpiresAt, @CreatedAt)";

        public string SelectLiveByScopeSql =>
            "SELECT * FROM challenges WHERE tenant_id = @TenantId AND key = @Key AND purpose = @Purpose " +
            "AND consumed_at IS NULL AND expires_at > @Now ORDER BY created_at DESC";

        public string SelectLiveByTokenHashSql =>
            "SELECT * FROM challenges WHERE token_hash = @TokenHash AND consumed_at IS NULL AND expires_at > @Now ORDER BY created_at DESC";

        public string ConsumeSql =>
            "UPDATE challenges SET consumed_at = @ConsumedAt WHERE id = @Id AND consumed_at IS NULL AND expires_at > @Now";

        public string RecordAttemptSql =>
            "UPDATE challenges SET attempts = attempts + 1 WHERE id = @Id AND consumed_at IS NULL";

        public string InvalidateLiveForScopeSql =>
            "UPDATE challenges SET consumed_at = @ConsumedAt WHERE tenant_id = @TenantId AND key = @Key AND purpose = @Purpose " +
            "AND consumed_at IS NULL AND expires_at > @Now";

        public string PurgeExpiredSql => "DELETE FROM challenges WHERE expires_at < @OlderThan";

        public string IncrementWindowSql =>
            "UPSERT challenge_rate_windows (id, tenant_id, key, purpose, window_start, count) " +
            "TARGET (@Id, @TenantId, @Key, @Purpose, @WindowStart, 1) INCREMENT count";

        public string SelectWindowCountsSql =>
            "SELECT purpose, count FROM challenge_rate_windows WHERE tenant_id = @TenantId AND key = @Key " +
            "AND ((purpose = @Purpose AND window_start = @ScopeWindowStart) OR (purpose IS NULL AND window_start = @KeyWindowStart))";

        public string DecrementWindowSql =>
            "UPDATE challenge_rate_windows SET count = GREATEST(count - 1, 0) " +
            "WHERE tenant_id = @TenantId AND key = @Key AND purpose = @Purpose AND window_start = @WindowStart";

        public string PurgeElapsedWindowsSql => "DELETE FROM challenge_rate_windows WHERE window_start < @OlderThan";
    }
}
