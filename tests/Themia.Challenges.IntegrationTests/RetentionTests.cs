using Dapper;

using Xunit;

namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// Pins the specific failure that made an earlier design wrong (see
/// <see cref="Migrations.ChallengeSchemaMigration"/>'s type-level remarks): counters and challenges once
/// shared one table, so purging expired challenges reset the per-key ceiling that bounds an SMS bill for
/// free. <see cref="IChallengeDialect.PurgeExpiredSql"/> only ever touches <c>challenges</c>;
/// <c>challenge_rate_windows</c> is a separate table with its own, much longer retention horizon
/// (<see cref="IChallengeDialect.PurgeElapsedWindowsSql"/>), purged independently.
/// <para>
/// Runs on PostgreSQL only: <c>PurgeExpiredSql</c> is an unconditional <c>DELETE ... WHERE expires_at &lt;
/// @OlderThan</c> with no engine-specific syntax on any of the three dialects, and the two-table split
/// this test protects is a schema-level property, not a per-engine one — a second and third run would
/// prove nothing the first didn't already prove.
/// </para>
/// </summary>
[Collection(PostgresChallengesCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RetentionTests
{
    private readonly PostgresChallengeFixture fixture;

    /// <summary>Creates the suite over the shared <see cref="PostgresChallengeFixture"/> container.</summary>
    public RetentionTests(PostgresChallengeFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task PurgingChallenges_ShouldNotResetThePerKeyCeiling()
    {
        var key = $"key-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var scope = new ChallengeScope(key, ChallengeEngineFixture.TightPurpose, tenantId);

        for (var i = 0; i < ChallengeEngineFixture.TightPerKeyLimit; i++)
        {
            var result = await fixture.Service.IssueAsync(scope);
            Assert.Equal(ChallengeIssueOutcome.Issued, result.Outcome);
        }

        var exhausted = await fixture.Service.IssueAsync(scope);
        Assert.Equal(ChallengeIssueOutcome.RateLimited, exhausted.Outcome);

        // Simulate the aggressive challenges-table retention job: purge every challenge, including ones
        // that are not even expired yet, to prove the counter's survival does not depend on TTL timing.
        await using var connection = fixture.Dialect.CreateConnection();
        await connection.OpenAsync();
        var purged = await connection.ExecuteAsync(
            fixture.Dialect.PurgeExpiredSql, new { OlderThan = DateTimeOffset.UtcNow.AddDays(1) });
        Assert.True(purged >= ChallengeEngineFixture.TightPerKeyLimit, $"expected at least {ChallengeEngineFixture.TightPerKeyLimit} rows purged, got {purged}");

        var remainingChallenges = await connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM challenges WHERE tenant_id = @TenantId AND {fixture.KeyColumn} = @Key",
            new { TenantId = tenantId, Key = key });
        Assert.Equal(0, remainingChallenges);

        // The specific failure this two-table split exists to prevent: the counter must have survived
        // the challenges purge untouched, so the next issue for this key is still refused.
        var stillRateLimited = await fixture.Service.IssueAsync(scope);
        Assert.Equal(ChallengeIssueOutcome.RateLimited, stillRateLimited.Outcome);
    }
}
